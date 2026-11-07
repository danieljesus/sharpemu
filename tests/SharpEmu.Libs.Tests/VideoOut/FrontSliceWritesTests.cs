// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_SLICE_WRITES: a taken command write is a render unit. The worker ends its segment there, the
// render thread applies it when it joins the slice, after the items before it and before the items after it, whichever
// way they end (joined, stolen, invalid, short rest, tail); until then a render-thread read of its bytes joins first.
public sealed class FrontSliceWritesTests
{
    private sealed class FakeSlice
    {
        public int Start;
        public int Count;
        public readonly Queue<FrontSliceSegment> Segments = new();
        public int Resume;
        public bool Stolen;
        public bool Done = true;
    }

    // Items are stream positions; the ones in Writes are command writes to the address they map to. When an item ends,
    // the backend notes which writes were applied, which is what that item's upload or recording would see.
    private sealed class Backend(HashSet<int> writes, FrontSliceWrites pending) : FrontSliceCutter<int, FakeSlice>.IBackend
    {
        public readonly List<int> Ended = new();
        public readonly HashSet<int> Applied = new();
        public readonly Dictionary<int, int[]> SeenAt = new();
        public readonly Dictionary<int, int> Tokens = new();
        public readonly HashSet<int> StealSlices = new();
        public readonly HashSet<(int Slice, int First)> InvalidSegments = new();
        public int WorkerCount = 4;
        private int _dispatched;

        public int Workers => WorkerCount;

        // As RecordSlice: a segment ends at each render unit (Serial "render unit") and the next one begins after it.
        public FakeSlice Dispatch(List<int> items, int start, int count)
        {
            var slice = new FakeSlice { Start = start, Count = count, Resume = count, Stolen = StealSlices.Contains(_dispatched++) };
            var first = 0;
            for (var index = 0; index < count; index++)
            {
                if (!writes.Contains(items[start + index]))
                {
                    continue;
                }

                slice.Segments.Enqueue(new FrontSliceSegment(first, index - first, true, "render unit", null));
                first = index + 1;
            }

            if (first < count)
            {
                slice.Segments.Enqueue(new FrontSliceSegment(first, count - first, false, null, null));
            }

            return slice;
        }

        public bool TryClaim(FakeSlice slice) => slice.Stolen;

        public bool IsDone(FakeSlice slice) => slice.Done;

        public bool NextSegment(FakeSlice slice, out FrontSliceSegment segment) => slice.Segments.TryDequeue(out segment);

        public bool JoinSegment(FakeSlice slice, in FrontSliceSegment segment) =>
            segment.Count != 0 && !InvalidSegments.Contains((slice.Start, segment.First));

        public int Resume(FakeSlice slice) => slice.Resume;

        public void Finish(FakeSlice slice)
        {
        }

        public void Complete(int item) => End(item);

        public void RunInline(int item, FrontSliceInline reason)
        {
            if (writes.Contains(item))
            {
                Applied.Add(item);
                pending.Applied(Tokens[item]);
            }

            End(item);
        }

        private void End(int item)
        {
            Ended.Add(item);
            SeenAt[item] = Applied.Order().ToArray();
        }
    }

    private static ulong AddressOf(int item) => 0x2_0000_0000UL + (ulong)item * 0x1000;

    private static (FrontSliceCutter<int, FakeSlice> Cutter, Backend Backend, FrontSliceWrites Pending) Create(HashSet<int> writes, int sliceItems = 16, int minimumItems = 8)
    {
        var pending = new FrontSliceWrites();
        var backend = new Backend(writes, pending);
        return (new FrontSliceCutter<int, FakeSlice>(backend, new FrontSliceStats(), sliceItems, minimumItems), backend, pending);
    }

    private static void Add(FrontSliceCutter<int, FakeSlice> cutter, Backend backend, FrontSliceWrites pending, HashSet<int> writes, int from, int to)
    {
        for (var item = from; item < to; item++)
        {
            if (writes.Contains(item))
            {
                backend.Tokens[item] = pending.Add(AddressOf(item), 8);
            }

            cutter.Add(item);
        }
    }

    // Every item ends in stream order and sees exactly the writes before it.
    private static void AssertSerialOrder(Backend backend, HashSet<int> writes, int count)
    {
        Assert.Equal(Enumerable.Range(0, count), backend.Ended);
        foreach (var item in Enumerable.Range(0, count))
        {
            Assert.Equal(writes.Where(write => write < item || write == item).Order(), backend.SeenAt[item]);
        }
    }

