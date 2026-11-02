// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_FRAME_MISS_PROBE=1 (off): what the presentations that missed the 30 Hz grid spent their time on. It turns
// on the flip timestamps of SHARPEMU_FLIP_PROBE (and its [FLIP_PROBE]/[FLIP_PACING] lines) and adds cheap cumulative
// counters, one Interlocked add per event: the render phases that wait (GPU completion, frame slot, uploads, the submit
// thread, idle) and the compiles, timed through the dormant RenderPhaseProfile scopes without the phase profiler, per thread
// role (render, front, submit, other emulator threads, guest threads); readback waits per role with their spin; compiles by
// CompileLog kind; the front's progressed slices; the submit thread's join, submit and wait; GC pauses and collections. Each
// presentation snapshots them. A presentation that skipped a grid slot after SHARPEMU_FRAME_MISS_START_S (default 130 s of
// uptime) prints [FRAME_MISS] with the flip's stage times against the deadline and the previous flip, and the counters of
// its interval and of the two before it; every 30 s [FRAME_HIST] (since the start: presentation intervals, skipped slots,
// how late) and [FRAME_ATTR] (per counter, mean ms per interval on missed and on normal presentations, ranked).
internal static class FrameMissProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_FRAME_MISS_PROBE") == "1";

    internal enum Role : byte
    {
        Unresolved,
        Render,
        Front,
        Submit,
        Emulator,
        Guest,
        Count,
    }

    // Counters outside the phase table; the phase table follows them.
    internal enum Counter
    {
        GcPause,
        Gen0,
        Gen1,
        Gen2,
        CompileTranslate,
        CompileGraphics,
        CompileCompute,
        CompileComputeAsync,
        CompileEmulator,
        CompileOther,
        CompileCount,
        DispatchCompileWait,
        FrontWork,
        SubmitJoin,
        SubmitCall,
        SubmitWait,
        ReadbackRender,
        ReadbackFront,
        ReadbackSubmit,
        ReadbackEmulator,
        ReadbackGuest,
        PresentLock,
        PresentCall,
        Count,
    }

    private static readonly RenderPhaseProfile.Phase[] TrackedPhases =
    [
        RenderPhaseProfile.Phase.Idle,
        RenderPhaseProfile.Phase.IdleBlockedCommands,
        RenderPhaseProfile.Phase.IdlePendingPresentation,
        RenderPhaseProfile.Phase.IdleNoQueuedWork,
        RenderPhaseProfile.Phase.FrameSlotWait,
        RenderPhaseProfile.Phase.CommandStream,
        RenderPhaseProfile.Phase.CommandGpuWait,
        RenderPhaseProfile.Phase.GpuCompletionWait,
        RenderPhaseProfile.Phase.DeferredUploadWait,
        RenderPhaseProfile.Phase.SubmitThreadWait,
        RenderPhaseProfile.Phase.SubmissionCapacity,
        RenderPhaseProfile.Phase.ImageReadback,
        RenderPhaseProfile.Phase.Acquire,
        RenderPhaseProfile.Phase.Present,
        RenderPhaseProfile.Phase.QueuePresent,
        RenderPhaseProfile.Phase.ProgramCompile,
        RenderPhaseProfile.Phase.PipelineCreation,
    ];

    private static readonly int[] PhaseSlot = BuildPhaseSlots();
    private static readonly int RoleCount = (int)Role.Count - 1;
    private static readonly int PhaseBase = (int)Counter.Count;
    internal static readonly int CounterCount = PhaseBase + TrackedPhases.Length * RoleCount;
    private static readonly long[] Cumulative = new long[CounterCount];
    private static readonly long[] Snapshot = new long[CounterCount];
    private static readonly FrameMissTracker? Tracker = Enabled ? CreateTracker() : null;

    [ThreadStatic] private static Role _role;

    // The front's guest submission being interpreted: when the guest queued it (its CommandSubmission.CreatedAt).
    internal static long FrontSubmissionCreatedAt;

    private static int[] BuildPhaseSlots()
    {
        var slots = new int[(int)RenderPhaseProfile.Phase.Count];
        Array.Fill(slots, -1);
        for (var index = 0; index < TrackedPhases.Length; index++) slots[(int)TrackedPhases[index]] = index;
        return slots;
    }

    private static FrameMissTracker CreateTracker()
    {
        var names = new string[CounterCount];
        var counts = new bool[CounterCount];
        for (var index = 0; index < (int)Counter.Count; index++) names[index] = ((Counter)index).ToString();
        counts[(int)Counter.Gen0] = counts[(int)Counter.Gen1] = counts[(int)Counter.Gen2] = counts[(int)Counter.CompileCount] = true;
        for (var role = 1; role <= RoleCount; role++)
        for (var phase = 0; phase < TrackedPhases.Length; phase++)
            names[PhaseBase + (role - 1) * TrackedPhases.Length + phase] = $"{(Role)role}.{TrackedPhases[phase]}";
        var start = double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRAME_MISS_START_S"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) ? seconds : 130;
        var startedAt = Process.GetCurrentProcess().StartTime;
        var startTicks = Stopwatch.GetTimestamp() - (long)((DateTime.Now - startedAt).TotalSeconds * Stopwatch.Frequency);
        return new FrameMissTracker(Stopwatch.Frequency, startTicks, start, 30, names, counts, Console.Error.WriteLine);
    }

    internal static Role CurrentRole
    {
        get
        {
            if (_role != Role.Unresolved) return _role;
            var name = Thread.CurrentThread.Name ?? "";
            _role = name switch
            {
                "SharpEmu Vulkan VideoOut" => Role.Render,
                "GPU front" => Role.Front,
                "SharpEmu GPU submit" => Role.Submit,
                _ when name.StartsWith("SharpEmu", StringComparison.Ordinal) || name.StartsWith("GPU", StringComparison.Ordinal) ||
                       name.StartsWith(".NET", StringComparison.Ordinal) => Role.Emulator,
                _ => Role.Guest,
            };
            return _role;
        }
    }

    internal static bool Tracks(RenderPhaseProfile.Phase phase) => PhaseSlot[(int)phase] >= 0;

    internal static void AddPhase(RenderPhaseProfile.Phase phase, long ticks)
    {
        var slot = PhaseSlot[(int)phase];
        if (slot < 0) return;
        Interlocked.Add(ref Cumulative[PhaseBase + ((int)CurrentRole - 1) * TrackedPhases.Length + slot], ticks);
    }

    internal static void Add(Counter counter, long value) => Interlocked.Add(ref Cumulative[(int)counter], value);

    internal static void AddReadbackWait(long ticks) => Add(CurrentRole switch
    {
        Role.Render => Counter.ReadbackRender,
        Role.Front => Counter.ReadbackFront,
        Role.Submit => Counter.ReadbackSubmit,
        Role.Emulator => Counter.ReadbackEmulator,
        _ => Counter.ReadbackGuest,
    }, ticks);

    internal static void NoteCompile(string kind, double milliseconds)
    {
        var ticks = (long)(milliseconds * Stopwatch.Frequency / 1000);
        if (kind == "dispatch-wait")
        {
            Add(Counter.DispatchCompileWait, ticks);
            return;
        }

        Add(kind switch
        {
            "translate" => Counter.CompileTranslate,
            "graphics" => Counter.CompileGraphics,
            "compute" => Counter.CompileCompute,
            "compute-async" => Counter.CompileComputeAsync,
            "emulator" => Counter.CompileEmulator,
            _ => Counter.CompileOther,
        }, ticks);
        Add(Counter.CompileCount, 1);
    }

    // Called under FlipLatencyProbe's gate, once per presented flip.
    internal static void Presented(in FrameMissTracker.FlipTimes flip, long openedAt, long flipInterval)
    {
        if (Tracker is null) return;
        for (var index = 0; index < CounterCount; index++) Snapshot[index] = Volatile.Read(ref Cumulative[index]);
        Snapshot[(int)Counter.GcPause] = GC.GetTotalPauseDuration().Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        Snapshot[(int)Counter.Gen0] = GC.CollectionCount(0);
        Snapshot[(int)Counter.Gen1] = GC.CollectionCount(1);
        Snapshot[(int)Counter.Gen2] = GC.CollectionCount(2);
        Tracker.Presented(flip, openedAt, openedAt == 0 ? 0 : flipInterval, Snapshot);
    }
}

