// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// A buffer store whose V# the shader loads from scalar-buffer data goes through the
// device-address page table, which only covers memory the host has registered. The
// first store into a range nobody registered is lost. When the V# comes from a table
// the host can locate (the table's own V# is a runtime value), every V# in that table
// is a range the program may write, so the host registers them before the dispatch.
public static class DeviceStoreTablePlanner
{
    public const int MaxEntries = 1 << 17;
    private static readonly bool FilterGpuWritable = System.Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_STORE_TABLES_FILTER") == "gpu"; // [local]
    private static readonly ulong MergeGap = System.Convert.ToUInt64(System.Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_STORE_TABLES_MERGE") ?? "10000", 16); // [local]
    private static readonly ulong TableMaxAddress = System.Convert.ToUInt64(System.Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_STORE_TABLES_MAXADDR") ?? "1000000000", 16); // [local]
    private static readonly bool TableRangesWritten = System.Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_STORE_TABLES_WRITTEN") != "0"; // [local]
    // [local] Ranges per table, scanned once; a command-processor, DMA or CPU write into a
    // table drops its entry (InvalidateTables), so a table refilled for the next build is rescanned.
    // One cache per thread: with the render pipeline the front sees queued command-processor
    // writes before the render thread applies them, so the two threads must not share scans.
    private sealed class TableCache
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Base, ulong Bytes), List<DeviceAddressRange>> Entries = new();
        public long Generation;
        // [local] SHARPEMU_STORE_TABLE_DIFF: the bytes and ranges of the owner thread's last scan of each table, kept
        // across drops; only the owner thread touches it.
        public readonly Dictionary<(ulong Base, ulong Bytes), RawScan> Raw = new();
    }

    private sealed class RawScan
    {
        public byte[] Bytes = [];
        public byte[]? Spare;
        public List<DeviceAddressRange>? Ranges;
    }

    // [local] SHARPEMU_STORE_TABLE_DIFF (=0 off, =1 on, the default, =2 check): a scan reads the table and compares
    // it, chunk by chunk, with the bytes of the thread's last scan of it; when they are the same it uses that scan's
    // ranges again: they are a pure function of the bytes, and the same list keeps the materializations that used it
    // (GATE_TABLES=3) on their fast path. A drop still rereads every byte, as a full scan would; only the decode,
    // sort and merge are skipped (GTA: the CPU rewrites 8 identical bytes of a 2 MB table every frame, 1.9 -> 0.33
    // ms per rescan). Any chunk the resident reader refuses scans the table the old way. =2 decodes anyway and
    // counts the differences.
    // [local] SHARPEMU_STORE_TABLE_COMPARE: what the render thread's resident read of a table chunk would see, compared
    // with expected in place.
    public enum TableChunkCompare { Uncompared, Equal, Different, Refused }

    internal static int DiffMode = int.TryParse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_DIFF"), out var diffMode) ? diffMode : 1;

    // [local] SHARPEMU_STORE_TABLE_COMPARE (=0 off, the default, =1 on, =2 verify; with SHARPEMU_STORE_TABLE_DIFF=1): the
    // rescan of a dropped table compares each chunk with the last scan's bytes where the resident reader would read
    // them (the in-place comparer: same ownership checks, same backing bytes) instead of copying the chunk out first
    // and comparing the copy. A chunk the comparer finds equal is the chunk the copy would have found equal; a chunk it
    // finds different, or cannot compare (the front thread, several backing views), takes the copy path as before. A
    // chunk the resident reader would refuse (GPU-owned) is synchronized as ReadTableChunk does and compared again, so
    // the bytes the decode sees and the synchronizations are the same. GTA: a 2 MB table the CPU rewrites
    // with identical bytes every frame, 0.3-1 ms of copies per presentation interval on the render thread. =2 also
    // copies every chunk found equal and counts the copies that differ (a second compare that agrees with the copy
    // means a write raced the two reads). Measured (2026-11-03): verify 0 mismatches in 95k equal chunks (~12 chunks,
    // ~0.8 MB of copies a frame avoided); render CommandStream 30.65 vs 30.92 ms normal, 47.2 vs 46.7 missed (noise);
    // panning 29.80 vs 29.80, static 30.09 vs 30.10: neutral, left off.
    public static readonly int CompareModeConfigured =
        int.TryParse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_COMPARE"), out var compareMode) ? compareMode : 0;

    private static readonly AsyncLocal<int?> CompareModeOverride = new();

    internal static int CompareMode
    {
        get => CompareModeOverride.Value ?? CompareModeConfigured;
        set => CompareModeOverride.Value = value;
    }

    private static long _compareEqual, _compareDiffer, _compareRefused, _compareChecked, _compareMismatches, _compareRaces;
    private static long _compareSynchronized, _compareUncompared;
    private static int _compareMismatchLogs;

    internal static (long Equal, long Differ, long Refused, long Checked, long Mismatches, long Races) CompareCounts =>
        (Interlocked.Read(ref _compareEqual), Interlocked.Read(ref _compareDiffer), Interlocked.Read(ref _compareRefused),
            Interlocked.Read(ref _compareChecked), Interlocked.Read(ref _compareMismatches), Interlocked.Read(ref _compareRaces));

    internal enum ChunkCompare { Copy, Equal, Read, Refused }

