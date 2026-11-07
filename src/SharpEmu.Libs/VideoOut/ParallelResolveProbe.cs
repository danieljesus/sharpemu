// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_PARRES_PROBE=1 (with SHARPEMU_RENDER_PIPELINE=1): how much of the render thread's back-queue work
// could go to the resolve pool and the slice workers. Every item the render thread runs is timed; a draw is split into
// executor and recorder time and classified by the resolve pool's gate (DrawHasTargets && DrawKnownNotToWrite), and the
// runs of consecutive gated draws are measured between the items that would end a slice. No behaviour changes.
// slice_runs and draw_barrier_runs are the runs step 3 (SHARPEMU_FRONT_SLICES) would cut with typed barriers: global
// barriers do not end them, and dispatches do not end slice_runs either unless NeedsSerialDispatch.
// With SHARPEMU_FRONT_SLICES the line also carries the slices' cost on the render thread (front_slices=…, slice_*), and
// draw_record_ms only counts the draws recorded through the executor's sink (the taken ones are recorded elsewhere).
// docs/plan-parallel-resolution-open-world.md §8 explains the [PERF][PARRES] line.
internal static class ParallelResolveProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PARRES_PROBE") == "1";

    public const int GateOk = 0, GateNoTargets = 1, GateUnknownPair = 2, GateWrites = 3, GateStalePair = 4, GateCount = 5;
    private static readonly string[] GateNames = ["ok", "no_targets", "unknown_pair", "writes", "stale_pair"];

    private const int KindOther = 0, KindDraw = 1, KindDispatch = 2;
    private const int BreakOther = 0, BreakDispatch = 1, BreakIneligible = 2, BreakScope = 3, BreakCount = 4;
    private static readonly string[] BreakNames = ["other", "dispatch", "ineligible", "scope"];
    private static readonly int[] Thresholds = [8, 16, 32];
    private const int BreakerSampleMask = 7;

    private static long _frames;
    private static long _windowStart = Stopwatch.GetTimestamp();

    // Render thread only from here on.
    private static int _kind;
    private static int _gate;
    private static long _itemRecordTicks;
    private static long _lookaheadTicks;

    private static long _draws, _dispatches, _others, _drawTicks, _dispatchTicks, _otherTicks, _drawRecordTicks, _dispatchRecordTicks;
    private static readonly long[] GateDraws = new long[GateCount];
    private static readonly long[] GateTicks = new long[GateCount];
    private static readonly long[] GateRecordTicks = new long[GateCount];
    private static long _scopeChanges, _lookupChanges, _dispatchesInRuns;
    private static DrawScopeToken _lastScope;
    private static int _breakerSample;
    private static readonly Dictionary<string, (long Count, long Ticks)> Breakers = new();

    private sealed class Runs
    {
        public int Length;
        public long Ticks;
        public long Count, Draws;
        public readonly long[] DrawsAtLeast = new long[Thresholds.Length];
        public readonly long[] TicksAtLeast = new long[Thresholds.Length];
        public readonly long[] Breaks = new long[BreakCount];

        public void Add(long ticks)
        {
            Length++;
            Ticks += ticks;
        }

        public bool End(int cause)
        {
            if (Length == 0)
                return false;
            Count++;
            Draws += Length;
            for (var index = 0; index < Thresholds.Length; index++)
            {
                if (Length >= Thresholds[index])
                {
                    DrawsAtLeast[index] += Length;
                    TicksAtLeast[index] += Ticks;
                }
            }

            Breaks[cause]++;
            Length = 0;
            Ticks = 0;
            return true;
        }

        public void Append(StringBuilder text, string name, double frames)
        {
            text.Append(CultureInfo.InvariantCulture, $" {name}=n:{Count / frames:F1},mean:{(Count == 0 ? 0 : Draws / (double)Count):F1}");
            for (var index = 0; index < Thresholds.Length; index++)
            {
                text.Append(CultureInfo.InvariantCulture, $",ge{Thresholds[index]}:{DrawsAtLeast[index] / frames:F0}/{Ms(TicksAtLeast[index]) / frames:F2}ms");
                DrawsAtLeast[index] = 0;
                TicksAtLeast[index] = 0;
            }

            text.Append(" breaks_").Append(name).Append('=');
            for (var cause = 0; cause < BreakCount; cause++)
            {
                text.Append(cause == 0 ? "" : ",").Append(BreakNames[cause]).Append(':')
                    .Append((Breaks[cause] / frames).ToString("F1", CultureInfo.InvariantCulture));
                Breaks[cause] = 0;
            }

            Count = 0;
            Draws = 0;
        }
    }

    private static readonly Runs GateRuns = new();
    private static readonly Runs ScopeRuns = new();
    private static readonly Runs SliceRuns = new();
    private static readonly Runs DrawBarrierRuns = new();

    // The front's global barrier item (one delegate instance), set when the front starts.
    public static Delegate? BarrierItem;

    // [local] SHARPEMU_FRONT_SLICES (render thread): handing slices to the workers, waiting for their segments (with the
    // relays run meanwhile), joining them, and the taken items recorded here after all. The workers' recording, any thread.
    internal static long SliceHandoffTicks, SliceWaitTicks, SliceRestWaitTicks, SliceJoinTicks, SliceInlineTicks;
    private static long _sliceWorkerTicks;

    public static void AddSliceWorkerTicks(long ticks) => Interlocked.Add(ref _sliceWorkerTicks, ticks);

    // [local] SHARPEMU_FRONT_SLICES, any worker thread: where a worker's time goes. Segments begun with the cost of
    // beginning (buffer, barrier, state reset) and ending (end rendering, end buffer) them, items recorded and their
    // cost, items it stopped at (Serial) and the time spent on them before it stopped, render scopes it began,
    // attachments it acquired (not kept from earlier in the segment) and kept, and its image lookups.
    internal static long WorkerSegments, WorkerBeginTicks, WorkerEndTicks, WorkerItems, WorkerItemTicks, WorkerSerial, WorkerSerialTicks,
        WorkerDispatches, WorkerDispatchTicks, WorkerScopes, WorkerAttach, WorkerAttachTicks, WorkerKept, WorkerLookups, WorkerLookupTicks;

    // [local] Render thread: the items it ran after all (slice_inline) and the joins caused by the front's preparation check.
    internal static long SliceInlineItems;

    private static string PerUnit(long ticks, long count) =>
        (count == 0 ? 0 : ticks * 1_000_000.0 / Stopwatch.Frequency / count).ToString("F1", CultureInfo.InvariantCulture);

    private static void AppendWorker(StringBuilder text, double frames)
    {
        long Take(ref long value) => Interlocked.Exchange(ref value, 0);
        var segments = Take(ref WorkerSegments);
        var begin = Take(ref WorkerBeginTicks);
        var end = Take(ref WorkerEndTicks);
        var items = Take(ref WorkerItems);
        var itemTicks = Take(ref WorkerItemTicks);
        var serial = Take(ref WorkerSerial);
        var serialTicks = Take(ref WorkerSerialTicks);
        var dispatches = Take(ref WorkerDispatches);
        var dispatchTicks = Take(ref WorkerDispatchTicks);
        var scopes = Take(ref WorkerScopes);
        var attach = Take(ref WorkerAttach);
        var attachTicks = Take(ref WorkerAttachTicks);
        var kept = Take(ref WorkerKept);
        var lookups = Take(ref WorkerLookups);
        var lookupTicks = Take(ref WorkerLookupTicks);
        text.Append(CultureInfo.InvariantCulture,
            $" slice_worker=segments:{segments / frames:F1}/begin_us:{PerUnit(begin, segments)}/end_us:{PerUnit(end, segments)},items:{items / frames:F0}/us:{PerUnit(itemTicks, items)},dispatches:{dispatches / frames:F0}/us:{PerUnit(dispatchTicks, dispatches)},serial:{serial / frames:F1}/us:{PerUnit(serialTicks, serial)},scopes:{scopes / frames:F1},attach:{attach / frames:F0}/us:{PerUnit(attachTicks, attach)},kept:{kept / frames:F0},lookups:{lookups / frames:F0}/us:{PerUnit(lookupTicks, lookups)}");
    }

    public static void OnFrame() => Interlocked.Increment(ref _frames);

    public static void NoteDraw(int gate)
    {
        _kind = KindDraw;
        _gate = gate;
    }

    public static void NoteDispatch() => _kind = KindDispatch;

    public static void AddRecordTicks(long ticks) => _itemRecordTicks += ticks;

    // SHARPEMU_FRONT_RESOLVE_POOL: the render thread's scan after each item (gate, hand-off to a worker), outside every item.
    public static void AddLookaheadTicks(long ticks) => _lookaheadTicks += ticks;

    // After the draw: the rendering scope it ended in and the image lookup generation.
    public static void NoteDrawScope(DrawScopeToken scope)
    {
        if (!scope.IsActive)
            return;
        if (_lastScope.IsActive)
        {
            if (scope.Scope != _lastScope.Scope)
                _scopeChanges++;
            if (scope.Lookups != _lastScope.Lookups)
                _lookupChanges++;
            if (scope.Scope != _lastScope.Scope && _gate == GateOk)
                ScopeRuns.End(BreakScope);
        }

        _lastScope = scope;
    }

    public static void EndItem(Delegate work, long ticks)
    {
        var record = _itemRecordTicks;
        _itemRecordTicks = 0;
        switch (_kind)
        {
            case KindDraw:
                _draws++;
                _drawTicks += ticks;
                _drawRecordTicks += record;
                GateDraws[_gate]++;
                GateTicks[_gate] += ticks;
                GateRecordTicks[_gate] += record;
                if (_gate == GateOk)
                {
                    GateRuns.Add(ticks);
                    ScopeRuns.Add(ticks);
                    SliceRuns.Add(ticks);
                    DrawBarrierRuns.Add(ticks);
                }
                else
                {
                    GateRuns.End(BreakIneligible);
                    ScopeRuns.End(BreakIneligible);
                    SliceRuns.End(BreakIneligible);
                    DrawBarrierRuns.End(BreakIneligible);
                }

                break;
            case KindDispatch:
                _dispatches++;
                _dispatchTicks += ticks;
                _dispatchRecordTicks += record;
                DrawBarrierRuns.End(BreakDispatch);
                if (RenderRecorder.NeedsSerialDispatch)
                {
                    GateRuns.End(BreakDispatch);
                    ScopeRuns.End(BreakDispatch);
                    SliceRuns.End(BreakDispatch);
                }
                else
                {
                    if (GateRuns.Length != 0)
                        _dispatchesInRuns++;
                    SliceRuns.Add(ticks);
                }

                break;
            default:
                _others++;
                _otherTicks += ticks;
                if (ReferenceEquals(work, BarrierItem))
                {
                    SliceRuns.Add(ticks);
                    DrawBarrierRuns.Add(ticks);
                }
                else
                {
                    SliceRuns.End(BreakOther);
                    DrawBarrierRuns.End(BreakOther);
                }

                var ended = GateRuns.End(BreakOther);
                ScopeRuns.End(BreakOther);
                if (ended && (++_breakerSample & BreakerSampleMask) == 0)
                {
                    var name = $"{work.Method.DeclaringType?.Name}.{work.Method.Name}";
                    var entry = Breakers.GetValueOrDefault(name);
                    Breakers[name] = (entry.Count + BreakerSampleMask + 1, entry.Ticks + ticks * (BreakerSampleMask + 1));
                }

                break;
        }

        _kind = KindOther;
        var now = Stopwatch.GetTimestamp();
        if (now - _windowStart >= 5 * Stopwatch.Frequency)
            Report(now);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static readonly string[] InlineNames = ["short", "stolen", "invalid", "serial", "tail"];
    private static readonly string[] FlushNames = ["item", "exit", "read", "record", "after", "prep"];

    // [local] SHARPEMU_FRONT_SLICES, per frame: slices handed out and their mean items, items joined (recorded by a worker),
    // the render thread's ms per part, the workers' ms, items recorded here after all by reason, joins by what caused them,
    // and the reasons a worker stopped (Serial).
    internal static void AppendSlices(StringBuilder text, double frames)
    {
        var stats = FrontSliceStats.Shared;
        text.Append(CultureInfo.InvariantCulture,
            $" front_slices=n:{stats.Slices / frames:F1},items_mean:{(stats.Slices == 0 ? 0 : stats.Items / (double)stats.Slices):F1},items:{stats.Items / frames:F0},joined:{stats.Joined / frames:F0},segments:{stats.Segments / frames:F1}");
        text.Append(CultureInfo.InvariantCulture,
            $" slice_handoff_ms={Ms(SliceHandoffTicks) / frames:F2} slice_join_wait_ms={Ms(SliceWaitTicks) / frames:F2} slice_rest_wait_ms={Ms(SliceRestWaitTicks) / frames:F2} slice_join_ms={Ms(SliceJoinTicks) / frames:F2} slice_inline_ms={Ms(SliceInlineTicks) / frames:F2} slice_worker_ms={Ms(Interlocked.Exchange(ref _sliceWorkerTicks, 0)) / frames:F2}");
        text.Append(CultureInfo.InvariantCulture, $" slice_inline_us={PerUnit(SliceInlineTicks, SliceInlineItems)}");
        SliceInlineItems = 0;
        AppendWorker(text, frames);
        text.Append(" slice_inline=");
        for (var index = 0; index < InlineNames.Length; index++)
        {
            text.Append(index == 0 ? "" : ",").Append(InlineNames[index]).Append(':').Append((stats.Inline[index] / frames).ToString("F1", CultureInfo.InvariantCulture));
            stats.Inline[index] = 0;
        }

        text.Append(" slice_flushes=");
        for (var index = 0; index < FlushNames.Length; index++)
        {
            text.Append(index == 0 ? "" : ",").Append(FlushNames[index]).Append(':').Append((stats.Flushes[index] / frames).ToString("F1", CultureInfo.InvariantCulture));
            stats.Flushes[index] = 0;
        }

        text.Append(" slice_serial=[");
        text.AppendJoin(",", stats.SerialReasons.OrderByDescending(pair => pair.Value).Take(8)
            .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key.Replace(' ', '_')}:{pair.Value / frames:F1}")));
        text.Append(']');
        stats.SerialReasons.Clear();
        stats.Slices = stats.Items = stats.Joined = stats.Segments = 0;
        SliceHandoffTicks = SliceWaitTicks = SliceRestWaitTicks = SliceJoinTicks = SliceInlineTicks = 0;
        // [local] Why the workers' image lookups failed (totals for the window); with PROFILE_RENDER the slice report takes them.
        if (!RenderPhaseProfile.Enabled)
            text.Append(' ').Append(SharpEmu.Libs.Gpu.Images.GuestImageCache.TakeSliceLookupReport());
    }

    private static void Report(long now)
    {
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        _windowStart = now;
        var frames = Interlocked.Exchange(ref _frames, 0);
        var f = (double)Math.Max(frames, 1);
        var text = new StringBuilder("[PERF][PARRES]");
        text.Append(CultureInfo.InvariantCulture,
            $" window_s={seconds:F1} frames={frames} items={(_draws + _dispatches + _others) / f:F0} draws={_draws / f:F0} dispatches={_dispatches / f:F0} other={_others / f:F0}");
        text.Append(CultureInfo.InvariantCulture,
            $" draw_ms={Ms(_drawTicks) / f:F2} draw_resolve_ms={Ms(_drawTicks - _drawRecordTicks) / f:F2} draw_record_ms={Ms(_drawRecordTicks) / f:F2}");
        text.Append(CultureInfo.InvariantCulture,
            $" dispatch_ms={Ms(_dispatchTicks) / f:F2} dispatch_record_ms={Ms(_dispatchRecordTicks) / f:F2} other_ms={Ms(_otherTicks) / f:F2} lookahead_ms={Ms(_lookaheadTicks) / f:F2}");
        text.Append(" gate=");
        for (var gate = 0; gate < GateCount; gate++)
        {
            text.Append(gate == 0 ? "" : ",").Append(GateNames[gate]).Append(CultureInfo.InvariantCulture,
                $":{GateDraws[gate] / f:F0}/{Ms(GateTicks[gate]) / f:F2}ms/rec{Ms(GateRecordTicks[gate]) / f:F2}ms");
            GateDraws[gate] = 0;
            GateTicks[gate] = 0;
            GateRecordTicks[gate] = 0;
        }

        text.Append(CultureInfo.InvariantCulture, $" dispatch_serial={(RenderRecorder.NeedsSerialDispatch ? 1 : 0)} dispatches_in_runs={_dispatchesInRuns / f:F1}");
        GateRuns.Append(text, "runs", f);
        ScopeRuns.Append(text, "scope_runs", f);
        SliceRuns.Append(text, "slice_runs", f);
        DrawBarrierRuns.Append(text, "draw_barrier_runs", f);
        text.Append(CultureInfo.InvariantCulture, $" scope_changes={_scopeChanges / f:F1} lookup_generation_changes={_lookupChanges / f:F1}");
        if (VulkanVideoPresenter.FrontSlicesEnabled)
            AppendSlices(text, f);
        text.Append(" breakers=[");
        text.AppendJoin(",", Breakers.OrderByDescending(pair => pair.Value.Count).Take(10)
            .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}:{pair.Value.Count / f:F1}/{Ms(pair.Value.Ticks) / f:F2}ms")));
        text.Append(']');
        Breakers.Clear();
        _draws = _dispatches = _others = 0;
        _drawTicks = _dispatchTicks = _otherTicks = _drawRecordTicks = _dispatchRecordTicks = _lookaheadTicks = 0;
        _scopeChanges = _lookupChanges = _dispatchesInRuns = 0;
        Console.Error.WriteLine(text.ToString());
        if (VulkanVideoPresenter.FrontResolvePoolEnabled && !RenderPhaseProfile.Enabled)
            Console.Error.WriteLine(SharpEmu.Libs.Agc.AgcExports.TakeResolvePoolReport());
    }
}
