// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.GuestMemory;

namespace SharpEmu.Libs.Gpu.Scheduling;

// Run queued commands on the GPU worker before submissions. The relay does not wait for GPU completion.
public sealed class GpuWorkerRelay : IGpuQueueRelay
{
    [ThreadStatic]
    private static GpuWorkerRelay? _boundWorker;

    private readonly object _gate = new();
    private readonly Queue<Action> _commands = new();
    // [local] SHARPEMU_RELAY_PUMP_IN_WAITS: nestable work, run in order with the commands by RunPendingCommands and
    // also by PumpNestableInWait while this worker waits for the GPU outside a relay command.
    private readonly Queue<Action> _nestable = new();
    private int _nestableCount;
    private int _commandDepth;
    private bool _pumping;
    private static long _pumpedInWaits;
    private readonly Action _wake;
    private readonly Func<Action, Action, bool>? _tryEnqueueAfterPendingWork;
    private readonly PendingCommandSignal _pending = new();
    private bool _accepting = true;

    private sealed class HandoffTiming
    {
        public long LockStarted;
        public long LockAcquired;
        public long Published;
        public long WakeStarted;
        public long WakeFinished;
        public long CallbackStarted;
        public long CompletionStarted;
    }

    public GpuWorkerRelay(Action wake, Func<Action, Action, bool>? tryEnqueueAfterPendingWork = null)
    {
        _wake = wake;
        _tryEnqueueAfterPendingWork = tryEnqueueAfterPendingWork;
    }

    public bool TryRunAfterPendingWork(Action work) => TryRunOnGpuQueue(work);

    public bool TryRunAfterAcceptedCommandStreams(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsGpuQueueThread || _tryEnqueueAfterPendingWork is null)
        {
            return TryRunOnGpuQueue(work);
        }

