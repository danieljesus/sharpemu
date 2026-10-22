// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_TLAS_HLE: the host-built topology fits the game's node area and holds every instance once.
public sealed class TlasHleTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(151)]
    [InlineData(1000)]
    public void TopologyHoldsEveryInstanceOnceAndFitsTheNodeArea(int count)
    {
        var random = new System.Random(count);
        var positions = new (float X, float Y, float Z)[count];
        for (var index = 0; index < count; index++)
            positions[index] = ((float)random.NextDouble() * 100, (float)random.NextDouble() * 10, (float)random.NextDouble() * 100);

        var (entries, nodes) = TlasHle.BuildTopology(positions);

        Assert.Equal(nodes * 4, entries.Count);
        Assert.True(nodes * 128 <= count * 64, $"nodes={nodes}");
        var leaves = new HashSet<int>();
        var parents = new int[nodes];
        Array.Fill(parents, -1);
        for (var node = 0; node < nodes; node++)
        {
            for (var child = 0; child < 4; child++)
            {
                var entry = entries[node * 4 + child];
                if (entry == 0xFFFFFFFF)
                    continue;
                if ((entry & 0x80000000) != 0)
                {
                    Assert.True(leaves.Add((int)(entry & 0x7FFFFFFF)));
                }
                else
                {
                    Assert.True(entry > node && entry < nodes);
                    Assert.Equal(-1, parents[entry]);
                    parents[entry] = node;
                }
            }
        }

        Assert.Equal(count, leaves.Count);
        for (var node = 1; node < nodes; node++)
            Assert.NotEqual(-1, parents[node]);
    }

    [Fact]
    public void NodeWordsUseTheGameEncoding()
    {
        var (entries, nodes) = TlasHle.BuildTopology(new (float, float, float)[] { (0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0) });
        var words = TlasHle.NodeWords(entries, nodes);

        Assert.Equal(2, nodes);
        Assert.Equal(((4u + 2) << 3) | 5, words[0]);
        Assert.Equal(0xFFFFFFFFu, words[31]);
        Assert.Equal(float.PositiveInfinity, BitConverter.UInt32BitsToSingle(words[4 + 2 * 6]));
        var header = TlasHle.HeaderWords(0x6643E2E00, 151, 76);
        Assert.Contains((0x10 / 4, 0x7240u), header);
        Assert.Contains((0x28 / 4, 0x26C0u), header);
        Assert.Contains((0x50 / 4, 0x9Bu), header);
        Assert.Contains((41, 0x643E54C0u), header);
    }

    [Fact]
    public void BoxShaderBuilds()
    {
        var spirv = TlasHleShaders.CreateTlasBoxes();
        Assert.True(spirv.Length > 100);
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(spirv, 0));
    }
}
