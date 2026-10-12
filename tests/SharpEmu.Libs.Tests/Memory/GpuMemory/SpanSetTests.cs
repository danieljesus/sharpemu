// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class SpanSetTests
{
    private static List<GuestSpan> Snapshot(SpanSet set)
    {
        var spans = new List<GuestSpan>();
        set.ForEach((address, size) => spans.Add(new GuestSpan(address, size)));
        return spans;
    }

    [Fact]
    public void Add_MergesAdjacentOverlappingAndContainedSpans()
    {
        var set = new SpanSet();
        Assert.True(set.IsEmpty);

        set.Add(0x1000, 0x1000);
        set.Add(0x2000, 0x1000);
        set.Add(0x2800, 0x1000);
        set.Add(0x1800, 0x100);
        set.Add(0x9000, 0x1000);

        Assert.Equal(
            new[] { new GuestSpan(0x1000, 0x2800), new GuestSpan(0x9000, 0x1000) },
            Snapshot(set));
        Assert.False(set.IsEmpty);
    }

    [Fact]
    public void Remove_TrimsHeadTailSplitsAndClearsWhole()
    {
        var set = new SpanSet();
        set.Add(0x1000, 0x4000);

        set.Remove(0x0800, 0x1000);
        Assert.Equal(new[] { new GuestSpan(0x1800, 0x3800) }, Snapshot(set));

        set.Remove(0x4800, 0x1000);
        Assert.Equal(new[] { new GuestSpan(0x1800, 0x3000) }, Snapshot(set));

        set.Remove(0x2000, 0x1000);
        Assert.Equal(new[] { new GuestSpan(0x1800, 0x800), new GuestSpan(0x3000, 0x1800) }, Snapshot(set));

        set.Remove(0x1000, 0x5000);
        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void Contains_RequiresFullCoverageWithEdgeEquality()
    {
        var set = new SpanSet();
        set.Add(0x1000, 0x1000);

        Assert.True(set.Contains(0x1000, 0x1000));
        Assert.True(set.Contains(0x1800, 0x800));
        Assert.False(set.Contains(0x1800, 0x801));
        Assert.False(set.Contains(0x0FFF, 0x10));
        Assert.False(set.Contains(0x2000, 0x10));
    }

    [Fact]
    public void GetOverlappingRanges_LimitsResultsToTheRequestedRange()
    {
        var set = new SpanSet();
        set.Add(0x1000, 0x1000);
        set.Add(0x4000, 0x1000);

        Assert.True(set.Overlaps(0x1FFF, 0x10));
        Assert.False(set.Overlaps(0x2000, 0x2000));
        Assert.True(set.Overlaps(0x0000, 0x10000));

        Assert.Equal(
            new[] { new GuestSpan(0x1800, 0x800), new GuestSpan(0x4000, 0x800) },
            set.GetOverlappingRanges(0x1800, 0x3000));
        Assert.Empty(set.GetOverlappingRanges(0x2000, 0x2000));

        set.Clear();
        Assert.True(set.IsEmpty);
    }

    // [local] The lock-free snapshot answers Contains like the set it was taken from.
    [Fact]
    public void Snapshot_ContainsMatchesTheSet()
    {
        var random = new System.Random(1234);
        var set = new SpanSet();
        Assert.False(set.Snapshot().Contains(0x1000, 1));
        for (var step = 0; step < 400; step++)
        {
            var address = (ulong)random.Next(0, 256) * 0x100;
            var size = (ulong)random.Next(1, 16) * 0x80;
            if (random.Next(3) == 0)
            {
                set.Remove(address, size);
            }
            else
            {
                set.Add(address, size);
            }

            var snapshot = set.Snapshot();
            for (var probe = 0; probe < 32; probe++)
            {
                var probeAddress = (ulong)random.Next(0, 0x10000);
                var probeSize = (ulong)random.Next(1, 0x1000);
                Assert.Equal(set.Contains(probeAddress, probeSize), snapshot.Contains(probeAddress, probeSize));
            }
        }
    }
}
