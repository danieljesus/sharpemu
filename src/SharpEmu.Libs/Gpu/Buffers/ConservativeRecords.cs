// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_CONSERVATIVE_RECORDS (0 off by default, 1 on, 2 verify, 3 precise read, 4 its verify): a GPU-written table word that only sizes
// a buffer descriptor (its num_records) is not read back synchronously; the materialization takes a sentinel and the
// buffer is bound to the rest of the cache buffer that holds its base. Exact only for the listed words, each checked
// against the shader: GTA V's BVH refit 0x5981037B07E391D5 loads [s4+0x60] at pc 0x1C into s6, whose only use is
// 0x30 s10 = s6 >> 2, num_records of the V# s[8:11] (base s4 + [s4+0x40], stride 4), whose only access is the
// node-index load 0x44 at index v3 = gid >> 2 (s[8:11] is rebuilt at 0x11C and s6 overwritten at 0x240). The same
// word is the dispatch's indirect thread count (initiator 0x61, thread dimensions, read by the GPU), so with the
// count a multiple of 4 no lane reads at or past num_records and the wider binding reads the same bytes.
// =2 reads the word as before and counts the counts that are not a multiple of 4 ([CONSERVATIVE_RECORDS]).
// Off by default: the cache buffer holding the list is 21 MB on average (42 MB at most), and binding it on each of the
// refit's dispatches cost more than the wait it saves (A/B 2026-10-30, r8A*/r8C* against r8B*).
// SHARPEMU_CONSERVATIVE_RECORDS_WORDS=<hash>+<hex offset>[,...] (default 5981037B07E391D5+60).
internal static class ConservativeRecords
{
    public static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_CONSERVATIVE_RECORDS"), out var mode) ? (mode is >= 1 and <= 4 ? mode : 0) : 0;

    // =3: the listed words are still read, but each read waits only for the word's own last writer (the precise write
    // tick, GuestBufferCache.TryPreciseRead) instead of its 16 KB block's tick; =4 captures that precise copy and checks
    // it against the ordinary read. Needs the write-history ring, which this mode records.
    private static long _precise, _preciseFallbacks, _preciseChecks, _preciseMismatches;

    public static void NotePrecise(long reads, long fallbacks, long checks, long mismatches)
    {
        (_precise, _preciseFallbacks, _preciseChecks, _preciseMismatches) = (reads, fallbacks, checks, mismatches);
        Report();
    }

    // The sentinel word: num_records = 0x3FFFFFFF after the shader's shift, which no real record count reaches.
    public const uint Sentinel = 0xFFFF_FFFC;
    public const uint SentinelRecords = Sentinel >> 2;

