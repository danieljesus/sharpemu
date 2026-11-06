// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_DISPATCH_CPU_PROBE=1 (off; with SHARPEMU_FPS_LOG=1): the CPU each compute program costs the thread
// that runs the translation's dispatch, by program hash. One stopwatch around RenderExecutor.Dispatch (the program's
// preparation when the front deferred it: materialization and its readback waits; then RenderRecorder.Record: bindings,
// pipeline and the recorded commands), and a second one around the Record part alone. With SHARPEMU_RENDER_PIPELINE=1
// that thread is the render thread, the critical path. One [PERF][DISPATCH_CPU] line per FPS window with the totals per
// frame, the pinned hashes (SHARPEMU_DISPATCH_CPU_PROBE_PINNED, a comma list of 16-hex-digit hashes; default the refit
// chain F43B/E45A/5981/AACC) and the top 16 by ms per frame, each as count/total ms(record ms) per frame.
internal static class DispatchCpuProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DISPATCH_CPU_PROBE") == "1";

    private sealed class Entry
    {
        public long Count, Ticks, RecordTicks;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Entry> Entries = new();
    private static readonly ulong[] Pinned = ReadPinned();

    // The hash of the program the executor resolved for the dispatch in flight on this thread (0 when it skipped it).
    [ThreadStatic] internal static ulong CurrentHash;
    [ThreadStatic] private static long _recordTicks;

    private static ulong[] ReadPinned()
    {
        var text = Environment.GetEnvironmentVariable("SHARPEMU_DISPATCH_CPU_PROBE_PINNED") ??
            "F43B5FF6DD312D34,E45A6BB95A843393,5981037B07E391D5,AACC3636B355E928";
        var list = new List<ulong>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
            {
                list.Add(hash);
            }
        }

        return list.ToArray();
    }

    public static long Begin()
    {
        CurrentHash = 0;
        _recordTicks = 0;
        return Stopwatch.GetTimestamp();
    }

    public static void NoteRecord(long ticks) => _recordTicks += ticks;

    public static void End(long started)
    {
        var ticks = Stopwatch.GetTimestamp() - started;
        var hash = CurrentHash;
        var recordTicks = _recordTicks;
        lock (Gate)
        {
            if (!Entries.TryGetValue(hash, out var entry))
            {
                entry = new Entry();
                Entries[hash] = entry;
            }

            entry.Count++;
            entry.Ticks += ticks;
            entry.RecordTicks += recordTicks;
        }
    }

    public static string TakeReport(long frames)
    {
        var perMs = Stopwatch.Frequency / 1000.0;
        var divisor = Math.Max(1, frames);
        var builder = new StringBuilder("[PERF][DISPATCH_CPU] frames=");
        builder.Append(frames.ToString(CultureInfo.InvariantCulture));
        lock (Gate)
        {
            long count = 0, ticks = 0, recordTicks = 0;
            foreach (var entry in Entries.Values)
            {
                count += entry.Count;
                ticks += entry.Ticks;
                recordTicks += entry.RecordTicks;
            }

            builder.Append(CultureInfo.InvariantCulture, $" dispatches={count / (double)divisor:F1}/{ticks / perMs / divisor:F2}ms({recordTicks / perMs / divisor:F2}) pinned:");
            foreach (var hash in Pinned)
            {
                Entries.TryGetValue(hash, out var entry);
                Append(builder, hash, entry, divisor, perMs);
            }

            builder.Append(" top:");
            foreach (var (hash, entry) in Entries.OrderByDescending(pair => pair.Value.Ticks).Take(16))
            {
                Append(builder, hash, entry, divisor, perMs);
            }

            Entries.Clear();
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, ulong hash, Entry? entry, long divisor, double perMs) =>
        builder.Append(CultureInfo.InvariantCulture, $" {hash >> 48:X4}={(entry?.Count ?? 0) / (double)divisor:F1}/{(entry?.Ticks ?? 0) / perMs / divisor:F3}({(entry?.RecordTicks ?? 0) / perMs / divisor:F3})");
}
