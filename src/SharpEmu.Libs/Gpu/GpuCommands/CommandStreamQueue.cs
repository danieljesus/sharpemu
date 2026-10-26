// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public enum IdleOutcome
{
    Completed,
    Cancelled,
    Failed,
}

public readonly record struct BlockedSnapshot(int Outstanding, double OldestAgeMilliseconds, ulong SampleWaitAddress, int SampleQueueId);

public enum SliceResult
{
    NoWork,
    AllBlocked,
    Progressed,
    BlockedWithoutProgress,
    Completed,
}

// Submissions of every queue; one thread runs the slices, other threads enqueue and wait.
public sealed class CommandStreamQueue
{
    private sealed record ControlBarrier(ulong CutoffOrdinal, Action Work, Action Cancel);

    public const int ComputeQueueCount = 56;
    public const int QueueCount = 1 + ComputeQueueCount;
    public const int AllBlockedRetryMilliseconds = 100;
    // One boundary may run ahead; explicit zero keeps the synchronous diagnostic mode.
    // [local] SHARPEMU_SUSPEND_POINTS_IN_FLIGHT=<n> lets the guest run n boundaries ahead (default 1).
    public static readonly int DefaultBoundariesInFlight =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SUSPEND_POINTS_IN_FLIGHT"), out var inFlight) && inFlight >= 0 ? inFlight : 1;

    private readonly ICommandStreamHost _host;
    private readonly object _gate = new();
    private readonly LinkedList<CommandSubmission>[] _queues = new LinkedList<CommandSubmission>[QueueCount];
    // [local] SHARPEMU_QUEUE_MASK (on by default; =0 disables): bit q is set while queue q holds a submission, and the
    // selection, the unblocking and the barrier checks visit only those queues instead of all 57 (with a modulo per
    // step). The scan ran for every slice and every wake of the front, ~13 ms per frame of its time in GTA V.
    private static readonly bool QueueMask = Environment.GetEnvironmentVariable("SHARPEMU_QUEUE_MASK") != "0";
    private ulong _occupied;
    // [local] SHARPEMU_FRONT_WAKE=1: a head blocked on a memory wait (WAIT_REG_MEM, memory semaphore, atomic compare) is
    // parked with its wait address, and a stream write the render thread applies (NotifyGuestWrite) unblocks the heads
    // parked on an overlapping address at once, instead of leaving them until the next completed slice, completed GPU
    // tick, presentation or 100 ms retry. It only adds wake-ups: every earlier trigger stays, so nothing that ran
    // before can be left blocked. With SHARPEMU_FRONT_PENDING_LABELS=1 a queued label retries only the heads parked on
    // its address instead of every head (only memory waits read pending labels).
    internal static readonly bool WakeOnWrite = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_WAKE") == "1";
    // The parked set is also kept for the wake-latency probe (SHARPEMU_PROFILE_FRONT).
    private static readonly bool TrackParked = WakeOnWrite || SharpEmu.Libs.VideoOut.FrontProfile.LoopEnabled;
    private ulong _parked;
    private readonly ulong[] _parkedAddress = new ulong[QueueCount];
    private readonly long[] _parkedFirstWrite = new long[QueueCount];
    private readonly long[] _parkedLastWrite = new long[QueueCount];
    private long _wakeLatencyFirstTicks, _wakeLatencyLastTicks, _wakeLatencyCount, _writeWakes, _writeNotices, _raceRetries;
    private long _appliedWrites;
    private readonly GpuCommandInterpreter?[] _interpreters = new GpuCommandInterpreter?[QueueCount];
    private readonly Queue<ControlBarrier> _controlBarriers = new();
    private int _nextQueue;
    private int _submissionCount;
    private ulong _admissionOrdinal;
    private bool _accepting = true;
    private bool _stopping;
    private bool _processing;
    private bool _graphicsDone;
    private ulong _submitId;
    private ulong _lastCompletedGpuTick;
    private int _doneCount;
    private int _pendingBoundaries;
    private IdleOutcome _outcome = IdleOutcome.Completed;
    private Thread? _processingThread;

    private readonly int _boundariesInFlight;

