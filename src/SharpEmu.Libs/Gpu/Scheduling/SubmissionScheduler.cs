// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
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

    private readonly record struct TickWork(Action Callback, ulong Tick);
    private readonly record struct SubmissionTraceEntry(ulong Tick, uint Op, ulong SubmitId,
        uint Arg0, uint Arg1, uint Arg2, uint Arg3, ulong Arg4, int Waits = 0);
    private readonly SubmissionTraceEntry[] _submissionHistory = new SubmissionTraceEntry[32];
    private int _nextSubmissionHistoryIndex;
    private int _submissionHistoryCount;

    private readonly IGpuTickDevice _device;
    private readonly TickTimeline _timeline;
    private readonly TickedBufferRing _ring;
    private readonly RecordingBuffer _command;
    private readonly Action<SubmitBundle>? _prepareSubmit;
    private readonly Action<ulong>? _submitted;
    private readonly Action<ulong>? _completed;
    private readonly Queue<TickWork> _pending = new();
    private readonly Queue<TickWork> _priority = new();
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

    // [local] SHARPEMU_SUBMIT_THREAD (on by default for the presenter; =0 disables): the recording thread seals each
    // buffer (end, tick, signal, completion work) and a submit thread joins its deferred uploads and calls
    // vkQueueSubmit2, in tick order. Host waits on the timeline stay legal before the submit (wait-before-signal);
    // present and the readback queue wait for the submit first. A/B of 2026-10-12: -1.5 ms/frame, +0.47 FPS.
    internal static readonly bool SubmitThreadDefault = Environment.GetEnvironmentVariable("SHARPEMU_SUBMIT_THREAD") != "0";

    private readonly record struct SealedSubmit(nint Buffer, SubmitBundle Bundle, ulong Tick, long Target, SubmissionTraceEntry Trace,
        long Recorded = 0);

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
        _ring = new TickedBufferRing(device, _timeline);
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

    // [local] The highest tick handed to the queue. Without a submit thread every sealed tick is submitted.
    public ulong SubmittedTick => _submitThread == null ? CurrentTick - 1 : Volatile.Read(ref _submittedTick);

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

    public TickTimeline Timeline => _timeline;

    internal bool PriorityWorkerAlive => _priorityThread.IsAlive;

    // Report the fatal error, then return an exception to stop the caller.
    internal static Exception Fatal(string message)
    {
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

    public bool TryDeferUntilRenderingEnds(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, List<ImageMemoryBarrier2> barriers) =>
        Active && !_command.IsInvalid && _rendering.IsRendering &&
        _rendering.TryDeferUntilRenderingEnds(sourceStages, destinationStages, barriers);

    public ulong Flush() => Flush(new SubmitBundle());

    public ulong Flush(SubmitBundle bundle)
    {
        var tick = Submit(bundle);
        BeginNextCommandBuffer();
        return tick;
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
                _pending.Count == 0 && _priority.Count == 0 && !_priorityActive)
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
            _timeline.Wait(tick);
        }
    }

    public void RunCompletedOperations()
    {
        _timeline.RefreshCompletedTick();
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

                operation = _pending.Dequeue();
            }

            WaitForPriorityOperations(operation.Tick);
            RunDeferredCallback(operation.Callback);
        }
    }

    public void QueueCompletionAction(Action operation)
    {
        RequireActiveScheduler();
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 8, "scheduler.op_8");
        lock (_operationLock)
        {
            if (_state == OperationState.Open)
            {
                _pending.Enqueue(new TickWork(operation, CurrentTick));
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
        if (!_command.IsInvalid)
        {
            throw Fatal("Cannot start recording. The command buffer is already recording.");
        }

        _command.Buffer = _ring.AcquireBuffer();
        _command.Begin();
        return _command;
    }

    // Add the timeline signal to a copy. Keep the caller's submission bundle unchanged.
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

    public ulong Submit(SubmitBundle? bundle = null)
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

        _command.End();
        var buffer = _command.Buffer;
        var recorded = _device.RecordedPosition; // [local]
        _device.MarkSealed(buffer); // [local]
        if (_submitThread != null)
        {
            return SealForSubmitThread(buffer, submit, target, recorded);
        }

        _device.WaitRecorded(recorded); // [local]

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
                submit.AddSignal(_timeline.Handle, tick);
                submitted = _device.TrySubmit(buffer, submit, out failure);
            }

            if (submitted && Diagnostics.FlipLatencyProbe.Enabled) Diagnostics.FlipLatencyProbe.NoteSubmitted(tick); // [local]
            if (submitted && Diagnostics.LabelLatencyProbe.Enabled) Diagnostics.LabelLatencyProbe.NoteSubmitted(tick); // [local]

            if (submitted && _completed is { } completed)
            {
                _priority.Enqueue(new TickWork(() => completed(tick), tick));
                Monitor.Pulse(_operationLock);
            }
        }

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
        _command.Buffer = 0;
        _submitted?.Invoke(tick);
        return tick;
    }

    // [local] Reserve the tick and queue the sealed buffer for the submit thread. The tick is reserved and queued under
    // one lock, so the thread submits in tick order and the sealed tick is the submitted tick.
    private ulong SealForSubmitThread(nint buffer, SubmitBundle submit, long target, long recorded)
    {
        SealedSubmit item;
        bool inline;
        SharpEmu.HLE.LockProbe.Probe(_operationLock, 4 + 12, "scheduler.op_12");
        lock (_operationLock)
        {
            var tick = _timeline.ReserveTick();
            if (VideoOut.LabelTrace.Enabled) VideoOut.LabelTrace.Note(VideoOut.LabelTrace.Kind.Seal, -1, 0, tick); // [local]
            Vulkan.VulkanCommandProfile.NoteSealed(buffer, tick); // [local] SHARPEMU_PROFILE_GPU_RUN
            if (Buffers.CpWriteDeferProbe.Enabled) Buffers.CpWriteDeferProbe.OnOtherOp(); // [local] a seal ends the scope
            submit.AddSignal(_timeline.Handle, tick);
            item = new SealedSubmit(buffer, submit, tick, target, new SubmissionTraceEntry(tick, _command.DebugOp,
                _command.DebugSubmitId, _command.DebugArg0, _command.DebugArg1, _command.DebugArg2, _command.DebugArg3,
                _command.DebugArg4, submit.WaitCount), recorded);
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
        _command.Buffer = 0;
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
        WaitSubmitTarget?.Invoke(item.Target);
        var joined = System.Diagnostics.Stopwatch.GetTimestamp();
        bool submitted;
        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitGate);
        lock (_device.QueueGate)
        {
            GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitCall);
            submitted = _device.TrySubmit(item.Buffer, item.Bundle, out failure);
        }

        GpuStarveProbe.SetSubmitState(GpuStarveProbe.SubmitOther);

        if (submitted && Diagnostics.FlipLatencyProbe.Enabled) Diagnostics.FlipLatencyProbe.NoteSubmitted(item.Tick); // [local]
        if (submitted && Diagnostics.LabelLatencyProbe.Enabled) Diagnostics.LabelLatencyProbe.NoteSubmitted(item.Tick); // [local]

        SubmitThreadStats.Submitted(joined - start, System.Diagnostics.Stopwatch.GetTimestamp() - joined);
        if (replayed != 0) GpuStarveProbe.NoteSubmit(replayed - start, joined - replayed, System.Diagnostics.Stopwatch.GetTimestamp() - joined); // [local]
        if (VideoOut.LabelTrace.Enabled) VideoOut.LabelTrace.Note(VideoOut.LabelTrace.Kind.Submitted, -1, 0, item.Tick); // [local]
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
            try
            {
                if (!SubmitSealed(item, out var failure))
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
                _sealed.Dequeue();
                if (message == null)
                {
                    Volatile.Write(ref _submittedTick, item.Tick);
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
        SharpEmu.HLE.Host.HostLaneReservation.ApplyToHelperThread();
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
