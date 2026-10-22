// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_TABLE_WORD_MEMO (=1 by default, =0 off): a materialization's synchronizing read of a GPU-dirty table word
// remembers the value it got. A later read of the same word reuses it, without waiting, while every GPU write marked
// over the word since then came from a program in SHARPEMU_TABLE_WORD_MEMO_WRITERS (hex hashes separated by '/', 0x5981 by
// default: the BLAS refit, whose written binding spans the whole blob, header included, though the refit rewrites
// nodes only) and no command-processor or CPU write touched it. =1 uses the value; =2 still synchronizes and counts
// how often the value would have been wrong ([PERF][TABLE_WORD_MEMO]). Render thread only.
internal static class TableWordMemo
{
    public static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO"), out var mode) ? mode : 1;
    public static bool Enabled => Mode != 0;
    private static readonly HashSet<ulong> Writers = (Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_WRITERS") ?? "5981037B07E391D5")
        .Split(['/', ','], StringSplitOptions.RemoveEmptyEntries).Select(hash => Convert.ToUInt64(hash, 16)).ToHashSet();

    private static readonly object _lock = new();
    private static readonly Dictionary<ulong, uint> _values = new();
    private static ulong[] _sorted = [];
    private static bool _dirtySort;
    [ThreadStatic] internal static ulong CurrentProgram;

    // The program whose written bindings are being marked: set when a draw's or dispatch's bindings are prepared, cleared
    // when it has been recorded (the scope's end), so GPU writes marked after it (fills, copies, label writes) belong to
    // no program and no allowlisted writer can hide them.
    internal static ProgramScope BeginProgram(ulong hash)
    {
        CurrentProgram = hash;
        return default;
    }

    internal readonly struct ProgramScope : IDisposable
    {
        public void Dispose() => CurrentProgram = 0;
    }

    internal static void ResetForTests()
    {
        lock (_lock)
        {
            _values.Clear();
            _sorted = [];
            _dirtySort = false;
            Volatile.Write(ref _count, 0);
        }
    }

    private static long _hits, _verified, _mismatches, _misses, _stores, _invalidGpu, _invalidCp;
    private static readonly Dictionary<ulong, long> _invalidByWriter = new();
    private static int _mismatchLogs;

    public static bool TryGet(ulong address, out uint value)
    {
        lock (_lock)
            return _values.TryGetValue(address, out value);
    }

    public static void NoteHit() => Interlocked.Increment(ref _hits);

    public static void NoteMiss() => Interlocked.Increment(ref _misses);

    public static void Store(ulong address, uint value)
    {
        lock (_lock)
            StoreLocked(address, value);
    }

    private static void StoreLocked(ulong address, uint value)
    {
        _stores++;
        if (_values.TryAdd(address, value))
        {
            _dirtySort = true;
            Volatile.Write(ref _count, _values.Count);
        }
        else
            _values[address] = value;
    }

    public static void Verify(ulong address, uint memo, uint synchronized)
    {
        lock (_lock)
            VerifyLocked(address, memo, synchronized);
    }

    private static void VerifyLocked(ulong address, uint memo, uint synchronized)
    {
        _verified++;
        if (memo == synchronized)
            return;
        _mismatches++;
        if (_mismatchLogs++ < 40)
            Console.Error.WriteLine($"[TABLE_WORD_MEMO] mismatch address=0x{address:X} memo=0x{memo:X8} synchronized=0x{synchronized:X8}");
    }

    // A GPU write marked over [address, address+size) by the current program.
    public static void NoteGpuWrite(ulong address, ulong size)
    {
        if (Volatile.Read(ref _count) == 0 || Writers.Contains(CurrentProgram))
            return;
        lock (_lock)
        if (Invalidate(address, size))
        {
            _invalidGpu++;
            _invalidByWriter[CurrentProgram] = _invalidByWriter.GetValueOrDefault(CurrentProgram) + 1;
        }
    }

    // [local] SHARPEMU_TABLE_WORD_MEMO_CP_UPDATE=1 (off by default): a command-processor write carries its
    // data, applied in stream order, so a remembered word it fully covers takes the new value instead of being dropped
    // (CP-written descriptors in GPU-dirty pages, re-written each frame with the same layout). Partly covered words drop.
    internal static bool CommandUpdates = Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_CP_UPDATE") == "1";
    private static long _cpUpdates, _hostStores;

    public static void NoteCommandWrite(ulong address, ReadOnlySpan<byte> data)
    {
        if (Volatile.Read(ref _count) == 0)
            return;
        if (!CommandUpdates)
        {
            NoteOtherWrite(address, (ulong)data.Length);
            return;
        }

        lock (_lock)
        {
            var end = address + (ulong)data.Length;
            for (var word = address & ~3UL; word < end; word += 4)
            {
                if (!_values.ContainsKey(word))
                    continue;
                if (word >= address && word + 4 <= end)
                {
                    _values[word] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(word - address)..]);
                    _cpUpdates++;
                }
                else if (Invalidate(word, 4))
                {
                    _invalidCp++;
                }
            }
        }
    }

    // [local] SHARPEMU_TLAS_HLE: a word the host wrote in stream order (so its value at this point of the stream).
    public static void StoreHostWrite(ulong address, uint value)
    {
        if (!CommandUpdates)
            return;
        lock (_lock)
        {
            StoreLocked(address, value);
            _hostStores++;
        }
    }

    // A command-processor or CPU write.
    public static void NoteOtherWrite(ulong address, ulong size)
    {
        if (Volatile.Read(ref _count) == 0)
            return;
        lock (_lock)
            if (Invalidate(address, size))
                _invalidCp++;
    }

    private static int _count;

    private static bool Invalidate(ulong address, ulong size)
    {
        if (_dirtySort)
        {
            _sorted = _values.Keys.Order().ToArray();
            _dirtySort = false;
        }

        var index = Array.BinarySearch(_sorted, address & ~3UL);
        if (index < 0)
            index = ~index;
        var removed = false;
        for (; index < _sorted.Length && _sorted[index] < address + size; index++)
            removed |= _values.Remove(_sorted[index]);
        if (removed)
        {
            _dirtySort = true;
            Volatile.Write(ref _count, _values.Count);
        }

        return removed;
    }

    public static string? TakeReport()
    {
        if (!Enabled)
            return null;
        lock (_lock)
            return TakeReportLocked();
    }

    private static string TakeReportLocked()
    {
        var writers = string.Join(",", _invalidByWriter.OrderByDescending(pair => pair.Value).Take(8).Select(pair => $"{pair.Key:X16}:{pair.Value}"));
        _invalidByWriter.Clear();
        var line = $"[PERF][TABLE_WORD_MEMO] mode={Mode} words={_values.Count} hits={_hits} misses={_misses} stores={_stores} verified={_verified} mismatches={_mismatches} invalid_gpu={_invalidGpu} invalid_cp_cpu={_invalidCp} invalid_by_writer=[{writers}] cp_updates={_cpUpdates} host_stores={_hostStores}";
        _hits = _verified = _mismatches = _misses = _stores = _invalidGpu = _invalidCp = _cpUpdates = _hostStores = 0;
        return line;
    }
}
