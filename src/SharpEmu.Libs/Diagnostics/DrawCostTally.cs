// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Kind = SharpEmu.Libs.Diagnostics.DrawCostProbe.Kind;
using Step = SharpEmu.Libs.Diagnostics.DrawCostProbe.Step;
using Counter = SharpEmu.Libs.Diagnostics.DrawCostProbe.Counter;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_DRAWCOST_PROBE: the sums of one report window, per item kind, and the open item. DrawCostProbe owns
// the thread, the sampling, the clock and the window; this holds what it adds up and writes the kind lines.
//
// SHARPEMU_DRAWCOST_DEPTH (DrawCostDepth): a draw's kind is known only when its targets are (Classify). Until then the
// laps and counts of a timed draw are kept on the side (one slot per step and counter, with a mask of those touched)
// and go to the kind it is classified as, or to draw when it never is; so every step of a depth-only draw is counted
// under draw_depth, the ones before its classification too. Without the switch nothing is kept on the side.
internal sealed class DrawCostTally
{
    internal const int Steps = (int)Step.Count;
    internal const int Counters = (int)Counter.Count;
    internal const int Kinds = (int)Kind.Count;

    private static readonly string[] KindNames = ["draw", "draw_depth", "dispatch"];
    private static readonly string[] StepNames = Names<Step>(Steps);
    private static readonly string[] CounterNames = Names<Counter>(Counters);

    private readonly bool _depthSplit;
    private readonly long[] _stepTicks = new long[Kinds * Steps];
    private readonly long[] _stepPasses = new long[Kinds * Steps];
    private readonly long[] _stepBytes = new long[Kinds * Steps]; // SHARPEMU_DRAWCOST_ALLOC
    private readonly long[] _counts = new long[Kinds * Counters];
    private readonly long[] _items = new long[Kinds];
    private readonly long[] _timed = new long[Kinds];
    private readonly long[] _itemTicks = new long[Kinds];

    // The open item: its kind (draw until classified), whether it is timed, and the laps and counts kept on the side.
    private bool _open;
    private bool _timedItem;
    private bool _classified;
    private bool _pending;
    private Kind _kind;
    private long _start;
    private long _last;
    private readonly long[] _pendingTicks = new long[Steps];
    private readonly long[] _pendingPasses = new long[Steps];
    private readonly long[] _pendingBytes = new long[Steps];
    private readonly long[] _pendingCounts = new long[Counters];
    private ulong _pendingSteps;
    private ulong _pendingCounters;

    internal DrawCostTally(bool depthSplit)
    {
        // The pending masks hold one bit per step and per counter.
        if (Steps > 64 || Counters > 64)
        {
            throw new InvalidOperationException("DrawCostTally keeps at most 64 steps and 64 counters.");
        }

        _depthSplit = depthSplit;
    }

    internal static string NameOf(Step step) => StepNames[(int)step];

    internal static string NameOf(Counter counter) => CounterNames[(int)counter];

    internal static string NameOf(Kind kind) => KindNames[(int)kind];

    internal Kind CurrentKind => _kind;

    internal bool Open => _open;

    internal long Last => _last;

    internal long Items(Kind kind) => _items[(int)kind];

    internal long Timed(Kind kind) => _timed[(int)kind];

    internal void Begin(Kind kind, bool timed, long now)
    {
        _items[(int)kind]++;
        _open = true;
        _timedItem = timed;
        _classified = false;
        _kind = kind;
        _pending = _depthSplit && timed && kind == Kind.Draw;
        _start = _last = now;
    }

    // The time since the last lap goes to the step; bytes is what the thread allocated in it (SHARPEMU_DRAWCOST_ALLOC).
    internal void Lap(Step step, long now, long bytes = 0)
    {
        var ticks = now - _last;
        _last = now;
        var index = (int)step;
        if (_pending)
        {
            _pendingTicks[index] += ticks;
            _pendingPasses[index]++;
            _pendingBytes[index] += bytes;
            _pendingSteps |= 1UL << index;
            return;
        }

        var slot = (int)_kind * Steps + index;
        _stepTicks[slot] += ticks;
        _stepPasses[slot]++;
        _stepBytes[slot] += bytes;
    }

    internal void Count(Counter counter, long amount)
    {
        var index = (int)counter;
        if (_pending)
        {
            _pendingCounts[index] += amount;
            _pendingCounters |= 1UL << index;
            return;
        }

        _counts[(int)_kind * Counters + index] += amount;
    }

    // The draw's targets are known: a depth-only draw becomes draw_depth, its item and what it has counted so far.
    // Only the first call of an item counts (a CPU-argument indirect draw resolves its targets again inside the item):
    // true when this call classified it.
    internal bool Classify(bool depthOnly)
    {
        if (!_depthSplit || !_open || _classified || _kind != Kind.Draw)
        {
            return false;
        }

        _classified = true;
        if (depthOnly)
        {
            _items[(int)Kind.Draw]--;
            _items[(int)Kind.DrawDepth]++;
            _kind = Kind.DrawDepth;
        }

        Flush();
        return true;
    }

