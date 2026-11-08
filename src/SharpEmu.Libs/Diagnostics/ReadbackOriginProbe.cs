// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.GuestMemory;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_READBACK_ORIGIN_PROBE=1 (diagnostic): every 20 s, the readback waits split by the waiting thread,
// the site that started the download (ordinary window, precise bytes, render table read, none = eager or unknown),
// the guest read's source, where the waited tick stood when the wait began (done, queued on the GPU, sealed but not
// submitted) and how the precise last writer of the requested bytes compares with it (pdone: the requested bytes'
// writer had already finished, the wait is for a neighbour in the window; older: an earlier tick still in flight;
// exact: the waited tick itself; nohist: no precise history). Per key: calls, ms per frame, mean ms, the waited
// tick's distance from the completed tick and the GPU queue depth (submitted - completed) at the wait's start.
// The guest threads' waits are also listed by 64 KB block and shader. The front's blocked spans are split by the
// kind of wait that ended them, with the GPU queue depth when the block began; flushes that a readback forced (its
// writer was in the batch being recorded) are counted per site.
// [local] 2026-11-02 (gta/cutpolicy): the presenter's batches too, by what ended them (cap: a full batch; idle: IDLE_FLUSH;
// readback: a download whose writer was in the batch; learn: LEARN_FLUSH; policy: SHARPEMU_CUT_POLICY; other: the
// slice ended, a label, a synchronizing wait), with their draws and dispatches and how long they stayed open.
internal static class ReadbackOriginProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_ORIGIN_PROBE") == "1";

    internal enum Site : byte
    {
        None,
        Ordinary,
        Table,
        Precise,
    }

    internal enum CutReason : byte
    {
        Other,
        Cap,
        Idle,
        Readback,
        Learn,
        Policy,
    }

    private static readonly string[] CutReasonNames = ["other", "cap", "idle", "readback", "learn", "policy"];
    private const int BatchBuckets = 8;
    private static CutReason _cutReason;
    private static readonly long[] _batchCount = new long[6];
    private static readonly long[] _batchDraws = new long[6];
    private static readonly long[] _batchTicks = new long[6];
    private static readonly long[] _batchDrawHistogram = new long[BatchBuckets];
    private static readonly long[] _batchOpenHistogram = new long[BatchBuckets];

    internal readonly record struct Origin(Site Site, byte Source, ulong Address, ulong Size, ulong PreciseTick, ulong Shader);

    [ThreadStatic] private static Origin _current;

    private static readonly object _gate = new();
    private static readonly Dictionary<(string Thread, Site Site, byte Source, byte State, byte Class), Bucket> _waits = new();
    private static readonly Dictionary<(string Thread, ulong Block, ulong Shader, byte Source), Bucket> _blocks = new();
    private static readonly Dictionary<(byte Kind, ulong Address), Bucket> _front = new();
    private static readonly long[] _flushes = new long[4];
    private static long _frames;
    private static long _reportStarted = Stopwatch.GetTimestamp();
    private static long _reportFrames;

    private sealed class Bucket
    {
        public long Calls;
        public long Ticks;
        public long Distance;
        public long Depth;
        public long Unsubmitted;
    }

    private static readonly string[] SiteNames = ["none", "ordinary", "table", "precise"];
    private static readonly string[] StateNames = ["done", "queued", "sealed"];
    private static readonly string[] ClassNames = ["nohist", "pdone", "older", "exact"];

    internal static Origin Current => _current;

    // On the GPU queue thread, before a download of [address, address + size) is planned: the origin the tickets
    // started under it carry. preciseTick is the requested bytes' last recorded writer (0 when unknown).
    internal static Origin Enter(Site site, GuestMemoryProfile.ReadbackSource source, ulong address, ulong size, ulong preciseTick)
    {
        var previous = _current;
        _current = new Origin(site, (byte)source, address, size, preciseTick, GpuReadTrace.CurrentShader);
        return previous;
    }

    internal static void Restore(Origin previous) => _current = previous;

    internal static void SetSite(Site site) => _current = _current with { Site = site };

    public static void NoteFrame()
    {
        if (Enabled)
            Interlocked.Increment(ref _frames);
    }

    public static void NoteFlush()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _flushes[(int)_current.Site & 3]);
            _cutReason = CutReason.Readback;
        }
    }

    // [local] SHARPEMU_BDA_WRITE_MEMO: what ended the presenter's last batch (render thread; with the probe on).
    internal static CutReason LastCut { get; private set; }

    internal static string CutName(int reason) => CutReasonNames[reason];

    internal const int CutReasons = 6;

    // On the render thread, right before a submit: what is ending the presenter's batch.
    public static void NoteCutReason(CutReason reason)
    {
        if (Enabled)
            _cutReason = reason;
    }

    // On the render thread, when a submit closes the presenter's batch: its draws and dispatches and how long
    // (Stopwatch ticks) it stayed open. Draw buckets 0, 1, 2-3, ..., 64+; open buckets < 0.25 ms, x2 each, 16+ ms.
    public static void NoteBatch(int draws, long openTicks)
    {
        var reason = (int)_cutReason;
        LastCut = _cutReason; // [local] SHARPEMU_BDA_WRITE_MEMO: the cut before the next tick
        _cutReason = CutReason.Other;
        var drawBucket = draws <= 0 ? 0 : Math.Min(BatchBuckets - 1, 1 + System.Numerics.BitOperations.Log2((uint)draws));
        var openMs = openTicks * 1000.0 / Stopwatch.Frequency;
        var openBucket = 0;
        for (var limit = 0.25; openBucket < BatchBuckets - 1 && openMs >= limit; limit *= 2)
            openBucket++;
        lock (_gate)
        {
            _batchCount[reason]++;
            _batchDraws[reason] += draws;
            _batchTicks[reason] += openTicks;
            _batchDrawHistogram[drawBucket]++;
            _batchOpenHistogram[openBucket]++;
        }
    }

    public static void NoteWait(Origin origin, ulong waitTick, ulong completed, ulong submitted, ulong current, long ticks)
    {
        var state = (byte)(waitTick <= completed ? 0 : waitTick <= submitted ? 1 : 2);
        var cls = (byte)(origin.PreciseTick == 0 ? 0 : origin.PreciseTick <= completed ? 1 : origin.PreciseTick < waitTick ? 2 : 3);
        var thread = Thread.CurrentThread.Name ?? $"tid{Environment.CurrentManagedThreadId}";
        var distance = waitTick > completed ? (long)(waitTick - completed) : 0;
        var depth = submitted > completed ? (long)(submitted - completed) : 0;
        var unsubmitted = current > submitted ? (long)(current - submitted) : 0;
        lock (_gate)
        {
            Add(_waits, (thread, origin.Site, origin.Source, state, cls), ticks, distance, depth, unsubmitted);
            if (!thread.StartsWith("SharpEmu_Vulkan", StringComparison.Ordinal))
                Add(_blocks, (thread, origin.Address & ~0xFFFFUL, origin.Shader, origin.Source), ticks, distance, depth, unsubmitted);
            ReportLocked();
        }
    }

    public static void NoteFrontBlocked(byte kind, ulong address, long ticks, ulong depth, ulong unsubmitted)
    {
        lock (_gate)
        {
            Add(_front, (kind, address), ticks, 0, (long)depth, (long)unsubmitted);
            ReportLocked();
        }
    }

    private static void Add<TKey>(Dictionary<TKey, Bucket> map, TKey key, long ticks, long distance, long depth, long unsubmitted)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var bucket))
        {
            if (map.Count >= 4096)
                return;
            map[key] = bucket = new Bucket();
        }

        bucket.Calls++;
        bucket.Ticks += ticks;
        bucket.Distance += distance;
        bucket.Depth += depth;
        bucket.Unsubmitted += unsubmitted;
    }

    private static void ReportLocked()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _reportStarted < 20 * Stopwatch.Frequency)
            return;
        var seconds = (now - _reportStarted) / (double)Stopwatch.Frequency;
        var totalFrames = Interlocked.Read(ref _frames);
        var frames = Math.Max(1, totalFrames - _reportFrames);
        var msPerTick = 1000.0 / Stopwatch.Frequency;
        string Line(Bucket b) => FormattableString.Invariant(
            $"calls={b.Calls} ms_per_frame={b.Ticks * msPerTick / frames:F3} mean_ms={b.Ticks * msPerTick / b.Calls:F2} dist={b.Distance / (double)b.Calls:F2} depth={b.Depth / (double)b.Calls:F2} unsub={b.Unsubmitted / (double)b.Calls:F2}");
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[READBACK_ORIGIN] window_s={seconds:F1} frames={frames} flushes_ordinary={_flushes[1]} flushes_table={_flushes[2]} flushes_precise={_flushes[3]} flushes_none={_flushes[0]}"));
        for (var reason = 0; reason < _batchCount.Length; reason++)
        {
            if (_batchCount[reason] != 0)
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[READBACK_ORIGIN] batch reason={CutReasonNames[reason]} per_frame={_batchCount[reason] / (double)frames:F2} draws={_batchDraws[reason] / (double)_batchCount[reason]:F1} open_ms={_batchTicks[reason] * msPerTick / _batchCount[reason]:F3}"));
        }

        Console.Error.WriteLine($"[READBACK_ORIGIN] batch_hist draws={string.Join(",", _batchDrawHistogram)} open={string.Join(",", _batchOpenHistogram)}");
        Array.Clear(_batchCount);
        Array.Clear(_batchDraws);
        Array.Clear(_batchTicks);
        Array.Clear(_batchDrawHistogram);
        Array.Clear(_batchOpenHistogram);
        foreach (var (k, b) in _waits.OrderByDescending(pair => pair.Value.Ticks).Take(40))
            Console.Error.WriteLine($"[READBACK_ORIGIN] wait thread={k.Thread.Replace(' ', '_')} site={SiteNames[(int)k.Site]} source={(GuestMemoryProfile.ReadbackSource)k.Source} state={StateNames[k.State]} class={ClassNames[k.Class]} {Line(b)}");
        foreach (var (k, b) in _blocks.OrderByDescending(pair => pair.Value.Ticks).Take(25))
            Console.Error.WriteLine($"[READBACK_ORIGIN] block thread={k.Thread.Replace(' ', '_')} block=0x{k.Block:X} shader=0x{k.Shader:X16} source={(GuestMemoryProfile.ReadbackSource)k.Source} {Line(b)}");
        foreach (var (k, b) in _front.OrderByDescending(pair => pair.Value.Ticks).Take(12))
            Console.Error.WriteLine($"[READBACK_ORIGIN] front kind={(VideoOut.FrontProfile.SuspendKind)k.Kind} address=0x{k.Address:X} {Line(b)}");
        _waits.Clear();
        _blocks.Clear();
        _front.Clear();
        Array.Clear(_flushes);
        _reportStarted = now;
        _reportFrames = totalFrames;
    }
}