// [local] The arithmetic of SHARPEMU_FRAME_MISS_PROBE, on an injected clock: grid slots, histograms, the miss records
// and the per-counter attribution. Not thread-safe; its caller serializes the presentations.
internal sealed class FrameMissTracker
{
    // Stage timestamps of one flip (Stopwatch ticks, 0 when unknown): the guest queued the submission the front was
    // interpreting when it reserved the flip, the front reserved it, the render thread started and captured it, its tick
    // was submitted and completed on the GPU, the flip was completed and presented.
    internal struct FlipTimes
    {
        public long Queued, Reserved, RenderStarted, Captured, Submitted, GpuCompleted, FlipCompleted, Presented;

        internal readonly long At(int stage) => stage switch
        {
            0 => Queued,
            1 => Reserved,
            2 => RenderStarted,
            3 => Captured,
            4 => Submitted,
            5 => GpuCompleted,
            6 => FlipCompleted,
            _ => Presented,
        };
    }

    internal static readonly string[] StageNames = ["queued", "reserved", "render", "captured", "submitted", "gpu_done", "flip_done", "presented"];

    private sealed class Record
    {
        public FlipTimes Flip;
        public long Slot;
        public int Skipped;
        public double IntervalMs;
        public long[] Delta = [];
    }

    private const int IntervalBins = 160; // 0.5 ms bins up to 80 ms, the last one open
    private const int LateBins = 12;
    private static readonly double[] LateEdges = [0.5, 1, 2, 3, 5, 8, 12, 20, 33.4, 66.7, 100];

