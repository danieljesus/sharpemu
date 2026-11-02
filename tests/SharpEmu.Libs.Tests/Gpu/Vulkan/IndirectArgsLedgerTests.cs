// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// [local] SHARPEMU_INDIRECT_ARGS_PROBE bookkeeping: each test owns its ledger, so nothing shared is switched.
public sealed class IndirectArgsLedgerTests
{
    private static byte[] Args(uint x, uint y, uint z)
    {
        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), x);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), z);
        return bytes;
    }

    // A ring the "GPU" fills: slot -> (x, y, z, sequence word).
    private sealed class FakeRing(int slots)
    {
        public readonly (uint X, uint Y, uint Z, uint Sequence)[] Words = new (uint, uint, uint, uint)[slots];

        public void Execute(int slot, ulong sequence, uint x, uint y, uint z) =>
            Words[slot] = (x, y, z, IndirectArgsLedger.SequenceWord(sequence));

        public (uint X, uint Y, uint Z, uint Sequence) Read(int slot) => Words[slot];
    }

    [Fact]
    public void SlotsWrapAroundTheRing()
    {
        var ledger = new IndirectArgsLedger(4);
        var slots = Enumerable.Range(0, 6).Select(_ => ledger.Reserve(0x10, 0x100, 1, Args(1, 1, 1), false)).ToList();
        Assert.Equal([1, 2, 3, 0, 1, 2], slots.Select(s => s.Slot));
        Assert.Equal([1UL, 2, 3, 4, 5, 6], slots.Select(s => s.Sequence));
    }

    [Fact]
    public void SequenceWordIsNeverZero()
    {
        Assert.Equal(1u, IndirectArgsLedger.SequenceWord(1UL << 32));
        Assert.Equal(7u, IndirectArgsLedger.SequenceWord(7));
    }

    [Fact]
    public void ScanReportsOnlyExecutedOldSlotsAndFlagsBigCounts()
    {
        var ledger = new IndirectArgsLedger(8, bigGroups: 1000);
        var ring = new FakeRing(8);
        for (var index = 0; index < 8; index++)
        {
            var (slot, sequence) = ledger.Reserve(0xAB, 0x2000 + (ulong)index * 16, 50, Args(10, 1, 1), false);
            // The third dispatch read a huge count on the GPU; the fourth has not run yet.
            if (index == 3)
                continue;
            ring.Execute(slot, sequence, index == 2 ? 5000u : 10u, 1, 1);
        }

        var lines = ledger.Scan(ring.Read);
        // Sequences 1..4 are half a ring old; 3 is the big one and is the only line.
        var big = Assert.Single(lines);
        Assert.Contains("big seq=3", big);
        Assert.Contains("gpu=5000,1,1 cpu=10,1,1", big);
        var stats = ledger.StatsOf(0xAB)!;
        Assert.Equal(3, stats.Count);
        Assert.Equal(5000u, stats.MaxX);
        Assert.Equal(1, stats.Big);
        Assert.Equal(1, stats.CpuMismatches);
    }

    [Fact]
    public void CpuMismatchIsNotCountedWhenTheGpuOwnsTheBytes()
    {
        var ledger = new IndirectArgsLedger(4);
        var ring = new FakeRing(4);
        for (var index = 0; index < 4; index++)
        {
            var (slot, sequence) = ledger.Reserve(0xCD, 0x3000, 7, Args(1, 1, 1), gpuDirty: true);
            ring.Execute(slot, sequence, 3, 1, 1);
        }

        ledger.Scan(ring.Read);
        var stats = ledger.StatsOf(0xCD)!;
        Assert.True(stats.Count > 0);
        Assert.Equal(0, stats.CpuMismatches);
    }

    [Fact]
    public void LatestSlotsShowPendingDispatches()
    {
        var ledger = new IndirectArgsLedger(16);
        var ring = new FakeRing(16);
        var (slot1, sequence1) = ledger.Reserve(0x8039D9800, 0x60A360080, 29043, Args(64, 1, 1), false);
        ring.Execute(slot1, sequence1, 0x7FFFFFFF, 1, 1);
        ledger.Reserve(0x8039D9800, 0x60A3600A0, 29044, Args(32, 1, 1), false);

        var lines = ledger.DescribeLatest(48, ring.Read);
        Assert.Equal(2, lines.Count);
        Assert.Equal("indirect_args seq=1 tick=29043 shader=0x8039D9800 address=0x60A360080 gpu=2147483647,1,1 cpu=64,1,1 gpu_dirty=False", lines[0]);
        Assert.Contains("gpu=pending cpu=32,1,1", lines[1]);
    }

    [Fact]
    public void PrefixDifferencesUseTheTotalForTheLastEntry()
    {
        var (wrapped, text) = IndirectArgsLedger.DescribePrefix([0, 100], 2, 250);
        Assert.False(wrapped);
        Assert.Equal("0:100,100:150,", text);
    }

    [Fact]
    public void PrefixWrapsWhenTheTotalIsBelowTheLastStart()
    {
        var (wrapped, text) = IndirectArgsLedger.DescribePrefix([0, 300], 2, 250);
        Assert.True(wrapped);
        Assert.Equal("0:300,300:4294967246,", text);
    }

    [Theory]
    [InlineData("0x8039D9800:0x10000000", 0x8039D9800UL, 0x10000000u, 1)]
    [InlineData("8039D9800:268435456:3", 0x8039D9800UL, 268435456u, 3)]
    [InlineData("8039D9800/0x10000000/2", 0x8039D9800UL, 0x10000000u, 2)]
    public void InjectParses(string value, ulong shader, uint groups, int nth)
    {
        Assert.Equal((shader, groups, nth), IndirectArgsProbe.ParseInject(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("8039D9800")]
    [InlineData("zz:1")]
    [InlineData("8039D9800:1:0")]
    public void InjectRejectsMalformedValues(string? value)
    {
        Assert.Null(IndirectArgsProbe.ParseInject(value));
    }
}
