// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_FRAME_MISS_GPU=1 (off; turns on SHARPEMU_FRAME_MISS_PROBE): the GPU timeline of each presentation.
// The command profile writes a timestamp after every command of every main-queue buffer (VulkanCommandProfile), each
// buffer is tied to the tick it was sealed with, and a presentation owns the ticks after the previous presentation's
// flip tick up to its own. Its GPU window runs from the completion of the previous flip tick to the completion of its
// own: busy (merged buffer spans), idle, and the idle split by what the next tick was doing when the GPU ran dry (not
// sealed yet, with the render thread running, waiting on the GPU, idle or presenting; sealed but not submitted;
// submitted but not started). GPU times are put on the CPU clock with the lowest (GPU start - submit) of the last 512
// buffers. Per class (missed, normal): those means, the frames over 33.3 ms of GPU work, the programs by completion
// interval (the time from the previous command's completion to this one's, so overlap goes to the later command), the
// render thread's GPU waits (count, ms, ticks behind, the GPU busy and the programs it ran meanwhile) and the front's.
// [FRAME_GPU] every 30 s (with [FRAME_HIST]) and [FRAME_GPU_MISS] per missed presentation after
// SHARPEMU_FRAME_MISS_START_S. =2 times whole buffers only (no per-command timestamps, one ~Tail interval per buffer):
// busy and idle with less of the probe's own cost, no program split (and SlowModeProbe's interval lines lose theirs).
internal static class FrameGpuProbe
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("SHARPEMU_FRAME_MISS_GPU");
    internal static readonly bool Enabled = Mode is "1" or "2";
    internal static readonly bool SpansOnly = Mode == "2";

    // Pipeline id -> program name; set by the presenter, called on the recording thread.
    internal static Func<ulong, string>? ProgramName { get; set; }

    private static readonly object Gate = new();
    private static readonly double NsPerTick = 1e9 / Stopwatch.Frequency;
    private static readonly FrameGpuTracker? Tracker = Enabled ? new FrameGpuTracker(Console.Error.WriteLine) : null;
    private static readonly Dictionary<string, int> Programs = new();
    private static readonly Thread? Worker = Enabled ? StartWorker() : null;

    // Frames are finished and reported here, off the render and presentation paths.
    private static Thread StartWorker()
    {
        var thread = new Thread(() =>
        {
            var reportAt = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            for (;;)
            {
                Thread.Sleep(50);
                lock (Gate)
                {
                    Tracker!.Drain();
                    if (Stopwatch.GetTimestamp() < reportAt || Tracker.MissFrames + Tracker.NormalFrames == 0) continue;
                    reportAt = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
                    foreach (var line in Tracker.Report()) Console.Error.WriteLine(line);
                }
            }
        })
        {
            IsBackground = true,
            Name = "SharpEmu frame GPU probe",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return thread;
    }

    private static long Now() => (long)(Stopwatch.GetTimestamp() * NsPerTick);

    internal static long ToNs(long stopwatchTicks) => (long)(stopwatchTicks * NsPerTick);

    internal static void NoteSealed(ulong tick)
    {
        if (Tracker is null) return;
        var now = Now();
        lock (Gate) Tracker.Sealed(tick, now);
    }

    internal static void NoteSubmitted(ulong tick)
    {
        if (Tracker is null) return;
        var now = Now();
        lock (Gate) Tracker.Submitted(tick, now);
    }

    // On the recording thread, from VulkanCommandProfile.Collect: GPU ns of the buffer's first timestamp and the
    // completion intervals after it (pipeline id 0 with a marker kind name for emulator work).
    // Once per (kind, pipeline) of a profile; programs with one name share an index.
    internal static int ProgramOf(ulong pipeline, string kind)
    {
        string name;
        try
        {
            name = pipeline != 0 ? ProgramName?.Invoke(pipeline) ?? $"pipe{pipeline}" : "~" + kind;
        }
        catch (InvalidOperationException)
        {
            name = $"pipe{pipeline}";
        }

        lock (Gate)
        {
            if (Programs.TryGetValue(name, out var index)) return index;
            index = Tracker!.Program(name);
            Programs.Add(name, index);
            return index;
        }
    }

    internal static void NoteBuffer(ulong tick, long gpuStartNs, ReadOnlySpan<(int Program, double Ms)> intervals)
    {
        if (Tracker is null) return;
        lock (Gate) Tracker.Buffer(tick, gpuStartNs, intervals);
    }

    internal static void NoteUnprofiled()
    {
        if (Tracker is null) return;
        lock (Gate) Tracker.UnprofiledBuffers++;
    }

    // A render-thread phase scope of SHARPEMU_FRAME_MISS_PROBE ended.
    internal static void NoteRenderSpan(RenderPhaseProfile.Phase phase, long startTicks, long endTicks)
    {
        if (Tracker is null || FrameMissProbe.CurrentRole != FrameMissProbe.Role.Render) return;
        var bucket = phase switch
        {
            RenderPhaseProfile.Phase.Idle or RenderPhaseProfile.Phase.IdleBlockedCommands or
                RenderPhaseProfile.Phase.IdlePendingPresentation or RenderPhaseProfile.Phase.IdleNoQueuedWork => FrameGpuTracker.SpanIdle,
            RenderPhaseProfile.Phase.Acquire or RenderPhaseProfile.Phase.Present or RenderPhaseProfile.Phase.QueuePresent or
                RenderPhaseProfile.Phase.FrameSlotWait => FrameGpuTracker.SpanPresent,
            RenderPhaseProfile.Phase.ProgramCompile or RenderPhaseProfile.Phase.PipelineCreation => FrameGpuTracker.SpanCompile,
            RenderPhaseProfile.Phase.CommandStream => -1,
            _ => FrameGpuTracker.SpanWait,
        };
        if (bucket < 0) return;
        lock (Gate) Tracker.RenderSpan(bucket, ToNs(startTicks), ToNs(endTicks));
    }

    // A GPU wait (readback or timeline) of any thread: the tick it waited for and the completed tick when it began.
    internal static void NoteWait(ulong waitTick, ulong completedAtStart, long startTicks)
    {
        if (Tracker is null) return;
        var end = Now();
        var role = FrameMissProbe.CurrentRole switch
        {
            FrameMissProbe.Role.Render => FrameGpuTracker.WaitRender,
            FrameMissProbe.Role.Front => FrameGpuTracker.WaitFront,
            _ => FrameGpuTracker.WaitOther,
        };
        lock (Gate) Tracker.Wait(role, waitTick, completedAtStart, ToNs(startTicks), end);
    }

    // From FrameMissProbe.Presented: the flip's tick and whether it skipped a slot (-1: before the start).
    internal static void Presented(ulong flipTick, int skipped)
    {
        if (Tracker is null) return;
        lock (Gate) Tracker.Frame(flipTick, skipped);
    }
}