    private readonly long _frequency;
    private readonly long _originTicks;
    private readonly long _startTicks;
    private readonly double _reportSeconds;
    private readonly string[] _names;
    private readonly bool[] _isCount;
    private readonly Action<string> _sink;
    private readonly Record[] _ring = [new(), new(), new()];
    private int _ringCount;
    private long[]? _previousCounters;
    private FlipTimes _previousFlip;
    private bool _hasPreviousFlip;
    private long _lastSlot = -1;
    private long _nextReport;

    internal readonly long[] IntervalHistogram = new long[IntervalBins];
    internal readonly long[] SkippedHistogram = new long[4];
    internal readonly long[] LateHistogram = new long[LateBins];
    // Time left before the end of the first usable slot when the render thread captured the flip (its presentation could
    // follow), 1 ms bins from 0 to 34, clamped; misses not included.
    internal readonly long[] SlackHistogram = new long[35];
    // Per stage (FlipTimes order), the lead of normal presentations over that deadline, 1 ms bins from 0 to 199, clamped.
    internal readonly long[][] LeadHistogram = Enumerable.Range(0, StageNames.Length).Select(_ => new long[200]).ToArray();
    internal long Presentations, Misses, SkippedSlots;
    internal readonly double[] MissSum, NormalSum, MissPreviousSum;
    internal long MissFrames, NormalFrames;

    internal FrameMissTracker(long frequency, long startTicks, double startSeconds, double reportSeconds, string[] names,
        bool[] isCount, Action<string> sink)
    {
        _frequency = frequency;
        _originTicks = startTicks;
        _startTicks = startTicks + (long)(startSeconds * frequency);
        _reportSeconds = reportSeconds;
        _names = names;
        _isCount = isCount;
        _sink = sink;
        MissSum = new double[names.Length];
        NormalSum = new double[names.Length];
        MissPreviousSum = new double[names.Length];
    }

    private double Ms(long ticks) => ticks * 1000.0 / _frequency;

    private double Value(int counter, long delta) => _isCount[counter] ? delta : Ms(delta);

    internal static int IntervalBin(double milliseconds) => Math.Clamp((int)(milliseconds * 2), 0, IntervalBins - 1);

    internal static int LateBin(double milliseconds)
    {
        var bin = 0;
        while (bin < LateEdges.Length && milliseconds >= LateEdges[bin]) bin++;
        return bin;
    }

    // Slot of the grid (port open + k * interval) a presentation fell in, and the slots skipped since the last one.
    internal static (long Slot, int Skipped) GridSlot(long presentedAt, long openedAt, long flipInterval, long lastSlot)
    {
        var slot = (presentedAt - openedAt) / flipInterval;
        var skipped = lastSlot < 0 ? 0 : (int)Math.Max(0, slot - lastSlot - 1);
        return (slot, skipped);
    }

