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
            _survivals.Clear();
            _pendingDrops.Clear();
            _banned.Clear();
            _speculated.Clear();
            _auditSeen = 0;
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

    // [local] SHARPEMU_TABLE_WORD_MEMO_FAST_HIT (=1 on, off by default): a hit answered without waiting (Mode 1) took the lock
    // twice (TryGet, then NoteAnsweredHit) and read the clock on every hit for the 20 s learning report (MaybeReportLocked);
    // the open world answers ~4,400 table words a frame this way on the render thread. TryAnswer does the lookup, the hit
    // count, the speculation check and the audit sampling under one hold, in the same order, and looks at the clock once
    // every 1,024 hits. The same value, the same audits (the sample counter advances exactly as before): only the
    // learning report's cadence changes. The reader also asks the GPU-modified set once for the word instead of twice.
    internal static readonly bool FastHit = Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_FAST_HIT") == "1";
    private static int _answeredSinceReport;

    public static bool TryAnswer(ulong address, out uint value, out bool audit, out bool speculated)
    {
        lock (_lock)
        {
            if (!_values.TryGetValue(address, out value))
            {
                audit = speculated = false;
                Interlocked.Increment(ref _misses);
                return false;
            }

            Interlocked.Increment(ref _hits);
            speculated = false;
            if (LearnThreshold == 0 && AuditRate == 0)
            {
                audit = false;
                return true;
            }

            speculated = _speculated.Contains(address);
            if (speculated)
                _learnHits++;
            if ((++_answeredSinceReport & 1023) == 0)
                MaybeReportLocked();
            audit = AuditRate > 0 && _auditSeen++ % AuditRate == 0;
            return true;
        }
    }

    public static void NoteMiss() => Interlocked.Increment(ref _misses);

    public static void Store(ulong address, uint value)
    {
        lock (_lock)
        {
            if (LearnThreshold > 0) // [local] SHARPEMU_TABLE_WORD_MEMO_LEARN
            {
                LearnLocked(address, value);
                MaybeReportLocked();
            }
            StoreLocked(address, value);
        }
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
        if (LearnThreshold > 0) // [local] SHARPEMU_TABLE_WORD_MEMO_LEARN: =2 found the memo wrong
        {
            _banned.Add(address);
            _pendingDrops.Remove(address);
            _auditMismatches++;
        }
        if (_mismatchLogs++ < 40)
            Console.Error.WriteLine($"[TABLE_WORD_MEMO] mismatch address=0x{address:X} memo=0x{memo:X8} synchronized=0x{synchronized:X8} speculated={_speculated.Contains(address)}");
    }

    // A GPU write marked over [address, address+size) by the current program.
    public static void NoteGpuWrite(ulong address, ulong size)
    {
        if (Volatile.Read(ref _count) == 0 || Writers.Contains(CurrentProgram))
            return;
        lock (_lock)
        if (LearnThreshold > 0 ? InvalidateUntrusted(address, size, CurrentProgram) : Invalidate(address, size, 'g', CurrentProgram))
        {
            _invalidGpu++;
            _invalidByWriter[CurrentProgram] = _invalidByWriter.GetValueOrDefault(CurrentProgram) + 1;
        }
    }

    // [local] SHARPEMU_TABLE_WORD_MEMO_LEARN=<k> (8 by default, =0 off): the writer allowlist learned per (word, program) instead
    // of given by hash. When a program's written binding drops a remembered word, the program and the value are kept; if
    // the next synchronizing read of the word gets the same value, that (word, program) pair survived once. After k
    // survivals in a row the program's marks no longer drop that word (a different value resets the run). In GTA V the
    // memo for 994A +0x28/+0x2C/+0x74 (the TLAS header: offset and record count of the V# 994A writes through) was dropped
    // every frame by 5F74's binding, and the read then waited for the open batch behind 5981's refit (~1.5-1.9 ms/frame).
    // A word whose memo turns out wrong (SHARPEMU_TABLE_WORD_MEMO_AUDIT, or =2) is banned from learning for the session.
    internal static int LearnThreshold = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_LEARN"), out var learn) ? learn : 8;

    // [local] SHARPEMU_TABLE_WORD_MEMO_AUDIT=<n> (64 by default, =0 off): one memo hit in n, answered without waiting, also copies
    // the word from the device buffer at that point of the stream and compares it at completion with the value given
    // ([TABLE_WORD_MEMO_LEARN] audits=/audit_mismatches=, has to stay 0). =1 audits every hit. A mismatch bans the word.
    internal static int AuditRate = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_AUDIT"), out var audit) ? audit : 64;

    // [local] SHARPEMU_TABLE_WORD_MEMO_LEARN_FLUSH=1 (off by default): a hit that learning kept (the synchronizing read it
    // replaces would have flushed the open batch when the word's writer was in it, then waited) still flushes the batch,
    // without the wait. It only moves a batch boundary to where the read put it before, so it is exact by construction.
    // With LEARN=8 alone the open batch holding 5981's refit and what follows it reaches the GPU later (2026-11-02,
    // gta/runahead).
    internal static readonly bool LearnFlush = Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_LEARN_FLUSH") == "1";
    private static long _learnFlushes, _learnFlushChecks;

    public static void NoteLearnFlush(bool flushed)
    {
        Interlocked.Increment(ref _learnFlushChecks);
        if (flushed)
            Interlocked.Increment(ref _learnFlushes);
    }

    private static readonly Dictionary<(ulong Word, ulong Program), int> _survivals = new();
    private static readonly Dictionary<ulong, (ulong Program, uint Value)> _pendingDrops = new();
    private static readonly HashSet<ulong> _banned = new();
    private static readonly HashSet<ulong> _speculated = new();
    private static long _learnSkips, _learnedPairs, _learnResets, _learnHits, _auditSeen, _audits, _auditMismatches, _auditUnchecked;
    private static long _learnReport = System.Diagnostics.Stopwatch.GetTimestamp();
    private static int _auditMismatchLogs;

    internal static bool IsTrusted(ulong word, ulong program) =>
        !_banned.Contains(word) && _survivals.GetValueOrDefault((word, program)) >= LearnThreshold;

    private static bool InvalidateUntrusted(ulong address, ulong size, ulong program)
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
        {
            var word = _sorted[index];
            if (!_values.TryGetValue(word, out var old))
                continue;
            if (program != 0 && IsTrusted(word, program))
            {
                _learnSkips++;
                _speculated.Add(word);
                continue;
            }

            if (program != 0 && !_banned.Contains(word))
                _pendingDrops[word] = (program, old);
            if (SharpEmu.ShaderCompiler.Resources.SrtReadProbe.Enabled)
                _dropped[word] = ('g', program, old);
            _values.Remove(word);
            _speculated.Remove(word);
            removed = true;
        }
        if (removed)
        {
            _dirtySort = true;
            Volatile.Write(ref _count, _values.Count);
        }

        return removed;
    }

    // The value a synchronizing read got: one survival sample for the program that dropped the word since it was stored.
    private static void LearnLocked(ulong address, uint value)
    {
        _speculated.Remove(address);
        if (!_pendingDrops.Remove(address, out var drop))
            return;
        var key = (address, drop.Program);
        if (drop.Value != value)
        {
            if (_survivals.Remove(key))
                _learnResets++;
            return;
        }

        var survivals = _survivals.GetValueOrDefault(key) + 1;
        _survivals[key] = survivals;
        if (survivals == LearnThreshold)
            _learnedPairs++;
    }

    // A memo hit answered without waiting: whether to audit it against the device copy, and whether learning kept it.
    public static bool NoteAnsweredHit(ulong address, out bool speculated)
    {
        speculated = false;
        if (LearnThreshold == 0 && AuditRate == 0)
            return false;
        lock (_lock)
        {
            speculated = _speculated.Contains(address);
            if (speculated)
                _learnHits++;
            MaybeReportLocked();
            return AuditRate > 0 && _auditSeen++ % AuditRate == 0;
        }
    }

    public static void NoteAudit(ulong address, uint memo, uint device, bool speculated, bool checkedOnDevice)
    {
        lock (_lock)
        {
            if (!checkedOnDevice)
            {
                _auditUnchecked++;
                return;
            }

            _audits++;
            if (memo == device)
                return;
            _auditMismatches++;
            _banned.Add(address);
            _pendingDrops.Remove(address);
            if (_values.ContainsKey(address))
                Invalidate(address, 4, 'a', 0);
            if (_auditMismatchLogs++ < 40)
                Console.Error.WriteLine($"[TABLE_WORD_MEMO_LEARN] audit_mismatch address=0x{address:X} memo=0x{memo:X8} device=0x{device:X8} speculated={speculated}");
        }
    }

    private static void MaybeReportLocked()
    {
        if (LearnThreshold == 0 && AuditRate == 0)
            return;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_learnReport).TotalSeconds < 20)
            return;
        _learnReport = System.Diagnostics.Stopwatch.GetTimestamp();
        var trusted = _survivals.Count(pair => pair.Value >= LearnThreshold && LearnThreshold > 0);
        Console.Error.WriteLine($"[TABLE_WORD_MEMO_LEARN] k={LearnThreshold} audit={AuditRate} words={_values.Count} trusted_pairs={trusted} learned={_learnedPairs} resets={_learnResets} banned={_banned.Count} skips={_learnSkips} learned_hits={_learnHits} learn_flushes={_learnFlushes}/{_learnFlushChecks} audits={_audits} audit_mismatches={_auditMismatches} audit_unchecked={_auditUnchecked}");
    }

    // [local] SHARPEMU_TABLE_WORD_MEMO_CP_UPDATE (on by default since 2026-10-30, =0 off): a command-processor write carries its
    // data, applied in stream order, so a remembered word it fully covers takes the new value instead of being dropped
    // (CP-written descriptors in GPU-dirty pages, re-written each frame with the same layout). Partly covered words drop.
    // In GTA V the BLAS build parameters F43B, F6F2 and 994A read (blob+0x48/0x4C/0x70, +0x30, +0x28) are re-written
    // by the CP every frame with the same value; =2 of the memo with this on: 0 mismatches. Panning GpuCompletionWait
    // 10.6 -> 7.7 ms/frame (probe sessions); FPS A/B 4+4 pan 25.87 -> 25.98, 2+2 static 29.20 -> 29.40.
    internal static bool CommandUpdates = Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_CP_UPDATE") != "0";
    private static long _cpUpdates, _hostStores;

    public static void NoteCommandWrite(ulong address, ReadOnlySpan<byte> data)
    {
        if (Volatile.Read(ref _count) == 0)
            return;
        if (!CommandUpdates)
        {
            lock (_lock)
                if (Invalidate(address, (ulong)data.Length, 'w', 0))
                    _invalidCp++;
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
                else if (Invalidate(word, 4, 'p', 0))
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
            if (Invalidate(address, size, 'c', 0))
                _invalidCp++;
    }

    private static int _count;

    // [local] SHARPEMU_SRT_READ_PROBE: why each dropped word was dropped, and the value it held.
    private static readonly Dictionary<ulong, (char Kind, ulong Program, uint Value)> _dropped = new();

    // "hit", "new" (never remembered) or the drop's kind ('g' GPU write of a program, 'w' CP write, 'p' partial CP write,
    // 'c' CPU write fault or other host write), with whether the synchronized value equals the dropped one.
    public static string ProbeReason(ulong address, uint synchronized)
    {
        lock (_lock)
        {
            if (_values.ContainsKey(address))
                return "hit";
            if (!_dropped.TryGetValue(address, out var drop))
                return "new";
            return $"{drop.Kind}{(drop.Program != 0 ? $":{drop.Program >> 48:X4}" : "")}:{(drop.Value == synchronized ? "same" : "diff")}";
        }
    }

    private static bool Invalidate(ulong address, ulong size, char kind, ulong program)
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
        {
            if (SharpEmu.ShaderCompiler.Resources.SrtReadProbe.Enabled && _values.TryGetValue(_sorted[index], out var old))
                _dropped[_sorted[index]] = (kind, program, old);
            removed |= _values.Remove(_sorted[index]);
            _speculated.Remove(_sorted[index]);
        }
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
