// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_DEFERRED_PROTECT (on by default; =0 disables): the render thread decides an upload and records its copy; this thread
// protects the cleared pages again and then copies their bytes into the reserved staging. A guest write before the
// protection lands in the copy; one after it faults and dirties the page for the next upload. The scheduler joins
// before every submit, so the GPU never reads staging that is still being written.
//
// [local] SHARPEMU_UPLOAD_GPU_WAIT=1 (off by default): the submit thread does not join the uploads on the CPU. A buffer
// whose jobs are not all done waits for them on the GPU: a timeline semaphore whose value is the completed count,
// signaled from the host (vkSignalSemaphore) by the worker that completes jobs while a submission waits for more. The
// buffer then starts when its staging is written, as before, but its vkQueueSubmit2 and the submit thread's wake-up
// overlap the last copies. The commands, their order and the bytes the GPU reads are the same.
internal sealed unsafe class DeferredUploadWorker : IDisposable
{
    public static readonly bool GpuWait = Environment.GetEnvironmentVariable("SHARPEMU_UPLOAD_GPU_WAIT") == "1";
    internal static DeferredUploadWorker? Instance;

    private Vk? _vk;
    private Device _device;
    private VkSemaphore _timeline;
    private long _requested;
    private long _signaled;
    private long _gpuWaits, _cpuDone, _signals, _gpuWaitCalls;
    private readonly object _signalGate = new();

    internal sealed record Job(List<TrackedRegion> Regions, GuestBufferUploader Uploader, GpuRingBuffer Source,
        List<(ulong Address, ulong Offset, int Size)> Reads);

    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_PROTECT") != "0";

    // SHARPEMU_DEFERRED_PROTECT_WORKERS: threads taking jobs. Jobs may then finish out of order, which is safe: each
    // protects under its regions' locks before it copies, and a write after any protection faults and dirties.
    // [local] The join is by job order: a job's number is taken with its place in the queue, and the completed count is
    // the longest prefix of finished jobs (with one worker, the order they finish in), so WaitIdle(target) returns
    // only once every job up to target has copied, also when jobs finish out of order.
    // [local] Default 2 (was 1; slow8395 A/B with RECORD_RING_WAKE=64: panning +0.6-0.8 FPS).
    private static readonly int WorkerCount = Math.Clamp(
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_PROTECT_WORKERS"), out var count) ? count : 2, 1, 8);

    // [local] SHARPEMU_DEFERRED_UPLOAD_WAIT_BLOCK (on by default, =0 turns it off): WaitIdle spins BlockSpins rounds of
    // Thread.SpinWait (16, 32, ... 512 iterations, some tens of microseconds in all) without yielding, then blocks on
    // a monitor the workers pulse after every job while a waiter is blocked (1 ms timeout as a backstop). =0
    // yield-spins until the target job is done (the submit thread's join, ~2 ms/frame in GTA V).
    internal static readonly bool BlockWait = Environment.GetEnvironmentVariable("SHARPEMU_DEFERRED_UPLOAD_WAIT_BLOCK") != "0";
    private const int BlockSpins = 6;

    private readonly BlockingCollection<(Job Job, long Number)> _queue = new();
    private readonly object _enqueueGate = new();
    private readonly object _orderGate = new();
    private readonly HashSet<long> _finishedOutOfOrder = new();
    private readonly object _idleGate = new();
    private long _enqueued;
    private long _completed;
    private int _blockedWaiters;
    private Exception? _failure;

    public DeferredUploadWorker(Vk? vk = null, Device device = default)
    {
        Instance = this;
        if (GpuWait && vk != null)
        {
            var typeInfo = new SemaphoreTypeCreateInfo
            {
                SType = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = SemaphoreType.Timeline,
                InitialValue = 0,
            };
            var createInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &typeInfo };
            if (vk.CreateSemaphore(device, &createInfo, null, out var timeline) == Result.Success)
            {
                _vk = vk;
                _device = device;
                _timeline = timeline;
            }

            Console.Error.WriteLine($"[GPU][UPLOAD_GPU_WAIT] timeline={(_vk != null ? "created" : "failed")}");
        }

        for (var index = 0; index < WorkerCount; index++)
        {
            new Thread(Run) { IsBackground = true, Name = $"SharpEmu deferred uploads {index}" }.Start();
        }
    }

