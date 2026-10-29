// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_SRT_READ_PROBE=1: the render thread's synchronizing table-word reads by reader program, plan and word
// offset from the read's base: wait, value changes, why the word memo did not answer, and what the word is for.
internal static class SrtReadProbeLog
{
    public static bool Enabled => SrtReadProbe.Enabled;

    private sealed class Entry
    {
        public long Reads, Changed, Ms100;
        public double Ms;
        public string Uses = "";
        public ulong LastAddress, LastBase;
        public uint LastWord;
        public long Bases, Addresses;
        public HashSet<ulong> SeenBases = new(), SeenAddresses = new();
        public readonly Dictionary<string, long> Reasons = new();
    }

    private static readonly Dictionary<(ulong Reader, ulong Plan, int Node), Entry> _entries = new();
    private static long _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();

    public static void Record(ulong reader, ulong address, double ms, uint before, uint after, string reason)
    {
        var plan = SrtReadProbe.CurrentPlanHash;
        var node = SrtReadProbe.CurrentNode;
        var key = (reader, plan, node);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry { Uses = SrtReadProbe.Describe() };
            _entries[key] = entry;
        }

        entry.Reads++;
        entry.Ms += ms;
        if (before != after) entry.Changed++;
        var baseAddress = SrtReadProbe.CurrentBaseAddress;
        if (entry.SeenBases.Count < 4096 && entry.SeenBases.Add(baseAddress)) entry.Bases++;
        if (entry.SeenAddresses.Count < 4096 && entry.SeenAddresses.Add(address)) entry.Addresses++;
        entry.LastAddress = address;
        entry.LastBase = baseAddress;
        entry.LastWord = after;
        entry.Reasons[reason] = entry.Reasons.GetValueOrDefault(reason) + 1;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = (now - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (seconds < 20)
            return;
        _windowStart = now;
        Console.Error.WriteLine(FormattableString.Invariant($"[SRT_PROBE] window_s={seconds:F1} keys={_entries.Count} reads={_entries.Values.Sum(e => e.Reads)} ms={_entries.Values.Sum(e => e.Ms):F0} device_data_reads_planned={ResourceTableReadPlanner.DeviceDataReads}"));
        foreach (var ((readerHash, planHash, readNode), value) in _entries.OrderByDescending(pair => pair.Value.Ms).Take(24))
        {
            var reasons = string.Join(",", value.Reasons.OrderByDescending(pair => pair.Value).Take(6).Select(pair => $"{pair.Key}={pair.Value}"));
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[SRT_PROBE] reader={readerHash >> 48:X4} plan=0x{planHash:X16} reads={value.Reads} ms={value.Ms:F1} changed={value.Changed} bases={value.Bases} addrs={value.Addresses} last=0x{value.LastAddress:X} base=0x{value.LastBase:X} off=0x{value.LastAddress - value.LastBase:X} word=0x{value.LastWord:X8} memo=[{reasons}] {value.Uses}"));
        }

        _entries.Clear();
    }
}