        using var done = new SemaphoreSlim(0);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        if (!_tryEnqueueAfterPendingWork(
                () =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception exception)
                    {
                        failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                    }
                    finally
                    {
                        done.Release();
                    }
                },
                () =>
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                        new OperationCanceledException(
                            "The command stream stopped before the ordered GPU worker action could run."));
                    done.Release();
                }))
        {
            return false;
        }

        done.Wait();
        failure?.Throw();
        return true;
    }

    public bool IsGpuQueueThread => _boundWorker == this;

    // [local] SHARPEMU_LABEL_TIMELINE: the calling thread runs some relay's GPU queue.
    internal static bool OnAnyGpuQueueThread => _boundWorker is not null;

    // [local] The signal is raised for nestable work too, so the command interpreter also yields to it.
    public bool HasPendingCommands => _pending.IsRaised;

    public PendingCommandSignal CommandSignal => _pending;

    // [local] SHARPEMU_RELAY_PUMP_IN_WAITS (on by default since 2026-10-26, =0 off): a guest download's completion, which
    // records nothing, also runs while the GPU worker waits for the GPU (timeline and readback waits, which then wait in
    // 0.2 ms slices), unless the wait is inside a relay command. The guest thread that faulted no longer waits for the
    // worker to finish its own wait: the label poll's completing relay 0.67 -> 0.23 ms (p90 2.57 -> 0.84), static 28.74
    // vs 28.36 FPS (5+5 alternating), pan 25.12 vs 23.58 (2+2).
    internal static readonly bool PumpInWaits = Environment.GetEnvironmentVariable("SHARPEMU_RELAY_PUMP_IN_WAITS") != "0";

    internal static bool CanPumpInWait => PumpInWaits && _boundWorker is { _commandDepth: 0, _pumping: false };

    internal const ulong PumpSliceNs = 200_000;

    internal static long TakePumpedInWaits() => Interlocked.Exchange(ref _pumpedInWaits, 0);

    // Runs the queued nestable work of this thread's relay; false when there was none or pumping is not allowed here.
    internal static bool PumpNestableInWait()
    {
        if (!CanPumpInWait || _boundWorker is not { } worker || Volatile.Read(ref worker._nestableCount) == 0)
        {
            return false;
        }

        worker._pumping = true;
        try
        {
            var ran = false;
            while (worker.TryTakeNestable(out var work))
            {
                work();
                ran = true;
                Interlocked.Increment(ref _pumpedInWaits);
            }

            return ran;
        }
        finally
        {
            worker._pumping = false;
        }
    }

    private bool TryTakeNestable(out Action work)
    {
        lock (_gate)
        {
            if (_nestable.Count == 0)
            {
                work = null!;
                return false;
            }

            work = _nestable.Dequeue();
            Interlocked.Decrement(ref _nestableCount);
            _pending.Lower();
            return true;
        }
    }

    public bool TryRunNestableOnGpuQueue(Action work)
    {
        if (!PumpInWaits || IsGpuQueueThread)
        {
            return TryRunOnGpuQueue(work);
        }

        using var done = new SemaphoreSlim(0);
        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            _nestable.Enqueue(() =>
            {
                work();
                done.Release();
            });
            Interlocked.Increment(ref _nestableCount);
            _pending.Raise();
        }

        _wake();
        done.Wait();
        return true;
    }

    public void BindCurrentThread() => _boundWorker = this;

    public void Post(Action work)
    {
        if (!TryPost(work))
        {
            throw SubmissionScheduler.Fatal("The GPU worker relay is closed.");
        }
    }

    public bool TryPost(Action work) => TryPost(work, null);

    private bool TryPost(Action work, HandoffTiming? timing)
    {
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        if (timing is not null)
            timing.LockStarted = GuestMemoryProfile.GetTimestamp();
        lock (_gate)
        {
            if (timing is not null)
                timing.LockAcquired = GuestMemoryProfile.GetTimestamp();
            if (!_accepting)
            {
                return false;
            }

            _commands.Enqueue(work);
            _pending.Raise();
            if (timing is not null)
                timing.Published = GuestMemoryProfile.GetTimestamp();
        }

        if (timing is not null)
            timing.WakeStarted = GuestMemoryProfile.GetTimestamp();
        _wake();
        if (timing is not null)
            timing.WakeFinished = GuestMemoryProfile.GetTimestamp();
        return true;
    }

    // True when at least one command ran.
    public bool RunPendingCommands()
    {
        if (!IsGpuQueueThread)
        {
            throw SubmissionScheduler.Fatal("Only the GPU worker can run relay commands.");
        }

        var ran = false;
        var probeStarted = SharpEmu.Libs.Diagnostics.RelayProbe.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local] SHARPEMU_RELAY_PROBE
        var drained = 0;
        while (HasPendingCommands)
        {
            Action command;
            lock (_gate)
            {
                if (_commands.Count == 0) // [local] only nestable work is pending: it runs below
                {
                    break;
                }

                command = _commands.Dequeue();
                _pending.Lower();
            }

            _commandDepth++;
            try
            {
                if (VideoOut.Render2Probe.Enabled) // [local] SHARPEMU_RENDER2_PROBE
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    command();
                    Delegate named = command;
                    if (command.Target?.GetType().GetField("work")?.GetValue(command.Target) is Delegate inner)
                        named = inner;
                    VideoOut.Render2Probe.NoteRelay(named, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                }
                else
                {
                    command();
                }
            }
            finally
            {
                _commandDepth--;
            }

            ran = true;
            drained++;
        }

        while (TryTakeNestable(out var work))
        {
            work();
            ran = true;
            drained++;
        }

        if (probeStarted != 0) SharpEmu.Libs.Diagnostics.RelayProbe.NoteDrain(drained, System.Diagnostics.Stopwatch.GetTimestamp() - probeStarted);
        return ran;
    }

    public void RunOnGpuQueue(Action work)
    {
        if (!TryRunOnGpuQueue(work))
        {
            throw SubmissionScheduler.Fatal("The GPU worker relay is closed.");
        }
    }

    public bool TryRunOnGpuQueue(Action work)
    {
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        using var done = new SemaphoreSlim(0);
        var timing = GuestMemoryProfile.GetTimestamp() != 0 ? new HandoffTiming() : null;
        if (!TryPost(() =>
            {
                if (timing is not null)
                    timing.CallbackStarted = GuestMemoryProfile.GetTimestamp();
                work();
                if (timing is not null)
                    timing.CompletionStarted = GuestMemoryProfile.GetTimestamp();
                done.Release();
            }, timing))
        {
            return false;
        }

        WaitForRelayedWork(done);
        if (timing is not null)
        {
            var callerResumed = GuestMemoryProfile.GetTimestamp();
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayQueueLockAcquisition,
                timing.LockStarted, timing.LockAcquired);
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayQueuePublication,
                timing.LockAcquired, timing.Published);
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayWakeCall,
                timing.WakeStarted, timing.WakeFinished);
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayQueueResidence,
                timing.Published, timing.CallbackStarted);
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayCallerCompletion,
                timing.CompletionStarted, callerResumed);
        }
        return true;
    }

    private static readonly long RelaySpinTicks = System.Diagnostics.Stopwatch.Frequency / 50_000;
    private static readonly ThreadCounter _spunWaits = new();
    private static readonly ThreadCounter _blockedWaits = new();

    public static string TakeRelayWaitReport() =>
        FormattableString.Invariant($"relay_wait=spun:{_spunWaits.Take()},blocked:{_blockedWaits.Take()}");

    private static void WaitForRelayedWork(SemaphoreSlim done)
    {
        var spinUntil = System.Diagnostics.Stopwatch.GetTimestamp() + RelaySpinTicks;
        while (done.CurrentCount == 0)
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() >= spinUntil)
            {
                _blockedWaits.Increment();
                done.Wait();
                return;
            }

            Thread.SpinWait(16);
        }

        _spunWaits.Increment();
        done.Wait();
    }

    // Reject new work from other threads. The worker can still run accepted commands.
    public void StopAcceptingWork()
    {
        lock (_gate)
        {
            _accepting = false;
        }
    }
}