    internal void Presented(in FlipTimes flip, long openedAt, long flipInterval, ReadOnlySpan<long> counters)
    {
        var record = _ring[_ringCount++ % _ring.Length];
        if (record.Delta.Length != counters.Length) record.Delta = new long[counters.Length];
        if (_previousCounters is null)
        {
            _previousCounters = counters.ToArray();
            record.Delta.AsSpan().Clear();
        }
        else
        {
            for (var index = 0; index < counters.Length; index++)
            {
                record.Delta[index] = Math.Max(0, counters[index] - _previousCounters[index]);
                _previousCounters[index] = counters[index];
            }
        }

        record.Flip = flip;
        record.IntervalMs = _hasPreviousFlip && _previousFlip.Presented != 0 ? Ms(flip.Presented - _previousFlip.Presented) : 0;
        var lastSlot = _lastSlot;
        if (flipInterval > 0)
        {
            (record.Slot, record.Skipped) = GridSlot(flip.Presented, openedAt, flipInterval, lastSlot);
            _lastSlot = record.Slot;
        }
        else
        {
            record.Slot = -1;
            record.Skipped = 0;
        }

        var counted = flip.Presented >= _startTicks && _hasPreviousFlip;
        if (counted)
        {
            Presentations++;
            IntervalHistogram[IntervalBin(record.IntervalMs)]++;
            SkippedHistogram[Math.Min(record.Skipped, 3)]++;
            if (record.Skipped == 0 && lastSlot >= 0 && flipInterval > 0)
                for (var stage = 0; stage < StageNames.Length; stage++)
                    if (flip.At(stage) is var at and not 0)
                        LeadHistogram[stage][Math.Clamp((int)Ms(openedAt + (lastSlot + 2) * flipInterval - at), 0, 199)]++;
            if (record.Skipped == 0 && lastSlot >= 0 && flipInterval > 0)
                SlackHistogram[Math.Clamp((int)Ms(openedAt + (lastSlot + 2) * flipInterval - (flip.Captured != 0 ? flip.Captured : flip.Presented)), 0, SlackHistogram.Length - 1)]++;
            var previous = _ringCount >= 2 ? _ring[(_ringCount - 2) % _ring.Length] : null;
            if (record.Skipped > 0)
            {
                Misses++;
                SkippedSlots += record.Skipped;
                // How far past the end of the first slot it could have used (lastSlot + 1) the flip was presented.
                var deadline = openedAt + (lastSlot + 2) * flipInterval;
                var lateMs = Ms(flip.Presented - deadline);
                LateHistogram[LateBin(lateMs)]++;
                MissFrames++;
                for (var index = 0; index < counters.Length; index++)
                {
                    MissSum[index] += Value(index, record.Delta[index]);
                    if (previous is not null) MissPreviousSum[index] += Value(index, previous.Delta[index]);
                }

                _sink(FormatMiss(record, lateMs, deadline));
            }
            else
            {
                NormalFrames++;
                for (var index = 0; index < counters.Length; index++) NormalSum[index] += Value(index, record.Delta[index]);
            }
        }

        _previousFlip = flip;
        _hasPreviousFlip = true;
        if (!counted) return;
        if (_nextReport == 0) _nextReport = flip.Presented + (long)(_reportSeconds * _frequency);
        else if (flip.Presented >= _nextReport)
        {
            _nextReport = flip.Presented + (long)(_reportSeconds * _frequency);
            foreach (var line in Report(flip.Presented)) _sink(line);
        }
    }

