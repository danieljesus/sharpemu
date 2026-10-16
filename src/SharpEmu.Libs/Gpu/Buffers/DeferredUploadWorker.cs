// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_DEFERRED_PROTECT (on by default; =0 disables): the render thread decides an upload and records its copy; this thread
// protects the cleared pages again and then copies their bytes into the reserved staging. A guest write before the
// protection lands in the copy; one after it faults and dirties the page for the next upload. The scheduler joins
// before every submit, so the GPU never reads staging that is still being written.
internal sealed class DeferredUploadWorker : IDisposable
{
    internal sealed record Job(List<TrackedRegion> Regions, GuestBufferUploader Uploader, GpuRingBuffer Source,
        List<(ulong Address, ulong Offset, int Size)> Reads);

    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_PROTECT") != "0";

    // SHARPEMU_DEFERRED_PROTECT_WORKERS: threads taking jobs. Jobs may then finish out of order, which is safe: each
    // protects under its regions' locks before it copies, and a write after any protection faults and dirties.
    private static readonly int WorkerCount = Math.Clamp(
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_PROTECT_WORKERS"), out var count) ? count : 1, 1, 8);

    // [local] SHARPEMU_DEFERRED_UPLOAD_WAIT_BLOCK (on by default, =0 turns it off): WaitIdle spins BlockSpins rounds of
    // Thread.SpinWait (16, 32, ... 512 iterations, some tens of microseconds in all) without yielding, then blocks on
    // a monitor the workers pulse after every job while a waiter is blocked (1 ms timeout as a backstop). =0
    // yield-spins until the target job is done (the submit thread's join, ~2 ms/frame in GTA V).
    internal static readonly bool BlockWait = Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_UPLOAD_WAIT_BLOCK") != "0";
    private const int BlockSpins = 6;

    private readonly BlockingCollection<Job> _queue = new();
    private readonly object _idleGate = new();
    private long _enqueued;
    private long _completed;
    private int _blockedWaiters;
    private Exception? _failure;

    public DeferredUploadWorker()
    {
        for (var index = 0; index < WorkerCount; index++)
        {
            new Thread(Run) { IsBackground = true, Name = $"SharpEmu deferred uploads {index}" }.Start();
        }
    }

    public void Enqueue(Job job)
    {
        Interlocked.Increment(ref _enqueued);
        _queue.Add(job);
    }

    // [local] The jobs enqueued so far: a submit captures this when its buffer is sealed and joins it before the submit.
    public long Enqueued => Volatile.Read(ref _enqueued);

    // Every job enqueued so far has protected and copied.
    public void WaitIdle() => WaitIdle(Volatile.Read(ref _enqueued));

    // [local] The first target jobs have protected and copied. With one worker, jobs finish in order.
    public void WaitIdle(long target)
    {
        if (Volatile.Read(ref _completed) >= target)
        {
            return;
        }

        // The submit thread keeps its own counters: the render-thread profile is not shared.
        using var profileScope = SharpEmu.Libs.Gpu.Scheduling.SubmissionScheduler.OnSubmitThread
            ? default
            : RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.DeferredUploadWait);
        var spinner = new SpinWait();
        var spins = 0;
        while (Volatile.Read(ref _completed) < target)
        {
            if (Volatile.Read(ref _failure) is { } failure)
            {
                throw new InvalidOperationException("A deferred upload failed.", failure);
            }

            if (!BlockWait)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }
            else if (spins < BlockSpins)
            {
                Thread.SpinWait(16 << spins++);
            }
            else
            {
                WaitBlocked(target);
            }
        }
    }

    // [local] SHARPEMU_DEFERRED_UPLOAD_WAIT_BLOCK=1. The waiter counts itself before it reads the counter and the
    // worker increments the counter before it reads the waiters (both interlocked), so one of them sees the other.
    private void WaitBlocked(long target)
    {
        Interlocked.Increment(ref _blockedWaiters);
        try
        {
            lock (_idleGate)
            {
                if (Volatile.Read(ref _completed) < target && Volatile.Read(ref _failure) is null)
                {
                    Monitor.Wait(_idleGate, 1);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _blockedWaiters);
        }
    }

    private void Run()
    {
        SharpEmu.HLE.Host.HostLaneReservation.ApplyToHelperThread();
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try
            {
                GuestPageTracker.ApplyDeferredProtection(job.Regions);
                job.Uploader.ReadInto(job.Source, job.Reads);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _failure, exception);
                Console.Error.WriteLine($"[GPU][ERROR] A deferred upload failed. {exception}");
            }

            Interlocked.Increment(ref _completed);
            if (Volatile.Read(ref _blockedWaiters) != 0)
            {
                lock (_idleGate)
                {
                    Monitor.PulseAll(_idleGate);
                }
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
