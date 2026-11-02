// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
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

    private readonly BlockingCollection<(Job Job, long Number, long Enqueued)> _queue = new();
    private readonly object _enqueueGate = new();
    private readonly object _orderGate = new();
    private readonly HashSet<long> _finishedOutOfOrder = new();
    private readonly object _idleGate = new();
    private long _enqueued;
    private long _completed;
    private int _blockedWaiters;
    private Exception? _failure;

    // [local] SHARPEMU_PROTECT_OUTSIDE_LOCK=2: after each job's copy, with the job's region locks held (a clean page
    // cannot change then: a write faults and waits for the lock), every clean page the job copied is read again from
    // the backing and compared with its staging bytes. A difference on a page that stayed clean, in a region where no
    // page turned CPU-dirty since the protection, is a lost write. Pages GPU-dirty or CPU-dirty again, and pages in
    // regions the job did not lock or that turned dirty meanwhile, are counted apart and not compared.
    internal delegate bool BackingReader(ulong address, Span<byte> destination);

    internal BackingReader? VerifyReader { get; set; }

    private readonly bool _verify = GuestPageTracker.ProtectOutsideLock == 2;
    private long _verifyJobs, _verifyPages, _verifyDirty, _verifySkipped, _verifyDiscrepancies;
    private static int _verifyLogs;
    private static long _probeFrames;
    private long _reportStarted = Stopwatch.GetTimestamp();
    private int _reporting;

    // [local] SHARPEMU_UPLOAD_JOB_PROBE / SHARPEMU_PROTECT_OUTSIDE_LOCK=2: frames presented, for the reports.
    public static void NoteFrame()
    {
        if (DeferredProtectProbe.Enabled || Instance is { _verify: true })
            Interlocked.Increment(ref _probeFrames);
    }

    public (long Jobs, long Pages, long Dirty, long Skipped, long Discrepancies) VerifyCounts =>
        (Volatile.Read(ref _verifyJobs), Volatile.Read(ref _verifyPages), Volatile.Read(ref _verifyDirty),
            Volatile.Read(ref _verifySkipped), Volatile.Read(ref _verifyDiscrepancies));

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
            _queue.Add((job, Interlocked.Increment(ref _enqueued), DeferredProtectProbe.Enabled ? Stopwatch.GetTimestamp() : 0));
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

    // [local] SHARPEMU_SUBMIT_BATCH: the first target jobs have protected and copied (never blocks).
    public bool IsDone(long target) => Volatile.Read(ref _completed) >= target && Volatile.Read(ref _failure) is null;

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
        DeferredProtectProbe.MarkWorkerThread();
        var probe = DeferredProtectProbe.Enabled;
        long[]? dirtyings = null;
        foreach (var (job, number, enqueued) in _queue.GetConsumingEnumerable())
        {
            try
            {
                var started = probe ? Stopwatch.GetTimestamp() : 0;
                GuestPageTracker.ApplyDeferredProtection(job.Regions);
                if (_verify)
                {
                    if (dirtyings == null || dirtyings.Length < job.Regions.Count)
                        dirtyings = new long[Math.Max(job.Regions.Count, 16)];
                    for (var index = 0; index < job.Regions.Count; index++)
                        dirtyings[index] = job.Regions[index].CpuDirtyings;
                }

                var copyStarted = probe ? Stopwatch.GetTimestamp() : 0;
                job.Uploader.ReadInto(job.Source, job.Reads);
                var copied = probe ? Stopwatch.GetTimestamp() : 0;
                if (_verify)
                    Verify(job, dirtyings!);
                if (probe)
                {
                    DeferredProtectProbe.Add(DeferredProtectProbe.Part.Queued, started - enqueued);
                    DeferredProtectProbe.Add(DeferredProtectProbe.Part.Copy, copied - copyStarted);
                    DeferredProtectProbe.Add(DeferredProtectProbe.Part.Verify, Stopwatch.GetTimestamp() - copied);
                    DeferredProtectProbe.NoteJob(job.Regions.Count);
                }

                if (probe || _verify)
                    MaybeReport();
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

    private void MaybeReport()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref _reportStarted) < 10 * Stopwatch.Frequency || Interlocked.Exchange(ref _reporting, 1) != 0)
            return;
        Volatile.Write(ref _reportStarted, now);
        var frames = Interlocked.Exchange(ref _probeFrames, 0);
        if (DeferredProtectProbe.Enabled)
            Console.Error.WriteLine(DeferredProtectProbe.TakeReport(frames));
        if (_verify)
        {
            var (jobs, pages, dirty, skipped, discrepancies) = VerifyCounts;
            Console.Error.WriteLine(
                $"[GPU][DEFERRED_PROTECT_VERIFY] jobs={jobs} pages={pages} dirty_again={dirty} skipped={skipped} discrepancies={discrepancies}");
        }

        Volatile.Write(ref _reporting, 0);
    }

    private const ulong VerifyPageBytes = TrackerLayout.PageBytes;
    private const ulong VerifyBlockBytes = TrackerLayout.BlockBytes;
    [ThreadStatic] private static byte[]? t_verifyBytes;

    private void Verify(Job job, long[] dirtyings)
    {
        var reader = VerifyReader;
        if (reader == null)
            return;
        var regions = job.Regions;
        var order = new int[regions.Count];
        for (var index = 0; index < order.Length; index++)
            order[index] = index;
        Array.Sort(order, (left, right) => regions[left].BaseAddress.CompareTo(regions[right].BaseAddress));
        var locked = new List<TrackedRegion>(order.Length);
        var buffer = t_verifyBytes ??= new byte[VerifyPageBytes];
        try
        {
            // Address order, as the tracker takes several region locks.
            foreach (var index in order)
            {
                if (locked.Count != 0 && ReferenceEquals(locked[^1], regions[index]))
                    continue;
                regions[index].Lock.Enter();
                locked.Add(regions[index]);
            }

            Interlocked.Increment(ref _verifyJobs);
            foreach (var (address, offset, size) in job.Reads)
            {
                var end = address + (ulong)size;
                for (var cursor = address; cursor < end;)
                {
                    var chunkEnd = Math.Min(end, (cursor / VerifyPageBytes + 1) * VerifyPageBytes);
                    var bytes = chunkEnd - cursor;
                    var at = cursor;
                    var regionIndex = regions.FindIndex(region => region.BaseAddress <= at && at < region.BaseAddress + VerifyBlockBytes);
                    var region = regionIndex >= 0 ? regions[regionIndex] : null;
                    var regionOffset = region == null ? 0 : cursor - region.BaseAddress;
                    if (region == null || region.IsModified(WriteOrigin.Gpu, regionOffset, bytes) ||
                        region.CpuDirtyings != dirtyings[regionIndex])
                    {
                        Interlocked.Increment(ref _verifySkipped);
                    }
                    else if (region.IsModified(WriteOrigin.Cpu, regionOffset, bytes))
                    {
                        Interlocked.Increment(ref _verifyDirty);
                    }
                    else
                    {
                        var current = buffer.AsSpan(0, (int)bytes);
                        var staged = job.Source.Mapped.Slice((int)(offset + (cursor - address)), (int)bytes);
                        if (!reader(cursor, current))
                        {
                            Interlocked.Increment(ref _verifySkipped);
                        }
                        else
                        {
                            Interlocked.Increment(ref _verifyPages);
                            if (!current.SequenceEqual(staged))
                            {
                                Interlocked.Increment(ref _verifyDiscrepancies);
                                if (Interlocked.Increment(ref _verifyLogs) <= 20)
                                    Console.Error.WriteLine(
                                        $"[GPU][DEFERRED_PROTECT_VERIFY_MISMATCH] addr=0x{cursor:X16} bytes=0x{bytes:X} first_diff={current.CommonPrefixLength(staged)}");
                            }
                        }
                    }

                    cursor = chunkEnd;
                }
            }
        }
        finally
        {
            foreach (var region in locked)
                region.Lock.Exit();
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
