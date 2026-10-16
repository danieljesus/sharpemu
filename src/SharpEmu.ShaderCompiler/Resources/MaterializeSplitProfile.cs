// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.ShaderCompiler.Resources;

// [local] SHARPEMU_PROFILE_MATERIALIZE_SPLIT=1: where the materialization cache's time goes, per thread: validating
// cached variants (and the bytes compared), rebase attempts, table refreshes, full walks on a miss, building the entry
// from the recorded reads (and its sort), with the misses that recorded more than BigReads words apart.
public static class MaterializeSplitProfile
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_MATERIALIZE_SPLIT") == "1";
    public const int BigReads = 65536;

    public enum Kind { Validate, Shift, Refresh, Walk, Build, Sort, BigWalk, BigBuild, BigSort, Count }

    private sealed class Counters
    {
        public required string Thread;
        public readonly long[] Ticks = new long[(int)Kind.Count];
        public readonly long[] Calls = new long[(int)Kind.Count];
        public long ValidatedBytes, Reads, BigReadsTotal;
    }

    [ThreadStatic] private static Counters? _counters;
    private static readonly List<Counters> All = new();

    private static Counters Mine
    {
        get
        {
            if (_counters is { } counters)
                return counters;
            counters = new Counters { Thread = System.Threading.Thread.CurrentThread.Name ?? $"#{Environment.CurrentManagedThreadId}" };
            lock (All) All.Add(counters);
            return _counters = counters;
        }
    }

    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void Note(Kind kind, long started)
    {
        if (started == 0)
            return;
        var counters = Mine;
        Interlocked.Add(ref counters.Ticks[(int)kind], Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref counters.Calls[(int)kind]);
    }

    public static void NoteValidatedBytes(long bytes)
    {
        if (Enabled)
            Interlocked.Add(ref Mine.ValidatedBytes, bytes);
    }

    public static void NoteReads(int reads)
    {
        if (!Enabled)
            return;
        var counters = Mine;
        Interlocked.Add(ref counters.Reads, reads);
        if (reads > BigReads)
            Interlocked.Add(ref counters.BigReadsTotal, reads);
    }

    public static IEnumerable<string> TakeReport()
    {
        if (!Enabled)
            yield break;
        Counters[] all;
        lock (All) all = All.ToArray();
        foreach (var counters in all)
        {
            var parts = new List<string>();
            var any = false;
            for (var index = 0; index < (int)Kind.Count; index++)
            {
                var ticks = Interlocked.Exchange(ref counters.Ticks[index], 0);
                var calls = Interlocked.Exchange(ref counters.Calls[index], 0);
                any |= calls != 0;
                parts.Add(FormattableString.Invariant($"{(Kind)index}={ticks * 1000.0 / Stopwatch.Frequency:F2}ms/n{calls}"));
            }

            var validated = Interlocked.Exchange(ref counters.ValidatedBytes, 0);
            var reads = Interlocked.Exchange(ref counters.Reads, 0);
            var big = Interlocked.Exchange(ref counters.BigReadsTotal, 0);
            if (any)
                yield return $"[PERF][MATERIALIZE_SPLIT] thread='{counters.Thread}' validated_kb={validated / 1024} reads={reads} big_reads={big} " + string.Join(" ", parts);
        }
    }
}
