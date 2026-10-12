// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] Which tracker pages a published readback piece clears.
public sealed class ReadbackPiecePagesTests
{
    private const ulong Page = 0x1000;

    private static List<(ulong Address, ulong Size)> Cleared(ulong address, ulong size, params ulong[] modifiedPages)
    {
        var cleared = new List<(ulong, ulong)>();
        GuestBufferCache.ClearPagesOfPublishedPiece(address, size, page => modifiedPages.Contains(page), (begin, bytes) => cleared.Add((begin, bytes)));
        return cleared;
    }

    [Fact]
    public void AlignedPieceClearsItsPagesInOneCall()
    {
        Assert.Equal([(0x4000UL, 3 * Page)], Cleared(0x4000, 3 * Page));
    }

    [Fact]
    public void PartialEdgesClearOnlyWithoutOtherModifiedBytes()
    {
        Assert.Equal([(0x2000UL, 3 * Page), (0x1000UL, Page), (0x5000UL, Page)], Cleared(0x1800, 0x4000));
        Assert.Equal([(0x2000UL, 3 * Page), (0x5000UL, Page)], Cleared(0x1800, 0x4000, 0x1000));
        Assert.Equal([(0x2000UL, 3 * Page)], Cleared(0x1800, 0x4000, 0x1000, 0x5000));
    }

    [Fact]
    public void PieceInsideOnePageChecksThatPageOnce()
    {
        var checks = 0;
        var cleared = new List<(ulong, ulong)>();
        GuestBufferCache.ClearPagesOfPublishedPiece(0x3010, 0x20, page => { checks++; return false; }, (begin, bytes) => cleared.Add((begin, bytes)));
        Assert.Equal(1, checks);
        Assert.Equal([(0x3000UL, Page)], cleared);
        Assert.Empty(Cleared(0x3010, 0x20, 0x3000));
    }

    [Fact]
    public void PieceEndingMidPageFromAPageStartChecksTheTail()
    {
        Assert.Equal([(0x3000UL, Page)], Cleared(0x3000, 0x800));
        Assert.Empty(Cleared(0x3000, 0x800, 0x3000));
        Assert.Equal([(0x3000UL, Page)], Cleared(0x3000, 0x1800, 0x4000));
    }
}