// [local] The arithmetic of SHARPEMU_FRAME_MISS_GPU on injected ns clocks (CPU and GPU). Not thread-safe; FrameGpuProbe
// serializes it. Ticks arrive late (a buffer is collected when it is reused), so frames wait in a queue until each of
// their ticks has its GPU data or the newest sealed tick is TickGrace past their flip tick.
internal sealed class FrameGpuTracker
{
    internal const int SpanWait = 0, SpanIdle = 1, SpanPresent = 2, SpanCompile = 3, SpanKinds = 4;
    internal const int WaitRender = 0, WaitFront = 1, WaitOther = 2;
    private const int TickRing = 1 << 13;
    internal const int TickGrace = 768;
    private const int OffsetWindow = 512;
    private const int SpanRing = 1 << 15;
    private const double Budget = 33.4;

    private sealed class TickData
    {
        public ulong Tick;
        public long Sealed, Submitted;
        public bool HasGpu;
        public long GpuStart, GpuEnd; // GPU ns
        public readonly List<(int Program, double Ms)> Intervals = new();
    }

    private readonly record struct Pending(ulong First, ulong Last, int Skipped);
    private readonly record struct WaitRecord(int Role, ulong Tick, ulong Completed, long Start, long End);

    // Per class: [0] missed, [1] normal.
    private sealed class Totals
    {
        public long Frames, Saturated, IncompleteFrames, Ticks, MissingTicks;
        public double Window, Busy, Idle, IdleSealLate, IdleSubmitLate, IdleStartLate;
        public readonly double[] IdleSealRender = new double[SpanKinds + 1]; // + running
        public double FlipQueue, FlipSubmit;
        public long RenderWaits, FrontWaits;
        public double RenderWaitMs, RenderWaitBehind, RenderWaitGpuBusy, FrontWaitMs, RenderWaitPrevFrame;
        public readonly Dictionary<int, double> Program = new();
        public readonly Dictionary<int, double> DuringWait = new();
    }

