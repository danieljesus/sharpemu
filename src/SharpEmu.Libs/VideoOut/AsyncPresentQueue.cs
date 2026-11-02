// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_ASYNC_PRESENT=1 (off by default): the render thread hands vkQueuePresentKHR to a present thread
// instead of blocking on it (~0.7 ms a frame in the driver, ~1.4 on heavy frames). The command buffers, submissions
// and semaphores are the same; only the host thread that calls the present changes. The present thread waits for the
// submit thread to queue the presentation's tick (the binary semaphore's signal), takes the queue gate, presents, and
// then reports the flip to the guest (presented, or discarded when the swapchain is out of date), as the render thread
// did right after the call. One present is in flight at most: the render thread waits for it before its next acquire
// (the swapchain needs external synchronization between the two), before recreating the swapchain and at shutdown,
// and handles its result there (recreation, errors). =2 (verify) takes the same path but waits for the present right
// after handing it off, as the synchronous call did. Both modes count invariant violations (a hand-off while a
// present is in flight, presents out of hand-off order, an acquire during a present call, a result never handled):
// [ASYNC_PRESENT] every 30 s.
internal sealed class AsyncPresentQueue : IDisposable
{
    private static readonly int EnvironmentMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_PRESENT"), out var mode) ? mode : 0;

    private static readonly AsyncLocal<int?> ModeOverride = new();

    // 0 off, 1 asynchronous, 2 verify (the asynchronous path, waited for at once).
    internal static int Mode
    {
        get => ModeOverride.Value ?? EnvironmentMode;
        set => ModeOverride.Value = value;
    }

    internal readonly record struct Job(long Serial, ulong Tick, uint ImageIndex, ulong FlipRequestId);

    private readonly Func<Job, int> _present;
    private readonly Action<Job, int>? _completed;
    private readonly Action<string>? _sink;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private Job _job;
    private bool _pending;     // handed off, not yet presented
    private bool _inCall;      // inside the present call
    private bool _hasResult;   // presented, result not yet handled by the render thread
    private int _result;
    private Exception? _failure;
    private bool _stop;
    private long _nextSerial = 1;
    private long _lastPresentedSerial;
    private long _reportAt;

    internal long Presents;
    internal long Violations;
    internal long WaitTicks;

    internal AsyncPresentQueue(Func<Job, int> present, Action<Job, int>? completed, Action<string>? sink = null, string name = "SharpEmu present")
    {
        _present = present;
        _completed = completed;
        _sink = sink;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    internal bool Pending
    {
        get
        {
            lock (_gate) return _pending;
        }
    }

    // The serial the next hand-off gets.
    internal long NextSerial => _nextSerial;

    // On the render thread: hand a present to the present thread. The previous one must be finished and handled.
    internal Job Submit(ulong tick, uint imageIndex, ulong flipRequestId)
    {
        lock (_gate)
        {
            if (_pending || _hasResult)
            {
                Violations++;
                while (_pending) Monitor.Wait(_gate);
            }

            _job = new Job(_nextSerial++, tick, imageIndex, flipRequestId);
            _pending = true;
            _hasResult = false;
            Monitor.PulseAll(_gate);
            return _job;
        }
    }

    // On the render thread: wait for the present in flight. Returns the result of a present not handled yet (and
    // marks it handled), or false when there is none. Rethrows a failure of the present call.
    internal bool TryFinish(out Job job, out int result)
    {
        var started = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while (_pending) Monitor.Wait(_gate);
            WaitTicks += Stopwatch.GetTimestamp() - started;
            job = _job;
            result = _result;
            var had = _hasResult;
            _hasResult = false;
            if (_failure is { } failure)
            {
                _failure = null;
                throw new InvalidOperationException($"The present thread failed: {failure.Message}", failure);
            }

            return had;
        }
    }

    // On the render thread, around vkAcquireNextImageKHR: no present call may run then.
    internal void NoteAcquire()
    {
        lock (_gate)
        {
            if (_inCall || _pending) Violations++;
        }
    }

    internal void MaybeReport()
    {
        if (_sink is null) return;
        var now = Stopwatch.GetTimestamp();
        if (_reportAt == 0) _reportAt = now + 30 * Stopwatch.Frequency;
        if (now < _reportAt) return;
        _reportAt = now + 30 * Stopwatch.Frequency;
        _sink($"[ASYNC_PRESENT] mode={Mode} presents={Interlocked.Read(ref Presents)} violations={Interlocked.Read(ref Violations)} render_wait_ms={WaitTicks * 1000.0 / Stopwatch.Frequency:F1}");
    }

    private void Run()
    {
        SharpEmu.HLE.Host.HostLaneReservation.ApplyToHelperThread();
        for (;;)
        {
            Job job;
            lock (_gate)
            {
                while (!_pending && !_stop) Monitor.Wait(_gate);
                if (!_pending) return;
                job = _job;
                _inCall = true;
            }

            var result = 0;
            Exception? failure = null;
            try
            {
                result = _present(job);
                _completed?.Invoke(job, result);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            lock (_gate)
            {
                if (job.Serial != _lastPresentedSerial + 1) Violations++;
                _lastPresentedSerial = job.Serial;
                Presents++;
                _inCall = false;
                _pending = false;
                _hasResult = true;
                _result = result;
                _failure = failure;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop = true;
            Monitor.PulseAll(_gate);
        }

        if (Thread.CurrentThread != _thread) _thread.Join();
        lock (_gate)
        {
            if (_hasResult) Violations++;
        }

        _sink?.Invoke($"[ASYNC_PRESENT] mode={Mode} presents={Presents} violations={Violations} render_wait_ms={WaitTicks * 1000.0 / Stopwatch.Frequency:F1} final=1");
    }
}