    // [local] SHARPEMU_STORE_TABLE_COMPARE: one chunk of a rescan against the previous scan's bytes, as ReadTableChunk would
    // read it. Equal: the chunk the resident reader would read equals previous (no copy). Read: it was synchronized and
    // its bytes are in probe, for the caller's compare. Refused: ReadTableChunk would have returned false. Copy: not
    // compared (the caller calls ReadTableChunk, which does what it did before: nothing has been read or synchronized).
    private static ChunkCompare CompareChunk(ResidentTableChunkComparer? compare, ResourceRuntimeInputs inputs, ResidentGuestBytesReader bulk,
        GuestWordReader read, ulong address, ReadOnlySpan<byte> previous, Span<byte> probe)
    {
        var mode = CompareMode;
        if (mode == 0 || compare is null || TableSyncMode == 2)
            return ChunkCompare.Copy;
        var result = compare(address, previous);
        if (result == TableChunkCompare.Refused)
        {
            // The resident read would refuse the chunk too: synchronize it as ReadTableChunk does, then compare again.
            Interlocked.Increment(ref _compareRefused);
            if (TableSyncMode == 0 || inputs.SynchronizeTableRange is not { } synchronize)
                return ChunkCompare.Refused;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var synchronized = synchronize([(address, previous.Length)]);
            var outcome = ChunkCompare.Refused;
            if (synchronized)
            {
                result = compare(address, previous);
                if (result == TableChunkCompare.Equal)
                    outcome = ChunkCompare.Equal;
                else if (result != TableChunkCompare.Refused && bulk(address, probe, false))
                    outcome = ChunkCompare.Read;
            }

            Interlocked.Add(ref _syncTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref outcome != ChunkCompare.Refused ? ref _syncChunks : ref _syncFailed);
            if (outcome != ChunkCompare.Equal)
                return outcome;
            Interlocked.Increment(ref _compareSynchronized);
        }
        else if (result != TableChunkCompare.Equal)
        {
            Interlocked.Increment(ref result == TableChunkCompare.Different ? ref _compareDiffer : ref _compareUncompared);
            return ChunkCompare.Copy;
        }

        Interlocked.Increment(ref _compareEqual);
        if (mode != 2)
            return ChunkCompare.Equal;
        var checkedCount = Interlocked.Increment(ref _compareChecked);
        if ((checkedCount & 0x3FF) == 0)
        {
            var counts = CompareCounts;
            System.Console.Error.WriteLine($"[STORE_TABLE_COMPARE] equal={counts.Equal} differ={counts.Differ} refused={counts.Refused} synchronized={Interlocked.Read(ref _compareSynchronized)} uncompared={Interlocked.Read(ref _compareUncompared)} checked={counts.Checked} mismatches={counts.Mismatches} races={counts.Races}");
        }

        // The copy decides, as without the compare; the chunk is resident now, so the resident read does not refuse it.
        if (!bulk(address, probe, false))
        {
            Interlocked.Increment(ref _compareMismatches);
            return ChunkCompare.Copy;
        }

        if (probe.SequenceEqual(previous))
            return ChunkCompare.Equal;
        if (compare(address, probe) == TableChunkCompare.Equal)
            Interlocked.Increment(ref _compareRaces);
        else
        {
            Interlocked.Increment(ref _compareMismatches);
            if (Interlocked.Increment(ref _compareMismatchLogs) <= 20)
                System.Console.Error.WriteLine($"[STORE_TABLE_COMPARE_MISMATCH] chunk=0x{address:X} bytes=0x{previous.Length:X}");
        }

        return ChunkCompare.Read;
    }

    // [local] SHARPEMU_STORE_TABLE_SYNC (=1 on, the default, =0 off, =2 verify): a chunk the resident reader refuses
    // because the GPU may own some of its bytes is synchronized whole and read again through the resident reader,
    // instead of word by word. The word reader synchronizes each GPU-dirty word before reading the backing, one
    // readback per 64 KB window; this does it once for the chunk (the readback window covers it), and its download
    // goes through the table readback queue, so it waits only for the writer ticks of the pieces and not behind the
    // asynchronous copies queued for later ticks. GTA: 10CD and 6A53 bind written a V# of the 2 MB table that points
    // inside the table (0x55F8C7820+~0x12000; they pick the V# with a waterfall, so no exact range), which makes one or
    // two chunks GPU-dirty after every dispatch; ~11 ms per rescan, two ~5.7 ms readbacks of a writer done ~33 ticks
    // before. A chunk still refused after the readback reads word by word as before. =2 also reads every word of each
    // synchronized chunk through the word reader and counts the differences; a word the resident reader then reads as
    // the word reader did changed in between (a guest write racing the scan, races), as STORE_TABLE_BULK=2 tells them.
    public static readonly int TableSyncMode = int.TryParse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_SYNC"), out var tableSyncMode) ? tableSyncMode : 1;
    private static long _syncChunks, _syncFailed, _syncTicks, _syncVerifiedWords, _syncMismatches, _syncUnread, _syncRaces;
    private static int _syncMismatchLogs;

    private static string TableSyncReport() => TableSyncMode == 0 ? string.Empty : System.FormattableString.Invariant(
        $" table_sync=[chunks={Interlocked.Exchange(ref _syncChunks, 0)} failed={Interlocked.Exchange(ref _syncFailed, 0)} ms={Interlocked.Exchange(ref _syncTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} verified_words={Interlocked.Read(ref _syncVerifiedWords)} mismatches={Interlocked.Read(ref _syncMismatches)} races={Interlocked.Read(ref _syncRaces)} unread={Interlocked.Read(ref _syncUnread)}]");

    // [local] SHARPEMU_STORE_TABLE_SYNC: one chunk of a table through the resident reader; a refusal synchronizes the chunk
    // in one readback and reads it again.
    private static bool ReadTableChunk(ResourceRuntimeInputs inputs, ResidentGuestBytesReader bulk, GuestWordReader read, ulong address, Span<byte> bytes)
    {
        if (bulk(address, bytes, false))
            return true;
        if (TableSyncMode == 0 || inputs.SynchronizeTableRange is not { } synchronize)
            return false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var synchronized = synchronize([(address, bytes.Length)]) && bulk(address, bytes, false);
        Interlocked.Add(ref _syncTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref synchronized ? ref _syncChunks : ref _syncFailed);
        if (synchronized && TableSyncMode == 2)
        {
            for (var offset = 0; offset + 4 <= bytes.Length; offset += 4)
            {
                var expected = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
                if (!read(address + (ulong)offset, out var word))
                {
                    Interlocked.Increment(ref _syncUnread);
                    continue;
                }

                Interlocked.Increment(ref _syncVerifiedWords);
                if (word == expected)
                    continue;
                // A guest write between the two reads changed the word: the resident reader now agrees with the word reader.
                Span<byte> again = stackalloc byte[4];
                if (bulk(address + (ulong)offset, again, false) && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(again) == word)
                {
                    Interlocked.Increment(ref _syncRaces);
                    continue;
                }

                Interlocked.Increment(ref _syncMismatches);
                if (Interlocked.Increment(ref _syncMismatchLogs) <= 20)
                    System.Console.Error.WriteLine($"[STORE_TABLE_SYNC_MISMATCH] address=0x{address + (ulong)offset:X} chunk=0x{address:X} synced=0x{expected:X8} word=0x{word:X8}");
            }

            var total = Interlocked.Read(ref _syncVerifiedWords);
            if ((total >> 16) != ((total - bytes.Length / 4) >> 16))
                System.Console.Error.WriteLine($"[STORE_TABLE_SYNC_VERIFY] verified_words={total} mismatches={Interlocked.Read(ref _syncMismatches)} races={Interlocked.Read(ref _syncRaces)} unread={Interlocked.Read(ref _syncUnread)}");
        }

        return synchronized;
    }
    private static long _diffReused, _diffDecoded, _diffRefused, _diffMismatches;
    private static int _refusedLogs;
    private const int RawScansKept = 8;

