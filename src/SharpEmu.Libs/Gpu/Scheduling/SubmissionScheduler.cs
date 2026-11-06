// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Rendering;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class SubmissionScheduler : IGpuTickScheduler, IDisposable
{
    internal static Action<string> OnFatal = message => Environment.FailFast(message);

    [ThreadStatic]
    private static SubmissionScheduler? _callbackOwner;

    private enum OperationState
    {
        Open,
        Draining,
        Closed,
    }

    private readonly record struct TickWork(Action Callback, ulong Tick, bool MemoryPublication = false);
    private readonly record struct ParkedWork(Action Callback, long Generation, ulong Tick = 0, bool MemoryPublication = false);
    private readonly record struct SubmissionTraceEntry(ulong Tick, uint Op, ulong SubmitId,
        uint Arg0, uint Arg1, uint Arg2, uint Arg3, ulong Arg4, int Waits = 0);
    private readonly SubmissionTraceEntry[] _submissionHistory = new SubmissionTraceEntry[32];
    private int _nextSubmissionHistoryIndex;
    private int _submissionHistoryCount;

    private readonly IGpuTickDevice _device;
    private readonly TickTimeline _timeline;
    private readonly RecordingBuffer _command;
    private readonly Action<SubmitBundle>? _prepareSubmit;
    private readonly Action<ulong>? _submitted;
    private readonly Action<ulong>? _completed;
    private readonly Queue<TickWork> _pending = new();
    private readonly Queue<TickWork> _priority = new();
    private readonly Queue<ParkedWork> _parked = new();
    private long _cutGeneration;
    private readonly SortedSet<long> _unjoinedCuts = new();
    private readonly object _operationLock = new();
    private readonly Thread _priorityThread;
    private bool _priorityActive;
    private ulong _priorityActiveTick;
    private OperationState _state = OperationState.Open;
    private bool _stopRequested;
    private bool _disposeRequested;
    private bool _deviceDisposed;
    private SubmissionContext? _context;
    private readonly IRenderingState _rendering;
    private ulong _lastMemoryDrainTick;
    private bool _publishingMemory;
    private ulong _pendingTailTick;
    private ulong _enqueueSequence;
    private const int MaxDeferredSubmissions = 8;
    private readonly List<nint> _deferredBuffers = new();
    private readonly List<ulong> _deferredTicks = new();
    private static long _deferredSubmissions;
    private static long _batchedSubmissions;
    private const int TickLedgerSize = 4096;
    private readonly ulong[] _ledgerTicks = new ulong[TickLedgerSize];
    private readonly long[] _reservedAt = new long[TickLedgerSize];
    private readonly long[] _submittedAt = new long[TickLedgerSize];
    private static readonly HLE.Diagnostics.ThreadCounter _heldTicks = new();
    private static readonly HLE.Diagnostics.ThreadCounter _heldTime = new();
    private static readonly HLE.Diagnostics.ThreadCounter _submitsAtLimit = new();
    private static readonly HLE.Diagnostics.ThreadCounter _submitsEnsured = new();
    private static readonly HLE.Diagnostics.ThreadCounter _submitsFolded = new();
    private static readonly HLE.Diagnostics.ThreadCounter _completions = new();
    private static readonly HLE.Diagnostics.ThreadCounter _completionAfterSubmit = new();

    public static string TakeTickLedgerReport()
    {
        var held = _heldTicks.Take();
        var completions = _completions.Take();
        return FormattableString.Invariant($"tick_hold=ticks:{held},avg_us:{Microseconds(_heldTime.Take(), held):F1} submits=limit:{_submitsAtLimit.Take()},ensure:{_submitsEnsured.Take()},fold:{_submitsFolded.Take()} ") +
            FormattableString.Invariant($"completion_callbacks={completions}/after_submit_us:{Microseconds(_completionAfterSubmit.Take(), completions):F1}");
    }

    private static double Microseconds(long ticks, long samples) =>
        samples == 0 ? 0 : ticks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / samples;

    public long ReservedAt(ulong tick) => Volatile.Read(ref _ledgerTicks[tick % TickLedgerSize]) == tick ? Volatile.Read(ref _reservedAt[tick % TickLedgerSize]) : 0;

    public long SubmittedAt(ulong tick) => Volatile.Read(ref _ledgerTicks[tick % TickLedgerSize]) == tick ? Volatile.Read(ref _submittedAt[tick % TickLedgerSize]) : 0;

    private void NoteReserved(ulong tick, long now, bool submitted)
    {
        var slot = tick % TickLedgerSize;
        Volatile.Write(ref _ledgerTicks[slot], 0);
        Volatile.Write(ref _reservedAt[slot], now);
        Volatile.Write(ref _submittedAt[slot], submitted ? now : 0);
        Volatile.Write(ref _ledgerTicks[slot], tick);
    }

    private void NoteDeferredSubmitted(long now)
    {
        foreach (var tick in _deferredTicks)
        {
            if (Diagnostics.FlipLatencyProbe.Enabled) Diagnostics.FlipLatencyProbe.NoteSubmitted(tick); // [local]
            if (Diagnostics.LabelLatencyProbe.Enabled) Diagnostics.LabelLatencyProbe.NoteSubmitted(tick); // [local]
            var slot = tick % TickLedgerSize;
            if (Volatile.Read(ref _ledgerTicks[slot]) != tick)
            {
                continue;
            }

            Volatile.Write(ref _submittedAt[slot], now);
            _heldTicks.Increment();
            _heldTime.Add(now - Volatile.Read(ref _reservedAt[slot]));
        }
    }

    // [local] SHARPEMU_SUBMIT_THREAD: the submit thread stamps the ledger when the queue takes a sealed tick.
    private void NoteSubmittedAt(ulong tick, long now)
    {
        var slot = tick % TickLedgerSize;
        if (Volatile.Read(ref _ledgerTicks[slot]) == tick)
        {
            Volatile.Write(ref _submittedAt[slot], now);
        }
    }

    public static string TakeDeferredReport() =>
        FormattableString.Invariant($"deferred_submits={Interlocked.Exchange(ref _deferredSubmissions, 0)} batched_submits={Interlocked.Exchange(ref _batchedSubmissions, 0)}");

    // [local] SHARPEMU_SUBMIT_THREAD (on by default for the presenter; =0 disables): the recording thread seals each
    // buffer (end, tick, signal, completion work) and a submit thread joins its deferred uploads and calls
    // vkQueueSubmit2, in tick order. Host waits on the timeline stay legal before the submit (wait-before-signal);
    // present and the readback queue wait for the submit first. A/B of 2026-10-12: -1.5 ms/frame, +0.47 FPS.
    internal static readonly bool SubmitThreadDefault = Environment.GetEnvironmentVariable("SHARPEMU_SUBMIT_THREAD") != "0";

    // Buffer is the scheduler's own buffer; Buffers is everything the submission carries in order (upstream's deferred
    // buffers, the own buffer, then the trailing worker buffers) under one bundle.
    private readonly record struct SealedSubmit(nint Buffer, nint[] Buffers, SubmitBundle Bundle, ulong Tick, long Target,
        SubmissionTraceEntry Trace, long Recorded = 0);

    [ThreadStatic]
    private static bool _onSubmitThread;
    private readonly Thread? _submitThread;
    private readonly Queue<SealedSubmit> _sealed = new();
    private readonly object _submitLock = new();
    private ulong _submittedTick;
    private bool _submitStopRequested;
    private bool _submitWorkerParked;
    private int _submitWaiters;
    private string? _submitFailure;

    public SubmissionScheduler(
        IGpuTickDevice device,
        IRenderingState rendering,
        Action<SubmitBundle>? prepareSubmit = null,
        Action<ulong>? submitted = null,
        Action<ulong>? completed = null,
        bool submitThread = false)
    {
        _device = device;
        _rendering = rendering;
        _prepareSubmit = prepareSubmit;
        _submitted = submitted;
        _completed = completed;
        _timeline = new TickTimeline(device);
        _command = new RecordingBuffer(this, device, rendering);
        _priorityThread = new Thread(RunPriorityWorker) { IsBackground = true, Name = "SharpEmu GPU priority" };
        _priorityThread.Start();
        if (submitThread) // [local]
        {
            _submitThread = new Thread(RunSubmitWorker) { IsBackground = true, Name = "SharpEmu GPU submit" };
            _submitThread.Start();
        }

        GpuStarveProbe.Start(this, device); // [local] SHARPEMU_GPU_STARVE_PROBE
    }

    // [local]
    public bool UsesSubmitThread => _submitThread != null;

    internal static bool OnSubmitThread => _onSubmitThread;

    // [local] The highest tick handed to the queue. Without a submit thread every sealed tick is submitted except the
    // ticks FlushDeferred holds back (upstream's deferred submissions; never held with the submit thread).
    public ulong SubmittedTick => _submitThread == null
        ? (Volatile.Read(ref _firstDeferredTick) is var firstDeferred and not 0 ? firstDeferred - 1 : CurrentTick - 1)
        : Volatile.Read(ref _submittedTick);

    private ulong _firstDeferredTick; // [local] the oldest tick in _deferredTicks, 0 when none; read by other threads

    public static bool InDeferredOperation => _callbackOwner != null;

    public bool InsideTickCallback => InDeferredOperation;

    public bool Active => _context != null;

    public RecordingBuffer Current
    {
        get
        {
            RequireActiveScheduler();
            return _command;
        }
    }

    public ulong CurrentTick => _timeline.CurrentTick;

    public ulong CompletedTick => _timeline.CompletedTick;

    public Action<nint, MemoryBarrierRequest>? MemoryBarrierRecorder { get; set; }

    public TickTimeline Timeline => _timeline;

    internal bool PriorityWorkerAlive => _priorityThread.IsAlive;

    // Set on a thread that does work ahead of the render thread: there a fatal condition only
    // abandons that work, which the render thread then does itself and fails on if it is real.
    [ThreadStatic]
    internal static bool ThrowOnFatal;

    public static bool ThrowsOnFatal => ThrowOnFatal;
    [ThreadStatic]
    private static Func<string, Exception>? s_readOnlyViolation;

    public static bool ReadOnlyThread => s_readOnlyViolation is not null;

    public static void MarkReadOnlyThread(Func<string, Exception> violation) => s_readOnlyViolation = violation;

    public static Exception ReadOnlyViolation(string operation) => s_readOnlyViolation!(operation);

    public sealed class AbandonedWorkException(string message) : Exception(message);

    // Report the fatal error, then return an exception to stop the caller.
    internal static Exception Fatal(string message)
    {
        if (VideoOut.FrontSpeculation.AbandonsFatal) // [local] SHARPEMU_FRONT_SPECULATE: stale bytes, not a broken stream
        {
            Interlocked.Increment(ref VideoOut.FrontSpeculation.Abandoned);
            throw new VideoOut.FrontSpeculation.AbandonException(message);
        }

        if (ThrowOnFatal)
        {
            throw new AbandonedWorkException(message);
        }

        Console.Error.WriteLine($"[GPU][FATAL] {message}");
        OnFatal(message);
        return new InvalidOperationException(message);
    }

    // Release the device once after shutdown. A callback leaves final disposal to the shutdown owner.
    public void Dispose()
    {
        GpuStarveProbe.Stop(); // [local]
        Volatile.Write(ref _disposeRequested, true);
        Shutdown();
        DisposeDeviceOnceClosed();
    }

    private void DisposeDeviceOnceClosed()
    {
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 1, "scheduler.op_1");
        lock (_operationLock)
        {
            if (_state != OperationState.Closed || _deviceDisposed)
            {
                return;
            }

            _deviceDisposed = true;
        }

        _device.Dispose();
    }

    public void Shutdown()
    {
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 2, "scheduler.op_2");
        lock (_operationLock)
        {
            if (_state == OperationState.Closed)
            {
                return;
            }

            if (_callbackOwner == this)
            {
                if (_state == OperationState.Open)
                {
                    throw Fatal("Cannot stop the scheduler from its own deferred callback.");
                }

                // Return to the shutdown owner so it can finish running the remaining callbacks.
                return;
            }

            if (_state == OperationState.Draining)
            {
                while (_state != OperationState.Closed)
                {
                    Monitor.Wait(_operationLock);
                }

                return;
            }

            _state = OperationState.Draining;
            while (_parked.Count != 0)
            {
                Unpark(_parked.Dequeue());
            }
        }

        if (!_command.IsInvalid)
        {
            Submit();
        }

        WaitSubmitted(CurrentTick - 1); // [local]
        StopSubmitWorker(); // [local]
        _timeline.Wait(CurrentTick - 1);
        RunCompletedOperations();
        WaitForAllPriorityOperations();
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 3, "scheduler.op_3");
        lock (_operationLock)
        {
            _stopRequested = true;
            Monitor.PulseAll(_operationLock);
        }

        _priorityThread.Join();
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 4, "scheduler.op_4");
        lock (_operationLock)
        {
            if (_pending.Count != 0 || _priority.Count != 0 || _priorityActive)
            {
                throw Fatal("The scheduler closed before all queued operations completed.");
            }

            _state = OperationState.Closed;
            Monitor.PulseAll(_operationLock);
        }

        if (Volatile.Read(ref _disposeRequested))
        {
            DisposeDeviceOnceClosed();
        }
    }

    public void Begin(SubmissionContext context)
    {
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 5, "scheduler.op_5");
        lock (_operationLock)
        {
            if (_state != OperationState.Open)
            {
                throw Fatal("Cannot start the scheduler after shutdown.");
            }
        }

        _context = context;
        if (_command.IsInvalid)
        {
            BeginNextCommandBuffer();
        }
        else
        {
            BindCurrentContext();
        }
    }

    public void EndRendering()
    {
        if (Active && !_command.IsInvalid)
        {
            Current.EndRendering();
        }
    }

    public void EndRendering(Vulkan.ScopeBreak reason)
    {
        if (Active && !_command.IsInvalid && _rendering.IsRendering)
        {
            Vulkan.BarrierTally.CountScopeBreak(reason);
        }

        EndRendering();
    }

    public bool TryDeferUntilRenderingEnds(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, List<ImageMemoryBarrier2> barriers) =>
        Active && !_command.IsInvalid && _rendering.IsRendering &&
        _rendering.TryDeferUntilRenderingEnds(sourceStages, destinationStages, barriers);

    public ulong Flush() => Flush(new SubmitBundle());

    public ulong Flush(SubmitBundle bundle) => Flush(bundle, default);

    public ulong Flush(SubmitBundle bundle, ReadOnlySpan<RecordingBuffer> trailing)
    {
        var tick = Submit(bundle, trailing);
        BeginNextCommandBuffer();
        return tick;
    }

    public ulong FlushDeferred(ReadOnlySpan<RecordingBuffer> trailing)
    {
        if (_command.IsInvalid)
        {
            throw Fatal("Cannot submit without a command buffer that is recording.");
        }

        // [local] SHARPEMU_SUBMIT_THREAD: the submit thread already keeps vkQueueSubmit2 off the recording thread and
        // submits in tick order; a tick held back here would be submitted out of order by the recording thread (a
        // timeline signal below one already submitted) and would never reach WaitSubmitted. Each tick is sealed.
        if (_submitThread != null)
        {
            return Flush(new SubmitBundle(), trailing);
        }

        var bundle = new SubmitBundle();
        _prepareSubmit?.Invoke(bundle);
        if (bundle.WaitCount != 0 || bundle.SignalCount != 0)
        {
            return Flush(bundle, trailing);
        }

        foreach (var recording in trailing)
        {
            if (recording.IsInvalid || !recording.Ended)
            {
                throw Fatal("A trailing command buffer must be recorded and ended before it is submitted.");
            }
        }

        // [local] As in Submit: the hooks that record into the buffer before it ends and join its deferred uploads.
        BeforeSubmit?.Invoke();
        WaitSubmitTarget?.Invoke(CaptureSubmitTarget?.Invoke() ?? 0);
        _command.End();
        _device.MarkSealed(_command.Buffer); // [local]
        ulong tick;
        lock (_operationLock)
        {
            lock (_device.QueueGate)
            {
                tick = _timeline.ReserveTick();
                if (Diagnostics.FrameGpuProbe.Enabled) // [local] SHARPEMU_FRAME_MISS_GPU
                {
                    Vulkan.VulkanCommandProfile.NoteSealed(_command.Buffer, tick);
                    Diagnostics.FrameGpuProbe.NoteSealed(tick);
                }
            }
        }

        _deferredBuffers.Add(_command.Buffer);
        foreach (var recording in trailing)
        {
            _deferredBuffers.Add(recording.Buffer);
        }

        _deferredTicks.Add(tick);
        if (_deferredTicks.Count == 1) Volatile.Write(ref _firstDeferredTick, tick); // [local]
        NoteReserved(tick, System.Diagnostics.Stopwatch.GetTimestamp(), submitted: false);
        _command.Submitted(tick);
        foreach (var recording in trailing)
        {
            recording.Submitted(tick);
        }

        _submitted?.Invoke(tick);
        Interlocked.Increment(ref _deferredSubmissions);
        if (_deferredTicks.Count >= MaxDeferredSubmissions)
        {
            _submitsAtLimit.Increment();
            SubmitDeferred();
        }

        BeginNextCommandBuffer();
        return tick;
    }

    public void SubmitDeferred()
    {
        if (_deferredTicks.Count == 0)
        {
            return;
        }

        var submit = new SubmitBundle();
        var last = _deferredTicks[^1];
        Span<nint> buffers = stackalloc nint[_deferredBuffers.Count];
        CollectionsMarshal.AsSpan(_deferredBuffers).CopyTo(buffers);
        _device.WaitRecorded(_device.RecordedPosition); // [local] SHARPEMU_RECORD_RING: the deferred buffers ended on the ring
        bool submitted;
        string failure;
        lock (_operationLock)
        {
            lock (_device.QueueGate)
            {
                submit.AddSignal(_timeline.Handle, last);
                submitted = _device.TrySubmit(buffers, submit, out failure);
            }

            if (submitted)
            {
                NoteDeferredSubmitted(System.Diagnostics.Stopwatch.GetTimestamp());
                EnqueueDeferredCompletions();
            }
        }

        if (!submitted)
        {
            throw Fatal($"vkQueueSubmit failed for the deferred buffers: {failure}, tick={last}");
        }

        _deferredBuffers.Clear();
        _deferredTicks.Clear();
        Volatile.Write(ref _firstDeferredTick, 0); // [local]
    }

    private void EnqueueDeferredCompletions()
    {
        if (_completed is not { } completed)
        {
            return;
        }

        foreach (var tick in _deferredTicks)
        {
            _priority.Enqueue(new TickWork(() => completed(tick), tick));
        }

        Monitor.Pulse(_operationLock);
    }

    public void EnsureSubmitted(ulong tick)
    {
        if (_deferredTicks.Count != 0 && tick >= _deferredTicks[0])
        {
            _submitsEnsured.Increment();
            SubmitDeferred();
        }
    }

    public void FlushAndWait()
    {
        var tick = Submit();
        WaitSubmitted(tick); // [local]
        _timeline.Wait(tick);
        BeginNextCommandBuffer();
    }

    public void Finish()
    {
        RequireActiveScheduler();
        if (!_command.IsInvalid)
        {
            Submit();
        }

        WaitSubmitted(CurrentTick - 1); // [local]
        _timeline.Wait(CurrentTick - 1);
        BeginNextCommandBuffer();
        RunCompletedOperations();
    }

    public void FinishMemoryAccess()
    {
        RequireActiveScheduler();
        if (InsideTickCallback)
        {
            throw Fatal("Cannot finish memory access from a GPU completion callback.");
        }

        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 6, "scheduler.op_6");
        lock (_operationLock)
        {
            // Reuse a completed drain only while its next command buffer and callback queues stay unused.
            if (_lastMemoryDrainTick == CurrentTick && !_command.IsInvalid && !_command.HasPendingCommands &&
                _pending.Count == 0 && _parked.Count == 0 && _priority.Count == 0 && !_priorityActive)
            {
                return;
            }
        }

        var tick = CurrentTick;
        var nextTick = tick + (_command.IsInvalid ? 0UL : 1UL);
        Finish();
        WaitForPriorityOperations(tick);
        // A callback can record or submit more work. It must not count as part of this drain.
        _lastMemoryDrainTick = nextTick;
    }

    public void Wait(ulong tick)
    {
        if (tick > CurrentTick)
        {
            throw Fatal($"Cannot wait for a tick that has not been issued: {tick} > {CurrentTick}.");
        }

        if (tick == CurrentTick)
        {
            RequireActiveScheduler();
            var submitted = Submit();
            if (submitted != tick)
            {
                throw Fatal($"The submitted tick {submitted} does not match the requested tick {tick}.");
            }

            WaitSubmitted(tick); // [local]
            _timeline.Wait(tick);
            BeginNextCommandBuffer();
        }
        else
        {
            WaitSubmitted(tick); // [local]
            EnsureSubmitted(tick);
            _timeline.Wait(tick);
        }
    }

    public void RunCompletedOperations() => RunCompletedOperations(refresh: true);

    // [local] refresh: false runs what the last read of the timeline completed, for a caller that has just read it.
    public void RunCompletedOperations(bool refresh)
    {
        if (refresh) _timeline.RefreshCompletedTick();
        for (;;)
        {
            TickWork operation;
            SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 7, "scheduler.op_7");
            lock (_operationLock)
            {
                if (_pending.Count == 0 || !_timeline.IsTickComplete(_pending.Peek().Tick))
                {
                    return;
                }

                if (_publishingMemory && _pending.Peek().MemoryPublication) return;

                operation = _pending.Dequeue();
            }

            WaitForPriorityOperations(operation.Tick);
            if (operation.MemoryPublication)
            {
                if (InsideTickCallback)
                    throw Fatal("Cannot publish guest memory inside a completion callback.");
                _publishingMemory = true;
                try { operation.Callback(); }
                finally { _publishingMemory = false; }
            }
            else
                RunDeferredCallback(operation.Callback);
        }
    }

    // Slices recorded ahead of the stream may still reference what a completion action destroys;
    // while any is unjoined the action waits until the slices cut so far have been submitted.
    public long BeginSliceCut()
    {
        lock (_operationLock)
        {
            var generation = ++_cutGeneration;
            _unjoinedCuts.Add(generation);
            return generation;
        }
    }

    public void NoteSliceJoined(long generation)
    {
        lock (_operationLock)
        {
            _unjoinedCuts.Remove(generation);
            var joined = _unjoinedCuts.Count == 0 ? _cutGeneration : _unjoinedCuts.Min - 1;
            while (_parked.Count != 0 && _parked.Peek().Generation <= joined)
            {
                Unpark(_parked.Dequeue());
            }
        }
    }

    private void Unpark(ParkedWork parked)
    {
        var tick = parked.MemoryPublication ? parked.Tick : CurrentTick;
        _pendingTailTick = _pending.Count == 0 ? tick : Math.Max(_pendingTailTick, tick);
        _pending.Enqueue(new TickWork(parked.Callback, tick, parked.MemoryPublication));
    }

    public void QueueCompletionAction(Action operation)
    {
        RequireActiveScheduler();
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 8, "scheduler.op_8");
        lock (_operationLock)
        {
            if (_state == OperationState.Open)
            {
                if (_unjoinedCuts.Count != 0)
                {
                    _parked.Enqueue(new ParkedWork(operation, _cutGeneration));
                }
                else
                {
                    _pending.Enqueue(new TickWork(operation, CurrentTick));
                    _pendingTailTick = CurrentTick;
                }

                _enqueueSequence++;
                return;
            }

            if (_callbackOwner != this)
            {
                while (_state != OperationState.Closed)
                {
                    Monitor.Wait(_operationLock);
                }
            }
        }

        operation();
    }

    // Publication runs on the queue owner, outside callback scope, so memory ownership can drain.
    // Dependent guest signals must not become visible before publication succeeds.
    internal void QueueMemoryPublication(Action operation) => QueueMemoryPublication(operation, CurrentTick);

    internal ulong QueueMemoryPublication(Action operation, ulong dependencyTick) =>
        QueueMemoryPublication(operation, dependencyTick, out _);

    // True while the publication with this sequence is still queued, nothing was queued after it,
    // and a new publication with this dependency would take the same tick, so it may join it.
    internal bool IsLastQueuedPublication(ulong sequence, ulong dependencyTick, ulong tick)
    {
        lock (_operationLock)
            return _enqueueSequence == sequence && _parked.Count == 0 && _pending.Count != 0 && Math.Max(dependencyTick, _pendingTailTick) == tick;
    }

    internal ulong QueueMemoryPublication(Action operation, ulong dependencyTick, out ulong sequence)
    {
        RequireActiveScheduler();
        if (InsideTickCallback)
            throw Fatal("Cannot queue a memory publication from a completion callback.");
        lock (_operationLock)
        {
            if (_state != OperationState.Open)
                throw Fatal("Cannot queue a memory publication after shutdown starts.");
            if (dependencyTick > CurrentTick)
                throw Fatal("Cannot publish memory for an unknown submission tick.");
            // Keep ready results behind earlier work without attaching them to an unused future tick.
            var tick = _pending.Count == 0 && _parked.Count == 0 ? dependencyTick : Math.Max(dependencyTick, _pendingTailTick);
            if (_parked.Count != 0)
            {
                _parked.Enqueue(new ParkedWork(operation, _cutGeneration, tick, MemoryPublication: true));
            }
            else
            {
                _pending.Enqueue(new TickWork(operation, tick, MemoryPublication: true));
                _pendingTailTick = tick;
            }

            sequence = ++_enqueueSequence;
            return tick;
        }
    }

    public void QueuePriorityCompletionAction(Action operation)
    {
        RequireActiveScheduler();
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 9, "scheduler.op_9");
        lock (_operationLock)
        {
            if (_state == OperationState.Open)
            {
                _priority.Enqueue(new TickWork(operation, CurrentTick));
                Monitor.Pulse(_operationLock);
                return;
            }

            if (_callbackOwner != this)
            {
                while (_state != OperationState.Closed)
                {
                    Monitor.Wait(_operationLock);
                }
            }
        }

        operation();
    }

    public void WaitForAllPriorityOperations()
    {
        if (_callbackOwner == this)
        {
            throw Fatal("Cannot drain priority operations from the scheduler's own deferred callback.");
        }

        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 10, "scheduler.op_10");
        lock (_operationLock)
        {
            while (_priority.Count != 0 || _priorityActive)
            {
                Monitor.Wait(_operationLock);
            }
        }
    }

    public void WaitForPriorityOperations(ulong tick)
    {
        if (_callbackOwner == this)
        {
            throw Fatal("Cannot wait for priority operations from the scheduler's own deferred callback.");
        }

        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 11, "scheduler.op_11");
        lock (_operationLock)
        {
            while ((_priorityActive && _priorityActiveTick <= tick) ||
                   (_priority.Count != 0 && _priority.Peek().Tick <= tick))
            {
                Monitor.Wait(_operationLock);
            }
        }
    }

    // [local] SHARPEMU_IDLE_FLUSH: every sealed tick has been handed to the queue and has completed.
    public bool IsQueueIdle()
    {
        var sealedTick = CurrentTick - 1;
        return SubmittedTick >= sealedTick && IsTickComplete(sealedTick);
    }

    // Uses the last completed tick read from the device; it does not query the device.
    internal bool IsTickKnownComplete(ulong tick) => _timeline.IsTickComplete(tick);

    public bool IsTickComplete(ulong tick)
    {
        if (_timeline.IsTickComplete(tick))
        {
            return true;
        }

        _timeline.RefreshCompletedTick();
        return _timeline.IsTickComplete(tick);
    }

    public void RequireActiveScheduler()
    {
        if (!Active)
        {
            throw Fatal("The scheduler must be active for this operation.");
        }
    }

    public RecordingBuffer BeginCommand()
    {
        _command.Acquire();
        return _command;
    }

    private static readonly bool SubmitProbe = Environment.GetEnvironmentVariable("SHARPEMU_SUBMIT_PROBE") == "1"; // [local]
    private static readonly Dictionary<string, int> _submitSites = new();
    private static long _submitReport = System.Diagnostics.Stopwatch.GetTimestamp();
    private static void RecordSubmit()
    {
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
        var site = string.Join(" < ", frames.Take(4).Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?"));
        lock (_submitSites)
        {
            _submitSites[site] = _submitSites.GetValueOrDefault(site) + 1;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_submitReport).TotalSeconds >= 10)
            {
                foreach (var (key, value) in _submitSites.OrderByDescending(pair => pair.Value).Take(10))
                    Console.Error.WriteLine($"[SUBMIT_PROBE] count={value} site={key}");
                _submitSites.Clear();
                _submitReport = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
    }

    // [local] Runs on the recording thread before every command buffer ends (it may record into it).
    public Action? BeforeSubmit { get; set; }

    // [local] The queue part of the submit: CaptureSubmitTarget runs on the recording thread once the buffer's work is
    // decided, WaitSubmitTarget joins that target right before vkQueueSubmit2 (the deferred uploads).
    public Func<long>? CaptureSubmitTarget { get; set; }

    public Action<long>? WaitSubmitTarget { get; set; }

    // [local] SHARPEMU_UPLOAD_GPU_WAIT: on the submit thread, instead of WaitSubmitTarget, the semaphore wait that
    // joins the target on the GPU ((0, 0): already joined).
    public Func<long, (ulong Semaphore, ulong Value)>? GpuWaitSubmitTarget { get; set; }

    // [local] SHARPEMU_SUBMIT_BATCH: whether a target is already joined (never blocks). Without it a batch holds only
    // buffers whose targets need no join.
    public Func<long, bool>? SubmitTargetDone { get; set; }

    // [local] SHARPEMU_SUBMIT_BATCH=<n> (off by default; 0 or 1 off): once the submit thread has joined the oldest
    // sealed buffer (ring replay and deferred uploads), it also takes the following sealed buffers whose replay and
    // uploads are already done, up to n in all, stopping at the first that is not, and submits them with one
    // vkQueueSubmit2 (one VkSubmitInfo2 each, own waits and signals). Same buffers, same order, same semaphores: only
    // the number of queue calls changes. Off with SHARPEMU_UPLOAD_GPU_WAIT (its waits are added per buffer). Tests set
    // SubmitBatchLimit on their own scheduler.
    // Hot (SHARPEMU_HOTFLAGS_FILE): a scheduler without its own limit follows the default; the submit thread reads it
    // once per batch.
    internal static volatile int SubmitBatchDefault = // [local]
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SUBMIT_BATCH"), out var submitBatch) ? Math.Clamp(submitBatch, 0, 32) : 0;

    private int? _submitBatchLimit; // [local]

    internal int SubmitBatchLimit
    {
        get => _submitBatchLimit ?? SubmitBatchDefault;
        set => _submitBatchLimit = value;
    }

    private readonly List<SealedSubmit> _batch = new();
    private nint[][] _batchBuffers = [];
    private SubmitBundle[] _batchBundles = [];

    public RecordingBuffer CreateRecordingBuffer(IRenderingState rendering) => new(this, _device.CreateBufferSource(), rendering);

    public void BeginRecording(RecordingBuffer buffer)
    {
        buffer.Bind(_context ?? throw Fatal("The scheduler requires a submission context."));
        buffer.Acquire();
    }

    public void EndRecording(RecordingBuffer buffer) => buffer.End();

    public void EndDiscardedRecording(RecordingBuffer buffer) => buffer.EndDiscarded();

    public void NoteMappedMemoryWrite()
    {
        if (Active && !_command.IsInvalid)
        {
            _command.NoteMappedMemoryWrite();
        }
    }

    public void DiscardRecording(RecordingBuffer buffer) => buffer.Discard();

    // Add the timeline signal to a copy. Keep the caller's submission bundle unchanged. Trailing
    // buffers were ended by their recording threads and execute after the current buffer.
    public ulong Submit(SubmitBundle? bundle = null, ReadOnlySpan<RecordingBuffer> trailing = default)
    {
        if (SubmitProbe) RecordSubmit(); // [local]
        ThrowIfSubmitFailed(); // [local]
        var submit = bundle == null ? new SubmitBundle() : new SubmitBundle(bundle);
        if (_command.IsInvalid)
        {
            throw Fatal("Cannot submit without a command buffer that is recording.");
        }

        _prepareSubmit?.Invoke(submit);
        BeforeSubmit?.Invoke();
        var target = CaptureSubmitTarget?.Invoke() ?? 0; // [local]
        if (_submitThread == null)
        {
            WaitSubmitTarget?.Invoke(target);
        }

        if (submit.WaitCount > SubmitBundle.MaxSemaphores || submit.SignalCount >= SubmitBundle.MaxSemaphores)
        {
            throw Fatal($"The submission exceeds its capacity: waits={submit.WaitCount} signals={submit.SignalCount}.");
        }

        var deferred = _deferredBuffers.Count;
        Span<nint> buffers = stackalloc nint[deferred + 1 + trailing.Length];
        CollectionsMarshal.AsSpan(_deferredBuffers).CopyTo(buffers);
        for (var index = 0; index < trailing.Length; index++)
        {
            if (trailing[index].IsInvalid || !trailing[index].Ended)
            {
                throw Fatal("A trailing command buffer must be recorded and ended before it is submitted.");
            }

            buffers[deferred + index + 1] = trailing[index].Buffer;
        }

        _command.End();
        var buffer = _command.Buffer;
        buffers[deferred] = buffer;
        var recorded = _device.RecordedPosition; // [local]
        _device.MarkSealed(buffer); // [local]
        if (_submitThread != null)
        {
            return SealForSubmitThread(buffer, buffers.ToArray(), submit, target, recorded, trailing);
        }

        _device.WaitRecorded(recorded); // [local] covers the deferred buffers too: they ended earlier on the ring

        bool submitted;
        string failure;
        ulong tick;
        // Keep completion notifications before callbacks for the next recording tick.
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 12, "scheduler.op_12");
        lock (_operationLock)
        {
            lock (_device.QueueGate)
            {
                tick = _timeline.ReserveTick();
                if (Diagnostics.FrameGpuProbe.Enabled) // [local] SHARPEMU_FRAME_MISS_GPU: the inline path seals at submit
                {
                    Vulkan.VulkanCommandProfile.NoteSealed(buffer, tick);
                    Diagnostics.FrameGpuProbe.NoteSealed(tick);
                }

                submit.AddSignal(_timeline.Handle, tick);
                submitted = _device.TrySubmit(buffers, submit, out failure);
            }

            if (submitted)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                NoteReserved(tick, now, submitted: true);
                if (deferred != 0)
                {
                    _submitsFolded.Increment();
                    NoteDeferredSubmitted(now);
                }

                EnqueueDeferredCompletions();
            }

            if (submitted && Diagnostics.FlipLatencyProbe.Enabled) Diagnostics.FlipLatencyProbe.NoteSubmitted(tick); // [local]
            if (submitted && Diagnostics.LabelLatencyProbe.Enabled) Diagnostics.LabelLatencyProbe.NoteSubmitted(tick); // [local]

            if (submitted && _completed is { } completed)
            {
                _priority.Enqueue(new TickWork(() => completed(tick), tick));
                Monitor.Pulse(_operationLock);
            }
        }

        if (submitted && deferred != 0)
        {
            Interlocked.Increment(ref _batchedSubmissions);
            _deferredBuffers.Clear();
            _deferredTicks.Clear();
            Volatile.Write(ref _firstDeferredTick, 0); // [local]
        }

        if (MeshDrawTrace.Enabled) MeshDrawTrace.Submitted((ulong)buffers[deferred], tick, submitted, failure);
        if (!submitted)
        {
            Console.Error.WriteLine($"[GPU][ERROR] submission history known_completed_tick={_timeline.CompletedTick}");
            foreach (var entry in FormatRecentSubmissions())
                Console.Error.WriteLine($"[GPU][ERROR] {entry}");
            throw Fatal(
                $"vkQueueSubmit failed: {failure}, tick={tick} debug_op={_command.DebugOp} debug_submit={_command.DebugSubmitId} " +
                $"args={_command.DebugArg0},{_command.DebugArg1},{_command.DebugArg2},{_command.DebugArg3},0x{_command.DebugArg4:X16}");
        }

        _submissionHistory[_nextSubmissionHistoryIndex] = new SubmissionTraceEntry(tick, _command.DebugOp, _command.DebugSubmitId,
            _command.DebugArg0, _command.DebugArg1, _command.DebugArg2, _command.DebugArg3, _command.DebugArg4, submit.WaitCount);
        _nextSubmissionHistoryIndex = (_nextSubmissionHistoryIndex + 1) % _submissionHistory.Length;
        _submissionHistoryCount = Math.Min(_submissionHistoryCount + 1, _submissionHistory.Length);
        _command.Submitted(tick);
        foreach (var recording in trailing)
        {
            recording.Submitted(tick);
        }

        _submitted?.Invoke(tick);
        return tick;
    }

    // [local] Reserve the tick and queue the sealed buffer for the submit thread. The tick is reserved and queued under
    // one lock, so the thread submits in tick order and the sealed tick is the submitted tick. The trailing buffers
    // (render workers) were recorded directly by their threads and go in the same submission.
    private ulong SealForSubmitThread(nint buffer, nint[] buffers, SubmitBundle submit, long target, long recorded,
        ReadOnlySpan<RecordingBuffer> trailing)
    {
        SealedSubmit item;
        bool inline;
        var deferred = _deferredBuffers.Count;
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 12, "scheduler.op_12");
        lock (_operationLock)
        {
            var tick = _timeline.ReserveTick();
            if (VideoOut.LabelTrace.Enabled) VideoOut.LabelTrace.Note(VideoOut.LabelTrace.Kind.Seal, -1, 0, tick); // [local]
            Vulkan.VulkanCommandProfile.NoteSealed(buffer, tick); // [local] SHARPEMU_PROFILE_GPU_RUN
            if (Diagnostics.FrameGpuProbe.Enabled) Diagnostics.FrameGpuProbe.NoteSealed(tick); // [local] SHARPEMU_FRAME_MISS_GPU
            if (Buffers.CpWriteDeferProbe.Enabled) Buffers.CpWriteDeferProbe.OnOtherOp(); // [local] a seal ends the scope
            submit.AddSignal(_timeline.Handle, tick);
            item = new SealedSubmit(buffer, buffers, submit, tick, target, new SubmissionTraceEntry(tick, _command.DebugOp,
                _command.DebugSubmitId, _command.DebugArg0, _command.DebugArg1, _command.DebugArg2, _command.DebugArg3,
                _command.DebugArg4, submit.WaitCount), recorded);
            NoteReserved(tick, System.Diagnostics.Stopwatch.GetTimestamp(), submitted: false);
            if (deferred != 0)
            {
                // FlushDeferred seals instead of deferring while the submit thread runs, so nothing is held here.
                _submitsFolded.Increment();
                NoteDeferredSubmitted(System.Diagnostics.Stopwatch.GetTimestamp());
                EnqueueDeferredCompletions();
            }

            lock (_submitLock)
            {
                // After shutdown stopped the thread, a late submit runs inline; the queue is empty by then.
                inline = _submitStopRequested;
                if (!inline)
                {
                    _sealed.Enqueue(item);
                    SubmitThreadStats.Sealed(_sealed.Count);
                    if (_submitWorkerParked)
                    {
                        Monitor.Pulse(_submitLock);
                    }
                }
            }

            if (_completed is { } completed)
            {
                _priority.Enqueue(new TickWork(() => completed(item.Tick), item.Tick));
                Monitor.Pulse(_operationLock);
            }
        }

        if (deferred != 0)
        {
            Interlocked.Increment(ref _batchedSubmissions);
            _deferredBuffers.Clear();
            _deferredTicks.Clear();
            Volatile.Write(ref _firstDeferredTick, 0); // [local]
        }

        if (inline)
        {
            if (!SubmitSealed(item, out var failure))
            {
                throw Fatal(FormatSubmitFailure(item, failure));
            }

            lock (_submitLock)
            {
                Volatile.Write(ref _submittedTick, item.Tick);
                Monitor.PulseAll(_submitLock);
            }
        }

        _submissionHistory[_nextSubmissionHistoryIndex] = item.Trace;
        _nextSubmissionHistoryIndex = (_nextSubmissionHistoryIndex + 1) % _submissionHistory.Length;
        _submissionHistoryCount = Math.Min(_submissionHistoryCount + 1, _submissionHistory.Length);
        _command.Submitted(item.Tick);
        foreach (var recording in trailing)
        {
            recording.Submitted(item.Tick);
        }

        _submitted?.Invoke(item.Tick);
        return item.Tick;
    }

    // [local] The queue part of a submit: join the buffer's deferred uploads, then submit under the queue gate.
    private bool SubmitSealed(in SealedSubmit item, out string failure)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitReplay); // [local] SHARPEMU_GPU_STARVE_PROBE
        _device.WaitRecorded(item.Recorded); // [local]
        var replayed = GpuStarveProbe.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local] SHARPEMU_GPU_STARVE_PROBE
        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitUploads);
        if (GpuWaitSubmitTarget is { } gpuWait && item.Bundle.WaitCount < SubmitBundle.MaxSemaphores) // [local] SHARPEMU_UPLOAD_GPU_WAIT
        {
            var (semaphore, value) = gpuWait(item.Target);
            if (semaphore != 0)
                item.Bundle.AddWait(semaphore, value);
        }
        else
        {
            WaitSubmitTarget?.Invoke(item.Target);
        }

        var joined = System.Diagnostics.Stopwatch.GetTimestamp();
        bool submitted;
        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitGate);
        lock (_device.QueueGate)
        {
            GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitCall);
            submitted = _device.TrySubmit(item.Buffers, item.Bundle, out failure);
        }

        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitOther);
        if (submitted) NoteSubmittedAt(item.Tick, System.Diagnostics.Stopwatch.GetTimestamp());
        if (MeshDrawTrace.Enabled) MeshDrawTrace.Submitted((ulong)item.Buffer, item.Tick, submitted, failure);

        if (submitted && Diagnostics.FlipLatencyProbe.Enabled) Diagnostics.FlipLatencyProbe.NoteSubmitted(item.Tick); // [local]
        if (submitted && Diagnostics.LabelLatencyProbe.Enabled) Diagnostics.LabelLatencyProbe.NoteSubmitted(item.Tick); // [local]

        SubmitThreadStats.Submitted(joined - start, System.Diagnostics.Stopwatch.GetTimestamp() - joined);
        if (replayed != 0) GpuStarveProbe.NoteSubmit(replayed - start, joined - replayed, System.Diagnostics.Stopwatch.GetTimestamp() - joined); // [local]
        if (VideoOut.LabelTrace.Enabled) VideoOut.LabelTrace.Note(VideoOut.LabelTrace.Kind.Submitted, -1, 0, item.Tick); // [local]
        return submitted;
    }

    // [local] SHARPEMU_SUBMIT_BATCH: the queue part for the oldest sealed buffer and the ready ones behind it.
    private bool SubmitSealedBatch(in SealedSubmit head, int limit, out int taken, out ulong lastTick, out string failure)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitReplay); // [local] SHARPEMU_GPU_STARVE_PROBE
        _device.WaitRecorded(head.Recorded);
        var replayed = GpuStarveProbe.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitUploads);
        WaitSubmitTarget?.Invoke(head.Target);
        var joined = System.Diagnostics.Stopwatch.GetTimestamp();

        _batch.Clear();
        _batch.Add(head);
        var targetDone = SubmitTargetDone;
        var joinsTargets = WaitSubmitTarget != null;
        lock (_submitLock)
        {
            var first = true;
            foreach (var next in _sealed)
            {
                if (first)
                {
                    first = false;
                    continue;
                }

                if (_batch.Count >= limit || !_device.IsRecorded(next.Recorded) ||
                    (joinsTargets && (targetDone == null || !targetDone(next.Target))))
                {
                    break;
                }

                _batch.Add(next);
            }
        }

        var count = _batch.Count;
        if (_batchBuffers.Length < count)
        {
            _batchBuffers = new nint[limit][];
            _batchBundles = new SubmitBundle[limit];
        }

        for (var index = 0; index < count; index++)
        {
            _batchBuffers[index] = _batch[index].Buffers;
            _batchBundles[index] = _batch[index].Bundle;
        }

        bool submitted;
        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitGate);
        lock (_device.QueueGate)
        {
            GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitCall);
            submitted = _device.TrySubmitBatch(_batchBuffers.AsSpan(0, count), _batchBundles.AsSpan(0, count), out failure);
        }

        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitOther);
        Array.Clear(_batchBundles, 0, count);
        Array.Clear(_batchBuffers, 0, count);
        var end = System.Diagnostics.Stopwatch.GetTimestamp();
        SubmitThreadStats.Batched(count);
        for (var index = 0; index < count; index++)
        {
            var tick = _batch[index].Tick;
            if (submitted) NoteSubmittedAt(tick, end);
            if (MeshDrawTrace.Enabled) MeshDrawTrace.Submitted((ulong)_batch[index].Buffer, tick, submitted, failure);
            if (submitted && Diagnostics.FlipLatencyProbe.Enabled) Diagnostics.FlipLatencyProbe.NoteSubmitted(tick);
            if (submitted && Diagnostics.LabelLatencyProbe.Enabled) Diagnostics.LabelLatencyProbe.NoteSubmitted(tick);
            SubmitThreadStats.Submitted(index == 0 ? joined - start : 0, index == 0 ? end - joined : 0);
            if (VideoOut.LabelTrace.Enabled) VideoOut.LabelTrace.Note(VideoOut.LabelTrace.Kind.Submitted, -1, 0, tick);
        }

        if (replayed != 0) GpuStarveProbe.NoteSubmit(replayed - start, joined - replayed, end - joined);
        taken = count;
        lastTick = _batch[count - 1].Tick;
        _batch.Clear();
        return submitted;
    }

    private static string FormatSubmitFailure(in SealedSubmit item, string failure)
    {
        var trace = item.Trace;
        return $"vkQueueSubmit failed: {failure}, tick={item.Tick} debug_op={trace.Op} debug_submit={trace.SubmitId} " +
            $"args={trace.Arg0},{trace.Arg1},{trace.Arg2},{trace.Arg3},0x{trace.Arg4:X16}";
    }

    private void RunSubmitWorker()
    {
        SharpEmu.HLE.Host.HostLaneReservation.ApplyToHelperThread();
        _onSubmitThread = true;
        for (;;)
        {
            SealedSubmit item;
            lock (_submitLock)
            {
                while (_sealed.Count == 0 && !_submitStopRequested)
                {
                    _submitWorkerParked = true;
                    Monitor.Wait(_submitLock);
                    _submitWorkerParked = false;
                }

                if (_sealed.Count == 0)
                {
                    return;
                }

                item = _sealed.Peek();
            }

            string? message = null;
            var taken = 1; // [local] SHARPEMU_SUBMIT_BATCH
            var lastTick = item.Tick;
            try
            {
                var batchLimit = SubmitBatchLimit; // [local] once: it is hot
                if (batchLimit > 1 && GpuWaitSubmitTarget == null)
                {
                    if (!SubmitSealedBatch(item, batchLimit, out taken, out lastTick, out var batchFailure))
                    {
                        message = FormatSubmitFailure(item, batchFailure);
                    }
                }
                else if (!SubmitSealed(item, out var failure))
                {
                    message = FormatSubmitFailure(item, failure);
                }
            }
            catch (Exception exception)
            {
                message = $"The submit thread failed at tick={item.Tick}: {exception.Message}";
            }

            if (message != null)
            {
                Console.Error.WriteLine($"[GPU][ERROR] submission history known_completed_tick={_timeline.CompletedTick}");
                foreach (var entry in FormatRecentSubmissions())
                    Console.Error.WriteLine($"[GPU][ERROR] {entry}");
                try
                {
                    // As without the thread: the fatal hook ends the process. A hook that throws (tests) leaves the
                    // failure for the recording thread, which rethrows it at its next submit or wait.
                    Fatal(message);
                }
                catch
                {
                }
            }

            lock (_submitLock)
            {
                for (var index = 0; index < taken; index++)
                {
                    _sealed.Dequeue();
                }

                if (message == null)
                {
                    Volatile.Write(ref _submittedTick, lastTick);
                }
                else
                {
                    _submitFailure = message;
                }

                if (_submitWaiters != 0 || message != null)
                {
                    Monitor.PulseAll(_submitLock);
                }
            }

            if (message != null)
            {
                return;
            }
        }
    }

    private void ThrowIfSubmitFailed()
    {
        if (_submitThread != null && Volatile.Read(ref _submitFailure) is { } failure)
        {
            throw Fatal(failure);
        }
    }

    // [local] Block until the submit thread has handed every tick up to this one to the queue. A tick that is not
    // sealed yet waits for the newest sealed one. Needed before a binary-semaphore wait (present), a submit on another
    // queue that waits on the timeline, and vkDeviceWaitIdle.
    public void WaitSubmitted(ulong tick)
    {
        if (_submitThread == null)
        {
            return;
        }

        tick = Math.Min(tick, CurrentTick - 1);
        if (Volatile.Read(ref _submittedTick) >= tick)
        {
            return;
        }

        if (_onSubmitThread)
        {
            throw Fatal("The submit thread cannot wait for its own submissions.");
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.SubmitThreadWait))
        {
            lock (_submitLock)
            {
                _submitWaiters++;
                try
                {
                    var reports = 0;
                    while (_submittedTick < tick && _submitFailure == null)
                    {
                        if (!Monitor.Wait(_submitLock, GpuStallReport.TimeoutMs) && reports++ < GpuStallReport.MaxReportsPerWait)
                        {
                            Monitor.Exit(_submitLock);
                            try
                            {
                                GpuStallReport.Report($"submit wait over {GpuStallReport.TimeoutMs / 1000} s: wait_submitted={tick}");
                            }
                            finally
                            {
                                Monitor.Enter(_submitLock);
                            }
                        }
                    }
                }
                finally
                {
                    _submitWaiters--;
                }
            }
        }

        SubmitThreadStats.Waited(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        ThrowIfSubmitFailed();
    }

    public void WaitAllSubmitted() => WaitSubmitted(CurrentTick - 1);

    private void StopSubmitWorker()
    {
        if (_submitThread == null)
        {
            return;
        }

        lock (_submitLock)
        {
            _submitStopRequested = true;
            Monitor.PulseAll(_submitLock);
        }

        if (!_onSubmitThread)
        {
            _submitThread.Join();
        }
    }

    // [local] For GpuStallReport: where the recording, submit and priority sides stand. Locks are only tried, so a
    // report never blocks behind the thread that is stuck.
    internal string DescribeStall()
    {
        var builder = new System.Text.StringBuilder();
        _timeline.RefreshCompletedTick();
        var completed = _timeline.CompletedTick;
        var submitted = SubmittedTick;
        builder.Append($"main: recording_tick={CurrentTick} open={!_command.IsInvalid} submitted={submitted} completed={completed}");
        if (Monitor.TryEnter(_submitLock, 100))
        {
            try
            {
                builder.Append($" sealed_queued={_sealed.Count}");
                if (_sealed.Count != 0)
                {
                    builder.Append($" sealed_head={_sealed.Peek().Tick}");
                }

                builder.Append($" submit_parked={_submitWorkerParked} submit_waiters={_submitWaiters}");
            }
            finally
            {
                Monitor.Exit(_submitLock);
            }
        }
        else
        {
            builder.Append(" submit_lock=busy");
        }

        if (Monitor.TryEnter(_operationLock, 100))
        {
            try
            {
                builder.Append($" priority_queued={_priority.Count} priority_active={_priorityActive} priority_active_tick={_priorityActiveTick}");
                if (_priority.Count != 0)
                {
                    builder.Append($" priority_head={_priority.Peek().Tick}");
                }

                builder.Append($" pending_ops={_pending.Count}");
            }
            finally
            {
                Monitor.Exit(_operationLock);
            }
        }
        else
        {
            builder.Append(" operation_lock=busy");
        }

        builder.Append($" device=[{_device.DescribeStall()}]");
        if (submitted > completed)
        {
            builder.Append($" busy=[{_command.DescribeTick(completed + 1)}]");
            if (completed + 2 <= submitted)
            {
                builder.Append($" next=[{_command.DescribeTick(completed + 2)}]");
            }

            // The main queue holds work it has not finished: name the oldest unfinished submission.
            for (var index = 0; index < _submissionHistoryCount; index++)
            {
                var slot = (_nextSubmissionHistoryIndex - _submissionHistoryCount + index + _submissionHistory.Length) % _submissionHistory.Length;
                var entry = _submissionHistory[slot];
                if (entry.Tick == completed + 1)
                {
                    builder.Append($" gpu_busy_tick={entry.Tick} last_op={(RecordedOperation)entry.Op} submit={entry.SubmitId} " +
                        $"args={entry.Arg0},{entry.Arg1},{entry.Arg2},{entry.Arg3},0x{entry.Arg4:X16}{(entry.Waits != 0 ? $" waits={entry.Waits}" : "")}");
                }
            }
        }

        return builder.ToString();
    }

    // Report the last operation in each saved submission. Earlier GPU work can cause device loss.
    internal IEnumerable<string> FormatRecentSubmissions()
    {
        for (var index = 0; index < _submissionHistoryCount; index++)
        {
            var slot = (_nextSubmissionHistoryIndex - _submissionHistoryCount + index + _submissionHistory.Length) % _submissionHistory.Length;
            var entry = _submissionHistory[slot];
            yield return $"tick={entry.Tick} op={(RecordedOperation)entry.Op} submit={entry.SubmitId} " +
                $"args={entry.Arg0},{entry.Arg1},{entry.Arg2},{entry.Arg3},0x{entry.Arg4:X16}{(entry.Waits != 0 ? $" waits={entry.Waits}" : "")}";
        }
    }

    private void RunPriorityWorker()
    {
        SharpEmu.HLE.Host.HostLaneReservation.ApplyToHostWorkerThread();
        SharpEmu.HLE.Host.HostLaneReservation.ApplyToHelperThread(); // [local] SHARPEMU_HOST_HELPER_LANES: narrows the affinity to the guest lanes
        for (;;)
        {
            TickWork operation;
            SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 13, "scheduler.op_13");
            lock (_operationLock)
            {
                while (!_stopRequested && _priority.Count == 0)
                {
                    Monitor.Wait(_operationLock);
                }

                if (_stopRequested)
                {
                    return;
                }

                operation = _priority.Dequeue();
                _priorityActive = true;
                _priorityActiveTick = operation.Tick;
            }

            _timeline.Wait(operation.Tick);
            if (VideoOut.LabelTrace.Enabled) VideoOut.LabelTrace.Note(VideoOut.LabelTrace.Kind.PriorityDone, -1, 0, operation.Tick); // [local]
            if (SubmittedAt(operation.Tick) is var submittedAt and not 0)
            {
                _completions.Increment();
                _completionAfterSubmit.Add(System.Diagnostics.Stopwatch.GetTimestamp() - submittedAt);
            }

            if (!Volatile.Read(ref _stopRequested))
            {
                RunDeferredCallback(operation.Callback);
            }

            SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 14, "scheduler.op_14");
            lock (_operationLock)
            {
                _priorityActive = false;
                _priorityActiveTick = 0;
                Monitor.PulseAll(_operationLock);
            }
        }
    }

    private void RunDeferredCallback(Action operation)
    {
        var previous = _callbackOwner;
        _callbackOwner = this;
        try
        {
            operation();
        }
        finally
        {
            _callbackOwner = previous;
        }
    }

    private void BindCurrentContext() => _command.Bind(_context ?? throw Fatal("The scheduler requires a submission context."));

    private void BeginNextCommandBuffer()
    {
        if (!_command.IsInvalid)
        {
            throw Fatal("Cannot start the next command buffer while the current buffer is recording.");
        }

        BindCurrentContext();
        BeginCommand();
    }
}