    [Fact]
    public void WritesInsideFullSlicesLandBetweenTheSegmentsAroundThem()
    {
        var writes = new HashSet<int> { 3, 17, 18, 31, 40 };
        var (cutter, backend, pending) = Create(writes);
        Add(cutter, backend, pending, writes, 0, 48);
        Assert.Equal(5, pending.Pending);

        cutter.Flush(FrontSliceFlush.Exit);

        AssertSerialOrder(backend, writes, 48);
        Assert.Equal(0, pending.Pending);
    }

    [Fact]
    public void WritesInAShortRestStolenSliceOrInvalidSegmentLandInOrder()
    {
        var writes = new HashSet<int> { 5, 20, 21, 34, 36 };
        var (cutter, backend, pending) = Create(writes);
        backend.StealSlices.Add(1);
        backend.InvalidSegments.Add((0, 6));
        Add(cutter, backend, pending, writes, 0, 38);

        cutter.Flush(FrontSliceFlush.Item);

        AssertSerialOrder(backend, writes, 38);
        Assert.Equal(0, pending.Pending);
    }

    [Fact]
    public void WritesInSlicesJoinedEarlyLandBeforeTheOpenItemsAfterThem()
    {
        var writes = new HashSet<int> { 7, 16, 30 };
        var (cutter, backend, pending) = Create(writes);
        Add(cutter, backend, pending, writes, 0, 40);

        Assert.Equal(2, cutter.JoinReady(ahead: 8));
        Assert.Equal(Enumerable.Range(0, 32), backend.Ended);
        Assert.Equal(0, pending.Pending);
        Assert.True(cutter.HasOpen);

        cutter.Flush(FrontSliceFlush.Exit);
        AssertSerialOrder(backend, writes, 40);
    }

    [Fact]
    public void AWriteAsTheLastItemOfASliceEndsIt()
    {
        var writes = new HashSet<int> { 15, 16 };
        var (cutter, backend, pending) = Create(writes);
        Add(cutter, backend, pending, writes, 0, 32);

        cutter.Flush(FrontSliceFlush.Exit);

        AssertSerialOrder(backend, writes, 32);
    }

    [Fact]
    public void AReadOfAWriteNotAppliedYetJoinsAndAnAppliedOneDoesNot()
    {
        var pending = new FrontSliceWrites();
        var token = pending.Add(0x1_0000_0FFC, 8);
        Func<ulong, ulong, bool> noGpuWrites = static (_, _) => false;

        Assert.Equal(FrontSliceFlush.Write, FrontSliceGuards.ReadNeedsJoin(0x1_0000_1000, 4, noGpuWrites, pending));
        Assert.Equal(FrontSliceFlush.Write, FrontSliceGuards.ReadNeedsJoin(0x1_0000_0000, 0x1000, noGpuWrites, pending));
        Assert.Null(FrontSliceGuards.ReadNeedsJoin(0x1_0000_1004, 4, noGpuWrites, pending));
        Assert.Null(FrontSliceGuards.ReadNeedsJoin(0x1_0000_0FF8, 4, noGpuWrites, pending));
        Assert.Null(FrontSliceGuards.ReadNeedsJoin(0x1_0000_0FFC, 0, noGpuWrites, pending));

        pending.Applied(token);
        pending.Applied(token);
        Assert.Equal(0, pending.Pending);
        Assert.Null(FrontSliceGuards.ReadNeedsJoin(0x1_0000_1000, 4, noGpuWrites, pending));
    }

    [Fact]
    public void APendingGpuWriteJoinsAsARead()
    {
        var pending = new FrontSliceWrites();
        pending.Add(0x5000, 4);

        Assert.Equal(FrontSliceFlush.Read, FrontSliceGuards.ReadNeedsJoin(0x5000, 4, static (_, _) => true, pending));
        Assert.Equal(FrontSliceFlush.Read, FrontSliceGuards.ReadNeedsJoin(0x9000, 4, static (_, _) => true, pending));
    }

    [Fact]
    public void ALongReadChecksTheRangesAndAClearForgetsEveryWrite()
    {
        var pending = new FrontSliceWrites();
        pending.Add(0x4000_0000 + 0x80_0000, 8);

        Assert.True(pending.Overlaps(0x4000_0000, 0x100_0000));
        Assert.False(pending.Overlaps(0x4000_0000, 0x80_0000));

        pending.Clear();
        Assert.Equal(0, pending.Pending);
        Assert.False(pending.Overlaps(0x4000_0000, 0x100_0000));
    }

    [Theory]
    [InlineData(false, false, "off")]
    [InlineData(false, true, "off")]
    [InlineData(true, true, "tables")]
    [InlineData(true, false, null)]
    public void AWriteIsTakenOnlyWhenOnAndNoCachedTableHoldsItsBytes(bool enabled, bool cachedTable, string? refusal) =>
        Assert.Equal(refusal, FrontSliceGuards.WriteRefusal(enabled, cachedTable));
}
