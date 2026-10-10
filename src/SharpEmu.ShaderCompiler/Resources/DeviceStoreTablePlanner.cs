// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

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
    private static readonly Dictionary<(ulong, ulong), List<DeviceAddressRange>> Cache = new(); // [local]
    private static readonly int RescanEvery = int.Parse(System.Environment.GetEnvironmentVariable("SHARPEMU_STORE_TABLE_RESCAN") ?? "0"); // [local]
    private static long _uses;
    private static int _logs; // [local]
    public const ulong MaxEntryBytes = 64UL * 1024 * 1024;

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

            // [local experiment] the whole table, scanned once per table address and cached.
            if (Cache.TryGetValue((tableBase, tableBytes), out var cached) &&
                (RescanEvery == 0 || ++_uses % RescanEvery != 0)) // [local] periodic rescan
            {
                ranges.AddRange(cached);
                continue;
            }

            var entries = (int)System.Math.Min(tableBytes / 16, MaxEntries);
            var before = ranges.Count; // [local]
            for (var entry = 0; entry < entries; entry++)
            {
                var address = tableBase + (ulong)entry * 16;
                var readable = true;
                for (var dword = 0; dword < 4 && readable; dword++)
                {
                    readable = read(address + (ulong)dword * 4, out words[dword]);
                }

                if (readable && TryDecode(words, out var entryBase, out var entryBytes) && entryBytes <= MaxEntryBytes && (FilterGpuWritable ? SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.IsGpuWritable(entryBase, entryBytes) : entryBase + entryBytes <= TableMaxAddress))
                {
                    ranges.Add(new DeviceAddressRange(uint.MaxValue - (uint)ranges.Count, entryBase, entryBytes, Planned: true, Written: TableRangesWritten)); // [local]
                }
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

                    ranges.Add(new DeviceAddressRange(uint.MaxValue - (uint)ranges.Count, start, end - start, Planned: true, Written: TableRangesWritten));
                    start = range.Base;
                    end = range.Base + range.Size;
                }

                ranges.Add(new DeviceAddressRange(uint.MaxValue - (uint)ranges.Count, start, end - start, Planned: true, Written: TableRangesWritten));
            }

            lock (Cache) Cache[(tableBase, tableBytes)] = ranges.GetRange(before, ranges.Count - before);
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
