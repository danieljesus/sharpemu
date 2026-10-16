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

    public static void InvalidateTables(ulong address, ulong size, int source = 0) // [local]
    {
        // Cheap reject first: this runs on every command-processor write, and IsEmpty/Keys on a
        // ConcurrentDictionary take all its locks (rt429: ~2 ms per frame of lock waits).
        if (size == 0 || address >= Volatile.Read(ref _tableEnd) || address + size <= Volatile.Read(ref _tableBegin))
            return;
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
            var tableCache = Cache;
            var generation = Interlocked.Read(ref tableCache.Generation);
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
            if (Interlocked.Read(ref tableCache.Generation) == generation) // a write that landed during the scan keeps it out
            {
                lock (AllCaches) // bounds before the entry, so an invalidation never misses it
                {
                    if (tableBase < _tableBegin) Volatile.Write(ref _tableBegin, tableBase);
                    if (tableBase + tableBytes > _tableEnd) Volatile.Write(ref _tableEnd, tableBase + tableBytes);
                }

                tableCache.Entries[(tableBase, tableBytes)] = ranges.GetRange(before, ranges.Count - before);
            }
            if (_logs++ < 40 && System.Environment.GetEnvironmentVariable("SHARPEMU_LOG_GLOBAL_STORES") == "1") // [local]
                System.Console.Error.WriteLine($"[STORE_TABLE] hash=0x{plan.Hash:X16} table=0x{tableBase:X} bytes=0x{tableBytes:X} ranges={ranges.Count - before} first={(ranges.Count > before ? $"0x{ranges[before].Base:X}+0x{ranges[before].Size:X}" : "-")}");
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
