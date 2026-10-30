// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_TABLE_READ_CENSUS=1: every synchronous read of a GPU-dirty table word on the render thread,
// grouped by reader shader and address: wait time, whether the word changed, where the writer tick of the word
// and of the whole readback window stand, and which binding last marked the word dirty.
internal static class TableReadCensus
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_TABLE_READ_CENSUS") == "1";

    private const int WriterRing = 1 << 15;
    private static readonly (ulong Address, ulong Size, ulong Shader, ulong Tick, long Stamp)[] _writers = new (ulong, ulong, ulong, ulong, long)[WriterRing];
    private static long _writerCount;

    private sealed class Entry
    {
        public long Reads, Changed, Ticks;
        public double Ms, LagMs;
        public readonly long[] Own = new long[3], Window = new long[3], Precise = new long[3];
        public ulong Writer, WriterAddress, WriterSize;
        public long WriterMisses;
        // [local] The newest recorded write overlapping the word's 16 KB block (what moves the block tick).
        public ulong BlockWriter, BlockWriterAddress, BlockWriterSize;
        public long BlockTicks, BlockFound, BlockCovers;
    }

    [ThreadStatic] private static ulong _blockBegin, _blockSize;

    // The word's 16 KB block, set by GuestBufferCache.CensusTicks before Record.
    public static void NoteBlock(ulong begin, ulong size)
    {
        _blockBegin = begin;
        _blockSize = size;
    }

    private static bool FindBlockWriter(ulong begin, ulong size, out (ulong Address, ulong Size, ulong Shader, ulong Tick, long Stamp) writer)
    {
        var count = Math.Min(_writerCount, WriterRing);
        for (long back = 1; back <= count; back++)
        {
            var candidate = _writers[(_writerCount - back) & (WriterRing - 1)];
            if (candidate.Address < begin + size && begin < candidate.Address + candidate.Size)
            {
                writer = candidate;
                return true;
            }
        }

        writer = default;
        return false;
    }

    private static readonly Dictionary<(ulong Reader, ulong Address), Entry> _entries = new();
    private static long _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();

    public static void NoteWrite(ulong address, ulong size, ulong tick) => NoteWrite(address, size, tick, GpuReadTrace.CurrentShader);

    // [local] The writer id of a CPU-to-GPU upload (the copy moves the block tick as a shader write does).
    public const ulong UploadWriter = 0x5550_4C4F_4144_0000;

    public static void NoteWrite(ulong address, ulong size, ulong tick, ulong shader)
    {
        var index = _writerCount++ & (WriterRing - 1);
        _writers[index] = (address, size, shader, tick, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private static bool FindWriter(ulong address, out (ulong Address, ulong Size, ulong Shader, ulong Tick, long Stamp) writer)
    {
        var count = Math.Min(_writerCount, WriterRing);
        for (long back = 1; back <= count; back++)
        {
            var candidate = _writers[(_writerCount - back) & (WriterRing - 1)];
            if (candidate.Address <= address && address < candidate.Address + candidate.Size)
            {
                writer = candidate;
                return true;
            }
        }

        writer = default;
        return false;
    }

    private static int Classify(ulong tick, ulong current, ulong completed) =>
        tick >= current ? 0 : tick > completed ? 1 : 2;

    public static void Record(ulong address, bool changed, double ms, ulong ownTick, ulong windowTick, ulong preciseTick, ulong current, ulong completed)
    {
        var reader = GpuReadTrace.CurrentShader;
        if (!_entries.TryGetValue((reader, address), out var entry))
        {
            entry = new Entry();
            _entries[(reader, address)] = entry;
        }

        entry.Reads++;
        entry.Ms += ms;
        if (changed) entry.Changed++;
        entry.Own[Classify(ownTick, current, completed)]++;
        entry.Window[Classify(windowTick, current, completed)]++;
        entry.Precise[Classify(preciseTick, current, completed)]++;
        if (FindWriter(address, out var writer))
        {
            entry.Writer = writer.Shader;
            entry.WriterAddress = writer.Address;
            entry.WriterSize = writer.Size;
            entry.Ticks += (long)(current - Math.Min(writer.Tick, current));
            entry.LagMs += System.Diagnostics.Stopwatch.GetElapsedTime(writer.Stamp).TotalMilliseconds;
        }
        else
        {
            entry.WriterMisses++;
        }

        if (_blockSize != 0 && FindBlockWriter(_blockBegin, _blockSize, out var blockWriter)) // [local]
        {
            entry.BlockFound++;
            entry.BlockWriter = blockWriter.Shader;
            entry.BlockWriterAddress = blockWriter.Address;
            entry.BlockWriterSize = blockWriter.Size;
            entry.BlockTicks += (long)(current - Math.Min(blockWriter.Tick, current));
            if (blockWriter.Address <= address && address < blockWriter.Address + blockWriter.Size) entry.BlockCovers++;
        }

        _blockSize = 0;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = (now - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (seconds < 20)
        {
            return;
        }

        _windowStart = now;
        Console.Error.WriteLine(FormattableString.Invariant($"[TABLE_CENSUS] window_s={seconds:F1} keys={_entries.Count} reads={_entries.Values.Sum(e => e.Reads)} ms={_entries.Values.Sum(e => e.Ms):F0} changed={_entries.Values.Sum(e => e.Changed)}"));
        foreach (var ((readerHash, wordAddress), value) in _entries.OrderByDescending(pair => pair.Value.Ms).Take(30))
        {
            var found = value.Reads - value.WriterMisses;
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[TABLE_CENSUS] reader=0x{readerHash:X16} addr=0x{wordAddress:X} reads={value.Reads} ms={value.Ms:F1} changed={value.Changed} own_cur={value.Own[0]} own_flight={value.Own[1]} own_done={value.Own[2]} win_cur={value.Window[0]} win_flight={value.Window[1]} win_done={value.Window[2]} pre_cur={value.Precise[0]} pre_flight={value.Precise[1]} pre_done={value.Precise[2]} writer=0x{value.Writer:X16} wrange=0x{value.WriterAddress:X}+0x{value.WriterSize:X} wmiss={value.WriterMisses} lag_ticks={(found == 0 ? 0 : value.Ticks / (double)found):F2} lag_ms={(found == 0 ? 0 : value.LagMs / found):F2} bwriter=0x{value.BlockWriter:X16} brange=0x{value.BlockWriterAddress:X}+0x{value.BlockWriterSize:X} blag_ticks={(value.BlockFound == 0 ? 0 : value.BlockTicks / (double)value.BlockFound):F2} bcovers={value.BlockCovers}/{value.BlockFound}"));
        }

        _entries.Clear();
    }
}
