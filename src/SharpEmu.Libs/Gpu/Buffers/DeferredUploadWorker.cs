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

    private readonly BlockingCollection<Job> _queue = new();
    private long _enqueued;
    private long _completed;
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
        while (Volatile.Read(ref _completed) < target)
        {
            if (Volatile.Read(ref _failure) is { } failure)
            {
                throw new InvalidOperationException("A deferred upload failed.", failure);
            }

            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    private void Run()
    {
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
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