    public CommandStreamQueue(ICommandStreamHost host) : this(host, DefaultBoundariesInFlight)
    {
    }

    public CommandStreamQueue(ICommandStreamHost host, int boundariesInFlight)
    {
        _host = host;
        _boundariesInFlight = Math.Max(0, boundariesInFlight);
        for (var index = 0; index < QueueCount; index++)
        {
            _queues[index] = new LinkedList<CommandSubmission>();
        }
    }

    public ulong SubmissionsStarted { get; private set; }

    public ulong SlicesRun { get; private set; }

    public ulong BlockedRetries { get; private set; }

    // [local] The wait the last selected submission was blocked on, if it was (SHARPEMU_PROFILE_FRONT).
    internal SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind LastRunBlockedKind { get; private set; }

    internal ulong LastRunBlockedAddress { get; private set; }

    internal long LastRunBlockedSince { get; private set; }

    public int FrameNumber => Volatile.Read(ref _doneCount);

    public bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _submissionCount != 0 || _controlBarriers.Count != 0;
            }
        }
    }

    public int PendingSubmissionCount
    {
        get
        {
            lock (_gate)
            {
                return _submissionCount;
            }
        }
    }

    public bool HasUnblockedPending
    {
        get
        {
            lock (_gate)
            {
                return HasRunnableWorkLocked();
            }
        }
    }

    public GpuCommandInterpreter GetInterpreter(int queueId)
    {
        if (queueId < 0 || queueId >= QueueCount)
        {
            throw _host.Fatal($"The queue id is outside the queue table: queue={queueId}.");
        }

        lock (_gate)
        {
            return _interpreters[queueId] ??= new GpuCommandInterpreter(_host, queueId, queueId == 0 ? 0 : GpuCommandInterpreter.ComputeQueueBase + queueId - 1);
        }
    }

    public void EnqueueGraphics(ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        if (LabelTrace.Enabled) LabelTrace.Note(LabelTrace.Kind.GuestSubmit, 0, address, dwordCount); // [local]
        if (dwordCount == 0)
        {
            return;
        }

        lock (_gate)
        {
            var submission = new CommandSubmission(CommandSubmissionKind.Graphics, 0, address, dwordCount, submissionId, geometrySnapshots)
            {
                ResetInterpreter = _graphicsDone,
            };
            _graphicsDone = false;
            EnqueueLocked(submission);
        }
    }

    // Compute queues are addressed 0x20 to 0x57; queue id 1 is the first of them.
    public void EnqueueCompute(uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        if (LabelTrace.Enabled) LabelTrace.Note(LabelTrace.Kind.GuestSubmit, (int)queue, address, dwordCount); // [local]
        if (queue < GpuCommandInterpreter.ComputeQueueBase || queue >= GpuCommandInterpreter.ComputeQueueBase + ComputeQueueCount)
        {
            throw _host.Fatal($"The compute queue is outside the supported range: queue=0x{queue:X}.");
        }

        if (dwordCount == 0)
        {
            throw _host.Fatal($"A compute submission is empty: queue=0x{queue:X} address=0x{address:X16}.");
        }

        lock (_gate)
        {
            EnqueueLocked(new CommandSubmission(CommandSubmissionKind.Compute, (int)(queue - GpuCommandInterpreter.ComputeQueueBase) + 1, address, dwordCount, submissionId, geometrySnapshots));
        }
    }

    // Queues a video-out export flip behind the graphics submissions accepted before it.
    public bool TryEnqueueFlipPreparation(int handle, int index, ulong requestId)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            EnqueueLocked(new CommandSubmission(CommandSubmissionKind.FlipPreparation, 0, 0, 0, 0, null)
            {
                FlipHandle = handle,
                FlipIndex = index,
                FlipRequestId = requestId,
            });
            return true;
        }
    }

    private void EnqueueLocked(CommandSubmission submission)
    {
        if (!_accepting)
        {
            if (_outcome != IdleOutcome.Failed)
            {
                throw new OperationCanceledException("The command stream has stopped accepting submissions.");
            }

            throw _host.Fatal($"The command stream no longer accepts submissions: queue={submission.QueueId} address=0x{submission.Address:X16}.");
        }

        submission.AdmissionOrdinal = ++_admissionOrdinal;
        _queues[submission.QueueId].AddLast(submission);
        _occupied |= 1UL << submission.QueueId;
        _submissionCount++;
        if (submission.Kind is not (CommandSubmissionKind.FlipPreparation or CommandSubmissionKind.FrameBoundary))
            SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.Enqueued, submission.QueueId,
                submission.SubmissionId, submission.Address, submission.DwordCount, _submissionCount);
        Monitor.PulseAll(_gate);
    }

    public bool TryEnqueueControlBarrier(Action work, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(cancel);
        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            _controlBarriers.Enqueue(new ControlBarrier(_admissionOrdinal, work, cancel));
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    public IdleOutcome Done()
    {
        if (_processingThread == Thread.CurrentThread)
        {
            lock (_gate)
            {
                _graphicsDone = true;
                _doneCount++;
            }

            return IdleOutcome.Completed;
        }

        lock (_gate)
        {
            if (_outcome != IdleOutcome.Completed)
            {
                return _outcome;
            }

            _graphicsDone = true;
            if (!_accepting)
            {
                // Shutdown: no slice will run a queued boundary, so drain as before.
                while (_outcome == IdleOutcome.Completed && (_processing || _submissionCount != 0))
                {
                    Monitor.Wait(_gate);
                }

                _doneCount++;
                return _outcome;
            }

            _pendingBoundaries++;
            EnqueueLocked(new CommandSubmission(CommandSubmissionKind.FrameBoundary, 0, 0, 0, 0, null));
            while (_outcome == IdleOutcome.Completed && _pendingBoundaries > _boundariesInFlight)
            {
                Monitor.Wait(_gate);
            }

            return _outcome;
        }
    }

    // Returns once nothing is pending; a cancelled or failed queue reports that instead.
    public IdleOutcome WaitForIdle()
    {
        lock (_gate)
        {
            while (_outcome == IdleOutcome.Completed &&
                   (_processing || _submissionCount != 0 || _controlBarriers.Count != 0))
            {
                Monitor.Wait(_gate);
            }

            return _outcome;
        }
    }

    public void Wake()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    // Waits until a submission can run or the timeout passes; returns whether one can run.
    public bool WaitForWork(int timeoutMilliseconds)
    {
        lock (_gate)
        {
            if (HasRunnableWorkLocked() || _stopping)
            {
                return HasRunnableWorkLocked();
            }

            Monitor.Wait(_gate, timeoutMilliseconds);
            return HasRunnableWorkLocked();
        }
    }

    // Waits the whole interval unless a submission becomes runnable; stopping does not cut it short.
    public bool WaitForRetryInterval(int intervalMilliseconds)
    {
        lock (_gate)
        {
            // [local] Stopwatch, not TickCount64: its ~15.6 ms steps stretched a 1 ms retry wait.
            var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + intervalMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000;
            while (!HasRunnableWorkLocked())
            {
                var remaining = (deadline - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                if (remaining <= 0)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)Math.Max(1, remaining));
            }

            return true;
        }
    }

    // Publishes a failure: nothing runs any more and every waiter is released with Failed.
    public void Fail()
    {
        Action[] cancellations;
        lock (_gate)
        {
            FailLocked();
            cancellations = CancelControlBarriersLocked();
        }

        foreach (var cancel in cancellations)
            cancel();
    }

    private void FailLocked()
    {
        _outcome = IdleOutcome.Failed;
        _accepting = false;
        _stopping = true;
        DropAllLocked();
        _processing = false;
        Monitor.PulseAll(_gate);
    }

    // A completed GPU tick can make a blocked memory wait ready.
    public void NotifyCompletedGpuTick(ulong completedTick)
    {
        lock (_gate)
        {
            if (completedTick <= _lastCompletedGpuTick)
            {
                return;
            }

            _lastCompletedGpuTick = completedTick;
            if (LabelTrace.Enabled) LabelTrace.Note(LabelTrace.Kind.RetryOnTick, -1, 0, completedTick); // [local]
            RetryBlocked();
        }
    }

    // [local] SHARPEMU_REWIND_PATCH_WAKE: unblocks the heads suspended on a REWIND whose valid bit was clear (only those;
    // every other head keeps waiting for its own trigger). Returns how many it unblocked.
    public int RetryRewindHeads()
    {
        lock (_gate)
        {
            var woken = 0;
            for (var queueId = 0; queueId < QueueCount; queueId++)
            {
                if (QueueMask && (_occupied & (1UL << queueId)) == 0) continue;
                if (_queues[queueId].First is not { } head || !head.Value.Blocked ||
                    head.Value.BlockedKind != SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.Rewind) continue;
                head.Value.Blocked = false;
                woken++;
            }

            if (woken != 0) Monitor.PulseAll(_gate);
            return woken;
        }
    }

    public void RetryBlocked()
    {
        lock (_gate)
        {
            UnblockHeadsLocked();
            BlockedRetries++;
            Monitor.PulseAll(_gate);
        }
    }

    // Runs one slice of the next unblocked submission on the calling thread.
    public SliceResult ProcessOne()
    {
        CommandSubmission? submission = null;
        ControlBarrier? control = null;
        lock (_gate)
        {
            if (_processing)
            {
                throw _host.Fatal("The command stream queue is already processing a slice.");
            }

            if (IsControlBarrierReadyLocked())
            {
                control = _controlBarriers.Dequeue();
                LastRunBlockedKind = SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.None;
            }
            else
            {
                var selected = SelectQueueLocked();
                if (selected < 0)
                {
                    return _submissionCount != 0 ? SliceResult.AllBlocked : SliceResult.NoWork;
                }

                var queue = _queues[selected];
                submission = queue.First!.Value;
                queue.RemoveFirst();
                if (queue.Count == 0)
                {
                    _occupied &= ~(1UL << selected);
                }
                if (TrackParked)
                {
                    UnparkLocked(selected);
                }
                LastRunBlockedKind = submission.BlockedKind;
                LastRunBlockedAddress = submission.BlockedWaitAddress;
                LastRunBlockedSince = submission.BlockedKind == SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.None ? 0 : submission.BlockedSinceTicks;
                submission.BlockedKind = SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.None;
                _submissionCount--;
                _nextQueue = (selected + 1) % QueueCount;
            }

            _processing = true;
            _processingThread = Thread.CurrentThread;
            if (submission is { Started: false } &&
                submission.Kind is not (CommandSubmissionKind.FlipPreparation or CommandSubmissionKind.FrameBoundary))
                SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.ProcessingStarted, submission.QueueId,
                    submission.SubmissionId, submission.Address, submission.DwordCount, _submissionCount);
        }

        if (control is not null)
        {
            try
            {
                _host.RunControlBarrier(control.Work);
            }
            catch (Exception exception)
            {
                try
                {
                    Console.Error.WriteLine($"[GPU][ERROR] Command stream control barrier failed. {exception}");
                }
                catch (Exception)
                {
                }

                Fail();
                throw;
            }

            lock (_gate)
            {
                _processing = false;
                Monitor.PulseAll(_gate);
            }

            return SliceResult.Completed;
        }

        bool complete;
        var writesBefore = Volatile.Read(ref _appliedWrites); // [local] SHARPEMU_FRONT_WAKE
        try
        {
            complete = RunSlice(submission!);
        }
        catch (Exception exception)
        {
            try
            {
                // Report the cause before another submitting thread observes the failed queue.
                Console.Error.WriteLine($"[GPU][ERROR] Command stream slice failed: queue={submission!.QueueId} address=0x{submission.Address:X16}. {exception}");
            }
            catch (Exception)
            {
                // An output failure must not prevent queue failure or replace the original exception.
            }

            Fail();
            throw;
        }

        lock (_gate)
        {
            SliceResult result;
            if (!complete)
            {
                submission!.Blocked = true;
                submission.BlockedSinceTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                submission.BlockedPacketAddress = submission.Commands.CurrentPacketAddress;
                if (_interpreters[submission.QueueId] is { } interpreter)
                {
                    submission.BlockedKind = interpreter.LastSuspendKind == SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.None
                        ? SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.Other : interpreter.LastSuspendKind;
                    submission.BlockedWaitAddress = interpreter.LastSuspendAddress;
                }
                if (TrackParked && submission.BlockedWaitAddress != 0 && submission.BlockedKind is SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.Memory
                        or SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.Semaphore or SharpEmu.Libs.VideoOut.FrontProfile.SuspendKind.Atomic)
                {
                    ParkLocked(submission.QueueId, submission.BlockedWaitAddress);
                    // A write applied while the slice ran may already satisfy the wait it just parked on, and its
                    // notice found nothing parked: run the head once more.
                    if (WakeOnWrite && Volatile.Read(ref _appliedWrites) != writesBefore)
                    {
                        submission.Blocked = false;
                        _raceRetries++;
                    }
                }
                _queues[submission.QueueId].AddFirst(submission);
                _occupied |= 1UL << submission.QueueId;
                _submissionCount++;
                result = submission.Commands.MadeProgress ? SliceResult.Progressed : SliceResult.BlockedWithoutProgress;
            }
            else
            {
                UnblockHeadsLocked();
                result = SliceResult.Completed;
            }

            _processing = false;
            Monitor.PulseAll(_gate);
            return result;
        }
    }

    private void ParkLocked(int queueId, ulong address)
    {
        _parkedAddress[queueId] = address;
        _parkedFirstWrite[queueId] = 0;
        _parkedLastWrite[queueId] = 0;
        Volatile.Write(ref _parked, _parked | (1UL << queueId));
    }

    // The head of the queue runs again. A head that was parked on a memory wait and saw a write to its address since
    // charges the time from that write (the first and the last one) to now: the wake-up latency the probe reports.
    private void UnparkLocked(int queueId)
    {
        if ((_parked & (1UL << queueId)) == 0)
        {
            return;
        }

        if (_parkedFirstWrite[queueId] != 0)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            _wakeLatencyFirstTicks += now - _parkedFirstWrite[queueId];
            _wakeLatencyLastTicks += now - _parkedLastWrite[queueId];
            _wakeLatencyCount++;
        }

        Volatile.Write(ref _parked, _parked & ~(1UL << queueId));
    }

    // [local] A stream write the render thread applied (labels, WRITE_DATA, event stores): heads parked on an
    // overlapping address are stamped for the probe and, with SHARPEMU_FRONT_WAKE=1, unblocked. A wait reads at most
    // 8 bytes at its address.
    public void NotifyGuestWrite(ulong address, int length)
    {
        if (WakeOnWrite)
        {
            Interlocked.Increment(ref _appliedWrites);
        }

        if (Volatile.Read(ref _parked) == 0 || length <= 0)
        {
            return;
        }

        lock (_gate)
        {
            var woke = false;
            var now = 0L;
            for (var bits = _parked; bits != 0; bits &= bits - 1)
            {
                var queueId = System.Numerics.BitOperations.TrailingZeroCount(bits);
                var wait = _parkedAddress[queueId];
                if (wait >= address + (ulong)length || address >= wait + sizeof(ulong))
                {
                    continue;
                }

                if (now == 0)
                {
                    now = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                if (_parkedFirstWrite[queueId] == 0)
                {
                    _parkedFirstWrite[queueId] = now;
                }

                _parkedLastWrite[queueId] = now;
                _writeNotices++;
                if (WakeOnWrite && _queues[queueId].First is { } head && head.Value.Blocked)
                {
                    head.Value.Blocked = false;
                    _writeWakes++;
                    woke = true;
                }
            }

            if (woke)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    // [local] For [PERF][FRONT]: wake-ups by applied writes and the mean latency from the first / last write to a
    // parked head's address until it ran again, since the last call.
    internal (long Wakes, long Notices, long RaceRetries, long Count, double FirstMs, double LastMs) TakeWakeReport()
    {
        lock (_gate)
        {
            var count = _wakeLatencyCount;
            var report = (_writeWakes, _writeNotices, _raceRetries, count,
                count == 0 ? 0 : _wakeLatencyFirstTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / count,
                count == 0 ? 0 : _wakeLatencyLastTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / count);
            _writeWakes = _writeNotices = _raceRetries = _wakeLatencyCount = _wakeLatencyFirstTicks = _wakeLatencyLastTicks = 0;
            return report;
        }
    }

    private bool RunSlice(CommandSubmission submission)
    {
        if (submission.Kind == CommandSubmissionKind.FlipPreparation)
        {
            SlicesRun++;
            _host.PrepareCpuFlip(submission.FlipHandle, submission.FlipIndex, submission.FlipRequestId);
            _host.Flush();
            return true;
        }

        // A suspend point drains the graphics pipe: submit what the frame recorded.
        if (submission.Kind == CommandSubmissionKind.FrameBoundary)
        {
            SlicesRun++;
            _host.Flush();
            // The boundary counts as done once that flush ran on the host's queue.
            _host.RunAfterFlush(CompleteFrameBoundary);
            return true;
        }

        var firstSlice = !submission.Started;
        var processor = GetInterpreter(submission.QueueId);
        if (firstSlice && submission.ResetInterpreter)
        {
            processor.Reset();
        }

        if (firstSlice)
        {
            submission.Started = true;
            processor.SubmitId = ++_submitId;
            processor.ResetCounters();
            processor.SetFlip(default);
            SubmissionsStarted++;
        }

        SlicesRun++;
        processor.ProbeSubmissionEnqueued = submission.CreatedAt; // [local] SHARPEMU_LABEL_PROBE
        processor.ProbeSubmission = submission;
        if (Diagnostics.LabelLatencyProbe.Enabled)
        {
            var sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (submission.ProbeSlices++ == 0)
            {
                submission.ProbeStartedAt = sliceStart;
                submission.ProbeGfxAtStart = Diagnostics.LabelLatencyProbe.SliceTicks(0);
                submission.ProbeComputeAtStart = Diagnostics.LabelLatencyProbe.SliceTicks(1);
            }

            submission.ProbeSliceStart = sliceStart;
            if (submission.ProbeLastSliceEnd != 0)
                (submission.ProbeGaps ??= new()).Add((submission.ProbeLastKind, submission.ProbeLastAddress, sliceStart - submission.ProbeLastSliceEnd));
        }

        _host.BeginSubmission(submission.QueueId, submission.SubmissionId, submission.GeometrySnapshots);
        processor.ConstantEngineComplete = true;
        var complete = processor.Process(submission.Commands, submission.Address, submission.DwordCount) == SubmissionProgress.Complete;
        if (Diagnostics.LabelLatencyProbe.Enabled) // [local]
        {
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - submission.ProbeSliceStart;
            submission.ProbeOwnTicks += elapsed;
            submission.ProbeSliceStart = 0;
            submission.ProbeLastSliceEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            submission.ProbeLastKind = processor.LastSuspendKind;
            submission.ProbeLastAddress = processor.LastSuspendAddress;
            Diagnostics.LabelLatencyProbe.AddSlice(submission.QueueId == 0 ? 0 : 1, elapsed);
        }
        submission.CommandsComplete = complete;
        if (submission.Commands.MadeProgress)
        {
            if (complete)
            {
                _host.RunGarbageCollector();
            }

            _host.Flush();
        }
        else if (complete)
        {
            _host.RunGarbageCollector();
        }

        return complete;
    }

    private void CompleteFrameBoundary()
    {
        lock (_gate)
        {
            _pendingBoundaries--;
            _doneCount++;
            Monitor.PulseAll(_gate);
        }
    }

    public void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
    }

    // Processes to empty; with the flag, heads still blocked after a full retry cycle are cancelled.
    public IdleOutcome DrainForShutdown(bool cancelBlockedOnNoProgress)
    {
        for (;;)
        {
            var result = ProcessOne();
            switch (result)
            {
                case SliceResult.NoWork:
                    return Outcome;
                case SliceResult.Progressed:
                case SliceResult.Completed:
                    continue;
                case SliceResult.BlockedWithoutProgress:
                    if (HasUnblockedPending)
                    {
                        continue;
                    }

                    goto case SliceResult.AllBlocked;
                case SliceResult.AllBlocked:
                    _ = WaitForRetryInterval(AllBlockedRetryMilliseconds);
                    RetryBlocked();
                    if (!RunRetryCycle() && cancelBlockedOnNoProgress)
                    {
                        CancelBlocked();
                    }

                    continue;
            }
        }
    }

    // One pass over every head after a retry; true when any slice made progress.
    private bool RunRetryCycle()
    {
        var progressed = false;
        while (HasUnblockedPending)
        {
            var result = ProcessOne();
            if (result is SliceResult.Progressed or SliceResult.Completed)
            {
                progressed = true;
            }

            if (result is SliceResult.NoWork or SliceResult.AllBlocked)
            {
                break;
            }
        }

        return progressed;
    }

    private void CancelBlocked()
    {
        lock (_gate)
        {
            foreach (var queue in _queues)
            {
                if (queue.First is { } head && head.Value.Blocked)
                {
                    _submissionCount -= queue.Count;
                    foreach (var dropped in queue)
                    {
                        if (dropped.Kind == CommandSubmissionKind.FrameBoundary)
                        {
                            _pendingBoundaries--;
                        }
                    }

                    queue.Clear();
                    _outcome = IdleOutcome.Cancelled;
                }
            }

            RecomputeOccupiedLocked();
            _parked = 0;
            Monitor.PulseAll(_gate);
        }
    }

    // Drops every submission without stopping admission; the presenter uses it after device loss.
    public void DiscardAll()
    {
        Action[] cancellations;
        lock (_gate)
        {
            _outcome = IdleOutcome.Failed;
            DropAllLocked();
            cancellations = CancelControlBarriersLocked();
            Monitor.PulseAll(_gate);
        }

        foreach (var cancel in cancellations)
            cancel();
    }

    // A diagnostic view of the blocked heads: their count, the oldest age and one sample.
    public BlockedSnapshot SnapshotBlocked()
    {
        lock (_gate)
        {
            var count = 0;
            var oldestTicks = 0L;
            var sampleAddress = 0UL;
            var sampleQueue = -1;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var queueId = 0; queueId < QueueCount; queueId++)
            {
                if (_queues[queueId].First is not { } head || !head.Value.Blocked)
                {
                    continue;
                }

                count++;
                var age = now - head.Value.BlockedSinceTicks;
                if (sampleQueue < 0 || age > oldestTicks)
                {
                    oldestTicks = age;
                    sampleAddress = head.Value.BlockedPacketAddress;
                    sampleQueue = queueId;
                }
            }

            return new BlockedSnapshot(count, oldestTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, sampleAddress, sampleQueue);
        }
    }

    public IdleOutcome Outcome
    {
        get
        {
            lock (_gate)
            {
                return _outcome;
            }
        }
    }

    public int BlockedQueueCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var queue in _queues)
                {
                    if (queue.First is { } head && head.Value.Blocked)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    // [local] The occupied queues from _nextQueue upwards, then from 0: the order of the full scan.
    private (ulong High, ulong Low) OccupiedInScanOrder()
    {
        var start = _nextQueue;
        var high = _occupied & (ulong.MaxValue << start);
        var low = start == 0 ? 0 : _occupied & ((1UL << start) - 1);
        return (high, low);
    }

    // [local] SHARPEMU_LABEL_TIMELINE: every queue head as queue:blocked:kind:wait address:packet address.
    internal string DescribeHeads()
    {
        lock (_gate)
        {
            var text = new System.Text.StringBuilder();
            for (var queueId = 0; queueId < QueueCount; queueId++)
            {
                if (_queues[queueId].First is not { } head) continue;
                var submission = head.Value;
                text.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $" {queueId}:{(submission.Blocked ? 1 : 0)}:{(int)submission.BlockedKind}:{submission.BlockedWaitAddress:X}:{submission.BlockedPacketAddress:X}");
            }

            return text.ToString();
        }
    }

    private void UnblockHeadsLocked()
    {
        if (!QueueMask)
        {
            foreach (var queue in _queues)
            {
                if (queue.First is { } head)
                {
                    head.Value.Blocked = false;
                }
            }

            return;
        }

        for (var bits = _occupied; bits != 0; bits &= bits - 1)
        {
            if (_queues[System.Numerics.BitOperations.TrailingZeroCount(bits)].First is { } head)
            {
                head.Value.Blocked = false;
            }
        }
    }

    private void RecomputeOccupiedLocked() => _occupied = ComputeOccupiedLocked();

    private ulong ComputeOccupiedLocked()
    {
        var occupied = 0UL;
        for (var queueId = 0; queueId < QueueCount; queueId++)
        {
            if (_queues[queueId].Count != 0)
            {
                occupied |= 1UL << queueId;
            }
        }

        return occupied;
    }

    // [local] SHARPEMU_QUEUE_MASK_CHECK=1: every selection also runs the full scan and stops on a difference.
    private static readonly bool QueueMaskCheck = Environment.GetEnvironmentVariable("SHARPEMU_QUEUE_MASK_CHECK") == "1";

    private int SelectQueueLocked()
    {
        if (QueueMask)
        {
            var selected = SelectOccupiedQueueLocked();
            if (QueueMaskCheck && (selected != ScanQueuesLocked() || _occupied != ComputeOccupiedLocked()))
            {
                throw _host.Fatal($"The queue mask selected queue {selected}, the scan {ScanQueuesLocked()}: occupied=0x{_occupied:X} actual=0x{ComputeOccupiedLocked():X} next={_nextQueue}.");
            }

            return selected;
        }

        return ScanQueuesLocked();
    }

    private int ScanQueuesLocked()
    {
        if (_controlBarriers.TryPeek(out var control))
        {
            for (var offset = 0; offset < QueueCount; offset++)
            {
                var queueId = (_nextQueue + offset) % QueueCount;
                if (_queues[queueId].First is { } head &&
                    head.Value.AdmissionOrdinal <= control.CutoffOrdinal &&
                    !head.Value.Blocked)
                {
                    return queueId;
                }
            }
        }

        for (var offset = 0; offset < QueueCount; offset++)
        {
            var queueId = (_nextQueue + offset) % QueueCount;
            if (_queues[queueId].First is { } head && !head.Value.Blocked)
            {
                return queueId;
            }
        }

        return -1;
    }

    private int SelectOccupiedQueueLocked()
    {
        if (_occupied == 0)
        {
            return -1;
        }

        var (high, low) = OccupiedInScanOrder();
        if (_controlBarriers.TryPeek(out var control))
        {
            foreach (var part in (ReadOnlySpan<ulong>)[high, low])
            {
                for (var bits = part; bits != 0; bits &= bits - 1)
                {
                    var queueId = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (_queues[queueId].First is { } head &&
                        head.Value.AdmissionOrdinal <= control.CutoffOrdinal &&
                        !head.Value.Blocked)
                    {
                        return queueId;
                    }
                }
            }
        }

        foreach (var part in (ReadOnlySpan<ulong>)[high, low])
        {
            for (var bits = part; bits != 0; bits &= bits - 1)
            {
                var queueId = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (_queues[queueId].First is { } head && !head.Value.Blocked)
                {
                    return queueId;
                }
            }
        }

        return -1;
    }

    private bool HasRunnableWorkLocked() => IsControlBarrierReadyLocked() || SelectQueueLocked() >= 0;

    private bool IsControlBarrierReadyLocked()
    {
        if (!_controlBarriers.TryPeek(out var control))
        {
            return false;
        }

        if (QueueMask)
        {
            for (var bits = _occupied; bits != 0; bits &= bits - 1)
            {
                if (_queues[System.Numerics.BitOperations.TrailingZeroCount(bits)].First is { } head &&
                    head.Value.AdmissionOrdinal <= control.CutoffOrdinal)
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var queue in _queues)
        {
            if (queue.First is { } head && head.Value.AdmissionOrdinal <= control.CutoffOrdinal)
            {
                return false;
            }
        }

        return true;
    }

    private Action[] CancelControlBarriersLocked()
    {
        if (_controlBarriers.Count == 0)
        {
            return [];
        }

        var cancellations = new Action[_controlBarriers.Count];
        for (var index = 0; index < cancellations.Length; index++)
        {
            cancellations[index] = _controlBarriers.Dequeue().Cancel;
        }

        return cancellations;
    }

    private void DropAllLocked()
    {
        foreach (var queue in _queues)
        {
            queue.Clear();
        }

        _occupied = 0;
        _parked = 0;
        _submissionCount = 0;
        _pendingBoundaries = 0;
    }
}
