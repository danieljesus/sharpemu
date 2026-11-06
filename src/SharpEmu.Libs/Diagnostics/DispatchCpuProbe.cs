// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_DISPATCH_CPU_PROBE=1 (off; with SHARPEMU_FPS_LOG=1): the CPU each compute program costs the recording
// threads, by program hash. One stopwatch around RenderRecorder.Record(ResolvedDispatch): preparation deferred to the
// render thread, materialization and its readback waits, bindings, pipeline and the recorded commands. Render-thread time
// (the critical path) and slice-worker time are kept apart. One [PERF][DISPATCH_CPU] line per FPS window with the totals
// per frame, the top programs by render-thread ms per frame and, always, SHARPEMU_DISPATCH_CPU_PROBE_PINNED (comma list
// of 16-hex-digit hashes; default the refit chain F43B/E45A/5981/AACC).
internal static class DispatchCpuProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DISPATCH_CPU_PROBE") == "1";

    private sealed class Entry
    {
        public long Count, Ticks, WorkerCount, WorkerTicks;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Entry> Entries = new();
    private static readonly ulong[] Pinned = ReadPinned();

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

    public static void Note(ulong hash, long ticks, bool onWorker)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(hash, out var entry))
            {
                entry = new Entry();
                Entries[hash] = entry;
            }

            if (onWorker)
            {
                entry.WorkerCount++;
                entry.WorkerTicks += ticks;
            }
            else
            {
                entry.Count++;
                entry.Ticks += ticks;
            }
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
            long count = 0, ticks = 0, workerCount = 0, workerTicks = 0;
            foreach (var entry in Entries.Values)
            {
                count += entry.Count;
                ticks += entry.Ticks;
                workerCount += entry.WorkerCount;
                workerTicks += entry.WorkerTicks;
            }

            builder.Append(CultureInfo.InvariantCulture, $" render={count / (double)divisor:F1}/{ticks / perMs / divisor:F2}ms worker={workerCount / (double)divisor:F1}/{workerTicks / perMs / divisor:F2}ms pinned:");
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

    // hash=count/ms per frame on the render thread (+count/ms on workers when any).
    private static void Append(StringBuilder builder, ulong hash, Entry? entry, long divisor, double perMs)
    {
        builder.Append(CultureInfo.InvariantCulture, $" {hash >> 48:X4}={(entry?.Count ?? 0) / (double)divisor:F1}/{(entry?.Ticks ?? 0) / perMs / divisor:F3}");
        if (entry is { WorkerCount: > 0 })
        {
            builder.Append(CultureInfo.InvariantCulture, $"(+{entry.WorkerCount / (double)divisor:F1}/{entry.WorkerTicks / perMs / divisor:F3})");
        }
    }
}