    // Closes the item. A timed item adds its time and the counts taken at its end (allocated bytes, ring records).
    internal void End(long allocatedBytes = 0, long ringRecords = 0)
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        Flush();
        if (!_timedItem)
        {
            return;
        }

        var kind = (int)_kind;
        _timed[kind]++;
        _itemTicks[kind] += _last - _start;
        _counts[kind * Counters + (int)Counter.AllocatedBytes] += allocatedBytes;
        _counts[kind * Counters + (int)Counter.RingRecords] += ringRecords;
    }

    private void Flush()
    {
        if (!_pending)
        {
            return;
        }

        _pending = false;
        var kind = (int)_kind;
        for (var mask = _pendingSteps; mask != 0; mask &= mask - 1)
        {
            var index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            var slot = kind * Steps + index;
            _stepTicks[slot] += _pendingTicks[index];
            _stepPasses[slot] += _pendingPasses[index];
            _stepBytes[slot] += _pendingBytes[index];
            _pendingTicks[index] = 0;
            _pendingPasses[index] = 0;
            _pendingBytes[index] = 0;
        }

        for (var mask = _pendingCounters; mask != 0; mask &= mask - 1)
        {
            var index = System.Numerics.BitOperations.TrailingZeroCount(mask);
            _counts[kind * Counters + index] += _pendingCounts[index];
            _pendingCounts[index] = 0;
        }

        _pendingSteps = 0;
        _pendingCounters = 0;
    }

    // One line per kind with items in the window:
    //   [PERF][DRAWCOST] kind=<draw|draw_depth|dispatch> items_per_frame=N timed=N us_per_item=U ms_per_frame=M
    //       steps=<name>=<us per pass>/<ms per frame>/<passes per item>,... counts=<name>=<per timed item>,...
    internal void Format(List<string> lines, long frames, long frequency, bool allocPerStep)
    {
        var toMs = 1000.0 / frequency;
        var perFrame = frames > 0 ? frames : 1;
        for (var kind = 0; kind < Kinds; kind++)
        {
            var items = _items[kind];
            var timed = _timed[kind];
            if (items == 0)
            {
                continue;
            }

            var scale = timed == 0 ? 0 : items / (double)timed;
            var line = new StringBuilder(2048);
            line.Append(FormattableString.Invariant(
                $"[PERF][DRAWCOST] kind={KindNames[kind]} items_per_frame={items / (double)perFrame:F0} timed={timed} "));
            line.Append(FormattableString.Invariant(
                $"us_per_item={(timed == 0 ? 0 : _itemTicks[kind] * toMs * 1000 / timed):F2} ms_per_frame={_itemTicks[kind] * toMs * scale / perFrame:F2} steps="));
            var first = true;
            for (var step = 0; step < Steps; step++)
            {
                var slot = kind * Steps + step;
                var passes = _stepPasses[slot];
                if (passes == 0)
                {
                    continue;
                }

                if (!first)
                {
                    line.Append(',');
                }

                first = false;
                line.Append(FormattableString.Invariant(
                    $"{StepNames[step]}={_stepTicks[slot] * toMs * 1000 / passes:F2}/{_stepTicks[slot] * toMs * scale / perFrame:F2}/{passes / (double)Math.Max(timed, 1):F2}"));
            }

            line.Append(" counts=");
            first = true;
            for (var counter = 0; counter < Counters; counter++)
            {
                var count = _counts[kind * Counters + counter];
                if (count == 0)
                {
                    continue;
                }

                if (!first)
                {
                    line.Append(',');
                }

                first = false;
                line.Append(FormattableString.Invariant($"{CounterNames[counter]}={count / (double)Math.Max(timed, 1):F2}"));
            }

            for (var step = 0; allocPerStep && step < Steps; step++) // SHARPEMU_DRAWCOST_ALLOC
            {
                var bytes = _stepBytes[kind * Steps + step];
                if (bytes != 0)
                    line.Append(FormattableString.Invariant($",alloc_{StepNames[step]}={bytes / (double)Math.Max(timed, 1):F2}"));
            }

            lines.Add(line.ToString());
        }
    }

    // The window's sums; an open item stays open.
    internal void Clear()
    {
        Array.Clear(_stepTicks);
        Array.Clear(_stepPasses);
        Array.Clear(_stepBytes);
        Array.Clear(_counts);
        Array.Clear(_items);
        Array.Clear(_timed);
        Array.Clear(_itemTicks);
    }

    internal void Reset()
    {
        Clear();
        _open = false;
        _pending = false;
        _pendingSteps = 0;
        _pendingCounters = 0;
        Array.Clear(_pendingTicks);
        Array.Clear(_pendingPasses);
        Array.Clear(_pendingBytes);
        Array.Clear(_pendingCounts);
    }

    private static string[] Names<T>(int count) where T : struct, Enum
    {
        var names = new string[count];
        for (var index = 0; index < count; index++)
        {
            names[index] = Snake(((T)(object)index).ToString());
        }

        return names;
    }

    internal static string Snake(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index != 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