    private readonly Action<string> _sink;
    private readonly TickData[] _ticks = Enumerable.Range(0, TickRing).Select(_ => new TickData()).ToArray();
    private readonly long[] _offsets = new long[OffsetWindow];
    private int _offsetCount;
    private readonly Queue<Pending> _frames = new();
    private readonly List<WaitRecord> _waits = new();
    private readonly (int Kind, long Start, long End)[] _spans = new (int, long, long)[SpanRing];
    private long _spanCount;
    private readonly List<string> _programs = new();
    private readonly Totals[] _totals = [new(), new()];
    private ulong _lastFlipTick;
    private ulong _newestSealed;
    internal long UnprofiledBuffers;
    internal long CalibrationViolations;

    internal FrameGpuTracker(Action<string> sink) => _sink = sink;

    internal long MissFrames => _totals[0].Frames;
    internal long NormalFrames => _totals[1].Frames;
    internal double MeanBusy(bool missed) => _totals[missed ? 0 : 1].Busy / Math.Max(1, _totals[missed ? 0 : 1].Frames);
    internal double MeanIdle(bool missed) => _totals[missed ? 0 : 1].Idle / Math.Max(1, _totals[missed ? 0 : 1].Frames);
    internal double MeanIdleSealLate(bool missed) => _totals[missed ? 0 : 1].IdleSealLate / Math.Max(1, _totals[missed ? 0 : 1].Frames);
    internal double MeanIdleSealRender(bool missed, int kind) => _totals[missed ? 0 : 1].IdleSealRender[kind] / Math.Max(1, _totals[missed ? 0 : 1].Frames);
    internal double MeanIdleSubmitLate(bool missed) => _totals[missed ? 0 : 1].IdleSubmitLate / Math.Max(1, _totals[missed ? 0 : 1].Frames);
    internal double MeanIdleStartLate(bool missed) => _totals[missed ? 0 : 1].IdleStartLate / Math.Max(1, _totals[missed ? 0 : 1].Frames);
    internal double MeanRenderWait(bool missed) => _totals[missed ? 0 : 1].RenderWaitMs / Math.Max(1, _totals[missed ? 0 : 1].Frames);
    internal double MeanRenderWaitGpuBusy(bool missed) => _totals[missed ? 0 : 1].RenderWaitGpuBusy / Math.Max(1, _totals[missed ? 0 : 1].Frames);
    internal long Saturated(bool missed) => _totals[missed ? 0 : 1].Saturated;
    internal double ProgramMs(bool missed, string name, bool duringWait = false)
    {
        var index = _programs.IndexOf(name);
        var totals = _totals[missed ? 0 : 1];
        return index >= 0 && (duringWait ? totals.DuringWait : totals.Program).TryGetValue(index, out var ms) ? ms / Math.Max(1, totals.Frames) : 0;
    }

    internal int Program(string name)
    {
        _programs.Add(name);
        return _programs.Count - 1;
    }

    private TickData Slot(ulong tick, bool create)
    {
        var data = _ticks[(int)(tick & (TickRing - 1))];
        if (data.Tick == tick) return data;
        if (!create) return null!;
        data.Tick = tick;
        data.Sealed = data.Submitted = 0;
        data.HasGpu = false;
        data.Intervals.Clear();
        return data;
    }

    private TickData? Find(ulong tick)
    {
        var data = _ticks[(int)(tick & (TickRing - 1))];
        return data.Tick == tick ? data : null;
    }

    internal void Sealed(ulong tick, long ns)
    {
        Slot(tick, true).Sealed = ns;
        if (tick > _newestSealed) _newestSealed = tick;
    }

    internal void Submitted(ulong tick, long ns)
    {
        var data = Slot(tick, true);
        data.Submitted = ns;
    }