    private static readonly HashSet<(ulong Hash, ulong Offset)> Words = (Environment.GetEnvironmentVariable("SHARPEMU_CONSERVATIVE_RECORDS_WORDS") ?? "5981037B07E391D5+60")
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Split('+'))
        .Select(parts => (Convert.ToUInt64(parts[0], 16), Convert.ToUInt64(parts[1], 16)))
        .ToHashSet();

    private static readonly HashSet<ulong> Hashes = Words.Select(word => word.Hash).ToHashSet();

    // [local] SHARPEMU_PRECISE_TABLE_READ (0 off by default, 1 on, 2 verify) with SHARPEMU_PRECISE_TABLE_WORDS=<hash>+<hex offset>[,...]
    // (default F5EB +0x50/+0x54 and 4481 +0xD8..+0xE4): the =3/=4 precise read of the listed table words, without the num_records lists above.
    // A listed word's synchronizing read waits only for its own last writer tick (GuestBufferCache.TryPreciseRead), not
    // for its 16 KB block's tick, and copies only the bytes whose own writer finished; exact by construction (a byte is
    // copied only once its last recorded GPU writer has completed), =2 also copies after the block tick and compares
    // ([READBACK_PRECISE_WAIT] verify_mismatches). Meant for GTA V's TLAS-chain reader 0xF5EB at struct+0x50: the CPU copy
    // is already right (census changed=0), its exact writer finished ~4 ticks earlier, but 0x5981's refit writes 0x18 B
    // at struct+0x0 of the same block in the tick being recorded, so with SHARPEMU_DEVICE_DATA_READS=B198 its read is the
    // one that flushes the open batch. 0x4481's V# at +0xD8 is the same pattern (writer 0x10CD's wide binding, changed=0).
    public static readonly int PreciseTableMode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PRECISE_TABLE_READ"), out var preciseMode) ? (preciseMode is 1 or 2 ? preciseMode : 0) : 0;

    private static readonly HashSet<(ulong Hash, ulong Offset)> PreciseWords = (Environment.GetEnvironmentVariable("SHARPEMU_PRECISE_TABLE_WORDS") ?? "F5EBBA997605508B+50,F5EBBA997605508B+54,4481C2A89BEA7646+D8,4481C2A89BEA7646+DC,4481C2A89BEA7646+E0,4481C2A89BEA7646+E4")
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Split('+'))
        .Select(parts => (Convert.ToUInt64(parts[0], 16), Convert.ToUInt64(parts[1], 16)))
        .ToHashSet();

    // Whether the reads are tracked at all (the reader hash and the write-history ring).
    public static bool Tracking => Mode != 0 || PreciseTableMode != 0;

    public static bool PreciseRecording => Mode >= 3 || PreciseTableMode != 0;

    public static bool MatchesPrecise(ulong reader, ulong baseAddress, ulong address) =>
        PreciseTableMode != 0 && baseAddress != 0 && address >= baseAddress && PreciseWords.Contains((reader, address - baseAddress));

    // The program whose resources are being materialized (set by ShaderProgramCache while the switch is on).
    [ThreadStatic] internal static ulong CurrentReader;

    private static long _sentinels, _checked, _notMultiple, _lastReport;

    public static bool Matches(ulong reader, ulong baseAddress, ulong address) =>
        Mode != 0 && baseAddress != 0 && address >= baseAddress && Words.Contains((reader, address - baseAddress));

    public static bool IsListedProgram(ulong hash) => Mode == 1 && Hashes.Contains(hash);

    public static bool IsVerifiedProgram(ulong hash) => Mode == 2 && Hashes.Contains(hash);

    private static long _noBuffer;
    private static readonly long[] _bindings = new long[16], _shorter = new long[16];

    public static void NoteNoBuffer(ulong fallback)
    {
        if (fallback != 0)
            _noBuffer++;
    }

    // =2: the extent the conservative binding would take for buffer `index` (0 without a cache buffer) against its exact
    // footprint; a shorter extent would cut records off, so the word's buffer must never count here.
    public static void VerifyBinding(int index, uint records, ulong requested, ulong extent)
    {
        if ((uint)index >= _bindings.Length)
            return;
        _bindings[index]++;
        if (extent < requested)
        {
            _shorter[index]++;
            if (_shorter[index] <= 4)
                Console.Error.WriteLine($"[CONSERVATIVE_RECORDS] buffer={index} records=0x{records:X} footprint=0x{requested:X} extent=0x{extent:X} (shorter)");
        }
    }

    private static long _extents;
    private static ulong _extentMax, _extentSum;

    // The extents the sentinel bindings took (count, mean, largest in [CONSERVATIVE_RECORDS]).
    public static ulong NoteExtent(ulong extent)
    {
        _extents++;
        _extentSum += extent;
        _extentMax = Math.Max(_extentMax, extent);
        return extent;
    }

    public static void NoteSentinel()
    {
        _sentinels++;
        Report();
    }

    // =2: the word as read back is the shader's thread count; a count that is not a multiple of 4 would let the last
    // lane index the record at num_records, which the exact binding answers with zero.
    public static void Verify(uint word)
    {
        _checked++;
        if ((word & 3) != 0)
        {
            _notMultiple++;
            if (_notMultiple <= 20)
                Console.Error.WriteLine($"[CONSERVATIVE_RECORDS] count=0x{word:X} is not a multiple of 4");
        }

        Report();
    }

    private static void Report()
    {
        var now = Environment.TickCount64;
        if (now - _lastReport < 20_000)
            return;
        _lastReport = now;
        Console.Error.WriteLine($"[CONSERVATIVE_RECORDS] mode={Mode} precise_reads={_precise} precise_fallbacks={_preciseFallbacks} precise_checks={_preciseChecks} precise_mismatches={_preciseMismatches} sentinels={_sentinels} checked={_checked} not_multiple_of_4={_notMultiple} no_buffer={_noBuffer} extents={_extents} extent_mean=0x{(_extents == 0 ? 0 : _extentSum / (ulong)_extents):X} extent_max=0x{_extentMax:X} bindings=[{string.Join(",", _bindings.TakeWhile((_, i) => i < 8))}] shorter=[{string.Join(",", _shorter.TakeWhile((_, i) => i < 8))}]");
    }
}
