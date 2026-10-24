// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// [local] SHARPEMU_PROGRAM_GATE_TABLES=3: a cached materialization of a plan with a store table serves the ranges a
// fresh walk would, after a table write dropped the scan, without walking the plan again.
public sealed class CurrentStoreTablesTests
{
    private sealed class Memory
    {
        public readonly Dictionary<ulong, uint> Words = new();

        public bool Read(ulong address, out uint word) => Words.TryGetValue(address, out word);

        public void SetEntry(ulong address, ulong baseAddress, uint bytes)
        {
            Words[address] = (uint)baseAddress;
            Words[address + 4] = (uint)(baseAddress >> 32);
            Words[address + 8] = bytes;
            Words[address + 12] = 0;
        }
    }

    // s[0:3] (user data) is the table's V#; the store's V# is loaded from it at a lane-dependent offset.
    private static ShaderResourcePlan Plan() => Extract(Program(
        ReadFirstLane(0, 16, 0),
        ScalarBufferLoad(4, 0, destination: 8, count: 4, dynamicOffsetRegister: 16),
        BufferStore(12, 8),
        EndProgram(20)));

    private static ResourceSnapshot Run(ResourceMaterializationCache cache, ShaderResourcePlan plan, Memory memory, uint[] userData)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, Inputs(userData, memory.Read, memory.Read), static (_, _, _) => false,
            ref snapshot, ref specialization, out _));
        return snapshot;
    }

    private static ResourceSnapshot Walk(ShaderResourcePlan plan, Memory memory, uint[] userData)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref snapshot, ref specialization));
        return snapshot;
    }

    private static (ulong, ulong, bool)[] Ranges(ResourceSnapshot snapshot) =>
        [.. snapshot.DeviceAddressRanges.Select(range => (range.Base, range.Size, range.Written))];

    [Fact]
    public void AHitAfterATableWriteServesTheRangesOfAFreshWalk()
    {
        const ulong table = 0x7E0000000;
        var plan = Plan();
        Assert.Single(plan.DeviceStoreTables);
        var memory = new Memory();
        memory.SetEntry(table, 0x10000, 0x100);
        memory.SetEntry(table + 16, 0, 0);
        uint[] userData = [unchecked((uint)table), (uint)(table >> 32), 0x20, 0];
        var cache = new ResourceMaterializationCache(stampStoreTables: false, currentStoreTables: true);

        var first = Run(cache, plan, memory, userData);
        Assert.Contains((0x10000UL, 0x100UL, true), Ranges(first));

        memory.SetEntry(table + 16, 0x30000, 0x80);
        DeviceStoreTablePlanner.InvalidateTables(table + 16, 16, DeviceStoreTablePlanner.InvalidationSource.CpuFault);
        var second = Run(cache, plan, memory, userData);

        Assert.Equal(1, cache.Hits);
        Assert.Contains((0x30000UL, 0x80UL, true), Ranges(second));
        Assert.Equal(Ranges(Walk(plan, memory, userData)), Ranges(second));
    }

    [Fact]
    public void AnEntryKeepsNoneOfTheScannedTableWords()
    {
        const ulong table = 0x7E1000000;
        var plan = Plan();
        var memory = new Memory();
        memory.SetEntry(table, 0x10000, 0x100);
        memory.SetEntry(table + 16, 0x20000, 0x100);
        uint[] userData = [unchecked((uint)table), (uint)(table >> 32), 0x20, 0];
        var cache = new ResourceMaterializationCache(currentStoreTables: true);

        var first = Run(cache, plan, memory, userData);
        // A word of the second entry changes with no invalidation: the scan cache still holds the table, so a fresh
        // walk keeps the scanned ranges, and the entry, which did not record the word, still hits.
        memory.Words[table + 16 + 8] = 0x200;
        var second = Run(cache, plan, memory, userData);

        Assert.Equal(1, cache.Hits);
        Assert.Equal(Ranges(first), Ranges(second));
        Assert.Equal(Ranges(Walk(plan, memory, userData)), Ranges(second));
    }
}
