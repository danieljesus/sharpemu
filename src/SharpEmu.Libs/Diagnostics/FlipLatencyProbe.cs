// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_FLIP_PROBE=1: the life of every GPU flip from the front to FlipDone. Per request: the front reserved
// it (t1), the render thread started its queued flip (t2) and finished capturing it in tick T (t3), T was submitted (t4)
// and seen complete (t5), the flip was completed (counters, t6) and marked presented (FlipDone, t7). Every 5 s as
// [FLIP_PROBE] with p50/p90 of each segment in ms, plus the requests the render thread had not started when the front
// reserved a new one (queue depth in flips).
internal static class FlipLatencyProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_FLIP_PROBE") == "1";

    private sealed class Flip
    {
        public long T1, T2, T3, T6, T7;
        public ulong Tick;
        public int Depth;
    }

    private const int TickRing = 1 << 14;
    private static readonly long[] SubmittedAt = new long[TickRing];
    private static readonly ulong[] SubmittedTick = new ulong[TickRing];
    private static readonly long[] CompletedAt = new long[TickRing];
    private static readonly ulong[] CompletedTick = new ulong[TickRing];
    private static ulong _completedUpTo;

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Flip> Open = new();
    private static readonly List<Flip> Done = new();
    private static long _reportAt;

    public static void Reserved(ulong requestId)
    {
        lock (Gate)
        {
            var depth = 0;
            foreach (var flip in Open.Values) if (flip.T2 == 0) depth++;
            Open[requestId] = new Flip { T1 = Stopwatch.GetTimestamp(), Depth = depth };
        }
    }

    public static void RenderStarted(ulong requestId)
    {
        lock (Gate) if (Open.TryGetValue(requestId, out var flip)) flip.T2 = Stopwatch.GetTimestamp();
    }

    public static void Captured(ulong requestId, ulong tick)
    {
        lock (Gate)
            if (Open.TryGetValue(requestId, out var flip))
            {
                flip.T3 = Stopwatch.GetTimestamp();
                flip.Tick = tick;
            }
    }

    public static void Completed(ulong requestId)
    {
        lock (Gate) if (Open.TryGetValue(requestId, out var flip) && flip.T6 == 0) flip.T6 = Stopwatch.GetTimestamp();
    }

    public static void Presented(ulong requestId)
    {
        lock (Gate)
        {
            if (!Open.Remove(requestId, out var flip)) return;
            flip.T7 = Stopwatch.GetTimestamp();
            Done.Add(flip);
            Report(flip.T7);
        }
    }

    public static void NoteSubmitted(ulong tick)
    {
        var slot = (int)(tick & (TickRing - 1));
        SubmittedAt[slot] = Stopwatch.GetTimestamp();
        Volatile.Write(ref SubmittedTick[slot], tick);
    }

    public static void NoteCompleted(ulong counter)
    {
        var now = Stopwatch.GetTimestamp();
        lock (CompletedAt)
        {
            for (var tick = Math.Max(_completedUpTo + 1, counter > TickRing ? counter - TickRing + 1 : 0); tick <= counter; tick++)
            {
                var slot = (int)(tick & (TickRing - 1));
                CompletedAt[slot] = now;
                CompletedTick[slot] = tick;
            }

            _completedUpTo = Math.Max(_completedUpTo, counter);
        }
    }

    private static void Report(long now)
    {
        if (_reportAt == 0) { _reportAt = now + Stopwatch.Frequency * 5; return; }
        if (now < _reportAt) return;
        _reportAt = now + Stopwatch.Frequency * 5;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var segments = new List<double>[8];
        for (var index = 0; index < segments.Length; index++) segments[index] = new List<double>();
        var depths = new List<double>();
        foreach (var flip in Done)
        {
            var slot = (int)(flip.Tick & (TickRing - 1));
            var submitted = Volatile.Read(ref SubmittedTick[slot]) == flip.Tick ? SubmittedAt[slot] : 0;
            long completed;
            lock (CompletedAt) completed = CompletedTick[slot] == flip.Tick ? CompletedAt[slot] : 0;
            if (flip.T2 != 0) segments[0].Add(Ms(flip.T2 - flip.T1));
            if (flip.T3 != 0 && flip.T2 != 0) segments[1].Add(Ms(flip.T3 - flip.T2));
            if (submitted != 0 && flip.T3 != 0) segments[2].Add(Ms(submitted - flip.T3));
            if (submitted != 0 && completed != 0) segments[3].Add(Ms(completed - submitted));
            if (completed != 0 && flip.T6 != 0) segments[4].Add(Ms(flip.T6 - completed));
            if (flip.T6 != 0) segments[5].Add(Ms(flip.T7 - flip.T6));
            segments[6].Add(Ms(flip.T7 - flip.T1));
            depths.Add(flip.Depth);
        }

        string Stat(List<double> values)
        {
            if (values.Count == 0) return "-";
            values.Sort();
            return FormattableString.Invariant($"{values[values.Count / 2]:F2}/{values[(int)(values.Count * 0.9)]:F2}");
        }

        Console.Error.WriteLine(string.Concat(
            FormattableString.Invariant($"[FLIP_PROBE] flips={Done.Count} ms_p50/p90: front_to_render={Stat(segments[0])} render_capture={Stat(segments[1])} capture_to_gpu_submit={Stat(segments[2])} "),
            FormattableString.Invariant($"gpu_submit_to_complete={Stat(segments[3])} complete_to_flip_completed={Stat(segments[4])} completed_to_presented={Stat(segments[5])} total={Stat(segments[6])} queued_flips_at_reserve={Stat(depths)} open={Open.Count}")));
        Done.Clear();
    }
}
