// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_DRAWCOST_PROBE=1 (off by default): where the serial cost of one draw and one dispatch goes on the
// thread that records them. The phase profiler times nested scopes (two timestamps and an allocation read per scope,
// ~60 scopes per draw with SHARPEMU_PROFILE_FINE) and leaves time between scopes unattributed; this probe takes one
// timestamp per boundary instead (a lap), so the steps of an item add up to the item: a step is the time since the
// lap before it. A step that an item does not reach (an early exit, a stage it does not have) costs nothing; its time
// lands in the next step the item reaches. Steps inside the host's PrepareBindings, BindResources and CommitBindings
// run once per stage (twice for a draw with a pixel stage).
//
// SHARPEMU_DRAWCOST_SAMPLE=N times one item in N (default 1): the others pay one counter increment. The counts (buffer
// obtains, stage memo outcomes, descriptor writes, barriers, ring records, allocated bytes, ...) are taken on the timed
// items only. SHARPEMU_DRAWCOST_REPORT_S (default 5) is the report window. One [PERF][DRAWCOST] line per window and
// kind, on the recording thread:
//
//   [PERF][DRAWCOST] window_s=5.0 frames=25 sample=1 lap_ns=21 gap=1.9us/n6900 gc=12/1/0 lock_contention=31
//   [PERF][DRAWCOST] kind=draw items_per_frame=5568 timed=139207 us_per_item=19.31 ms_per_frame=107.5
//       steps=<name>=<us per pass>/<ms per frame>/<passes per item>,... counts=<name>=<per timed item>,...
//
// ms per frame is scaled from the timed items to all items. lap_ns is the measured cost of one lap (it is inside every
// step once). gap is the time between the end of a timed item and the start of the next item when it is under 200 µs
// (the queue's own per-item cost); gc is the gen0/1/2 collections and lock_contention Monitor's contention count over
// the window, process-wide. The gap also holds the queue items that are not draws or dispatches (labels, barriers).
// pipeline_would_keep8 counts the graphics pipeline lookups that eight kept pipelines (SHARPEMU_KEPT_PIPELINES=8) would
// have answered; pipeline_kept_older the ones the older kept pipelines did answer.
// [local] SHARPEMU_DRAWCOST_DEPTH=1 (with the probe) reports the depth-only draws on their own kind=draw_depth line,
// between kind=draw (then the other draws only) and kind=dispatch; see DrawCostDepth.
internal static class DrawCostProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DRAWCOST_PROBE") == "1";

    internal static readonly bool DepthSplit = Enabled && DrawCostDepth.Requested; // [local] SHARPEMU_DRAWCOST_DEPTH

    private static readonly int SampleEvery =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DRAWCOST_SAMPLE"), out var every) && every > 0 ? every : 1;

    private static readonly long ReportTicks = (long)((double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DRAWCOST_REPORT_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 5.0) * Stopwatch.Frequency);

    private static readonly long GapLimit = Stopwatch.Frequency / 5000; // 200 µs

    internal enum Kind
    {
        Draw,
        DrawDepth, // [local] SHARPEMU_DRAWCOST_DEPTH: before Dispatch, so the dispatch line stays the last of a window
        Dispatch,
        Count,
    }

    // In the order a draw (then a dispatch) reaches them; the report keeps this order.
    internal enum Step
    {
        // Draw: the command stream's state, then the executor's resolution.
        Snapshots,
        ExecPrelude,
        ExecTargets,
        ExecPrograms,
        ExecAdapt,
        ExecFill,
        // Both, inside the program resolution (draws: ExecPrograms, dispatches: DispatchProgram): the user data and stage
        // input of a rebound or resolved compute program, the resource materialization (with its guest reads), the
        // permutation and stage resources. What follows them up to the lap below is the step that holds them.
        ProgramRebind,
        ProgramMaterialize,
        ProgramStage,
        // Dispatch: the executor's resolution.
        DispatchProgram,
        DispatchDecode,
        // Both: the hand-over to the recorder (pending-write marks), its pending operations and debug information.
        RecHandOver,
        RecPending,
        // Draw: the targets of the recorder (in place or looked up).
        RecTargets,
        // Dispatch: the recorder's set-up, the consumers, the end of the open rendering scope.
        DispatchSetup,
        DispatchConsume,
        DispatchEndRendering,
        // Both: the resource preparation opens (batch cap, idle flush, submission capacity, stream retention).
        PrepBegin,
        DispatchPipeline,
        // Host PrepareBindings, per stage: prelude (program checks, bindless heap, call marks), the stage memo lookup with
        // its freshness check, then on a hit the reuse (rent, images, shader data, device ranges, buffers) or on a miss
        // the whole resolution.
        BindPrelude,
        BindMemoFind,
        BindMemoRent,
        BindMemoImages,
        BindMemoShaderData,
        BindMemoRanges,
        BindMemoBuffers,
        BindMiss,
        // Both: the device-address preparation after the bindings.
        Bda,
        // Host BindResources, per stage: storage buffers, device-address ranges, the upload batch, the images.
        ResBuffers,
        ResRanges,
        ResUploadEnd,
        ResImages,
        // Draw: vertex/index/indirect buffers, attachments, stage images, pipeline, vertex binds.
        DrawBuffers,
        DrawAttachments,
        DrawStageImages,
        // [local] SHARPEMU_DRAW_PLAN: the shape key and store lookup, then the comparison of the kept inputs.
        PlanLookup,
        PlanCompare,
        DrawPipeline,
        DrawBindVertex,
        // Host CommitBindings: the descriptor writes built (with the stage image work and checks), their push, the
        // submission's resource list.
        CommitBuild,
        CommitPush,
        CommitTail,
        // Draw: index bind and dynamic state, the rendering scope, pipeline bind and draw, the snapshot end and
        // write barrier.
        DrawDynamic,
        DrawBeginRendering,
        DrawEmit,
        DrawTail,
        // Dispatch: the hazard barrier, snapshot, bind, dispatch and access barrier, the hooks after it.
        DispatchBarrier,
        DispatchEmit,
        DispatchTail,
        // Both: the preparation closes (stage scratch returns), the bindings reset, the record returns to its pool.
        PrepEnd,
        RecReturn,
        // [local] SHARPEMU_DRAWCOST_DEPTH: the switch's own notes (classification, pixel class, draw shape); with the
        // switch off no item reaches it.
        DepthNote,
        // Both: from the record's return back to the front's queue.
        Exit,
        Count,
    }

    internal enum Counter
    {
        Obtain,
        ObtainReadMemo,
        ObtainWritten,
        ObtainStream,
        ObtainLookup,
        ObtainLite, // [local] SHARPEMU_LITE_OBTAIN: read obtains the light path answered
        FindBuffer,
        FindLite, // [local] SHARPEMU_LITE_OBTAIN: read V# the light find answered
        ImageLookup,
        MemoLast,
        MemoContent,
        MemoMiss,
        MemoBuffersChanged,
        Textures,
        StorageBuffers,
        DeviceRanges,
        DescriptorWrites,
        DescriptorPush,
        DescriptorShadowed,
        DescriptorHeap,
        PushConstants,
        PipelineKept,
        PipelineKeptOlder,
        PipelineLookup,
        PipelineWouldKeep8,
        ContextUnchanged,
        TargetsInPlace,
        // [local] SHARPEMU_DRAW_PLAN: draws whose pipeline the plan answered, missed, and answered wrong (=2).
        PlanHit,
        PlanMiss,
        PlanWrong,
        Barriers,
        ScopeBegin,
        ScopeEnd,
        RingRecords,
        // [local] What the program resolution's materialization did: the fast path (MaterializeFast), its memo
        // (SHARPEMU_FAST_MATERIALIZE_MEMO: answered, recorded, gated off), the cache's hits and its full walks.
        MaterializeFast,
        FastMemoHit,
        FastMemoRecorded,
        FastMemoSkipped,
        MaterializeCacheHit,
        MaterializeFull,
        // [local] SHARPEMU_DRAWCOST_DEPTH (both draw kinds): the pixel class at the classification, the pixel stage
        // prepared and with resources, and the draw shape against the previous draw of the same kind (DrawCostDepth).
        PsNone,
        PsKill,
        PsExport,
        PsOther,
        PsIdle,
        PixelBound,
        PixelResources,
        WritesMemory,
        Shaped,
        SameVertex,
        SameIndex,
        SameTargets,
        SameDynamic,
        SamePixel,
        SameAll,
        AllocatedBytes,
        Count,
    }

    // [local] SHARPEMU_DRAWCOST_ALLOC=1 (with the probe): the bytes the thread allocated in each step, reported among the
    // counts as alloc_<step>=<bytes per timed item>. One more counter read per lap.
    private static readonly bool AllocPerStep = Enabled && Environment.GetEnvironmentVariable("SHARPEMU_DRAWCOST_ALLOC") == "1";
    private static long _lastAllocated;

    // [local] The window's sums per kind and the open item (DrawCostTally).
    private static readonly DrawCostTally _tally = new(DepthSplit);

    // The recording thread's state: the first thread that begins an item owns the probe.
    private static int _thread;
    private static int _depth;
    private static bool _active;
    private static long _allocated;
    private static long _ring;
    private static long _lastEnd;
    private static long _sequence;
    private static long _gapTicks;
    private static long _gaps;
    private static long _windowStart;
    private static long _frames;
    private static int _gen0, _gen1, _gen2;
    private static long _contention;

    internal static bool Timing => Enabled && _active;

    // [local] SHARPEMU_DRAWCOST_DEPTH: a timed item is open on this thread (its notes are taken).
    internal static bool DepthTiming => DepthSplit && _active && Environment.CurrentManagedThreadId == _thread;

    // [local] SHARPEMU_DRAWCOST_DEPTH: the open draw's targets are known; a depth-only draw is counted as draw_depth,
    // timed or not. The first call of an item decides.
    // True when this call classified it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Classify(bool depthOnly) => DepthSplit && _depth != 0 && ClassifyCore(depthOnly);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ClassifyCore(bool depthOnly) =>
        Environment.CurrentManagedThreadId == _thread && _tally.Classify(depthOnly);

    // [local] SHARPEMU_DRAWCOST_DEPTH: the kind the open item is counted as.
    internal static Kind CurrentKind => _tally.CurrentKind;

    // Called once per presented frame, from any thread.
    internal static void NoteFrame()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _frames);
        }

        LiteObtainProbe.NoteFrame(); // [local] SHARPEMU_LITE_OBTAIN_PROBE, SHARPEMU_PLAN_PROBE
        PlanProbe.NoteFrame();
        BdaCostProbe.NoteFrame(); // [local] SHARPEMU_BDA_COST_PROBE
    }

    internal static void Begin(Kind kind)
    {
        if (Enabled)
        {
            BeginCore(kind);
        }

        if (RepeatProbe.Enabled) // [local] SHARPEMU_REPEAT_PROBE: the same items, timed on their own
        {
            RepeatProbe.Begin(kind == Kind.Dispatch);
        }

        if (LiteObtainProbe.Enabled) // [local] SHARPEMU_LITE_OBTAIN_PROBE: the item kind of the obtains
        {
            LiteObtainProbe.Begin(kind == Kind.Dispatch);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BeginCore(Kind kind)
    {
        var thread = Environment.CurrentManagedThreadId;
        if (_thread == 0)
        {
            _thread = thread;
            _windowStart = Stopwatch.GetTimestamp();
            TakeProcessCounters(out _, out _, out _, out _);
        }

        if (thread != _thread || _depth++ != 0)
        {
            return;
        }

        long now = 0;
        if (_lastEnd != 0)
        {
            now = Stopwatch.GetTimestamp();
            var gap = now - _lastEnd;
            if (gap < GapLimit)
            {
                _gapTicks += gap;
                _gaps++;
            }

            _lastEnd = 0;
        }

        if (++_sequence % SampleEvery != 0)
        {
            _active = false;
            _tally.Begin(kind, timed: false, 0); // [local] counted, not timed
            return;
        }

        _active = true;
        _tally.Begin(kind, timed: true, now != 0 ? now : Stopwatch.GetTimestamp()); // [local]
        _allocated = GC.GetAllocatedBytesForCurrentThread();
        _lastAllocated = _allocated;
        _ring = Gpu.Vulkan.CommandRing.Active?.Produced ?? 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Lap(Step step)
    {
        if (Enabled && _active)
        {
            LapCore(step);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LapCore(Step step)
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        long bytes = 0; // [local]
        if (AllocPerStep) // [local]
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            bytes = allocated - _lastAllocated; // [local]
            _lastAllocated = allocated;
        }

        _tally.Lap(step, now, bytes); // [local] DrawCostTally
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Count(Counter counter, long amount = 1)
    {
        if (Enabled && _active)
        {
            CountCore(counter, amount);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CountCore(Counter counter, long amount)
    {
        if (Environment.CurrentManagedThreadId == _thread)
        {
            _tally.Count(counter, amount); // [local] DrawCostTally
        }
    }

    internal static void End()
    {
        if (Enabled)
        {
            EndCore();
        }

        if (RepeatProbe.Enabled) // [local] SHARPEMU_REPEAT_PROBE
        {
            RepeatProbe.End();
        }

        if (LiteObtainProbe.Enabled) // [local] SHARPEMU_LITE_OBTAIN_PROBE
        {
            LiteObtainProbe.End();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EndCore()
    {
        if (Environment.CurrentManagedThreadId != _thread || _depth == 0 || --_depth != 0)
        {
            return;
        }

        if (!_active)
        {
            _tally.End(); // [local]
            return;
        }

        LapCore(Step.Exit);
        var now = _tally.Last; // [local]
        _tally.End(GC.GetAllocatedBytesForCurrentThread() - _allocated, // [local]
            Gpu.Vulkan.CommandRing.Active is { } ring ? ring.Produced - _ring : 0); // [local]
        _active = false;
        if (now - _windowStart >= ReportTicks)
        {
            Report(now);
            now = Stopwatch.GetTimestamp();
        }

        _lastEnd = now;
    }

    private static void TakeProcessCounters(out int gen0, out int gen1, out int gen2, out long contention)
    {
        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);
        var lockContention = Monitor.LockContentionCount;
        gen0 = g0 - _gen0;
        gen1 = g1 - _gen1;
        gen2 = g2 - _gen2;
        contention = lockContention - _contention;
        _gen0 = g0;
        _gen1 = g1;
        _gen2 = g2;
        _contention = lockContention;
    }

    // The cost of one lap, timed on the reporting thread with the probe's own arrays hot.
    private static double CalibrateLapNanoseconds()
    {
        const int Laps = 4096;
        var sink = 0L;
        var started = Stopwatch.GetTimestamp();
        var last = started;
        for (var index = 0; index < Laps; index++)
        {
            if (Environment.CurrentManagedThreadId == _thread)
            {
                var now = Stopwatch.GetTimestamp();
                sink += now - last;
                last = now;
            }
        }

        var elapsed = Stopwatch.GetTimestamp() - started;
        return sink < 0 ? 0 : elapsed * 1e9 / Stopwatch.Frequency / Laps;
    }

    private static void Report(long now)
    {
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        var frames = Interlocked.Exchange(ref _frames, 0);
        TakeProcessCounters(out var gen0, out var gen1, out var gen2, out var contention);
        var toMs = 1000.0 / Stopwatch.Frequency;
        var header = FormattableString.Invariant(
            $"[PERF][DRAWCOST] window_s={seconds:F1} frames={frames} sample={SampleEvery} lap_ns={CalibrateLapNanoseconds():F0} ") +
            FormattableString.Invariant($"gap={(_gaps == 0 ? 0 : _gapTicks * toMs * 1000 / _gaps):F2}us/n{_gaps} gc={gen0}/{gen1}/{gen2} lock_contention={contention}");
        Console.Error.WriteLine(header);
        var kindLines = new List<string>(DrawCostTally.Kinds); // [local] DrawCostTally
        _tally.Format(kindLines, frames, Stopwatch.Frequency, AllocPerStep); // [local]
        foreach (var line in kindLines) // [local]
        {
            Console.Error.WriteLine(line); // [local]
        }

        _tally.Clear(); // [local]
        _gapTicks = 0;
        _gaps = 0;
        LiteObtainProbe.Report(); // [local] SHARPEMU_LITE_OBTAIN_PROBE, SHARPEMU_PLAN_PROBE: the same window, after its lines
        PlanProbe.Report();
        Gpu.Buffers.LiteObtainUse.Report(Gpu.Buffers.LiteObtainUse.DefaultMode); // [local] SHARPEMU_LITE_OBTAIN
        Gpu.Rendering.DrawPlan.Report(); // [local] SHARPEMU_DRAW_PLAN
        Gpu.Buffers.BdaWriteMemo.Report(Gpu.Buffers.BdaWriteMemo.DefaultMode); // [local] SHARPEMU_BDA_WRITE_MEMO
        BdaCostProbe.Report(); // [local] SHARPEMU_BDA_COST_PROBE: at its next dispatch, on its thread
        _windowStart = Stopwatch.GetTimestamp();
    }

    // [local] SHARPEMU_DRAWCOST_PROBE: the last graphics pipeline keys the recorder resolved, to count the lookups an
    // eight-entry memo (SHARPEMU_KEPT_PIPELINES=8) would have answered. Updated on every draw while the probe is on.
    internal sealed class KeyShadow<TKey> where TKey : IEquatable<TKey>
    {
        private readonly TKey[] _keys = new TKey[8];
        private readonly bool[] _valid = new bool[8];
        private int _next;

        public bool Contains(in TKey key)
        {
            for (var index = 0; index < _keys.Length; index++)
            {
                if (_valid[index] && _keys[index].Equals(key))
                {
                    return true;
                }
            }

            return false;
        }

        public void Add(in TKey key)
        {
            _keys[_next] = key;
            _valid[_next] = true;
            _next = (_next + 1) & 7;
        }
    }

    // [local] For tests: clears the thread ownership and the window.
    internal static void ResetForTests()
    {
        _thread = 0;
        _depth = 0;
        _active = false;
        _lastEnd = 0;
        _sequence = 0;
        _tally.Reset(); // [local]
    }
}
