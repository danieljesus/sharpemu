// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_TABLE_DIRTY_PROBE=<hex base>+<hex bytes>: logs every distinct GPU-written range that overlaps the
// watched range (who marks a store table GPU-dirty), with the caller frames and a running count.
internal static class TableDirtyProbe
{
    public static readonly ulong Base, Bytes;
    public static readonly bool Enabled;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Seen = new();
    private static long _hits;

    static TableDirtyProbe()
    {
        var value = System.Environment.GetEnvironmentVariable("SHARPEMU_TABLE_DIRTY_PROBE");
        if (string.IsNullOrEmpty(value) || value.Split('+') is not [var b, var n])
            return;
        Base = System.Convert.ToUInt64(b, 16);
        Bytes = System.Convert.ToUInt64(n, 16);
        Enabled = Bytes != 0;
    }

    public static bool Overlaps(ulong address, ulong size) => Enabled && address < Base + Bytes && Base < address + size;

    private static long _downloads, _changedDownloads, _changedBytes;

    public static void NoteDownload(ulong address, byte[] before, byte[] after, int pieces, ulong tick, ulong completed)
    {
        var downloads = System.Threading.Interlocked.Increment(ref _downloads);
        var first = before.AsSpan().CommonPrefixLength(after);
        if (first < before.Length)
        {
            var changed = 0;
            var last = first;
            for (var offset = first; offset < before.Length; offset++)
            {
                if (before[offset] != after[offset])
                {
                    changed++;
                    last = offset;
                }
            }

            var total = System.Threading.Interlocked.Increment(ref _changedDownloads);
            System.Threading.Interlocked.Add(ref _changedBytes, changed);
            if (total <= 200)
                System.Console.Error.WriteLine($"[TABLE_DIRTY_PROBE] download changed window=0x{address:X}+0x{before.Length:X} first=0x{address + (ulong)first:X} last=0x{address + (ulong)last:X} bytes={changed} pieces={pieces} tick={tick} completed={completed} before={System.Convert.ToHexString(before, first & ~15, System.Math.Min(64, before.Length - (first & ~15)))} after={System.Convert.ToHexString(after, first & ~15, System.Math.Min(64, after.Length - (first & ~15)))}");
        }

        if (downloads % 200 == 0 || downloads <= 3)
            System.Console.Error.WriteLine($"[TABLE_DIRTY_PROBE] downloads={downloads} changed={System.Threading.Interlocked.Read(ref _changedDownloads)} changed_bytes={System.Threading.Interlocked.Read(ref _changedBytes)} tick={tick} completed={completed}");
    }

    private static readonly long[] _pathCounts = new long[6];
    private static readonly long[] _pathTicks = new long[6];
    private static long _pathReports;

    // Async (by the writer tick) or the synchronous batch (the whole queue), why not async (0 none, 1 no readback, 2 a
    // piece without a writer tick), and where the writer tick stood.
    public static void NoteDownloadPath(bool async, int reason, long ticks, ulong waitTick, ulong current, ulong completed, int pieces)
    {
        var kind = waitTick == 0 ? 3 : waitTick >= current ? 0 : waitTick > completed ? 1 : 2;
        var slot = async ? kind : 4 + System.Math.Min(reason, 1);
        System.Threading.Interlocked.Increment(ref _pathCounts[slot]);
        System.Threading.Interlocked.Add(ref _pathTicks[slot], ticks);
        if (!async && System.Threading.Interlocked.Increment(ref _pathReports) <= 40)
            System.Console.Error.WriteLine($"[TABLE_DIRTY_PROBE] sync download reason={reason} kind={kind} pieces={pieces} ms={ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F2}");
        var total = 0L;
        foreach (var count in _pathCounts) total += count;
        if (total % 200 == 0)
            System.Console.Error.WriteLine($"[TABLE_DIRTY_PROBE] paths async(cur/flight/done/none)={_pathCounts[0]}/{_pathCounts[1]}/{_pathCounts[2]}/{_pathCounts[3]} ms={Ms(0)}/{Ms(1)}/{Ms(2)}/{Ms(3)} sync(ok/reason)={_pathCounts[4]}/{_pathCounts[5]} ms={Ms(4)}/{Ms(5)}");
    }

    private static string Ms(int slot) => (_pathCounts[slot] == 0 ? 0 : _pathTicks[slot] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _pathCounts[slot]).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    public static void Note(string what, ulong address, ulong size, ulong tick)
    {
        var hits = System.Threading.Interlocked.Increment(ref _hits);
        var key = $"{what} range=0x{address:X}+0x{size:X}";
        var count = Seen.AddOrUpdate(key, 1, static (_, value) => value + 1);
        if (count == 1 && Seen.Count <= 200)
        {
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
            var site = string.Join(" < ", frames.Take(6).Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?"));
            System.Console.Error.WriteLine($"[TABLE_DIRTY_PROBE] new {key} tick={tick} thread={System.Environment.CurrentManagedThreadId} site={site}");
        }

        if (hits % 2000 == 0)
            System.Console.Error.WriteLine($"[TABLE_DIRTY_PROBE] hits={hits} top=[{string.Join("; ", Seen.ToArray().OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key}:{p.Value}"))}]");
    }
}