    internal void Buffer(ulong tick, long gpuStartNs, ReadOnlySpan<(int Program, double Ms)> intervals)
    {
        var data = Find(tick);
        if (data is null || data.HasGpu) return;
        data.HasGpu = true;
        data.GpuStart = gpuStartNs;
        var total = 0.0;
        foreach (var interval in intervals)
        {
            data.Intervals.Add(interval);
            total += interval.Ms;
        }

        data.GpuEnd = gpuStartNs + (long)(total * 1e6);
        if (data.Submitted != 0)
        {
            _offsets[_offsetCount++ % OffsetWindow] = gpuStartNs - data.Submitted;
        }
    }

    internal void RenderSpan(int kind, long start, long end)
    {
        if (end <= start) return;
        _spans[_spanCount++ % SpanRing] = (kind, start, end);
    }

    internal void Wait(int role, ulong tick, ulong completed, long start, long end)
    {
        if (role == WaitOther || end <= start) return;
        if (_waits.Count >= 1 << 16) _waits.RemoveRange(0, 1 << 15);
        _waits.Add(new WaitRecord(role, tick, completed, start, end));
    }

    internal void Frame(ulong flipTick, int skipped)
    {
        if (flipTick == 0) return;
        if (_lastFlipTick != 0 && flipTick > _lastFlipTick && skipped >= 0)
            _frames.Enqueue(new Pending(_lastFlipTick, flipTick, skipped));
        if (flipTick > _lastFlipTick) _lastFlipTick = flipTick;
    }

    // GPU ns minus CPU ns: the lowest start-after-submit of the window (a buffer never starts before its submit).
    internal long Offset()
    {
        var count = Math.Min(_offsetCount, OffsetWindow);
        if (count == 0) return 0;
        var offset = long.MaxValue;
        for (var index = 0; index < count; index++) offset = Math.Min(offset, _offsets[index]);
        return offset;
    }

    private bool Ready(in Pending frame)
    {
        if (_newestSealed >= frame.Last + TickGrace) return true;
        for (var tick = frame.First; tick <= frame.Last; tick++)
            if (Find(tick) is not { HasGpu: true }) return false;
        return true;
    }

    internal void Drain()
    {
        while (_frames.Count != 0 && Ready(_frames.Peek()))
        {
            var frame = _frames.Dequeue();
            if (frame.Last - frame.First > TickRing / 2) continue;
            Finish(frame);
        }
    }

    private static double Ms(long ns) => ns / 1e6;