    public void Enqueue(Job job)
    {
        lock (_enqueueGate)
        {
            _queue.Add((job, Interlocked.Increment(ref _enqueued)));
        }
    }

    // [local] SHARPEMU_UPLOAD_GPU_WAIT: the wait a buffer that needs the first target jobs submits with, or (0, 0)
    // when they are done. The request is published before the completed count is read again, and a worker publishes
    // the count before it reads the requests (both interlocked), so either this sees the jobs done or the worker
    // that completes them sees the request and signals.
    public (ulong Semaphore, ulong Value) GpuWaitFor(long target)
    {
        if ((++_gpuWaitCalls & 8191) == 0)
            Console.Error.WriteLine(TakeGpuWaitReport());
        if (_vk == null || Volatile.Read(ref _completed) >= target)
        {
            Interlocked.Increment(ref _cpuDone);
            return (0, 0);
        }

        var seen = Volatile.Read(ref _requested);
        while (seen < target)
        {
            var previous = Interlocked.CompareExchange(ref _requested, target, seen);
            if (previous == seen)
                break;
            seen = previous;
        }

        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _completed) >= target)
        {
            Interlocked.Increment(ref _cpuDone);
            return (0, 0);
        }

        Interlocked.Increment(ref _gpuWaits);
        return (_timeline.Handle, (ulong)target);
    }

    // True while a submitted buffer waits on the GPU for jobs not yet done (the starve probe's state).
    public bool GpuWaitPending => _vk != null && Volatile.Read(ref _requested) > Volatile.Read(ref _completed);

    public string TakeGpuWaitReport() =>
        $"[GPU][UPLOAD_GPU_WAIT] gpu_waits={Interlocked.Exchange(ref _gpuWaits, 0)} already_done={Interlocked.Exchange(ref _cpuDone, 0)} signals={Interlocked.Exchange(ref _signals, 0)} signaled={Volatile.Read(ref _signaled)} requested={Volatile.Read(ref _requested)} completed={Volatile.Read(ref _completed)}";

    private void SignalWaitingSubmissions()
    {
        if (_vk == null || Volatile.Read(ref _requested) <= Volatile.Read(ref _signaled))
            return;
        lock (_signalGate)
        {
            var completed = Volatile.Read(ref _completed);
            if (completed <= _signaled)
                return;
            var signal = new SemaphoreSignalInfo
            {
                SType = StructureType.SemaphoreSignalInfo,
                Semaphore = _timeline,
                Value = (ulong)completed,
            };
            var result = _vk.SignalSemaphore(_device, &signal);
            if (result != Result.Success)
                Console.Error.WriteLine($"[GPU][ERROR] vkSignalSemaphore(upload timeline, {completed}) failed: {result}");
            Volatile.Write(ref _signaled, completed);
            Interlocked.Increment(ref _signals);
        }
    }

    private void Finished(long number)
    {
        if (WorkerCount == 1)
        {
            Interlocked.Increment(ref _completed);
            SignalWaitingSubmissions();
            return;
        }

        lock (_orderGate)
        {
            if (number != Volatile.Read(ref _completed) + 1)
            {
                _finishedOutOfOrder.Add(number);
                return;
            }

            var completed = number;
            while (_finishedOutOfOrder.Remove(completed + 1))
                completed++;
            Interlocked.Exchange(ref _completed, completed);
        }

        SignalWaitingSubmissions();
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
        foreach (var (job, number) in _queue.GetConsumingEnumerable())
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

            Finished(number);
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