    [System.ThreadStatic]
    private static TableCache? _threadCache;
    private static readonly List<TableCache> AllCaches = new();

    // [local] Every invalidation inside the table bounds, whichever cache holds the table: a scan is cached only if none
    // landed while it ran. The per-cache generation only moved when that cache already held an overlapping entry, so a
    // write landing during the first scan of a table (or a rescan after a drop) from another thread was missed and the
    // pre-write ranges stayed cached until the next write.
    private static long _scanGeneration;

    // [local] SHARPEMU_STORE_TABLE_APPLIED_CHECK (on by default, =0 disables): the invalidations that change table memory
    // now (CPU faults, and command-processor, fill and copy writes the render thread applies), counted so a preparation
    // made ahead of the render thread (front or prep worker) can be checked when its draw or dispatch executes: a write
    // applied after the preparation read the tables precedes the draw in stream order and the preparation missed it.
    public static readonly bool AppliedCheck = System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_APPLIED_CHECK") != "0";
    private static long _appliedGeneration;

    public static long AppliedGeneration => Interlocked.Read(ref _appliedGeneration);

    // [local] SHARPEMU_PROGRAM_GATE_TABLE_STAMP: the scans an evaluation took from this thread's cache, or committed to
    // it (Committed false when the commit lost to an invalidation), while a caller records them. A cached scan's words
    // are not read again, so a materialization that used one holds only while the cache still holds the same scan.
    public readonly record struct UsedScan(ulong TableBase, ulong TableBytes, List<DeviceAddressRange> Ranges, bool Committed);

    [System.ThreadStatic]
    private static List<UsedScan>? _usedScans;

    [System.ThreadStatic]
    private static List<UsedScan>? _usedScansSpare;

    // [local] SHARPEMU_PROGRAM_GATE_TABLES=3: while set, a table scan's reads bypass the materialization recorder, so an
    // entry carries none of the table's words (up to 2 MB validated on every hit) and the cache checks the scans it
    // used instead (ResourceMaterializationCache.CurrentTables).
    [System.ThreadStatic]
    private static bool _unrecordedScans;

    [System.ThreadStatic]
    private static bool _scanning;

    public static bool ScanUnrecorded => _scanning;

    public static void BeginUsedScans(bool unrecorded = false)
    {
        _usedScans = _usedScansSpare ??= new();
        _usedScans.Clear();
        _unrecordedScans = unrecorded;
    }

    public static UsedScan[] EndUsedScans()
    {
        var used = _usedScans is { Count: > 0 } scans ? scans.ToArray() : [];
        _usedScans = null;
        _unrecordedScans = false;
        return used;
    }

    // [local] Ends a recording begun by BeginUsedScans: true when it used the same scan lists as previous, in order;
    // otherwise the scans it used.
    public static bool EndUsedScansSame(UsedScan[] previous, out UsedScan[]? used)
    {
        var scans = _usedScans!;
        _usedScans = null;
        _unrecordedScans = false;
        used = null;
        var same = scans.Count == previous.Length;
        for (var index = 0; same && index < previous.Length; index++)
            same = ReferenceEquals(scans[index].Ranges, previous[index].Ranges);
        if (!same)
            used = scans.ToArray();
        return same;
    }

    public static bool ScansCurrent(UsedScan[] used)
    {
        if (used.Length == 0)
            return true;
        var cache = Cache;
        foreach (var scan in used)
        {
            if (!scan.Committed || !cache.Entries.TryGetValue((scan.TableBase, scan.TableBytes), out var current) ||
                !ReferenceEquals(current, scan.Ranges))
                return false;
        }

        return true;
    }

    [System.ThreadStatic]
    private static bool _usedTables;

    // [local] Whether this thread evaluated a store table since the last call.
    public static bool TakeUsedTables()
    {
        var used = _usedTables;
        _usedTables = false;
        return used;
    }

    private static TableCache Cache
    {
        get
        {
            if (_threadCache is { } cache)
                return cache;
            cache = new TableCache();
            lock (AllCaches) AllCaches.Add(cache);
            return _threadCache = cache;
        }
    }
    private static readonly int RescanEvery = int.Parse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_RESCAN") ?? "0"); // [local]
    private static long _uses;
    private static int _logs; // [local]
    public const ulong MaxEntryBytes = 64UL * 1024 * 1024;

    // [local experiment] SHARPEMU_DEVICE_STORE_TABLES_WRITTEN_MIN=<hex bytes>: smaller table ranges are registered
    // but not marked written (a tiny range marked written makes its whole 4 KB page GPU-owned).
    private static readonly ulong WrittenMin = System.Convert.ToUInt64(System.Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_STORE_TABLES_WRITTEN_MIN") ?? "0", 16);

    private static bool MarksWritten(ulong bytes) => TableRangesWritten && bytes >= WrittenMin;

    // [local] SHARPEMU_STORE_TABLE_EXACT (0 by default): the whole-table scan marks every V# of the table written for
    // every program that picks a store V# from it (one 40 MB merged range for seven GTA programs, which made each frame's
    // readbacks wait for them). When every store handle of the program evaluates at dispatch (its table offsets are
    // uniform), the stores can only reach those V#s: =1 marks only them written and keeps the table's ranges registered
    // read-only; a handle that does not evaluate, or a V# whose bounds checks leave its extent open, keeps the scan as
    // it is. =2 keeps the scan written (Conservative) and the cache checks every readback byte the exact set leaves out.
    public static readonly int ExactMode = int.TryParse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_EXACT"), out var exactMode) ? exactMode : 0;
    private static long _exactPlans, _exactFallbacks, _exactRanges;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Hash, int Reason), long> _exactByHash = new();

