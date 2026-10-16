// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_PROFILE_RESOLVE=1 (with SHARPEMU_PROFILE_RENDER=1): what the phase profile's exclusive times leave
// out about the render thread's resolve time (sondas2, 2026-10-16).
//   - inclusive time, inner scope count and allocated bytes per phase (outermost scope of a phase only) and per
//     caller -> callee edge, from the timestamps the phase profile already reads (no extra clock read per scope);
//   - the cost of one measured scope, timed on real nested scopes at every report, so the profile's own share of
//     each phase can be subtracted (scope_ns x inner scopes);
//   - every draw and dispatch the render thread executes, timed and keyed by its pipeline, with the bindings it
//     prepared; one in SHARPEMU_PROFILE_RESOLVE_SAMPLE (default 8) also reads the thread's cycle counter, so
//     wall - cycles / TSC is the time the thread was off the CPU inside draws (preempted, blocked, GC-suspended);
//   - binding counts, image lookups by role, GC collections and pauses, Monitor contention and the contended
//     RegionLock spins (render thread apart).
// All counters are render-thread only (the thread that records frames), like the phase profile's.
internal static class ResolveProfile
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RESOLVE") == "1" && RenderPhaseProfile.Enabled;

    private static readonly int SampleEvery =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RESOLVE_SAMPLE"), out var every) && every > 0 ? every : 8;

    private const int Phases = (int)RenderPhaseProfile.Phase.Count;
    private const int CalibrationScopes = 20000;
    private const int MaxKeys = 4096;
    private const ulong DispatchKey = 1UL << 63;

    private static readonly long[] InclusiveTicks = new long[Phases];
    private static readonly long[] InclusiveCalls = new long[Phases];
    private static readonly long[] InnerCharges = new long[Phases];
    private static readonly long[] InclusiveBytes = new long[Phases];
    private static readonly int[] Depth = new int[Phases];
    private static readonly long[] EdgeTicks = new long[Phases * Phases];
    private static readonly long[] EdgeCalls = new long[Phases * Phases];

    [ThreadStatic] private static bool t_render;
    [ThreadStatic] private static bool t_inDraw;
    [ThreadStatic] private static int t_drawCounter;

    private static long _frames;
    private static long _draws, _dispatches, _drawTicks, _dispatchTicks;
    private static long _sampled, _sampledTicks, _sampledCycles;
    private static long _prepares, _images, _storageImages, _samplers, _buffers, _nullBuffers, _deviceRanges;
    private static readonly long[] FindImageRoles = new long[6];
    private static long _findImageOutside;

    private sealed class KeyStat
    {
        public bool Dispatch;
        public ulong Pipeline, Vertex, Pixel, Compute;
        public long Calls, Ticks, MaxTicks, Images, Buffers, Samplers, Prepares;
    }

    private static readonly Dictionary<ulong, KeyStat> Keys = new();
    private static readonly KeyStat OtherKeys = new();

    // [local] The render thread's resource materializations by program: time, cache hits and the GPU completion
    // waits inside them (the synchronous table reads).
    private sealed class MaterializeStat
    {
        public long Calls, Ticks, Hits, Waits, WaitTicks;
    }

    private static readonly Dictionary<ulong, MaterializeStat> Materializations = new();

    internal static long CompletionWaitTicks => InclusiveTicks[(int)RenderPhaseProfile.Phase.GpuCompletionWait];

    internal static long CompletionWaits => InclusiveCalls[(int)RenderPhaseProfile.Phase.GpuCompletionWait];

    internal static void NoteMaterialize(ulong hash, long ticks, bool hit, long waits, long waitTicks)
    {
        if (!t_render)
            return;
        if (!Materializations.TryGetValue(hash, out var stat))
        {
            if (Materializations.Count >= MaxKeys)
                return;
            Materializations[hash] = stat = new MaterializeStat();
        }

        stat.Calls++;
        stat.Ticks += ticks;
        stat.Hits += hit ? 1 : 0;
        stat.Waits += waits;
        stat.WaitTicks += waitTicks;
    }

    private static double _tscHz;
    private static ulong _windowCycles;
    private static long _windowAllocated;
    private static int[] _gcCounts = new int[3];
    private static TimeSpan _gcPause;
    private static long _gcAllocated;
    private static long _monitorContention;
    private static bool _windowStarted;

    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    private static ulong ThreadCycles() => QueryThreadCycleTime(GetCurrentThread(), out var cycles) ? cycles : 0;

    // Phase scopes, called by RenderPhaseProfile.Measure and Scope.Dispose when Enabled.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void OpenScope(RenderPhaseProfile.Phase phase)
    {
        if (t_render)
            Depth[(int)phase]++;
    }

    internal static void CloseScope(RenderPhaseProfile.Phase parent, RenderPhaseProfile.Phase phase, long ticks, long charges, long bytes)
    {
        if (!t_render)
            return;
        var index = (int)phase;
        // A scope opened before the thread was marked has no matching open.
        if (Depth[index] == 0 || --Depth[index] != 0)
            return;
        InclusiveTicks[index] += ticks;
        InclusiveCalls[index]++;
        InnerCharges[index] += charges;
        InclusiveBytes[index] += bytes;
        var edge = (int)parent * Phases + index;
        EdgeTicks[edge] += ticks;
        EdgeCalls[edge]++;
    }

    internal readonly struct DrawToken(long started, ulong cycles, bool sampled, long images, long buffers, long samplers, long prepares)
    {
        public readonly long Started = started;
        public readonly ulong Cycles = cycles;
        public readonly bool Sampled = sampled;
        public readonly long Images = images;
        public readonly long Buffers = buffers;
        public readonly long Samplers = samplers;
        public readonly long Prepares = prepares;
    }

    internal static DrawToken BeginDraw()
    {
        if (!t_render || t_inDraw)
            return default;
        t_inDraw = true;
        var sampled = ++t_drawCounter % SampleEvery == 0;
        var cycles = sampled ? ThreadCycles() : 0;
        return new DrawToken(Stopwatch.GetTimestamp(), cycles, sampled, _images, _buffers, _samplers, _prepares);
    }

    internal static void EndDraw(in DrawToken token, bool dispatch, ulong pipeline, ulong vertex, ulong pixel, ulong compute)
    {
        if (token.Started == 0)
            return;
        var ticks = Stopwatch.GetTimestamp() - token.Started;
        if (token.Sampled)
        {
            var cycles = ThreadCycles();
            if (cycles >= token.Cycles && token.Cycles != 0)
            {
                _sampled++;
                _sampledTicks += ticks;
                _sampledCycles += (long)(cycles - token.Cycles);
            }
        }

        t_inDraw = false;
        if (dispatch)
        {
            _dispatches++;
            _dispatchTicks += ticks;
        }
        else
        {
            _draws++;
            _drawTicks += ticks;
        }

        var key = pipeline | (dispatch ? DispatchKey : 0);
        if (!Keys.TryGetValue(key, out var stat))
        {
            stat = Keys.Count < MaxKeys ? new KeyStat { Dispatch = dispatch, Pipeline = pipeline, Vertex = vertex, Pixel = pixel, Compute = compute } : OtherKeys;
            if (stat != OtherKeys)
                Keys.Add(key, stat);
        }

        stat.Calls++;
        stat.Ticks += ticks;
        stat.MaxTicks = Math.Max(stat.MaxTicks, ticks);
        stat.Images += _images - token.Images;
        stat.Buffers += _buffers - token.Buffers;
        stat.Samplers += _samplers - token.Samplers;
        stat.Prepares += _prepares - token.Prepares;
    }

    internal static void CountBindings(int images, int storageImages, int samplers, int buffers, int deviceRanges)
    {
        if (!t_render)
            return;
        _prepares++;
        _images += images;
        _storageImages += storageImages;
        _samplers += samplers;
        _buffers += buffers;
        _deviceRanges += deviceRanges;
    }

    internal static void CountNullBuffer()
    {
        if (t_render)
            _nullBuffers++;
    }

    internal static void CountFindImage(Gpu.Images.ImageRole role)
    {
        if (!t_render)
            return;
        FindImageRoles[Math.Min((int)role, FindImageRoles.Length - 1)]++;
        if (!t_inDraw)
            _findImageOutside++;
    }

    // Once per presented frame on the render thread.
    internal static void OnFrame()
    {
        if (!t_render)
        {
            t_render = true;
            RegionLock.MarkProbeThread();
        }

        if (!_windowStarted)
        {
            StartWindow();
        }

        _frames++;
    }

    private static void StartWindow()
    {
        _windowStarted = true;
        _windowCycles = ThreadCycles();
        _windowAllocated = GC.GetAllocatedBytesForCurrentThread();
        for (var gen = 0; gen < 3; gen++)
            _gcCounts[gen] = GC.CollectionCount(gen);
        _gcPause = GC.GetTotalPauseDuration();
        _gcAllocated = GC.GetTotalAllocatedBytes(false);
        _monitorContention = Monitor.LockContentionCount;
    }

    // A 2 ms spin: cycles per second of the thread counter while the thread runs. The largest value seen is kept
    // (a preempted spin reads low).
    private static void CalibrateTsc()
    {
        var c0 = ThreadCycles();
        var q0 = Stopwatch.GetTimestamp();
        var until = q0 + Stopwatch.Frequency / 500;
        long q1;
        do
        {
            q1 = Stopwatch.GetTimestamp();
        }
        while (q1 < until);
        var c1 = ThreadCycles();
        if (c0 != 0 && c1 > c0)
            _tscHz = Math.Max(_tscHz, (c1 - c0) * (double)Stopwatch.Frequency / (q1 - q0));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // Called by RenderPhaseProfile.RecordFrame after its window report, on the render thread.
    internal static IEnumerable<string> TakeReport(double seconds, long phaseFrames)
    {
        if (!Enabled || !t_render)
            yield break;

        var frames = Math.Max(_frames, 1);
        var f = (double)frames;
        var cyclesNow = ThreadCycles();
        var windowCycles = cyclesNow - _windowCycles;
        var renderAllocated = GC.GetAllocatedBytesForCurrentThread() - _windowAllocated;
        var gc = new int[3];
        for (var gen = 0; gen < 3; gen++)
            gc[gen] = GC.CollectionCount(gen) - _gcCounts[gen];
        var gcPause = GC.GetTotalPauseDuration() - _gcPause;
        var gcAllocated = GC.GetTotalAllocatedBytes(false) - _gcAllocated;
        var contention = Monitor.LockContentionCount - _monitorContention;
        var waits = new long[4];
        var waitTicks = new long[4];
        RegionLock.TakeProbeWaits(waits, waitTicks);

        var chargesBefore = RenderPhaseProfile.ChargeCount;
        var scopeNs = RenderPhaseProfile.CalibrateScopeNanoseconds(CalibrationScopes);
        CalibrateTsc();

        var windowCharges = chargesBefore - _windowChargeStart;
        var sampledWallMs = Ms(_sampledTicks);
        var sampledCpuMs = _tscHz > 0 ? _sampledCycles * 1000.0 / _tscHz : double.NaN;
        var threadCpuMs = _tscHz > 0 ? windowCycles * 1000.0 / _tscHz : double.NaN;
        yield return Inv($"[PERF][RESOLVE] frames={frames} phase_frames={phaseFrames} window_s={seconds:F1} scope_ns={scopeNs:F1} charges_per_frame={windowCharges / f:F0} profiler_est_ms={windowCharges / 2.0 * scopeNs / 1e6 / f:F2} tsc_ghz={_tscHz / 1e9:F3} thread_cpu_ms={threadCpuMs / f:F2} wall_ms={seconds * 1000.0 / f:F2} draws={_draws / f:F0} dispatches={_dispatches / f:F0} draw_ms={Ms(_drawTicks) / f:F2} dispatch_ms={Ms(_dispatchTicks) / f:F2} sample_every={SampleEvery} sampled={_sampled} sampled_wall_ms={sampledWallMs:F1} sampled_cpu_ms={sampledCpuMs:F1} offcpu_pct={(sampledWallMs > 0 ? 100.0 * (1 - sampledCpuMs / sampledWallMs) : double.NaN):F1} gc0={gc[0]} gc1={gc[1]} gc2={gc[2]} gc_pause_ms={gcPause.TotalMilliseconds / f:F2} alloc_mb={gcAllocated / 1048576.0 / f:F2} render_alloc_kb={renderAllocated / 1024.0 / f:F0} monitor_contention={contention / f:F1} rl_tracker_waits={waits[0] / f:F2} rl_tracker_wait_ms={Ms(waitTicks[0]) / f:F3} rl_image_waits={waits[2] / f:F2} rl_image_wait_ms={Ms(waitTicks[2]) / f:F3} rl_other_thread_waits={(waits[1] + waits[3]) / f:F2} rl_other_thread_wait_ms={Ms(waitTicks[1] + waitTicks[3]) / f:F3}");

        yield return Inv($"[PERF][RESOLVE_BIND] frames={frames} prepares={_prepares / f:F0} images={_images / f:F0} storage_images={_storageImages / f:F0} samplers={_samplers / f:F0} buffers={_buffers / f:F0} null_buffers={_nullBuffers / f:F0} device_ranges={_deviceRanges / f:F0} find_texture={FindImageRoles[0] / f:F0} find_storage={FindImageRoles[1] / f:F0} find_color={FindImageRoles[2] / f:F0} find_depth={FindImageRoles[3] / f:F0} find_display={FindImageRoles[4] / f:F0} find_outside_draws={_findImageOutside / f:F0}");

        var calibration = (int)RenderPhaseProfile.Phase.ProfilerCalibration;
        var calibrationInner = (int)RenderPhaseProfile.Phase.ProfilerCalibrationInner;
        var phases = new List<(int Phase, long Ticks)>();
        for (var index = 0; index < Phases; index++)
        {
            if (index != calibration && index != calibrationInner && InclusiveTicks[index] > 0)
                phases.Add((index, InclusiveTicks[index]));
        }

        phases.Sort(static (left, right) => right.Ticks.CompareTo(left.Ticks));
        var line = new StringBuilder(Inv($"[PERF][RESOLVE_PHASE] frames={frames} scope_ns={scopeNs:F1}"));
        foreach (var (phase, ticks) in phases)
        {
            if (Ms(ticks) / f < 0.02)
                break;
            line.Append(Inv($" {(RenderPhaseProfile.Phase)phase}={Ms(ticks) / f:F3}ms/n{InclusiveCalls[phase] / f:F0}/c{InnerCharges[phase] / f:F0}/kb{InclusiveBytes[phase] / 1024.0 / f:F0}"));
        }

        yield return line.ToString();

        var edges = new List<(int Edge, long Ticks)>();
        for (var index = 0; index < EdgeTicks.Length; index++)
        {
            var child = index % Phases;
            var parent = index / Phases;
            if (EdgeTicks[index] > 0 && child != calibration && child != calibrationInner && parent != calibration)
                edges.Add((index, EdgeTicks[index]));
        }

        edges.Sort(static (left, right) => right.Ticks.CompareTo(left.Ticks));
        line.Clear().Append(Inv($"[PERF][RESOLVE_EDGE] frames={frames}"));
        foreach (var (edge, ticks) in edges.Take(60))
            line.Append(Inv($" {(RenderPhaseProfile.Phase)(edge / Phases)}>{(RenderPhaseProfile.Phase)(edge % Phases)}={Ms(ticks) / f:F3}ms/n{EdgeCalls[edge] / f:F0}"));
        yield return line.ToString();

        var keys = Keys.Values.Append(OtherKeys).Where(static stat => stat.Calls > 0).OrderByDescending(static stat => stat.Ticks).Take(25).ToArray();
        for (var rank = 0; rank < keys.Length; rank++)
        {
            var stat = keys[rank];
            var calls = (double)stat.Calls;
            yield return Inv($"[PERF][RESOLVE_TOP] frames={frames} rank={rank} kind={(stat == OtherKeys ? "other" : stat.Dispatch ? "dispatch" : "draw")} pipeline={stat.Pipeline} vertex=0x{stat.Vertex:X16} pixel=0x{stat.Pixel:X16} compute=0x{stat.Compute:X16} n={stat.Calls / f:F1} ms={Ms(stat.Ticks) / f:F3} us_per_call={Ms(stat.Ticks) * 1000.0 / calls:F2} max_us={Ms(stat.MaxTicks) * 1000.0:F1} images_per_call={stat.Images / calls:F1} buffers_per_call={stat.Buffers / calls:F1} samplers_per_call={stat.Samplers / calls:F1} prepares_per_call={stat.Prepares / calls:F2}");
        }

        var materializations = Materializations.Where(static pair => pair.Value.Calls > 0).OrderByDescending(static pair => pair.Value.Ticks).Take(12).ToArray();
        if (materializations.Length != 0)
        {
            line.Clear().Append(Inv($"[PERF][RESOLVE_MATERIALIZE] frames={frames}"));
            foreach (var (hash, stat) in materializations)
                line.Append(Inv($" 0x{hash:X16}={Ms(stat.Ticks) / f:F3}ms/n{stat.Calls / f:F1}/hit{100.0 * stat.Hits / stat.Calls:F0}%/wait{Ms(stat.WaitTicks) / f:F3}ms/nw{stat.Waits / f:F2}"));
            yield return line.ToString();
        }

        Reset();
        _windowChargeStart = RenderPhaseProfile.ChargeCount;
        StartWindow();
    }

    private static long _windowChargeStart;

    private static void Reset()
    {
        Array.Clear(InclusiveTicks);
        Array.Clear(InclusiveCalls);
        Array.Clear(InnerCharges);
        Array.Clear(InclusiveBytes);
        Array.Clear(EdgeTicks);
        Array.Clear(EdgeCalls);
        Array.Clear(FindImageRoles);
        foreach (var stat in Keys.Values)
        {
            stat.Calls = stat.Ticks = stat.MaxTicks = stat.Images = stat.Buffers = stat.Samplers = stat.Prepares = 0;
        }

        OtherKeys.Calls = OtherKeys.Ticks = OtherKeys.MaxTicks = OtherKeys.Images = OtherKeys.Buffers = OtherKeys.Samplers = OtherKeys.Prepares = 0;
        foreach (var stat in Materializations.Values)
        {
            stat.Calls = stat.Ticks = stat.Hits = stat.Waits = stat.WaitTicks = 0;
        }
        _frames = 0;
        _draws = _dispatches = _drawTicks = _dispatchTicks = 0;
        _sampled = _sampledTicks = _sampledCycles = 0;
        _prepares = _images = _storageImages = _samplers = _buffers = _nullBuffers = _deviceRanges = 0;
        _findImageOutside = 0;
    }
}
