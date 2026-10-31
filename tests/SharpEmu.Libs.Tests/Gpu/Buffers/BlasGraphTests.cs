// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_BLAS_CYCLE: the host's walk of a BLAS blob from its root tells trees, cycles and shared boxes apart.
public sealed class BlasGraphTests
{
    private const uint None = uint.MaxValue;

    // 32 units of 64 bytes; box units 4..7, pair records from record 128 (unit 16 on).
    private static uint[] Blob()
    {
        var words = new uint[32 * 16];
        for (var unit = 0; unit < 32; unit++)
        {
            for (var child = 0; child < 4; child++)
                words[unit * 16 + child] = None;
        }

        return words;
    }

    private static uint Box(int unit) => (uint)(unit << 3) | 5;

    private static uint Pairs(int record) => (uint)(record << 3);

    private static void Record(uint[] words, int record, bool last) => words[record * 2 + 1] = last ? 0x8000_0000u : 0;

    [Fact]
    public void Tree_HasNoBackOrCrossEdges()
    {
        var words = Blob();
        words[4 * 16 + 0] = Box(5);
        words[4 * 16 + 1] = Box(6);
        words[5 * 16 + 0] = Pairs(128);
        words[6 * 16 + 0] = Pairs(130);
        Record(words, 128, false);
        Record(words, 129, true);
        Record(words, 130, true);

        var graph = RayQueryHle.AnalyzeGraph(words, Box(4));

        Assert.Equal(3, graph.Boxes);
        Assert.Equal(3, graph.Pairs);
        Assert.Equal(6, graph.TreeBound);
        Assert.Equal(0, graph.BackEdges);
        Assert.Equal(0, graph.CrossEdges);
        Assert.Equal(0, graph.PairOutOfBlob);
    }

    [Fact]
    public void ChildPointingToAnAncestor_IsABackEdge()
    {
        var words = Blob();
        words[4 * 16 + 0] = Box(5);
        words[5 * 16 + 0] = Box(6);
        words[6 * 16 + 0] = Box(4);

        var graph = RayQueryHle.AnalyzeGraph(words, Box(4));

        Assert.Equal(1, graph.BackEdges);
        Assert.Equal(0, graph.CrossEdges);
        Assert.Equal("0x4>0x5>0x6>0x4", graph.Cycle);
    }

    [Fact]
    public void SharedBox_IsACrossEdge()
    {
        var words = Blob();
        words[4 * 16 + 0] = Box(5);
        words[4 * 16 + 1] = Box(6);
        words[5 * 16 + 0] = Box(7);
        words[6 * 16 + 0] = Box(7);

        var graph = RayQueryHle.AnalyzeGraph(words, Box(4));

        Assert.Equal(4, graph.Boxes);
        Assert.Equal(0, graph.BackEdges);
        Assert.Equal(1, graph.CrossEdges);
    }

    [Fact]
    public void PairListWithoutItsLastRecord_RunsOutOfTheBlob()
    {
        var words = Blob();
        words[4 * 16 + 0] = Pairs(254);

        var graph = RayQueryHle.AnalyzeGraph(words, Box(4));

        Assert.Equal(1, graph.PairOutOfBlob);
        Assert.Equal(2, graph.Pairs);
        Assert.Equal(1, graph.Boxes);
    }

    [Fact]
    public void ChildOutsideTheBlob_IsCounted()
    {
        var words = Blob();
        words[4 * 16 + 0] = Box(40);

        Assert.Equal(1, RayQueryHle.AnalyzeGraph(words, Box(4)).OutOfBlob);
    }
}