    private string FormatCounters(long[] delta)
    {
        var builder = new StringBuilder();
        var ranked = Enumerable.Range(0, delta.Length).Where(index => delta[index] != 0)
            .OrderByDescending(index => _isCount[index] ? 0 : delta[index]);
        foreach (var index in ranked)
        {
            if (!_isCount[index] && Ms(delta[index]) < 0.05) continue;
            builder.Append(' ').Append(_names[index]).Append('=');
            builder.Append(_isCount[index]
                ? delta[index].ToString(CultureInfo.InvariantCulture)
                : Ms(delta[index]).ToString("F2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private string FormatMiss(Record record, double lateMs, long deadline)
    {
        var builder = new StringBuilder();
        var uptime = (record.Flip.Presented - _originTicks) / (double)_frequency;
        builder.Append(CultureInfo.InvariantCulture,
            $"[FRAME_MISS] uptime_s={uptime:F2} interval_ms={record.IntervalMs:F2} skipped={record.Skipped} late_ms={lateMs:F2} stage_vs_deadline_ms:");
        // Each stage against the deadline (the end of the first usable slot) and against the previous flip's same stage.
        for (var stage = 0; stage < StageNames.Length; stage++)
        {
            var at = record.Flip.At(stage);
            builder.Append(' ').Append(StageNames[stage]).Append('=');
            builder.Append(at == 0 ? "-" : Ms(at - deadline).ToString("F1", CultureInfo.InvariantCulture));
        }

        builder.Append(" stage_interval_ms:");
        for (var stage = 0; stage < StageNames.Length; stage++)
        {
            var at = record.Flip.At(stage);
            var before = _previousFlip.At(stage);
            builder.Append(' ').Append(StageNames[stage]).Append('=');
            builder.Append(at == 0 || before == 0 ? "-" : Ms(at - before).ToString("F1", CultureInfo.InvariantCulture));
        }

        builder.Append(" | d0:").Append(FormatCounters(record.Delta));
        for (var back = 1; back <= 2 && _ringCount - 1 - back >= 0; back++)
        {
            var older = _ring[(_ringCount - 1 - back) % _ring.Length];
            builder.Append(CultureInfo.InvariantCulture, $" | d-{back}(interval_ms={older.IntervalMs:F1} skipped={older.Skipped}):");
            builder.Append(FormatCounters(older.Delta));
        }

        return builder.ToString();
    }

    internal IEnumerable<string> Report(long now)
    {
        var hist = new StringBuilder();
        for (var bin = 0; bin < IntervalBins; bin++)
            if (IntervalHistogram[bin] != 0) hist.Append(CultureInfo.InvariantCulture, $" {bin / 2.0:F1}:{IntervalHistogram[bin]}");
        var late = new StringBuilder();
        for (var bin = 0; bin < LateBins; bin++)
        {
            if (LateHistogram[bin] == 0) continue;
            var label = bin == 0 ? FormattableString.Invariant($"<{LateEdges[0]}")
                : bin == LateEdges.Length ? FormattableString.Invariant($">={LateEdges[^1]}")
                : FormattableString.Invariant($"{LateEdges[bin - 1]}-{LateEdges[bin]}");
            late.Append(CultureInfo.InvariantCulture, $" {label}:{LateHistogram[bin]}");
        }

        var slack = string.Join(" ", Enumerable.Range(0, SlackHistogram.Length).Where(bin => SlackHistogram[bin] != 0)
            .Select(bin => string.Create(CultureInfo.InvariantCulture, $"{bin}:{SlackHistogram[bin]}")));
        var leads = new StringBuilder();
        for (var stage = 0; stage < StageNames.Length; stage++)
        {
            var bins = LeadHistogram[stage];
            var total = bins.Sum();
            if (total == 0) continue;
            int Percentile(double fraction)
            {
                var target = (long)Math.Ceiling(total * fraction);
                var seen = 0L;
                for (var bin = 0; bin < bins.Length; bin++)
                    if ((seen += bins[bin]) >= Math.Max(1, target)) return bin;
                return bins.Length - 1;
            }

            leads.Append(CultureInfo.InvariantCulture, $" {StageNames[stage]}={Percentile(0.1)}/{Percentile(0.5)}/{Percentile(0.9)}");
        }

        yield return string.Create(CultureInfo.InvariantCulture,
            $"[FRAME_LEAD] normal presentations, ms before the end of their slot, p10/p50/p90:{leads}");
        yield return string.Create(CultureInfo.InvariantCulture,
            $"[FRAME_HIST] uptime_s={(now - _originTicks) / (double)_frequency:F0} presentations={Presentations} misses={Misses} skipped_slots={SkippedSlots} by_skipped=[1:{SkippedHistogram[1]} 2:{SkippedHistogram[2]} 3+:{SkippedHistogram[3]}] late_ms=[{late.ToString().Trim()}] slack_ms_1=[{slack}] interval_ms_0.5=[{hist.ToString().Trim()}]");
        if (MissFrames == 0) yield break;
        var normal = Math.Max(1, NormalFrames);
        var ranked = Enumerable.Range(0, _names.Length)
            .Select(index => (Index: index, Miss: MissSum[index] / MissFrames, Before: MissPreviousSum[index] / MissFrames, Normal: NormalSum[index] / normal))
            .Where(entry => entry.Miss + entry.Before + entry.Normal > 0.01)
            .OrderByDescending(entry => _isCount[entry.Index] ? double.MinValue : entry.Miss - entry.Normal)
            .Take(40);
        var builder = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"[FRAME_ATTR] miss_frames={MissFrames} normal_frames={NormalFrames} per_interval miss/before/normal:"));
        foreach (var entry in ranked)
            builder.Append(CultureInfo.InvariantCulture, $" {_names[entry.Index]}={entry.Miss:F2}/{entry.Before:F2}/{entry.Normal:F2}");
        yield return builder.ToString();
    }
}
