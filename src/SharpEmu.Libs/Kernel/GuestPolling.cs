// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.Kernel;

// [local] Guest polling calls: usleep(0), usleep(1..999), longer usleeps, sched_yield/scePthreadYield and nanosleeps
// under 1 ms. A short usleep and a yield map to Thread.Yield, which returns at once when no other thread is ready on
// the lane, so a guest loop around them burns the lane.
//
// SHARPEMU_GUEST_POLL_BACKOFF=N (16 by default, -1 turns it off): per guest thread, a short usleep or a yield that starts less than
// SHARPEMU_GUEST_POLL_GAP_US (default 50) after the previous one returned extends a streak; any other call starts a
// new streak of 1. From call N+1 of a streak on, the call sleeps 1 ms (Thread.Sleep(1), 1 ms timer resolution)
// instead of yielding. N=0 sleeps on every short usleep and yield.
//
// SHARPEMU_USLEEP_SHORT_PUMP (skipped by default, =1 keeps the Pump): usleep(1..999) skips the cooperative scheduler's Pump (a scan of
// every guest thread under the scheduler gate). The dispatcher thread still wakes expired waits every 1 ms and the
// ready dispatcher still runs ready threads.
//
// SHARPEMU_PROFILE_YIELDS=1: calls per second of each kind and backoff sleeps, per host thread, one
// [PERF][YIELDS] line per active thread every 5 s.
internal static class GuestPolling
{
    internal static readonly int BackoffAfter = ReadBackoff(Environment.GetEnvironmentVariable("SHARPEMU_GUEST_POLL_BACKOFF") ?? "16");
    internal static readonly long GapTicks =
        (int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_GUEST_POLL_GAP_US"), out var gap) && gap >= 0 ? gap : 50) *
        Stopwatch.Frequency / 1_000_000;
    internal static readonly bool SkipShortUsleepPump = Environment.GetEnvironmentVariable("SHARPEMU_USLEEP_SHORT_PUMP") != "1";
    internal static readonly bool Profile = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_YIELDS") == "1";

    internal enum Kind
    {
        Usleep0,
        UsleepShort,
        UsleepLong,
        Yield,
        NanosleepShort,
        BackoffSleep,
        Count,
    }

    internal static int ReadBackoff(string? value) => int.TryParse(value, out var count) && count >= 0 ? count : -1;

    // The streak of one thread. Next is called at the start of a poll with that time; Done when the poll returns.
    internal struct Streak
    {
        public int Length;
        public long LastEnd;

        public bool Next(long now, long gapTicks, int backoffAfter)
        {
            Length = LastEnd != 0 && now - LastEnd < gapTicks ? Length + 1 : 1;
            return backoffAfter >= 0 && Length > backoffAfter;
        }

        public void Done(long now) => LastEnd = now;
    }

    private sealed class Counters
    {
        public readonly long[] Calls = new long[(int)Kind.Count];
        public string Name = "";
    }

    [ThreadStatic]
    private static Streak _streak;

    [ThreadStatic]
    private static Counters? _counters;

    private static readonly List<Counters> AllCounters = new();
    private static long _windowStart = Stopwatch.GetTimestamp();

    // A short usleep or a yield: true when the caller should sleep 1 ms instead of yielding.
    internal static bool BeginPoll()
    {
        if (BackoffAfter < 0)
        {
            return false;
        }

        var block = _streak.Next(Stopwatch.GetTimestamp(), GapTicks, BackoffAfter);
        if (block && Profile)
        {
            Note(Kind.BackoffSleep);
        }

        return block;
    }

    internal static void EndPoll()
    {
        if (BackoffAfter >= 0)
        {
            _streak.Done(Stopwatch.GetTimestamp());
        }
    }

    internal static void Note(Kind kind)
    {
        if (!Profile)
        {
            return;
        }

        var counters = _counters;
        if (counters is null)
        {
            counters = _counters = new Counters { Name = Thread.CurrentThread.Name ?? $"tid {Environment.CurrentManagedThreadId}" };
            lock (AllCounters)
            {
                AllCounters.Add(counters);
            }
        }

        Interlocked.Increment(ref counters.Calls[(int)kind]);
        var start = Volatile.Read(ref _windowStart);
        var now = Stopwatch.GetTimestamp();
        if (now - start >= 5 * Stopwatch.Frequency && Interlocked.CompareExchange(ref _windowStart, now, start) == start)
        {
            Report((now - start) / (double)Stopwatch.Frequency);
        }
    }

    private static void Report(double seconds)
    {
        var rows = new List<(string Name, long[] Calls)>();
        lock (AllCounters)
        {
            foreach (var counters in AllCounters)
            {
                var calls = new long[(int)Kind.Count];
                long total = 0;
                for (var index = 0; index < calls.Length; index++)
                {
                    calls[index] = Interlocked.Exchange(ref counters.Calls[index], 0);
                    total += calls[index];
                }

                if (total != 0)
                {
                    rows.Add((counters.Name, calls));
                }
            }
        }

        rows.Sort((a, b) => b.Calls.Sum().CompareTo(a.Calls.Sum()));
        foreach (var (name, calls) in rows.Take(16))
        {
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[PERF][YIELDS] window_s={seconds:F1} usleep0_per_s={calls[0] / seconds:F0} usleep_short_per_s={calls[1] / seconds:F0} " +
                $"usleep_long_per_s={calls[2] / seconds:F0} yield_per_s={calls[3] / seconds:F0} nanosleep_short_per_s={calls[4] / seconds:F0} " +
                $"backoff_sleeps_per_s={calls[5] / seconds:F0} thread='{name}'"));
        }
    }
}
