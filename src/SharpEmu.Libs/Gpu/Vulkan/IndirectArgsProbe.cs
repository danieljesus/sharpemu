// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] What the GPU actually read as indirect dispatch arguments. GTA V's intro/mission-start hang (gcQb1, s2LF:
// one tick of thread-unit indirect dispatches of 0x8039D9800 runs 19-27 s, then everything resumes) is suspected to be
// a huge thread count in the 12 argument bytes: TryDispatchIndirectThreads hands the thread counts to
// vkCmdDispatchIndirect as group counts (64x over-dispatch by design), so a count near 2^28-2^31 is tens of seconds
// of early-exiting groups. With SHARPEMU_INDIRECT_ARGS_PROBE=1 every indirect dispatch first copies its arguments on
// the GPU into a slot of a host-visible ring, next to a sequence word; the ledger keeps the shader, guest address,
// recording tick and the CPU's view of the same bytes (raw guest memory, no synchronization, with whether the buffer
// store holds GPU-written bytes there). Old enough slots are scanned into a per-shader summary and a line for every
// dispatch whose group product passes BigGroups; a [GPU][STALL] report prints the latest slots, which shows the
// arguments of the dispatches in the stuck tick. Reading a mapped slot while the GPU still runs is best effort
// (coherent memory, no host barrier), good enough for a diagnostic.
// SHARPEMU_INDIRECT_ARGS_INJECT=<shader hex>/<groups>[/<nth>] (':' also separates; needs the probe) dispatches the nth (default 1)
// thread-unit indirect dispatch of that shader with <groups> X groups from the ring slot, keeping the real thread
// limits: it measures what a huge group count costs without changing what the shader computes.
// Off by default.
internal static class IndirectArgsProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_INDIRECT_ARGS_PROBE") == "1";
    internal const int Slots = 8192;
    internal const int SlotBytes = 16;

    // The shader of the dispatch being recorded, set by the render executor before it calls the host.
    [ThreadStatic] internal static ulong CurrentShader;

    internal static readonly IndirectArgsLedger Ledger = new(Slots);

    internal static readonly (ulong Shader, uint Groups, int Nth)? Inject = ParseInject(Environment.GetEnvironmentVariable("SHARPEMU_INDIRECT_ARGS_INJECT"));
    private static int _injectSeen;

    internal static (ulong Shader, uint Groups, int Nth)? ParseInject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Split([':', '/'], StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return null;
        var shaderText = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0];
        var groupsText = parts[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[1][2..] : parts[1];
        var groupsStyle = parts[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!ulong.TryParse(shaderText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shader) ||
            !uint.TryParse(groupsText, groupsStyle, CultureInfo.InvariantCulture, out var groups))
            return null;
        var nth = 1;
        if (parts.Length > 2 && (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out nth) || nth < 1))
            return null;
        return (shader, groups, nth);
    }

    // True once, for the configured dispatch of the configured shader.
    internal static bool ShouldInject(ulong shader) =>
        Inject is { } inject && inject.Shader == shader && Interlocked.Increment(ref _injectSeen) == inject.Nth;
}

// [local] The bookkeeping of IndirectArgsProbe without Vulkan: slot assignment, the scan of executed slots into
// per-shader statistics, and the text of the reports. Thread-safe; one instance per ring.
internal sealed class IndirectArgsLedger
{
    internal const ulong DefaultBigGroups = 1UL << 24;
    private const int SummaryEvery = 65536;
    private const int MaxBigLines = 64;

    internal readonly record struct Entry(ulong Sequence, ulong Shader, ulong Address, ulong Tick, uint CpuX, uint CpuY, uint CpuZ, bool GpuDirty);

    internal sealed class ShaderStats
    {
        public long Count;
        public uint MaxX, MaxY, MaxZ;
        public ulong MaxGroups;
        public long Big;
        public long CpuMismatches;
    }

    // Reads one ring slot: the three copied words and the sequence word.
    internal delegate (uint X, uint Y, uint Z, uint Sequence) SlotReader(int slot);

    private readonly object _lock = new();
    private readonly Entry[] _entries;
    private readonly Dictionary<ulong, ShaderStats> _stats = new();
    private ulong _next = 1;
    private ulong _scanned;
    private int _bigLines;
    private long _sinceSummary;

    internal IndirectArgsLedger(int slots, ulong bigGroups = DefaultBigGroups)
    {
        if (slots < 2)
            throw new ArgumentOutOfRangeException(nameof(slots));
        _entries = new Entry[slots];
        BigGroups = bigGroups;
    }

    internal int SlotCount => _entries.Length;
    internal ulong BigGroups { get; }

    internal static int SlotOf(ulong sequence, int slots) => (int)(sequence % (ulong)slots);

    // The sequence word written next to a slot's arguments; never 0, so a zeroed ring reads as not yet executed.
    internal static uint SequenceWord(ulong sequence) => (uint)sequence == 0 ? 1u : (uint)sequence;

