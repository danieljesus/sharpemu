// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_LITE_OBTAIN_PROBE=1 (off by default): the gate of a light read obtain (LITE_OBTAIN, A-1 in
// sharpemu-rediseno-draws.md §8.1), in shadow. For every read obtain (GuestBufferCache.ObtainBuffer with isWritten
// false) on the recording thread inside a draw or a dispatch, and every V# FindBuffers resolves, the light path runs
// first and its answer is kept apart; the real obtain or lookup then runs as before and its answer is what the item
// uses. Nothing recorded changes.
//
// The light obtain answers "this range is clean, it is this buffer + offset" from the checks the real one ends with,
// cheapest first, and falls to the real path on the first special case:
//   pub       a pending occlusion dump overlaps the range (the real read completes or overlays it)
//   unmapped  no registered buffer owns the range's first 16 KB page (the real one creates or merges one)
//   cross     the owner of the first page does not contain the whole range
//   cpu/hot   the range may have CPU-dirty pages (hot: on CPU-write-hot pages, the stream ring's candidates)
//   img       a texel read of pages that may hold image bytes
//   fast      the page owner and its offset
// Each check reads a structure the cache already keeps (the publications' dump list, the registry's page owners, the
// tracker's dirty summary, the image page owners); a new page map and one bitmap of special pages would answer the
// same in fewer reads, so lite_ns is an upper bound of what A-1 costs.
//
// The real obtain did work when, around it, the cache counted a synchronous upload, the registry's epoch moved (a
// buffer created, merged or retired), the answer came from the stream ring, the presenter completed or overlaid a
// publication, or a texel range was copied from an image. agree = the light path said fast and the real one returned
// the same buffer and offset with no work; fast_wrong = it said fast and the real one returned something else or did
// work (must be 0; the first cases are printed as [GPU][LITE_OBTAIN_WRONG]); slow = it fell to the real path, by
// cause, and slow_clean is the part of those where the real one did no work either.
//
// A V# of FindBuffers: the light lookup is the page owner of [address, address + footprint) before the real lookup
// (memo, ring-slot reuse or FindBuffer, after the clamp to the mapped size); find_wrong = it found an owner and the
// real answer was another buffer or created one. Every storage buffer a stage binds is classified: rt (the program is
// one of the ray-tracing/BVH passes 0x8395, 0x5981, 0x6A53, 0x56D6, 0x743E) or, outside those, raw (read only, not
// formatted, not atomic: what a page-table read can serve; BufferResourceInfo has no typed flag, so typed raw loads
// count as raw).
//
// One [PERF][LITE_OBTAIN] line per kind (draw, dispatch) per window, with the [PERF][DRAWCOST] window when that probe is
// on (SHARPEMU_LITE_OBTAIN_REPORT_S, 5 by default, otherwise):
//
//   [PERF][LITE_OBTAIN] window_s=5.0 frames=25 kind=draw items_per_frame=3793 obtains_per_item=7.10 fast=82.1
//       fast_wrong=0 fast_wrong_total=0 slow=cpu:4.0,pub:0.0,img:0.2,hot:9.1,cross:0.3,unmapped:4.3 slow_clean=2.0
//       lite_ns=31 real_ns=228 lite_fast_ns=29 real_fast_ns=190 finds_per_item=1.40 find_fast=97.0 find_wrong=0
//       find_slow=cross:0.5,unmapped:2.5 find_lite_ns=14 slots_per_item=6.65 eligible_raw=88.0 rt_excluded=0.0
//       ts_ns=19 unattributed=0
//
// Percentages are of the kind's read obtains (fast, slow causes, slow_clean), finds (find_*) or bound storage buffers
// (rt_excluded; eligible_raw is of the slots outside rt). *_ns are per call, net of one timestamp read (ts_ns).
internal static class LiteObtainProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_LITE_OBTAIN_PROBE") == "1";

    private static readonly long ReportTicks = (long)((double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_LITE_OBTAIN_REPORT_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 5.0) * Stopwatch.Frequency);

    private const int DumpLimit = 12;

    internal enum Cause : byte
    {
        Fast,
        Cpu,
        Pub,
        Img,
        Hot,
        Cross,
        Unmapped,
        Count,
    }

    [Flags]
    internal enum Work : byte
    {
        None = 0,
        Upload = 1,
        Stream = 2,
        Publication = 4,
        ImageCopy = 8,
        Registry = 16,
    }

    internal enum Outcome
    {
        Agree,
        FastWrong,
        Slow,
    }

    private static readonly string[] CauseNames = ["fast", "cpu", "pub", "img", "hot", "cross", "unmapped"];

    private const int Kinds = 2;
    private const int Causes = (int)Cause.Count;

    // What the light path answered against what the real one did.
    internal static Outcome Classify(Cause cause, bool sameResult, Work work) =>
        cause != Cause.Fast ? Outcome.Slow : sameResult && work == Work.None ? Outcome.Agree : Outcome.FastWrong;

    // [local] Bumped by the presenter when a read's publications were completed or overlaid, and by the cache when a
    // texel range was copied from an image: the real obtain's work the cache cannot see from its own counters.
    internal static long PublicationWork;
    internal static long ImageCopies;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NotePublicationWork()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref PublicationWork);
        }
    }

    // The sums of one window, per kind.
    internal sealed class Window
    {
        public long Frames;
        public long Unattributed;
        public readonly long[] Items = new long[Kinds];
        public readonly long[] Obtains = new long[Kinds];
        public readonly long[] Agree = new long[Kinds];
        public readonly long[] FastWrong = new long[Kinds];
        public readonly long[] Slow = new long[Kinds * Causes];
        public readonly long[] SlowClean = new long[Kinds];
        public readonly long[] LiteTicks = new long[Kinds];
        public readonly long[] RealTicks = new long[Kinds];
        public readonly long[] LiteFastTicks = new long[Kinds];
        public readonly long[] RealFastTicks = new long[Kinds];
        public readonly long[] Finds = new long[Kinds];
        public readonly long[] FindFast = new long[Kinds];
        public readonly long[] FindWrong = new long[Kinds];
        public readonly long[] FindSlow = new long[Kinds * Causes];
        public readonly long[] FindLiteTicks = new long[Kinds];
        public readonly long[] Slots = new long[Kinds];
        public readonly long[] SlotsRt = new long[Kinds];
        public readonly long[] SlotsRaw = new long[Kinds];

        public void Clear()
        {
            Frames = Unattributed = 0;
            foreach (var array in new[] { Items, Obtains, Agree, FastWrong, Slow, SlowClean, LiteTicks, RealTicks, LiteFastTicks, RealFastTicks,
                         Finds, FindFast, FindWrong, FindSlow, FindLiteTicks, Slots, SlotsRt, SlotsRaw })
            {
                Array.Clear(array);
            }
        }

        public Outcome AddObtain(int kind, Cause cause, bool sameResult, Work work, long liteTicks, long realTicks)
        {
            var outcome = Classify(cause, sameResult, work);
            Obtains[kind]++;
            LiteTicks[kind] += liteTicks;
            RealTicks[kind] += realTicks;
            switch (outcome)
            {
                case Outcome.Agree:
                    Agree[kind]++;
                    break;
                case Outcome.FastWrong:
                    FastWrong[kind]++;
                    break;
                default:
                    Slow[kind * Causes + (int)cause]++;
                    if (work == Work.None)
                    {
                        SlowClean[kind]++;
                    }

                    break;
            }

            if (cause == Cause.Fast)
            {
                LiteFastTicks[kind] += liteTicks;
                RealFastTicks[kind] += realTicks;
            }

            return outcome;
        }

        public Outcome AddFind(int kind, Cause cause, bool sameResult, bool created, long liteTicks)
        {
            var outcome = Classify(cause, sameResult, created ? Work.Registry : Work.None);
            Finds[kind]++;
            FindLiteTicks[kind] += liteTicks;
            switch (outcome)
            {
                case Outcome.Agree:
                    FindFast[kind]++;
                    break;
                case Outcome.FastWrong:
                    FindWrong[kind]++;
                    break;
                default:
                    FindSlow[kind * Causes + (int)cause]++;
                    break;
            }

            return outcome;
        }

        public void AddSlot(int kind, bool rt, bool raw)
        {
            Slots[kind]++;
            if (rt)
            {
                SlotsRt[kind]++;
            }
            else if (raw)
            {
                SlotsRaw[kind]++;
            }
        }
    }

    // The recording thread's state: the first thread that begins an item owns the probe.
    private static int _thread;
    private static int _depth;
    private static int _kind = -1;
    private static long _windowStart;
    private static long _frames;
    private static long _fastWrongTotal;
    private static int _dumps;
    private static readonly Window _window = new();

    internal static Window WindowForTests => _window;

    internal static bool IsRtProgram(ulong hash) => (hash >> 48) is 0x8395 or 0x5981 or 0x6A53 or 0x56D6 or 0x743E;

    internal static void NoteFrame()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _frames);
        }
    }

    internal static void Begin(bool dispatch)
    {
        var thread = Environment.CurrentManagedThreadId;
        if (_thread == 0)
        {
            _thread = thread;
            _windowStart = Stopwatch.GetTimestamp();
        }

        if (thread != _thread || _depth++ != 0)
        {
            return;
        }

        _kind = dispatch ? 1 : 0;
        _window.Items[_kind]++;
    }

    internal static void End()
    {
        if (Environment.CurrentManagedThreadId != _thread || _depth == 0 || --_depth != 0)
        {
            return;
        }

        _kind = -1;
        if (!DrawCostProbe.Enabled && Stopwatch.GetTimestamp() - _windowStart >= ReportTicks)
        {
            Report();
        }
    }

    // True on the recording thread inside an item; a note elsewhere is counted as unattributed by the caller's check.
    internal static bool Owns
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _kind >= 0 && Environment.CurrentManagedThreadId == _thread;
    }

    internal static Outcome NoteObtain(Cause cause, bool sameResult, Work work, long liteTicks, long realTicks)
    {
        var outcome = _window.AddObtain(_kind, cause, sameResult, work, liteTicks, realTicks);
        if (outcome == Outcome.FastWrong)
        {
            _fastWrongTotal++;
        }

        return outcome;
    }

    internal static Outcome NoteFind(Cause cause, bool sameResult, bool created, long liteTicks)
    {
        var outcome = _window.AddFind(_kind, cause, sameResult, created, liteTicks);
        if (outcome == Outcome.FastWrong)
        {
            _fastWrongTotal++;
        }

        return outcome;
    }

    internal static void NoteSlot(ulong programHash, bool written, bool formatted, bool atomic)
    {
        if (Owns)
        {
            _window.AddSlot(_kind, IsRtProgram(programHash), !written && !formatted && !atomic);
        }
    }

    internal static void NoteUnattributed() => _window.Unattributed++;

    // True for the first few disagreements, which the caller prints.
    internal static bool TakeDump() => _dumps++ < DumpLimit;

    // Called by DrawCostProbe at its own report when it is on, so the lines share its window.
    internal static void Report()
    {
        if (!Enabled || _thread == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _window.Frames = Interlocked.Exchange(ref _frames, 0);
        foreach (var line in Report(_window, (now - _windowStart) / (double)Stopwatch.Frequency, CalibrateTimestampTicks(), _fastWrongTotal))
        {
            Console.Error.WriteLine(line);
        }

        _window.Clear();
        _windowStart = Stopwatch.GetTimestamp();
    }

    // The cost of one timestamp read, in ticks, as a double.
    internal static double CalibrateTimestampTicks()
    {
        const int Reads = 4096;
        var started = Stopwatch.GetTimestamp();
        var sink = 0L;
        for (var index = 0; index < Reads; index++)
        {
            sink ^= Stopwatch.GetTimestamp();
        }

        var elapsed = Stopwatch.GetTimestamp() - started;
        return sink == 1 ? 0 : elapsed / (double)Reads;
    }

    internal static List<string> Report(Window window, double seconds, double timestampTicks, long fastWrongTotal)
    {
        var lines = new List<string>(Kinds);
        var toNs = 1e9 / Stopwatch.Frequency;
        var frames = Math.Max(window.Frames, 1);
        for (var kind = 0; kind < Kinds; kind++)
        {
            var items = window.Items[kind];
            if (items == 0)
            {
                continue;
            }

            var obtains = window.Obtains[kind];
            var fast = window.Agree[kind] + window.FastWrong[kind];
            var finds = window.Finds[kind];
            var slots = window.Slots[kind];
            var line = new StringBuilder(768);
            line.Append(FormattableString.Invariant(
                $"[PERF][LITE_OBTAIN] window_s={seconds:F1} frames={window.Frames} kind={(kind == 0 ? "draw" : "dispatch")} items_per_frame={items / (double)frames:F0} "));
            line.Append(FormattableString.Invariant(
                $"obtains_per_item={obtains / (double)items:F2} fast={Percent(fast, obtains):F1} fast_wrong={window.FastWrong[kind]} fast_wrong_total={fastWrongTotal} slow="));
            AppendCauses(line, window.Slow, kind, obtains);
            line.Append(FormattableString.Invariant($" slow_clean={Percent(window.SlowClean[kind], obtains):F1}"));
            line.Append(FormattableString.Invariant(
                $" lite_ns={NetNs(window.LiteTicks[kind], obtains, timestampTicks, toNs):F0} real_ns={NetNs(window.RealTicks[kind], obtains, timestampTicks, toNs):F0}"));
            line.Append(FormattableString.Invariant(
                $" lite_fast_ns={NetNs(window.LiteFastTicks[kind], fast, timestampTicks, toNs):F0} real_fast_ns={NetNs(window.RealFastTicks[kind], fast, timestampTicks, toNs):F0}"));
            line.Append(FormattableString.Invariant(
                $" finds_per_item={finds / (double)items:F2} find_fast={Percent(window.FindFast[kind] + window.FindWrong[kind], finds):F1} find_wrong={window.FindWrong[kind]} find_slow="));
            AppendCauses(line, window.FindSlow, kind, finds);
            line.Append(FormattableString.Invariant($" find_lite_ns={NetNs(window.FindLiteTicks[kind], finds, timestampTicks, toNs):F0}"));
            line.Append(FormattableString.Invariant(
                $" slots_per_item={slots / (double)items:F2} eligible_raw={Percent(window.SlotsRaw[kind], slots - window.SlotsRt[kind]):F1} rt_excluded={Percent(window.SlotsRt[kind], slots):F1}"));
            line.Append(FormattableString.Invariant($" ts_ns={timestampTicks * toNs:F0} unattributed={window.Unattributed}"));
            lines.Add(line.ToString());
        }

        return lines;
    }

    private static void AppendCauses(StringBuilder line, long[] counts, int kind, long total)
    {
        for (var cause = 1; cause < Causes; cause++)
        {
            line.Append(FormattableString.Invariant($"{(cause == 1 ? "" : ",")}{CauseNames[cause]}:{Percent(counts[kind * Causes + cause], total):F1}"));
        }
    }

    // Per call, net of the one timestamp read each measured span holds.
    private static double NetNs(long ticks, long calls, double timestampTicks, double toNs) =>
        calls == 0 ? 0 : Math.Max(0, ticks / (double)calls - timestampTicks) * toNs;

    private static double Percent(long part, long whole) => whole <= 0 ? 0 : part * 100.0 / whole;

    // [local] For tests: clears the thread ownership and the window.
    internal static void ResetForTests()
    {
        _thread = 0;
        _depth = 0;
        _kind = -1;
        _frames = 0;
        _fastWrongTotal = 0;
        _dumps = 0;
        _window.Clear();
    }
}
