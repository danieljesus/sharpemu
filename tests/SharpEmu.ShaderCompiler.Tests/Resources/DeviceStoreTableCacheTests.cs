// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// [local] The store-table cache keeps a scan only if no table write landed while it ran, whichever thread wrote and
// whether or not a cache already held the table; the applied-write generation counts only writes that change memory now.
public sealed class DeviceStoreTableCacheTests
{
    private static List<DeviceAddressRange> Ranges(ulong address) => [new DeviceAddressRange(1, address, 0x100, Planned: true, Written: true)];

    [Fact]
    public void AScanWithoutAWriteIsCached()
    {
        const ulong table = 0x7A0000000;
        var generation = DeviceStoreTablePlanner.BeginScan(table, 0x1000);

        Assert.True(DeviceStoreTablePlanner.CommitScan(table, 0x1000, generation, Ranges(0x1000)));
        Assert.True(DeviceStoreTablePlanner.TryGetCached(table, 0x1000, out var cached));
        Assert.Equal(0x1000UL, cached![0].Base);
    }

    [Fact]
    public void AWriteDuringTheFirstScanOfATableKeepsTheOldRangesOutOfTheCache()
    {
        const ulong table = 0x7B0000000;
        var generation = DeviceStoreTablePlanner.BeginScan(table, 0x1000);
        // From another thread, while no cache holds the table yet (the case the per-cache generation missed).
        var writer = new Thread(() => DeviceStoreTablePlanner.InvalidateTables(table + 0x40, 16, DeviceStoreTablePlanner.InvalidationSource.CommandRender));
        writer.Start();
        writer.Join();

        Assert.False(DeviceStoreTablePlanner.CommitScan(table, 0x1000, generation, Ranges(0x2000)));
        Assert.False(DeviceStoreTablePlanner.TryGetCached(table, 0x1000, out _));
    }

    [Fact]
    public void AWriteDuringARescanAfterADropKeepsTheOldRangesOutOfTheCache()
    {
        const ulong table = 0x7C0000000;
        Assert.True(DeviceStoreTablePlanner.CommitScan(table, 0x1000, DeviceStoreTablePlanner.BeginScan(table, 0x1000), Ranges(0x3000)));
        DeviceStoreTablePlanner.InvalidateTables(table, 16, DeviceStoreTablePlanner.InvalidationSource.CommandFront);
        Assert.False(DeviceStoreTablePlanner.TryGetCached(table, 0x1000, out _));

        var generation = DeviceStoreTablePlanner.BeginScan(table, 0x1000);
        DeviceStoreTablePlanner.InvalidateTables(table + 0x800, 16, DeviceStoreTablePlanner.InvalidationSource.CpuFault);

        Assert.False(DeviceStoreTablePlanner.CommitScan(table, 0x1000, generation, Ranges(0x4000)));
        Assert.False(DeviceStoreTablePlanner.TryGetCached(table, 0x1000, out _));
    }

    [Fact]
    public void OnlyWritesThatChangeMemoryNowMoveTheAppliedGeneration()
    {
        const ulong table = 0x7D0000000;
        DeviceStoreTablePlanner.BeginScan(table, 0x1000);

        var before = DeviceStoreTablePlanner.AppliedGeneration;
        DeviceStoreTablePlanner.InvalidateTables(table, 16, DeviceStoreTablePlanner.InvalidationSource.CommandFront);
        DeviceStoreTablePlanner.InvalidateTables(table, 16, DeviceStoreTablePlanner.InvalidationSource.DmaFront);
        Assert.Equal(before, DeviceStoreTablePlanner.AppliedGeneration);

        DeviceStoreTablePlanner.InvalidateTables(table, 16, DeviceStoreTablePlanner.InvalidationSource.CommandRender);
        DeviceStoreTablePlanner.InvalidateTables(table, 16, DeviceStoreTablePlanner.InvalidationSource.FillRender);
        DeviceStoreTablePlanner.InvalidateTables(table, 16, DeviceStoreTablePlanner.InvalidationSource.CopyRender);
        DeviceStoreTablePlanner.InvalidateTables(table, 16, DeviceStoreTablePlanner.InvalidationSource.CpuFault);
        Assert.True(DeviceStoreTablePlanner.AppliedGeneration >= before + 4);
    }
}