    public static IReadOnlyList<ScalarValue> PlanHandles(ShaderResourcePlan plan)
    {
        var handles = new List<ScalarValue>();
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.Kind != MemoryResourceKind.Buffer || !memory.DeviceDescriptor ||
                memory.Access is not (MemoryAccess.Write or MemoryAccess.Atomic) ||
                plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.BufferHandle } handle)
            {
                continue;
            }

            if (!handles.Exists(existing => plan.Graph.Equivalent(existing, handle)))
                handles.Add(handle);
        }

        return handles;
    }

    // [local] The exact written ranges of the plan's store handles, or the reason they are not exact (1 a handle
    // does not evaluate, 2 a V# without a base or records, 3 a V# whose bounds checks leave the extent open).
    private static int TryExactRanges(ShaderResourcePlan plan, RuntimeValueEvaluator evaluator, List<DeviceAddressRange> exact)
    {
        Span<uint> words = stackalloc uint[4];
        foreach (var handle in plan.DeviceStoreHandles)
        {
            if (handle.Operands.Length != 4)
                return 1;
            for (var dword = 0; dword < 4; dword++)
            {
                if (!evaluator.Evaluate(handle.Operands[dword], out words[dword]))
                    return 1;
            }

            if (!TryDecode(words, out var baseAddress, out var bytes) || bytes > MaxEntryBytes)
                return 2;
            // The V# bounds every byte only when its checks cover the offset: OOB_SELECT 0 checks the index and the
            // offset against the stride, 3 checks a raw offset against the records. ADD_TID moves the address per lane.
            var stride = (words[1] >> 16) & 0x3FFF;
            var outOfBounds = (words[3] >> 28) & 3;
            var addTid = (words[3] >> 23) & 1;
            if (addTid != 0 || !(outOfBounds == 0 || (outOfBounds == 3 && stride == 0)))
                return 3;
            exact.Add(new DeviceAddressRange(uint.MaxValue - 0x8000u - (uint)exact.Count, baseAddress, bytes, Planned: true, Written: true));
        }

        return 0;
    }

    public static string? TakeExactReport()
    {
        if (ExactMode == 0)
            return null;
        var pairs = _exactByHash.ToArray();
        foreach (var (key, _) in pairs)
            _exactByHash.TryRemove(key, out _);
        var top = string.Join(",", pairs.OrderByDescending(pair => pair.Value).Take(12).Select(pair => $"{pair.Key.Hash:X16}/{pair.Key.Reason}:{pair.Value}"));
        return $"[PERF][STORE_TABLE_EXACT] mode={ExactMode} exact={Interlocked.Exchange(ref _exactPlans, 0)} fallback={Interlocked.Exchange(ref _exactFallbacks, 0)} ranges={Interlocked.Exchange(ref _exactRanges, 0)} by_hash(hash/reason)=[{top}]";
    }

    private static readonly bool TableLog = System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_LOG") == "1"; // [local]
    // [local] SHARPEMU_STORE_TABLE_COVER=<hex address>: logs every scanned table V# (before the merge) that covers the address.
    private static readonly ulong CoverWatch = System.Convert.ToUInt64(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_COVER") ?? "0", 16);
    private static int _coverLogs;
    private static int _tableLogs;

    private static string Describe(List<DeviceAddressRange> ranges) =>
        ranges.Count == 0 ? "0" : $"{ranges.Count}:" + string.Join(",", ranges.Take(6).Select(range => $"0x{range.Base:X}+0x{range.Size:X}")) + (ranges.Count > 6 ? ",..." : "");

    private static ulong _tableBegin = ulong.MaxValue, _tableEnd; // [local] bounds of every table ever cached

    // [local] SHARPEMU_PROFILE_STORE_TABLES=1: per report window, the tables evaluated, the cache hits, the scans and
    // the entries they read, and the cache entries dropped by each kind of write (InvalidationSource), with the
    // tables scanned most often. Camera moves rescan store tables of tens of thousands of entries on the front.
    internal static readonly bool Profile = System.Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_STORE_TABLES") == "1";
    private static long _evaluated, _hits, _scans, _scanEntries, _scanTicks, _bulkChunks, _bulkFallbacks, _bulkMismatches, _bulkRaces;
    private static long _totalScanTicks; // [local] the scan with its sort and merge
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Scans, long Ticks)> _scansByThread = new();
    private static int _bulkMismatchLogs;
    private static readonly string[] SourceNames = ["other", "cpu_fault", "cp_front", "dma_front", "cp_render", "fill_render", "copy_render"];
    private static readonly long[] _dropCalls = new long[SourceNames.Length];
    private static readonly long[] _drops = new long[SourceNames.Length];
    private static readonly long[] _dropBytes = new long[SourceNames.Length];
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Base, ulong Bytes), long> _scannedKeys = new();

    public static class InvalidationSource
    {
        public const int CpuFault = 1, CommandFront = 2, DmaFront = 3, CommandRender = 4, FillRender = 5, CopyRender = 6;
    }

    public static string? TakeReport()
    {
        if (!Profile)
            return null;
        var keys = _scannedKeys.ToArray();
        foreach (var (key, _) in keys)
            _scannedKeys.TryRemove(key, out _);
        var drops = string.Join(" ", Enumerable.Range(1, SourceNames.Length - 1).Select(index =>
            $"{SourceNames[index]}={Interlocked.Exchange(ref _drops[index], 0)}/{Interlocked.Exchange(ref _dropCalls[index], 0)}/{Interlocked.Exchange(ref _dropBytes[index], 0)}b"));
        var top = string.Join(",", keys.OrderByDescending(pair => pair.Value).Take(4).Select(pair => $"0x{pair.Key.Base:X}+0x{pair.Key.Bytes:X}:{pair.Value}"));
        var threads = _scansByThread.ToArray();
        foreach (var (name, _) in threads)
            _scansByThread.TryRemove(name, out _);
        var byThread = string.Join(",", threads.Select(pair => System.FormattableString.Invariant($"{pair.Key}:{pair.Value.Scans}/{pair.Value.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms")));
        return System.FormattableString.Invariant(
            $"[PERF][STORE_TABLES] evaluated={Interlocked.Exchange(ref _evaluated, 0)} hits={Interlocked.Exchange(ref _hits, 0)} scans={Interlocked.Exchange(ref _scans, 0)} scan_entries={Interlocked.Exchange(ref _scanEntries, 0)} scan_ms={Interlocked.Exchange(ref _scanTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} bulk_chunks={Interlocked.Exchange(ref _bulkChunks, 0)} bulk_fallbacks={Interlocked.Exchange(ref _bulkFallbacks, 0)} bulk_mismatches={Interlocked.Exchange(ref _bulkMismatches, 0)} bulk_races={Interlocked.Exchange(ref _bulkRaces, 0)} total_ms={Interlocked.Exchange(ref _totalScanTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} diff_reused={Interlocked.Exchange(ref _diffReused, 0)} diff_decoded={Interlocked.Exchange(ref _diffDecoded, 0)} diff_refused={Interlocked.Exchange(ref _diffRefused, 0)} diff_mismatches={Interlocked.Read(ref _diffMismatches)} by_thread=[{byThread}] keys={keys.Length} drops(entries/calls/bytes)=[{drops}] top=[{top}]") + FastReport() + TableSyncReport();
    }

    // [local] SHARPEMU_STORE_TABLE_BULK (1 by default, =0 turns it off): a scan reads the table in 64 KB chunks through the
    // resident reader, which applies the same ownership rules once per chunk, instead of four reader calls per
    // entry; a chunk the resident reader refuses is read word by word as before. The recorder keeps the same
    // words either way. =2 also reads every word through the word reader and counts the differences: a word whose
    // second resident read agrees with the word reader changed in between (a guest write racing the scan, bulk_races);
    // one that still differs is a difference between the two readers (bulk_mismatches, logged).
    private static readonly int BulkMode = int.TryParse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_BULK"), out var bulkMode) ? bulkMode : 1;
    private const int BulkChunkEntries = 4096;

    // [local] SHARPEMU_STORE_TABLE_FAST (=1 on, =2 verify, 0 off; with SHARPEMU_STORE_TABLE_BULK=1): a chunk the resident
    // reader refuses is read again through it 4 KB at a time, and only the 4 KB pieces it refuses again go word by word:
    // the resident reader accepts a piece under the same ownership rules as the whole chunk, and the word reader returns
    // the same words for memory it accepts. =2 also reads every word of an accepted piece of a refused chunk through
    // the word reader and counts the differences as races (a second resident read agrees with the word reader) or
    // mismatches.
    public static readonly int FastMode = int.TryParse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_FAST"), out var fastMode) ? fastMode : 0;
    private const int PieceEntries = 256;
    private static long _fastPieces, _fastPieceFallbacks, _fastChecked, _fastRaces, _fastMismatches, _fastMismatchTotal;
    private static int _fastMismatchLogs;

    private static string FastReport() => FastMode == 0 ? string.Empty : System.FormattableString.Invariant(
        $" fast_pieces={Interlocked.Exchange(ref _fastPieces, 0)} fast_piece_fallbacks={Interlocked.Exchange(ref _fastPieceFallbacks, 0)} fast_checked={Interlocked.Exchange(ref _fastChecked, 0)} fast_races={Interlocked.Exchange(ref _fastRaces, 0)} fast_mismatches={Interlocked.Exchange(ref _fastMismatches, 0)} fast_mismatches_total={Interlocked.Read(ref _fastMismatchTotal)}");

    // [local] Before a table is scanned: its bounds join the invalidation filter first, so a write landing during the
    // scan is counted; the generation the scan starts from.
    internal static long BeginScan(ulong tableBase, ulong tableBytes)
    {
        lock (AllCaches)
        {
            if (tableBase < _tableBegin) Volatile.Write(ref _tableBegin, tableBase);
            if (tableBase + tableBytes > _tableEnd) Volatile.Write(ref _tableEnd, tableBase + tableBytes);
        }

        return Interlocked.Read(ref _scanGeneration);
    }

    // [local] After the scan: cached only if no invalidation landed while it ran. True when cached.
    internal static bool CommitScan(ulong tableBase, ulong tableBytes, long generation, List<DeviceAddressRange> ranges) =>
        CommitScan(Cache, tableBase, tableBytes, generation, ranges);

    private static bool CommitScan(TableCache cache, ulong tableBase, ulong tableBytes, long generation, List<DeviceAddressRange> ranges)
    {
        if (Interlocked.Read(ref _scanGeneration) != generation)
            return false;
        cache.Entries[(tableBase, tableBytes)] = ranges;
        return true;
    }

    // [local] Tests: this thread's cached ranges of a table.
    internal static bool TryGetCached(ulong tableBase, ulong tableBytes, out List<DeviceAddressRange>? ranges) =>
        Cache.Entries.TryGetValue((tableBase, tableBytes), out ranges);

    public static void InvalidateTables(ulong address, ulong size, int source = 0) // [local]
    {
        // Cheap reject first: this runs on every command-processor write, and IsEmpty/Keys on a
        // ConcurrentDictionary take all its locks (rt429: ~2 ms per frame of lock waits).
        if (size == 0 || address >= Volatile.Read(ref _tableEnd) || address + size <= Volatile.Read(ref _tableBegin))
            return;
        Interlocked.Increment(ref _scanGeneration);
        if (source is InvalidationSource.CpuFault or InvalidationSource.CommandRender or InvalidationSource.FillRender or InvalidationSource.CopyRender)
            Interlocked.Increment(ref _appliedGeneration);
        TableCache[] caches;
        lock (AllCaches) caches = AllCaches.ToArray();
        var dropped = 0;
        foreach (var cache in caches)
        {
            foreach (var (key, _) in cache.Entries) // the enumerator takes no locks, unlike Keys
            {
                if (address < key.Base + key.Bytes && key.Base < address + size)
                {
                    Interlocked.Increment(ref cache.Generation);
                    if (cache.Entries.TryRemove(key, out _))
                        dropped++;
                }
            }
        }

        if (Profile && dropped != 0)
        {
            var kind = (uint)source < (uint)SourceNames.Length ? source : 0;
            Interlocked.Add(ref _drops[kind], dropped);
            Interlocked.Increment(ref _dropCalls[kind]);
            Interlocked.Add(ref _dropBytes[kind], (long)size);
        }
    }

    public static IReadOnlyList<ScalarValue> Plan(ShaderResourcePlan plan)
    {
        var tables = new List<ScalarValue>();
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.Kind != MemoryResourceKind.Buffer || !memory.DeviceDescriptor ||
                memory.Access is not (MemoryAccess.Write or MemoryAccess.Atomic) ||
                plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.BufferHandle } handle)
            {
                continue;
            }

            foreach (var dword in handle.Operands)
            {
                CollectTables(plan, dword, tables);
            }
        }

        return tables;
    }

    private static void CollectTables(ShaderResourcePlan plan, ScalarValue value, List<ScalarValue> tables)
    {
        var pending = new Stack<ScalarValue>();
        var visited = new HashSet<ScalarValue>();
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (current.Kind == ScalarValueKind.ScalarBufferWord)
            {
                if (current.Operands.Length >= 1 &&
                    current.Operands[0] is { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } table &&
                    System.Array.TrueForAll(table.Operands, operand => plan.ValidateRuntimeValue(operand)) &&
                    !tables.Exists(existing => plan.Graph.Equivalent(existing, table)))
                {
                    tables.Add(table);
                }

                continue;
            }

            foreach (var operand in current.Operands)
            {
                pending.Push(operand);
            }
        }
    }

    // The written ranges the V#s of the plan's store tables describe, for one dispatch.
    public static void Evaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, List<DeviceAddressRange> ranges)
    {
        if (plan.DeviceStoreTables.Count == 0 || inputs.ReadMemory is not { } read)
        {
            return;
        }

        using var scratch = RuntimeEvaluationScratch.Rent();
        var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs);
        List<DeviceAddressRange>? exact = null; // [local] SHARPEMU_STORE_TABLE_EXACT
        var tableStart = ranges.Count;
        if (ExactMode != 0 && plan.DeviceStoreHandles.Count != 0)
        {
            exact = new List<DeviceAddressRange>(plan.DeviceStoreHandles.Count);
            var reason = TryExactRanges(plan, evaluator, exact);
            _exactByHash.AddOrUpdate((plan.Hash, reason), 1, static (_, value) => value + 1);
            if (reason != 0)
            {
                exact = null;
                Interlocked.Increment(ref _exactFallbacks);
            }
            else
            {
                Interlocked.Increment(ref _exactPlans);
                Interlocked.Add(ref _exactRanges, exact.Count);
            }
        }

        Span<uint> words = stackalloc uint[4];
        Span<byte> again = stackalloc byte[4]; // [local] SHARPEMU_STORE_TABLE_BULK=2
        var fast = FastMode != 0 && BulkMode == 1; // [local] SHARPEMU_STORE_TABLE_FAST
        Span<bool> pieceRead = stackalloc bool[BulkChunkEntries / PieceEntries];
        foreach (var table in plan.DeviceStoreTables)
        {
            var known = true;
            for (var dword = 0; dword < 4 && known; dword++)
            {
                known = evaluator.Evaluate(table.Operands[dword], out words[dword]);
            }

            if (!known || !TryDecode(words, out var tableBase, out var tableBytes))
            {
                continue;
            }

            if (Profile)
                Interlocked.Increment(ref _evaluated);
            // [local experiment] the whole table, scanned once per table address and cached.
            _usedTables = true;
            var tableCache = Cache;
            if (tableCache.Entries.TryGetValue((tableBase, tableBytes), out var cached) &&
                (RescanEvery == 0 || ++_uses % RescanEvery != 0)) // [local] periodic rescan
            {
                ranges.AddRange(cached);
                _usedScans?.Add(new UsedScan(tableBase, tableBytes, cached, true));
                if (Profile)
                    Interlocked.Increment(ref _hits);
                if (TableLog && Interlocked.Increment(ref _tableLogs) <= 20000)
                    System.Console.Error.WriteLine($"[STORE_TABLE_USE] hash=0x{plan.Hash:X16} thread={System.Environment.CurrentManagedThreadId} table=0x{tableBase:X} bytes=0x{tableBytes:X} hit ranges={Describe(cached)}");
                continue;
            }

            var generation = BeginScan(tableBase, tableBytes);
            var entries = (int)System.Math.Min(tableBytes / 16, MaxEntries);
            var before = ranges.Count; // [local]
            var scanStarted = Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var bulk = BulkMode != 0 ? inputs.ReadResidentMemory : null;
            _scanning = _usedScans is not null && _unrecordedScans; // [local] only the scan: the V# words above stay recorded
            RawScan? raw = null; // [local] SHARPEMU_STORE_TABLE_DIFF
            byte[]? whole = null;
            List<DeviceAddressRange>? reuse = null, check = null;
            if (DiffMode != 0 && BulkMode == 1 && bulk is not null && !FilterGpuWritable)
            {
                if (!tableCache.Raw.TryGetValue((tableBase, tableBytes), out raw))
                {
                    if (tableCache.Raw.Count >= RawScansKept) // up to 4 MB each: a game that moves its tables starts over
                        tableCache.Raw.Clear();
                    tableCache.Raw[(tableBase, tableBytes)] = raw = new RawScan();
                }
                var length = entries * 16;
                var previous = raw.Ranges is not null && raw.Bytes.Length == length ? raw.Bytes : null;
                var differs = previous is null ? 0 : -1; // the first entry of the first chunk that differs, -1 none
                var refused = false;
                var probe = previous is null ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(BulkChunkEntries * 16);
                try
                {
                    // Chunk by chunk against the previous scan: an unchanged table is read once and never copied whole.
                    for (var first = 0; probe is not null && differs < 0 && first < entries; first += BulkChunkEntries)
                    {
                        var count = System.Math.Min(BulkChunkEntries, entries - first);
                        var bytes = probe.AsSpan(0, count * 16);
                        var compared = CompareChunk(inputs.CompareTableChunk, inputs, bulk, read, tableBase + (ulong)first * 16,
                            previous!.AsSpan(first * 16, count * 16), bytes); // [local] SHARPEMU_STORE_TABLE_COMPARE
                        if (compared == ChunkCompare.Equal)
                            continue;
                        if (compared == ChunkCompare.Refused ||
                            (compared == ChunkCompare.Copy && !ReadTableChunk(inputs, bulk, read, tableBase + (ulong)first * 16, bytes)))
                        {
                            refused = true;
                            if (Profile && Interlocked.Increment(ref _refusedLogs) <= 400)
                                System.Console.Error.WriteLine($"[STORE_TABLE_REFUSED] table=0x{tableBase:X} chunk=0x{tableBase + (ulong)first * 16:X} thread={System.Threading.Thread.CurrentThread.Name}");
                            break;
                        }

                        if (!bytes.SequenceEqual(previous!.AsSpan(first * 16, count * 16)))
                            differs = first;
                    }

                    if (!refused && differs >= 0)
                    {
                        whole = raw.Spare is { } spare && spare.Length == length ? spare : new byte[length];
                        if (previous is not null)
                            previous.AsSpan(0, differs * 16).CopyTo(whole); // the chunks just found equal
                        for (var first = differs; first < entries; first += BulkChunkEntries)
                        {
                            var count = System.Math.Min(BulkChunkEntries, entries - first);
                            if (first == differs && probe is not null)
                                probe.AsSpan(0, count * 16).CopyTo(whole.AsSpan(first * 16));
                            else if (!ReadTableChunk(inputs, bulk, read, tableBase + (ulong)first * 16, whole.AsSpan(first * 16, count * 16)))
                            {
                                refused = true;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    _scanning = false;
                    throw;
                }
                finally
                {
                    if (probe is not null)
                        System.Buffers.ArrayPool<byte>.Shared.Return(probe);
                }

                if (refused)
                {
                    whole = null;
                    Interlocked.Increment(ref _diffRefused);
                }
                else if (differs < 0)
                {
                    if (DiffMode == 2)
                        (check, whole) = (raw.Ranges, previous);
                    else
                        reuse = raw.Ranges;
                }
            }

            var chunk = bulk is null || whole is not null ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(BulkChunkEntries * 16);
            try
            {
                for (var first = 0; reuse is null && first < entries; first += BulkChunkEntries)
                {
                    var count = System.Math.Min(BulkChunkEntries, entries - first);
                    var chunkBytes = whole is not null ? whole.AsSpan(first * 16, count * 16) : chunk is null ? default : chunk.AsSpan(0, count * 16);
                    var bulkRead = whole is not null || (bulk is not null && ReadTableChunk(inputs, bulk, read, tableBase + (ulong)first * 16, chunkBytes));
                    if (Profile && bulk is not null)
                        Interlocked.Increment(ref bulkRead ? ref _bulkChunks : ref _bulkFallbacks);
                    var piecesRead = false;
                    if (!bulkRead && fast && bulk is not null) // [local] the refused chunk again, 4 KB at a time
                    {
                        for (var piece = 0; piece * PieceEntries < count; piece++)
                        {
                            var pieceFirst = piece * PieceEntries;
                            var pieceCount = System.Math.Min(PieceEntries, count - pieceFirst);
                            pieceRead[piece] = bulk(tableBase + (ulong)(first + pieceFirst) * 16, chunkBytes.Slice(pieceFirst * 16, pieceCount * 16), false);
                            piecesRead |= pieceRead[piece];
                            Interlocked.Increment(ref pieceRead[piece] ? ref _fastPieces : ref _fastPieceFallbacks);
                        }
                    }

                    for (var entry = first; entry < first + count; entry++)
                    {
                        var address = tableBase + (ulong)entry * 16;
                        var readable = true;
                        var pieceBytes = !bulkRead && piecesRead && pieceRead[(entry - first) / PieceEntries];
                        if (bulkRead || pieceBytes)
                        {
                            var offset = (entry - first) * 16;
                            for (var dword = 0; dword < 4; dword++)
                                words[dword] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(chunkBytes[(offset + dword * 4)..]);
                            if (BulkMode == 2)
                            {
                                for (var dword = 0; dword < 4; dword++)
                                {
                                    if (read(address + (ulong)dword * 4, out var checkWord) && checkWord == words[dword])
                                        continue;
                                    if (bulk!(address + (ulong)dword * 4, again, false) &&
                                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(again) == checkWord)
                                    {
                                        Interlocked.Increment(ref _bulkRaces);
                                        continue;
                                    }

                                    Interlocked.Increment(ref _bulkMismatches);
                                    if (Interlocked.Increment(ref _bulkMismatchLogs) <= 20)
                                        System.Console.Error.WriteLine($"[STORE_TABLE_BULK_MISMATCH] thread={System.Environment.CurrentManagedThreadId} address=0x{address + (ulong)dword * 4:X} bulk=0x{words[dword]:X8} word=0x{checkWord:X8}");
                                }
                            }

                            if (pieceBytes && FastMode == 2) // [local] SHARPEMU_STORE_TABLE_FAST=2
                            {
                                for (var dword = 0; dword < 4; dword++)
                                {
                                    Interlocked.Increment(ref _fastChecked);
                                    if (read(address + (ulong)dword * 4, out var checkWord) && checkWord == words[dword])
                                        continue;
                                    if (bulk!(address + (ulong)dword * 4, again, false) &&
                                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(again) == checkWord)
                                    {
                                        Interlocked.Increment(ref _fastRaces);
                                        continue;
                                    }

                                    Interlocked.Increment(ref _fastMismatches);
                                    Interlocked.Increment(ref _fastMismatchTotal);
                                    if (Interlocked.Increment(ref _fastMismatchLogs) <= 20)
                                        System.Console.Error.WriteLine($"[STORE_TABLE_FAST_MISMATCH] thread={System.Environment.CurrentManagedThreadId} address=0x{address + (ulong)dword * 4:X} piece=0x{words[dword]:X8} word=0x{checkWord:X8}");
                                }
                            }
                        }
                        else
                        {
                            for (var dword = 0; dword < 4 && readable; dword++)
                            {
                                readable = read(address + (ulong)dword * 4, out words[dword]);
                            }
                        }

                        if (readable && TryDecode(words, out var entryBase, out var entryBytes) && entryBytes <= MaxEntryBytes && (FilterGpuWritable ? SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.IsGpuWritable(entryBase, entryBytes) : entryBase + entryBytes <= TableMaxAddress))
                        {
                            ranges.Add(new DeviceAddressRange(uint.MaxValue - (uint)ranges.Count, entryBase, entryBytes, Planned: true, Written: MarksWritten(entryBytes))); // [local]
                            if (CoverWatch != 0 && CoverWatch >= entryBase && CoverWatch - entryBase < entryBytes && Interlocked.Increment(ref _coverLogs) <= 400) // [local] SHARPEMU_STORE_TABLE_COVER
                                System.Console.Error.WriteLine($"[STORE_TABLE_COVER] hash=0x{plan.Hash:X16} table=0x{tableBase:X} entry={entry} vsharp=0x{entryBase:X}+0x{entryBytes:X} words={words[0]:X8} {words[1]:X8} {words[2]:X8} {words[3]:X8}");
                        }
                    }
                }
            }
            finally
            {
                _scanning = false;
                if (chunk is not null)
                    System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
            }

            if (Profile)
            {
                Interlocked.Increment(ref _scans);
                Interlocked.Add(ref _scanEntries, entries);
                Interlocked.Add(ref _scanTicks, System.Diagnostics.Stopwatch.GetTimestamp() - scanStarted);
                _scannedKeys.AddOrUpdate((tableBase, tableBytes), 1, static (_, value) => value + 1);
            }
            if (reuse is not null)
                ranges.AddRange(reuse);
            else if (MergeGap != ulong.MaxValue && ranges.Count - before > 1) // [local] coalesce neighbouring V# ranges
            {
                var found = ranges.GetRange(before, ranges.Count - before);
                found.Sort((a, b) => a.Base.CompareTo(b.Base));
                ranges.RemoveRange(before, ranges.Count - before);
                var start = found[0].Base;
                var end = found[0].Base + found[0].Size;
                foreach (var range in found)
                {
                    if (range.Base <= end + MergeGap && (!FilterGpuWritable || SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.IsGpuWritable(start, System.Math.Max(end, range.Base + range.Size) - start)))
                    {
                        end = System.Math.Max(end, range.Base + range.Size);
                        continue;
                    }

                    ranges.Add(new DeviceAddressRange(uint.MaxValue - (uint)ranges.Count, start, end - start, Planned: true, Written: MarksWritten(end - start)));
                    start = range.Base;
                    end = range.Base + range.Size;
                }

                ranges.Add(new DeviceAddressRange(uint.MaxValue - (uint)ranges.Count, start, end - start, Planned: true, Written: MarksWritten(end - start)));
            }

            if (TableLog && Interlocked.Increment(ref _tableLogs) <= 20000)
                System.Console.Error.WriteLine($"[STORE_TABLE_USE] hash=0x{plan.Hash:X16} thread={System.Environment.CurrentManagedThreadId} table=0x{tableBase:X} bytes=0x{tableBytes:X} scan ranges={Describe(ranges.GetRange(before, ranges.Count - before))}");
            var scanned = reuse ?? ranges.GetRange(before, ranges.Count - before);
            if (raw is not null)
            {
                if (Profile)
                    Interlocked.Increment(ref reuse is not null ? ref _diffReused : ref _diffDecoded);
                if (check is not null && !SameRanges(check, scanned) && Interlocked.Increment(ref _diffMismatches) <= 20)
                    System.Console.Error.WriteLine($"[STORE_TABLE_DIFF_MISMATCH] table=0x{tableBase:X} bytes=0x{tableBytes:X} kept={Describe(check)} decoded={Describe(scanned)}");
                if (whole is not null && reuse is null)
                {
                    if (!ReferenceEquals(raw.Bytes, whole))
                    {
                        raw.Spare = raw.Bytes.Length == whole.Length ? raw.Bytes : null;
                        raw.Bytes = whole;
                    }

                    raw.Ranges = scanned;
                }
            }

            if (Profile)
            {
                var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - scanStarted;
                Interlocked.Add(ref _totalScanTicks, ticks);
                _scansByThread.AddOrUpdate((System.Threading.Thread.CurrentThread.Name ?? "other") + (reuse is not null ? "/reused" : ""), (1, ticks), (_, value) => (value.Scans + 1, value.Ticks + ticks));
            }
            var committed = CommitScan(tableCache, tableBase, tableBytes, generation, scanned);
            _usedScans?.Add(new UsedScan(tableBase, tableBytes, scanned, committed));
            if (_logs++ < 40 && System.Environment.GetEnvironmentVariable("SHARPEMU_LOG_GLOBAL_STORES") == "1") // [local]
                System.Console.Error.WriteLine($"[STORE_TABLE] hash=0x{plan.Hash:X16} table=0x{tableBase:X} bytes=0x{tableBytes:X} ranges={ranges.Count - before} first={(ranges.Count > before ? $"0x{ranges[before].Base:X}+0x{ranges[before].Size:X}" : "-")}");
        }

        if (exact is not null) // [local] the table's ranges stay registered; only the exact V#s are written
        {
            for (var index = tableStart; index < ranges.Count; index++)
            {
                if (ranges[index].Written)
                    ranges[index] = ExactMode == 2 ? ranges[index] with { Conservative = true } : ranges[index] with { Written = false };
            }

            ranges.AddRange(exact);
        }
    }

    // [local] Two scans' ranges, labels aside.
    private static bool SameRanges(List<DeviceAddressRange> left, List<DeviceAddressRange> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] with { Handle = 0 } != right[index] with { Handle = 0 })
                return false;
        }

        return true;
    }

    // Base and byte size of a buffer V#: 48-bit base, a 14-bit stride, and a record
    // count that counts bytes when the stride is zero.
    private static bool TryDecode(ReadOnlySpan<uint> words, out ulong baseAddress, out ulong bytes)
    {
        baseAddress = words[0] | ((ulong)(words[1] & 0xFFFF) << 32);
        var stride = (words[1] >> 16) & 0x3FFF;
        bytes = stride == 0 ? words[2] : (ulong)stride * words[2];
        return baseAddress != 0 && bytes != 0;
    }
}
