// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_BDA_COST_PROBE=1 (off by default): where the bda and res_ranges steps of [PERF][DRAWCOST] go for a
// dispatch, per program. Measured on the render thread only (the recorder's dispatches when not on a worker; the
// thread that begins the first dispatch owns the probe), from the bindings to the end of the device-address ranges:
//
//   bind_host       the host's PrepareBindings (already split by DRAWCOST's bind_* steps; DRAWCOST's bda starts at its
//                   last bind_* lap, just before the host returns)
//   bind_note       the recorder's tail of PrepareBindings (NoteSampledTextures: one MipStatistics note per sampled image)
//   bda_prelude     PrepareDeviceAddresses up to its span list (RequirePreparation, BufferUploadProfile, DeviceAddressSpans)
//   bda_windows     the NoteDeviceAddressWindow loop over the stages' ranges
//   bda_gate        the per-submission gate (SHARPEMU_BDA_SWEEP_PER_SUBMIT): to its return or to PrepareBda
//   bda_touch       PrepareBda's recency touch (once per retirement tick or mapping)
//   bda_walk        PrepareBda's CPU-dirty sweep walk (once per CPU-dirty epoch or mapping)
//   bda_close       the sweep's close (cleared runs, staged uploads, barrier)
//   bda_exit        the rest, back in the recorder (scope disposal; all of it for a program without device addresses)
//   res_buffers     BindResources up to ObtainDeviceAddressRanges (upload batch, BindBuffers); DRAWCOST's res_buffers
//   range_check     per taken range: the checks before it (planned, size, bounds, CanRead, ClampMappedSize); a
//                   skipped range's checks land in the next taken range's range_check or in range_tail
//   w_obtain        written range: ObtainBuffer(isWritten) with the trace/table-dirty checks before it
//   w_invalidate    written range: InvalidateMemoryCopiesFromGpu
//   r_publications  read range: CompleteOverlappingPublications
//   r_find          read range: FindBuffer
//   r_sync          read range: SynchronizeBuffersInRange
//   range_tail      after the last taken range to the end of ObtainDeviceAddressRanges
//
// range_check .. range_tail are DRAWCOST's res_ranges. A step is the time since the lap before it (one timestamp per
// boundary). Report once per DRAWCOST window when SHARPEMU_DRAWCOST_PROBE=1 (at the next dispatch after it), else every
// 5 s:
//
//   [PERF][BDA_COST] window_s=5.0 frames=25 dispatches=35000 dispatches_per_frame=1400 uses_bda=0.62 gated=0.60
//       prepared=0.02 touches=3 sweeps=20 spans_rebuilt=0 ranges_per=3.10 written_per=1.20 read_per=1.90
//       skipped_per=0.20 mb_written_per=12.30 mb_read_per=40.10 us_per_dispatch=9.40 ms_per_frame=13.16 programs=210
//       steps=<step>=<us per pass>/<ms per frame>/<passes per dispatch>,...
//   [PERF][BDA_COST_SIZE] kind=written|read le64k=<ranges>/<us per range>/<mb per range>,le1m=...,le16m=...,gt16m=...
//   [PERF][BDA_COST_PROG] hash=0x... dispatches=N ranges_per=N mb_per=N us_per=N ms_per_frame=N <step>=<us total>,...
//
// uses_bda, gated and prepared are per dispatch (programs with device addresses; preparations the per-submission gate
// answered; preparations that reached PrepareBda); touches and sweeps are counts in the window. The size line takes
// each taken range's own steps (range_check to its last op) by the range's size: a cost that grows with the bucket is
// size-proportional, a flat one is per range. The program lines are the 12 programs with the most time, sorted.
internal static class BdaCostProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_BDA_COST_PROBE") == "1";

    private static readonly long ReportTicks = 5 * Stopwatch.Frequency;

    internal enum Step
    {
        BindHost,
        BindNote,
        BdaPrelude,
        BdaWindows,
        BdaGate,
        BdaTouch,
        BdaWalk,
        BdaClose,
        BdaExit,
        ResBuffers,
        RangeCheck,
        WObtain,
        WInvalidate,
        RPublications,
        RFind,
        RSync,
        RangeTail,
        Count,
    }

    internal enum Counter
    {
        UsesBda,
        Gated,
        Prepared,
        Touches,
        Sweeps,
        SpansRebuilt,
        Skipped,
        Count,
    }

    private static readonly BdaCostTally _tally = new();
    private static int _thread;
    private static bool _active;
    private static int _slot;
    private static long _last;
    private static long _rangeStart;
    private static long _windowStart;
    private static long _frames;
    private static volatile bool _reportRequested;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Begin(ulong hash)
    {
        if (Enabled)
        {
            BeginCore(hash);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BeginCore(ulong hash)
    {
        var thread = Environment.CurrentManagedThreadId;
        if (_thread == 0)
        {
            _thread = thread;
            _windowStart = Stopwatch.GetTimestamp();
        }

        if (thread != _thread)
        {
            return;
        }

        _slot = _tally.Begin(hash);
        _active = true;
        _last = Stopwatch.GetTimestamp();
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
        _tally.AddStep(_slot, step, now - _last);
        _last = now;
        if (step == Step.RangeCheck)
        {
            _rangeStart = now;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Count(Counter counter)
    {
        if (Enabled && _active)
        {
            CountCore(counter);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CountCore(Counter counter)
    {
        if (Environment.CurrentManagedThreadId == _thread)
        {
            _tally.Count(counter);
        }
    }

    // After the last op lap of a taken range: its kind, size and own time (from its range_check lap).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NoteRange(bool written, ulong bytes)
    {
        if (Enabled && _active)
        {
            NoteRangeCore(written, bytes);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NoteRangeCore(bool written, ulong bytes)
    {
        if (Environment.CurrentManagedThreadId == _thread)
        {
            _tally.AddRange(_slot, written, bytes, _last - _rangeStart);
        }
    }

    // The end of the dispatch's ranges; a no-op for draws and worker hosts (not active, or not the owner).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void End()
    {
        if (Enabled && _active)
        {
            EndCore();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EndCore()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            return;
        }

        LapCore(Step.RangeTail);
        _active = false;
        var now = _last;
        if (_reportRequested || (!DrawCostProbe.Enabled && now - _windowStart >= ReportTicks))
        {
            _reportRequested = false;
            var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
            var lines = new List<string>(16);
            _tally.Format(lines, seconds, Interlocked.Exchange(ref _frames, 0), Stopwatch.Frequency);
            foreach (var line in lines)
            {
                Console.Error.WriteLine(line);
            }

            _tally.Reset();
            _windowStart = Stopwatch.GetTimestamp();
        }
    }

    // Called once per presented frame, from any thread.
    internal static void NoteFrame()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _frames);
        }
    }

    // DRAWCOST's window closed (on its thread): the next dispatch's end reports this one on the owner thread.
    internal static void Report()
    {
        if (Enabled)
        {
            _reportRequested = true;
        }
    }

    // For tests: clears the thread ownership and the window.
    internal static void ResetForTests()
    {
        _thread = 0;
        _active = false;
        _reportRequested = false;
        _frames = 0;
        _tally.Reset();
    }
}

// [local] SHARPEMU_BDA_COST_PROBE: the window's sums, per program slot (a hash's slot is kept for the window; past
// MaxPrograms the rest share an overflow slot printed as hash=other), and the report lines. No allocation per dispatch
// once a program has a slot.
internal sealed class BdaCostTally
{
    internal const int MaxPrograms = 256;
    internal const int TopPrograms = 12;
    internal const int Buckets = 4;

    private const int Steps = (int)BdaCostProbe.Step.Count;
    private const int Counters = (int)BdaCostProbe.Counter.Count;
    private const int Slots = MaxPrograms + 1;
    private const int Overflow = MaxPrograms;

    private static readonly string[] StepNames =
    [
        "bind_host", "bind_note", "bda_prelude", "bda_windows", "bda_gate", "bda_touch", "bda_walk", "bda_close", "bda_exit",
        "res_buffers", "range_check", "w_obtain", "w_invalidate", "r_publications", "r_find", "r_sync", "range_tail",
    ];

    private static readonly string[] BucketNames = ["le64k", "le1m", "le16m", "gt16m"];

    private readonly Dictionary<ulong, int> _slots = new(MaxPrograms);
    private readonly ulong[] _hashes = new ulong[Slots];
    private readonly long[] _ticks = new long[Slots * Steps];
    private readonly long[] _passes = new long[Slots * Steps];
    private readonly long[] _dispatches = new long[Slots];
    private readonly long[] _written = new long[Slots];
    private readonly long[] _read = new long[Slots];
    private readonly long[] _writtenBytes = new long[Slots];
    private readonly long[] _readBytes = new long[Slots];
    private readonly long[] _counts = new long[Counters];
    // [kind * Buckets + bucket], kind 0 written, 1 read.
    private readonly long[] _bucketRanges = new long[2 * Buckets];
    private readonly long[] _bucketTicks = new long[2 * Buckets];
    private readonly long[] _bucketBytes = new long[2 * Buckets];

    internal static string NameOf(BdaCostProbe.Step step) => StepNames[(int)step];

    internal static int BucketOf(ulong bytes) =>
        bytes <= 64UL << 10 ? 0 : bytes <= 1UL << 20 ? 1 : bytes <= 16UL << 20 ? 2 : 3;

    internal int Begin(ulong hash)
    {
        if (!_slots.TryGetValue(hash, out var slot))
        {
            slot = _slots.Count < MaxPrograms ? _slots.Count : Overflow;
            if (slot != Overflow)
            {
                _slots.Add(hash, slot);
                _hashes[slot] = hash;
            }
        }

        _dispatches[slot]++;
        return slot;
    }

    internal void AddStep(int slot, BdaCostProbe.Step step, long ticks)
    {
        var index = slot * Steps + (int)step;
        _ticks[index] += ticks;
        _passes[index]++;
    }

    internal void Count(BdaCostProbe.Counter counter) => _counts[(int)counter]++;

    internal void AddRange(int slot, bool written, ulong bytes, long ticks)
    {
        if (written)
        {
            _written[slot]++;
            _writtenBytes[slot] += (long)bytes;
        }
        else
        {
            _read[slot]++;
            _readBytes[slot] += (long)bytes;
        }

        var bucket = (written ? 0 : Buckets) + BucketOf(bytes);
        _bucketRanges[bucket]++;
        _bucketTicks[bucket] += ticks;
        _bucketBytes[bucket] += (long)bytes;
    }

    private long SlotTicks(int slot)
    {
        var total = 0L;
        for (var step = 0; step < Steps; step++)
        {
            total += _ticks[slot * Steps + step];
        }

        return total;
    }

    internal void Format(List<string> lines, double seconds, long frames, long frequency)
    {
        var dispatches = 0L;
        long written = 0, read = 0, writtenBytes = 0, readBytes = 0, ticks = 0;
        for (var slot = 0; slot < Slots; slot++)
        {
            dispatches += _dispatches[slot];
            written += _written[slot];
            read += _read[slot];
            writtenBytes += _writtenBytes[slot];
            readBytes += _readBytes[slot];
            ticks += SlotTicks(slot);
        }

        if (dispatches == 0)
        {
            return;
        }

        var perFrame = frames > 0 ? frames : 1;
        var toUs = 1e6 / frequency;
        var perDispatch = 1.0 / dispatches;
        const double Mb = 1.0 / (1 << 20);
        var line = new StringBuilder(1024);
        line.Append(FormattableString.Invariant(
            $"[PERF][BDA_COST] window_s={seconds:F1} frames={frames} dispatches={dispatches} dispatches_per_frame={dispatches / (double)perFrame:F0} "));
        line.Append(FormattableString.Invariant(
            $"uses_bda={Of(BdaCostProbe.Counter.UsesBda) * perDispatch:F2} gated={Of(BdaCostProbe.Counter.Gated) * perDispatch:F2} prepared={Of(BdaCostProbe.Counter.Prepared) * perDispatch:F2} "));
        line.Append(FormattableString.Invariant(
            $"touches={Of(BdaCostProbe.Counter.Touches)} sweeps={Of(BdaCostProbe.Counter.Sweeps)} spans_rebuilt={Of(BdaCostProbe.Counter.SpansRebuilt)} "));
        line.Append(FormattableString.Invariant(
            $"ranges_per={(written + read) * perDispatch:F2} written_per={written * perDispatch:F2} read_per={read * perDispatch:F2} skipped_per={Of(BdaCostProbe.Counter.Skipped) * perDispatch:F2} "));
        line.Append(FormattableString.Invariant(
            $"mb_written_per={writtenBytes * Mb * perDispatch:F2} mb_read_per={readBytes * Mb * perDispatch:F2} "));
        line.Append(FormattableString.Invariant(
            $"us_per_dispatch={ticks * toUs * perDispatch:F2} ms_per_frame={ticks * toUs / 1000 / perFrame:F2} programs={_slots.Count}{(_dispatches[Overflow] != 0 ? "+" : "")} steps="));
        var first = true;
        for (var step = 0; step < Steps; step++)
        {
            long stepTicks = 0, passes = 0;
            for (var slot = 0; slot < Slots; slot++)
            {
                stepTicks += _ticks[slot * Steps + step];
                passes += _passes[slot * Steps + step];
            }

            if (passes == 0)
            {
                continue;
            }

            line.Append(first ? "" : ",");
            first = false;
            line.Append(FormattableString.Invariant(
                $"{StepNames[step]}={stepTicks * toUs / passes:F2}/{stepTicks * toUs / 1000 / perFrame:F2}/{passes * perDispatch:F2}"));
        }

        lines.Add(line.ToString());
        for (var kind = 0; kind < 2; kind++)
        {
            line.Clear();
            line.Append(kind == 0 ? "[PERF][BDA_COST_SIZE] kind=written " : "[PERF][BDA_COST_SIZE] kind=read ");
            for (var bucket = 0; bucket < Buckets; bucket++)
            {
                var index = kind * Buckets + bucket;
                var ranges = _bucketRanges[index];
                line.Append(bucket == 0 ? "" : ",");
                line.Append(FormattableString.Invariant(
                    $"{BucketNames[bucket]}={ranges}/{(ranges == 0 ? 0 : _bucketTicks[index] * toUs / ranges):F2}/{(ranges == 0 ? 0 : _bucketBytes[index] * Mb / ranges):F2}"));
            }

            lines.Add(line.ToString());
        }

        Span<int> top = stackalloc int[TopPrograms];
        Span<long> topTicks = stackalloc long[TopPrograms];
        var count = 0;
        for (var slot = 0; slot < Slots; slot++)
        {
            if (_dispatches[slot] == 0)
            {
                continue;
            }

            var slotTicks = SlotTicks(slot);
            var at = count < TopPrograms ? count++ : TopPrograms;
            if (at == TopPrograms)
            {
                if (slotTicks <= topTicks[TopPrograms - 1])
                {
                    continue;
                }

                at = TopPrograms - 1;
            }

            while (at > 0 && topTicks[at - 1] < slotTicks)
            {
                top[at] = top[at - 1];
                topTicks[at] = topTicks[at - 1];
                at--;
            }

            top[at] = slot;
            topTicks[at] = slotTicks;
        }

        for (var rank = 0; rank < count; rank++)
        {
            var slot = top[rank];
            var slotDispatches = _dispatches[slot];
            line.Clear();
            line.Append(slot == Overflow ? "[PERF][BDA_COST_PROG] hash=other " : FormattableString.Invariant($"[PERF][BDA_COST_PROG] hash=0x{_hashes[slot]:X16} "));
            line.Append(FormattableString.Invariant(
                $"dispatches={slotDispatches} ranges_per={(_written[slot] + _read[slot]) / (double)slotDispatches:F2} mb_per={(_writtenBytes[slot] + _readBytes[slot]) * Mb / slotDispatches:F2} "));
            line.Append(FormattableString.Invariant(
                $"us_per={topTicks[rank] * toUs / slotDispatches:F2} ms_per_frame={topTicks[rank] * toUs / 1000 / perFrame:F2}"));
            for (var step = 0; step < Steps; step++)
            {
                var stepTicks = _ticks[slot * Steps + step];
                if (_passes[slot * Steps + step] != 0)
                {
                    line.Append(FormattableString.Invariant($" {StepNames[step]}={stepTicks * toUs:F0}"));
                }
            }

            lines.Add(line.ToString());
        }
    }

    private long Of(BdaCostProbe.Counter counter) => _counts[(int)counter];

    internal void Reset()
    {
        _slots.Clear();
        Array.Clear(_hashes);
        Array.Clear(_ticks);
        Array.Clear(_passes);
        Array.Clear(_dispatches);
        Array.Clear(_written);
        Array.Clear(_read);
        Array.Clear(_writtenBytes);
        Array.Clear(_readBytes);
        Array.Clear(_counts);
        Array.Clear(_bucketRanges);
        Array.Clear(_bucketTicks);
        Array.Clear(_bucketBytes);
    }
}
