// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_READ_MEMO: the granule answers equal the set's answers through adds, and the published
// pending-write ranges answer as the locked walk does.
public sealed class FrontReadMemoTests
{
    private sealed class Ranges : IEverGpuWrittenRanges
    {
        private readonly SpanSet _set = new();
        public long Version;
        public int Queries;

        public long EverGpuWrittenVersion => Version;

        public bool EverGpuWrittenRanges(ulong guestAddress, ulong size)
        {
            Queries++;
            return _set.Overlaps(guestAddress, size);
        }

        public void Add(ulong address, ulong size)
        {
            _set.Add(address, size);
            Version++;
        }
    }

    [Fact]
    public void AnswersEqualTheSetThroughAdds()
    {
        var random = new System.Random(1234);
        var ranges = new Ranges();
        var memo = new FrontReadMemo(mode: 1);
        const ulong Base = 0x2_0000_0000;
        for (var step = 0; step < 200_000; step++)
        {
            if (random.Next(500) == 0)
                ranges.Add(Base + (ulong)random.Next(0, 64 << 12), (ulong)random.Next(1, 300));
            var address = Base + (ulong)random.Next(0, 64 << 12);
            var size = random.Next(8) == 0 ? (ulong)random.Next(1, 9000) : 4UL;
            var expected = ranges.EverGpuWrittenRanges(address, size);
            Assert.Equal(expected, memo.Overlaps(ranges, address, size));
        }

        Assert.True(memo.Hits > memo.Misses);
        Assert.True(memo.Spans > 0);
        Assert.True(memo.WrittenHits > 0);
    }

    [Fact]
    public void ACleanGranuleIsAskedOncePerVersion()
    {
        var ranges = new Ranges();
        var memo = new FrontReadMemo(mode: 1);
        ranges.Add(0x1_0000_2000, 16);
        for (var word = 0UL; word < 1024; word++)
            Assert.False(memo.Overlaps(ranges, 0x1_0000_0000 + word * 4, 4));
        Assert.Equal(1, ranges.Queries);

        ranges.Add(0x1_0000_0100, 4); // a new range inside the granule
        Assert.True(memo.Overlaps(ranges, 0x1_0000_0100, 4));
        Assert.False(memo.Overlaps(ranges, 0x1_0000_0104, 4));
        Assert.True(memo.Overlaps(ranges, 0x1_0000_2008, 4));
    }

    [Fact]
    public void VerifyCountsNoMismatchOnAStableSet()
    {
        var ranges = new Ranges();
        var memo = new FrontReadMemo(mode: 2);
        ranges.Add(0x4000, 0x10);
        for (var word = 0UL; word < 4096; word++)
            memo.Overlaps(ranges, word * 4, 4);
        Assert.Equal(4096, memo.Verified);
        Assert.Equal(0, memo.Mismatches);
    }

    [Fact]
    public void PublishedRangesAnswerAsTheWalk()
    {
        var random = new System.Random(77);
        for (var round = 0; round < 200; round++)
        {
            var list = Enumerable.Range(0, random.Next(0, 12))
                .Select(_ => (Start: (ulong)random.Next(0, 4096), Length: (ulong)random.Next(1, 64)))
                .ToArray();
            var published = new FrontReadMemo.Published(list.Select(range => (range.Start, range.Start + range.Length)).ToArray());
            for (var query = 0; query < 200; query++)
            {
                var address = (ulong)random.Next(0, 4200);
                var size = (ulong)random.Next(1, 16);
                var expected = list.Any(range => address < range.Start + range.Length && range.Start < address + size);
                Assert.Equal(expected, published.Overlaps(address, size));
            }
        }

        Assert.False(FrontReadMemo.Published.Empty.Overlaps(0, ulong.MaxValue));
    }
}
