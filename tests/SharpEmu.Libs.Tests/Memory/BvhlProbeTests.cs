// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE.GuestMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// [local] SHARPEMU_BVHL_PROBE: the pure parts of the pool scanner.
public sealed class BvhlProbeTests
{
    private const ulong Base = 0x2_4000_0000;

    private static byte[] Block(int nodes)
    {
        // Every node free, chained in order, the last one ends the list.
        var block = new byte[nodes * 16];
        for (var index = 0; index < nodes; index++)
        {
            var next = index + 1 < nodes ? Base + (ulong)(index + 1) * 16 : 0;
            BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(index * 16), next);
        }

        return block;
    }

    [Fact]
    public void FindMagicReportsTheFirstAlignedQword()
    {
        var block = Block(8);
        Assert.Equal(-1, BvhlProbe.FindMagic(block));
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(5 * 16), BvhlProbe.Magic);
        Assert.Equal(5 * 16, BvhlProbe.FindMagic(block));
        Assert.Equal(0x4C4856425F525350UL, BvhlProbe.Magic);
        Assert.Equal("PSR_BVHL", System.Text.Encoding.ASCII.GetString(block, 5 * 16, 8));
    }

    [Fact]
    public void WalkFreeListFollowsAWholeChain()
    {
        var block = Block(8);
        var walk = BvhlProbe.WalkFreeList(block, Base, Base, 8);
        Assert.Null(walk.Problem);
        Assert.Equal(8, walk.Steps);
    }

    [Fact]
    public void WalkFreeListStopsAtANodeOutsideTheBlock()
    {
        var block = Block(8);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(3 * 16), BvhlProbe.Magic);
        var walk = BvhlProbe.WalkFreeList(block, Base, Base, 8);
        Assert.NotNull(walk.Problem);
        Assert.Equal(4, walk.Steps);
        Assert.Equal(BvhlProbe.Magic, walk.BadNode);
    }

    [Fact]
    public void WalkFreeListReportsACycle()
    {
        var block = Block(4);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(3 * 16), Base);
        var walk = BvhlProbe.WalkFreeList(block, Base, Base, 4);
        Assert.Equal("cycle", walk.Problem);
    }

    [Fact]
    public void ProbeIsOffByDefault()
    {
        Assert.False(BvhlProbe.Enabled);
    }
}