    private static double Overlap(long a0, long a1, long b0, long b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    private void Finish(in Pending frame)
    {
        var offset = Offset();
        var missed = frame.Skipped > 0;
        var totals = _totals[missed ? 0 : 1];
        var first = Find(frame.First);
        var last = Find(frame.Last);
        var ticks = new List<TickData>();
        long missing = 0;
        for (var tick = frame.First + 1; tick <= frame.Last; tick++)
        {
            if (Find(tick) is { HasGpu: true } data) ticks.Add(data);
            else missing++;
        }

        if (first is not { HasGpu: true } || last is not { HasGpu: true } || ticks.Count == 0)
        {
            totals.IncompleteFrames++;
            PruneWaits(last?.Sealed ?? 0);
            return;
        }

        totals.Frames++;
        totals.Ticks += ticks.Count;
        totals.MissingTicks += missing;
        // On the CPU clock.
        var windowStart = first.GpuEnd - offset;
        var windowEnd = last.GpuEnd - offset;
        var window = Math.Max(0, windowEnd - windowStart);
        long busy = 0, idle = 0, sealLate = 0, submitLate = 0, startLate = 0;
        var sealRender = new double[SpanKinds + 1];
        var cursor = windowStart;
        foreach (var data in ticks)
        {
            var start = Math.Max(data.GpuStart - offset, windowStart);
            var end = Math.Min(data.GpuEnd - offset, windowEnd);
            if (start > cursor)
            {
                // The GPU ran dry until this tick started: before its seal, between seal and submit, after submit.
                var gap = start - cursor;
                idle += gap;
                var sealedAt = data.Sealed != 0 ? Math.Clamp(data.Sealed, cursor, start) : cursor;
                var submittedAt = data.Submitted != 0 ? Math.Clamp(data.Submitted, sealedAt, start) : sealedAt;
                sealLate += sealedAt - cursor;
                submitLate += submittedAt - sealedAt;
                startLate += start - submittedAt;
                if (sealedAt > cursor) SplitRender(cursor, sealedAt, sealRender);
                cursor = start;
            }

            if (end > cursor)
            {
                busy += end - cursor;
                cursor = end;
            }

            if (data.Sealed != 0 && data.GpuEnd - offset < data.Sealed) CalibrationViolations++;
        }

        totals.Window += Ms(window);
        totals.Busy += Ms(busy);
        totals.Idle += Ms(idle);
        totals.IdleSealLate += Ms(sealLate);
        totals.IdleSubmitLate += Ms(submitLate);
        totals.IdleStartLate += Ms(startLate);
        for (var kind = 0; kind <= SpanKinds; kind++) totals.IdleSealRender[kind] += sealRender[kind] / 1e6;
        if (Ms(busy) > Budget) totals.Saturated++;
        if (last.Submitted != 0)
        {
            totals.FlipQueue += Ms(last.GpuEnd - offset - last.Submitted);
            if (last.Sealed != 0) totals.FlipSubmit += Ms(last.Submitted - last.Sealed);
        }

        var programs = new Dictionary<int, double>();
        foreach (var data in ticks)
        foreach (var (program, ms) in data.Intervals)
            programs[program] = programs.GetValueOrDefault(program) + ms;
        foreach (var (program, ms) in programs) totals.Program[program] = totals.Program.GetValueOrDefault(program) + ms;

        // The waits that began while this frame's ticks were recorded (after the previous flip tick's seal).
        double renderWait = 0, renderBusy = 0, frontWait = 0;
        long renderWaits = 0;
        var during = new Dictionary<int, double>();
        var from = first.Sealed;
        var to = last.Sealed;
        for (var index = 0; index < _waits.Count;)
        {
            var wait = _waits[index];
            if (wait.Start >= to) { index++; continue; }
            _waits.RemoveAt(index);
            if (wait.Start < from) continue;
            if (wait.Role == WaitFront)
            {
                totals.FrontWaits++;
                frontWait += Ms(wait.End - wait.Start);
                continue;
            }

            renderWaits++;
            renderWait += Ms(wait.End - wait.Start);
            totals.RenderWaitBehind += wait.Tick > wait.Completed ? wait.Tick - wait.Completed : 0;
            if (wait.Tick <= frame.First) totals.RenderWaitPrevFrame += Ms(wait.End - wait.Start);
            // What the GPU executed meanwhile, interval by interval.
            for (var tick = Math.Max(wait.Completed + 1, frame.First > 64 ? frame.First - 64 : 0); tick <= Math.Min(wait.Tick, frame.Last); tick++)
            {
                if (Find(tick) is not { HasGpu: true } data) continue;
                var at = data.GpuStart - offset;
                foreach (var (program, ms) in data.Intervals)
                {
                    var next = at + (long)(ms * 1e6);
                    var overlap = Overlap(at, next, wait.Start, wait.End);
                    if (overlap > 0)
                    {
                        renderBusy += overlap / 1e6;
                        during[program] = during.GetValueOrDefault(program) + overlap / 1e6;
                    }

                    at = next;
                }
            }
        }

        totals.RenderWaits += renderWaits;
        totals.RenderWaitMs += renderWait;
        totals.RenderWaitGpuBusy += renderBusy;
        totals.FrontWaitMs += frontWait;
        foreach (var (program, ms) in during) totals.DuringWait[program] = totals.DuringWait.GetValueOrDefault(program) + ms;

        if (missed)
            _sink(string.Create(CultureInfo.InvariantCulture,
                $"[FRAME_GPU_MISS] ticks={frame.First + 1}-{frame.Last} skipped={frame.Skipped} window_ms={Ms(window):F2} busy_ms={Ms(busy):F2} idle_ms={Ms(idle):F2} idle_unsealed={Ms(sealLate):F2} (run={sealRender[SpanKinds] / 1e6:F2} gpuwait={sealRender[SpanWait] / 1e6:F2} idle={sealRender[SpanIdle] / 1e6:F2} present={sealRender[SpanPresent] / 1e6:F2} compile={sealRender[SpanCompile] / 1e6:F2}) idle_unsubmitted={Ms(submitLate):F2} idle_unstarted={Ms(startLate):F2} flip_queue_ms={(last.Submitted != 0 ? Ms(last.GpuEnd - offset - last.Submitted) : 0):F2} render_waits={renderWaits}/{renderWait:F2}ms gpu_busy_in_waits={renderBusy:F2} front_waits_ms={frontWait:F2} top:{Top(programs, 1, 6)} waited:{Top(during, 1, 4)}"));
    }

    // Splits [start, end) by the render thread's phase spans; what no span covers is the render thread running.
    private void SplitRender(long start, long end, double[] into)
    {
        var covered = new List<(long Start, long End)>();
        var count = Math.Min(_spanCount, SpanRing);
        for (long index = 0; index < count; index++)
        {
            var (kind, spanStart, spanEnd) = _spans[(_spanCount - 1 - index) % SpanRing];
            if (spanEnd <= start) { if (spanEnd < start - 100_000_000L) break; continue; }
            if (spanStart >= end) continue;
            var a = Math.Max(spanStart, start);
            var b = Math.Min(spanEnd, end);
            into[kind] += b - a;
            covered.Add((a, b));
        }

        covered.Sort(static (x, y) => x.Start.CompareTo(y.Start));
        long union = 0, runStart = 0, runEnd = 0;
        var open = false;
        foreach (var (a, b) in covered)
        {
            if (open && a <= runEnd) { runEnd = Math.Max(runEnd, b); continue; }
            if (open) union += runEnd - runStart;
            runStart = a;
            runEnd = b;
            open = true;
        }

        if (open) union += runEnd - runStart;
        into[SpanKinds] += end - start - union;
    }

    private void PruneWaits(long before)
    {
        if (before == 0) return;
        _waits.RemoveAll(wait => wait.Start < before);
    }

    private string Top(Dictionary<int, double> programs, double frames, int count)
    {
        var builder = new StringBuilder();
        foreach (var (program, ms) in programs.OrderByDescending(pair => pair.Value).Take(count))
            builder.Append(CultureInfo.InvariantCulture, $" {_programs[program]}={ms / frames:F2}");
        return builder.ToString();
    }

    internal IEnumerable<string> Report()
    {
        for (var index = 0; index < 2; index++)
        {
            var totals = _totals[index];
            if (totals.Frames == 0) continue;
            double frames = totals.Frames;
            var idle = totals.IdleSealRender;
            yield return string.Create(CultureInfo.InvariantCulture,
                $"[FRAME_GPU] class={(index == 0 ? "miss" : "normal")} frames={totals.Frames} incomplete={totals.IncompleteFrames} ticks={totals.Ticks / frames:F1} missing_ticks={totals.MissingTicks / frames:F2} window_ms={totals.Window / frames:F2} busy_ms={totals.Busy / frames:F2} idle_ms={totals.Idle / frames:F2} saturated_33={totals.Saturated} idle_unsealed={totals.IdleSealLate / frames:F2} (run={idle[SpanKinds] / frames:F2} gpuwait={idle[SpanWait] / frames:F2} idle={idle[SpanIdle] / frames:F2} present={idle[SpanPresent] / frames:F2} compile={idle[SpanCompile] / frames:F2}) idle_unsubmitted={totals.IdleSubmitLate / frames:F2} idle_unstarted={totals.IdleStartLate / frames:F2} flip_seal_to_submit_ms={totals.FlipSubmit / frames:F2} flip_submit_to_done_ms={totals.FlipQueue / frames:F2} render_waits={totals.RenderWaits / frames:F1} render_wait_ms={totals.RenderWaitMs / frames:F2} render_wait_prev_frame_ms={totals.RenderWaitPrevFrame / frames:F2} wait_ticks_behind={totals.RenderWaitBehind / Math.Max(1, totals.RenderWaits):F1} gpu_busy_in_render_waits={totals.RenderWaitGpuBusy / frames:F2} front_waits={totals.FrontWaits / frames:F1} front_wait_ms={totals.FrontWaitMs / frames:F2} calib_violations={CalibrationViolations} unprofiled_buffers={UnprofiledBuffers} offset_ns={Offset()}");
            yield return string.Create(CultureInfo.InvariantCulture,
                $"[FRAME_GPU_TOP] class={(index == 0 ? "miss" : "normal")} ms_per_frame:{Top(totals.Program, frames, 25)}");
            yield return string.Create(CultureInfo.InvariantCulture,
                $"[FRAME_GPU_WAITED] class={(index == 0 ? "miss" : "normal")} ms_per_frame_during_render_waits:{Top(totals.DuringWait, frames, 15)}");
        }
    }
}
