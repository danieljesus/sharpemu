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
    }

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
    private static int _tableLogs;

    private static string Describe(List<DeviceAddressRange> ranges) =>
        ranges.Count == 0 ? "0" : $"{ranges.Count}:" + string.Join(",", ranges.Take(6).Select(range => $"0x{range.Base:X}+0x{range.Size:X}")) + (ranges.Count > 6 ? ",..." : "");

    private static ulong _tableBegin = ulong.MaxValue, _tableEnd; // [local] bounds of every table ever cached

    // [local] SHARPEMU_PROFILE_STORE_TABLES=1: per report window, the tables evaluated, the cache hits, the scans and
    // the entries they read, and the cache entries dropped by each kind of write (InvalidationSource), with the
    // tables scanned most often. Camera moves rescan store tables of tens of thousands of entries on the front.
    internal static readonly bool Profile = System.Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_STORE_TABLES") == "1";
    private static long _evaluated, _hits, _scans, _scanEntries, _scanTicks, _bulkChunks, _bulkFallbacks, _bulkMismatches, _bulkRaces;
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
        return System.FormattableString.Invariant(
            $"[PERF][STORE_TABLES] evaluated={Interlocked.Exchange(ref _evaluated, 0)} hits={Interlocked.Exchange(ref _hits, 0)} scans={Interlocked.Exchange(ref _scans, 0)} scan_entries={Interlocked.Exchange(ref _scanEntries, 0)} scan_ms={Interlocked.Exchange(ref _scanTicks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} bulk_chunks={Interlocked.Exchange(ref _bulkChunks, 0)} bulk_fallbacks={Interlocked.Exchange(ref _bulkFallbacks, 0)} bulk_mismatches={Interlocked.Exchange(ref _bulkMismatches, 0)} bulk_races={Interlocked.Exchange(ref _bulkRaces, 0)} keys={keys.Length} drops(entries/calls/bytes)=[{drops}] top=[{top}]");
    }

    // [local] SHARPEMU_STORE_TABLE_BULK (1 by default, =0 turns it off): a scan reads the table in 64 KB chunks through the
    // resident reader, which applies the same ownership rules once per chunk, instead of four reader calls per
    // entry; a chunk the resident reader refuses is read word by word as before. The recorder keeps the same
    // words either way. =2 also reads every word through the word reader and counts the differences: a word whose
    // second resident read agrees with the word reader changed in between (a guest write racing the scan, bulk_races);
    // one that still differs is a difference between the two readers (bulk_mismatches, logged).
    private static readonly int BulkMode = int.TryParse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_BULK"), out var bulkMode) ? bulkMode : 1;
    private const int BulkChunkEntries = 4096;

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
            var chunk = bulk is null ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(BulkChunkEntries * 16);
            try
            {
                for (var first = 0; first < entries; first += BulkChunkEntries)
                {
                    var count = System.Math.Min(BulkChunkEntries, entries - first);
                    var chunkBytes = chunk is null ? default : chunk.AsSpan(0, count * 16);
                    var bulkRead = bulk is not null && bulk(tableBase + (ulong)first * 16, chunkBytes, false);
                    if (Profile && bulk is not null)
                        Interlocked.Increment(ref bulkRead ? ref _bulkChunks : ref _bulkFallbacks);
                    for (var entry = first; entry < first + count; entry++)
                    {
                        var address = tableBase + (ulong)entry * 16;
                        var readable = true;
                        if (bulkRead)
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
                        }
                    }
                }
            }
            finally
            {
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
            if (MergeGap != ulong.MaxValue && ranges.Count - before > 1) // [local] coalesce neighbouring V# ranges
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
            CommitScan(tableCache, tableBase, tableBytes, generation, ranges.GetRange(before, ranges.Count - before));
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