    internal (int Slot, ulong Sequence) Reserve(ulong shader, ulong address, ulong tick, ReadOnlySpan<byte> cpuBytes, bool gpuDirty)
    {
        uint x = 0, y = 0, z = 0;
        if (cpuBytes.Length >= 12)
        {
            x = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cpuBytes);
            y = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cpuBytes[4..]);
            z = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cpuBytes[8..]);
        }

        lock (_lock)
        {
            var sequence = _next++;
            var slot = SlotOf(sequence, _entries.Length);
            _entries[slot] = new Entry(sequence, shader, address, tick, x, y, z, gpuDirty);
            return (slot, sequence);
        }
    }

    // Scans the slots at least half a ring old (their dispatches have run unless the GPU is a ring behind) and
    // returns the lines to print: one per dispatch past BigGroups (at most MaxBigLines per session) and a summary
    // every SummaryEvery dispatches.
    internal List<string> Scan(SlotReader read)
    {
        var lines = new List<string>();
        lock (_lock)
        {
            var lag = (ulong)(_entries.Length / 2);
            var limit = _next > lag ? _next - lag : 0;
            while (_scanned + 1 < limit)
            {
                var sequence = ++_scanned;
                var slot = SlotOf(sequence, _entries.Length);
                var entry = _entries[slot];
                if (entry.Sequence != sequence)
                    continue;
                var (x, y, z, word) = read(slot);
                if (word != SequenceWord(sequence))
                    continue;
                Note(entry, x, y, z, lines);
            }

            if (_sinceSummary >= SummaryEvery)
            {
                _sinceSummary = 0;
                lines.Add(Summary());
            }
        }

        return lines;
    }

    private void Note(Entry entry, uint x, uint y, uint z, List<string> lines)
    {
        if (!_stats.TryGetValue(entry.Shader, out var stats))
            _stats[entry.Shader] = stats = new ShaderStats();
        stats.Count++;
        _sinceSummary++;
        stats.MaxX = Math.Max(stats.MaxX, x);
        stats.MaxY = Math.Max(stats.MaxY, y);
        stats.MaxZ = Math.Max(stats.MaxZ, z);
        var groups = (ulong)x * y * z;
        stats.MaxGroups = Math.Max(stats.MaxGroups, groups);
        var mismatch = !entry.GpuDirty && (entry.CpuX != x || entry.CpuY != y || entry.CpuZ != z);
        if (mismatch)
            stats.CpuMismatches++;
        if (groups >= BigGroups)
        {
            stats.Big++;
            if (_bigLines++ < MaxBigLines)
                lines.Add($"[GPU][INDIRECT_ARGS] big {Describe(entry, x, y, z, true)}");
        }
    }

    internal ShaderStats? StatsOf(ulong shader)
    {
        lock (_lock)
            return _stats.TryGetValue(shader, out var stats) ? stats : null;
    }

    private string Summary()
    {
        var builder = new StringBuilder("[GPU][INDIRECT_ARGS] summary");
        foreach (var (shader, stats) in _stats.OrderByDescending(pair => pair.Value.MaxGroups))
        {
            builder.Append(CultureInfo.InvariantCulture,
                $" 0x{shader:X}:n={stats.Count},max={stats.MaxX}x{stats.MaxY}x{stats.MaxZ},groups={stats.MaxGroups},big={stats.Big},cpu_mismatch={stats.CpuMismatches}");
        }

        return builder.ToString();
    }

    // The latest count slots, newest last, with what the GPU copied (or "pending" when the sequence word does not
    // match yet): for a [GPU][STALL] report.
    internal List<string> DescribeLatest(int count, SlotReader read)
    {
        var lines = new List<string>();
        lock (_lock)
        {
            var first = _next > (ulong)count ? _next - (ulong)count : 1;
            for (var sequence = first; sequence < _next; sequence++)
            {
                var slot = SlotOf(sequence, _entries.Length);
                var entry = _entries[slot];
                if (entry.Sequence != sequence)
                    continue;
                var (x, y, z, word) = read(slot);
                lines.Add($"indirect_args {Describe(entry, x, y, z, word == SequenceWord(sequence))}");
            }
        }

        return lines;
    }

    // The counts 0x8039D8A00 derives from a prefix array (see Presenter.NoteIndirectArgsWriter): P[i+1] - P[i], and
    // total - P[last] for the last of count entries; prefix holds the entries that could be read. Wrapped when one
    // difference passes 2^24 (a negative difference wraps to ~4e9). The text lists start:difference for the first 8.
    internal static (bool Wrapped, string Text) DescribePrefix(ReadOnlySpan<uint> prefix, uint count, uint total)
    {
        var wrapped = false;
        var builder = new StringBuilder();
        for (var index = 0; index < prefix.Length; index++)
        {
            var end = index + 1 < count && index + 1 < prefix.Length ? prefix[index + 1] : total;
            var difference = end - prefix[index];
            wrapped |= difference > 1u << 24;
            if (index < 8)
                builder.Append(CultureInfo.InvariantCulture, $"{prefix[index]}:{difference},");
        }

        return (wrapped, builder.ToString());
    }

    private static string Describe(Entry entry, uint x, uint y, uint z, bool executed) =>
        string.Create(CultureInfo.InvariantCulture,
            $"seq={entry.Sequence} tick={entry.Tick} shader=0x{entry.Shader:X} address=0x{entry.Address:X} " +
            $"gpu={(executed ? $"{x},{y},{z}" : "pending")} cpu={entry.CpuX},{entry.CpuY},{entry.CpuZ} gpu_dirty={entry.GpuDirty}");
}
