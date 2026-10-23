// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_FRONT_PREP_WORKER=1 (on by default, =0 off): the front hands each draw's program preparation to one worker
// thread instead of running it inline, and goes on interpreting. The worker runs the preparations in the order the front
// submitted them. When the render thread executes the draw it takes the result: a preparation the worker has not started
// is abandoned (the worker skips it) and the render thread prepares the draw itself, as it does today for a preparation
// that came back empty; one the worker is running is waited for; a finished one is used only if it is still valid (no
// range became GPU-written since the front submitted it), otherwise the render thread prepares again. The worker only
// reads guest memory, so nothing the guest can observe changes order. =1 runs the worker on the front's core (its other
// SMT lane), =2 on the guest lanes. Dispatch preparations stay on the front unless SHARPEMU_FRONT_PREP_WORKER_COMPUTE=1
// (with them on the worker the static scene lost 0.68 FPS in w7S*; draws only: static +0.05 to +0.9, pan move=1 +1.48 in x3*).
internal sealed class FrontPrepWorker : IDisposable
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_PREP_WORKER");
    public static readonly bool Enabled = Mode is null or "1" or "2";
    public static readonly bool OnGuestLanes = Mode == "2";
    public static readonly bool Compute = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_PREP_WORKER_COMPUTE") == "1";

    internal abstract class Job
    {
        internal const int Pending = 0;
        internal const int Running = 1;
        internal const int Done = 2;
        internal const int Abandoned = 3;

        internal int State;
        internal bool Failed;
        internal bool Waiting;
        internal long Version;
        internal long Mark = -1; // [local] from endMark: what else the result depends on, checked when taken (-1 nothing)
        internal long SubmittedTicks;

        internal abstract void Run();
    }

    internal sealed class Job<T> : Job
    {
        private readonly Func<T> _work;
        internal T? Result;

        internal Job(Func<T> work) => _work = work;

        internal override void Run() => Result = _work();
    }

    private readonly object _gate = new();
    private readonly Queue<Job> _queue = new();
    private readonly Thread _thread;
    private readonly Action? _threadStart;
    private readonly Func<long>? _beginMark;
    private readonly Func<long, long>? _endMark;
    private readonly long _reportTicks;
    private bool _idle;
    private bool _stop;
    private long _lastReport;

    internal long Submitted;
    internal long Taken;
    internal long Empty;
    internal long Late;
    internal long Waited;
    internal long WaitTicks;
    internal long Stale;
    internal long Failures;
    internal long Skipped;
    internal long RunTicks;
    internal long LeadTicks;

    public FrontPrepWorker(Action? threadStart = null, string name = "GPU front prep", double reportSeconds = 20,
        Func<long>? beginMark = null, Func<long, long>? endMark = null)
    {
        _threadStart = threadStart;
        _beginMark = beginMark;
        _endMark = endMark;
        _reportTicks = (long)(reportSeconds * Stopwatch.Frequency);
        _lastReport = Stopwatch.GetTimestamp();
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public Job<T> Submit<T>(Func<T> work, long version)
    {
        var job = new Job<T>(work) { Version = version, SubmittedTicks = Stopwatch.GetTimestamp() };
        Interlocked.Increment(ref Submitted);
        lock (_gate)
        {
            _queue.Enqueue(job);
            if (_idle)
                Monitor.Pulse(_gate);
        }

        return job;
    }

    // On the render thread, when the draw executes. False: prepare the draw there.
    public bool TryTake<T>(Job<T> job, Func<long, bool> stillValid, out T? result, Func<long, bool>? markStillValid = null)
    {
        result = default;
        var state = Interlocked.CompareExchange(ref job.State, Job.Abandoned, Job.Pending);
        if (state == Job.Pending)
        {
            Interlocked.Increment(ref Late);
            return false;
        }

        if (state == Job.Running)
        {
            var started = Stopwatch.GetTimestamp();
            var spin = new SpinWait();
            for (var index = 0; index < 64 && Volatile.Read(ref job.State) != Job.Done; index++)
                spin.SpinOnce(-1);
            if (Volatile.Read(ref job.State) != Job.Done)
            {
                lock (job)
                {
                    job.Waiting = true;
                    while (Volatile.Read(ref job.State) != Job.Done)
                        Monitor.Wait(job);
                }
            }

            Interlocked.Increment(ref Waited);
            Interlocked.Add(ref WaitTicks, Stopwatch.GetTimestamp() - started);
        }

        if (job.Failed)
            return false;
        if (!stillValid(job.Version) || (job.Mark >= 0 && markStillValid is not null && !markStillValid(job.Mark)))
        {
            Interlocked.Increment(ref Stale);
            return false;
        }

        Interlocked.Increment(ref Taken);
        result = job.Result;
        return true;
    }

    private void Loop()
    {
        _threadStart?.Invoke();
        while (true)
        {
            Job job;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_stop)
                {
                    _idle = true;
                    Monitor.Wait(_gate);
                    _idle = false;
                }

                if (_queue.Count == 0)
                    return;
                job = _queue.Dequeue();
            }

            if (Interlocked.CompareExchange(ref job.State, Job.Running, Job.Pending) != Job.Pending)
            {
                Interlocked.Increment(ref Skipped);
                continue;
            }

            var started = Stopwatch.GetTimestamp();
            Interlocked.Add(ref LeadTicks, started - job.SubmittedTicks);
            try
            {
                var mark = _beginMark?.Invoke() ?? -1;
                job.Run();
                job.Mark = _endMark?.Invoke(mark) ?? -1;
            }
            catch (Exception)
            {
                // The render thread prepares the draw itself and meets the same failure there.
                job.Failed = true;
                Interlocked.Increment(ref Failures);
            }

            Interlocked.Add(ref RunTicks, Stopwatch.GetTimestamp() - started);
            lock (job)
            {
                Volatile.Write(ref job.State, Job.Done);
                if (job.Waiting)
                    Monitor.PulseAll(job);
            }
        }
    }

    public int Depth
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    // On the render thread: one line per report interval.
    public void MaybeReport(TextWriter writer)
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastReport < _reportTicks)
            return;
        var seconds = (double)(now - _lastReport) / Stopwatch.Frequency;
        _lastReport = now;
        var submitted = Interlocked.Exchange(ref Submitted, 0);
        var taken = Interlocked.Exchange(ref Taken, 0);
        var empty = Interlocked.Exchange(ref Empty, 0);
        var late = Interlocked.Exchange(ref Late, 0);
        var waited = Interlocked.Exchange(ref Waited, 0);
        var waitTicks = Interlocked.Exchange(ref WaitTicks, 0);
        var stale = Interlocked.Exchange(ref Stale, 0);
        var failures = Interlocked.Exchange(ref Failures, 0);
        var skipped = Interlocked.Exchange(ref Skipped, 0);
        var runTicks = Interlocked.Exchange(ref RunTicks, 0);
        var leadTicks = Interlocked.Exchange(ref LeadTicks, 0);
        var ran = Math.Max(1, submitted - skipped);
        writer.WriteLine(FormattableString.Invariant(
            $"[FRONT_PREP_WORKER] window_s={seconds:F1} submitted={submitted} taken={taken} empty={empty} late={late} waited={waited} wait_ms_per_s={waitTicks * 1000.0 / Stopwatch.Frequency / seconds:F2} ") +
            FormattableString.Invariant(
            $"stale={stale} failed={failures} skipped={skipped} run_ms_per_s={runTicks * 1000.0 / Stopwatch.Frequency / seconds:F1} queue_lead_us={leadTicks * 1e6 / Stopwatch.Frequency / ran:F0} depth={Depth}"));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop = true;
            Monitor.PulseAll(_gate);
        }

        _thread.Join();
    }
}
