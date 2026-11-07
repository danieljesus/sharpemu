// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_SLICES' cutter: slices close at the slice size and are handed out at once, a cut splits the
// rest into min(N, len/16) slices and runs a short rest here, and every item ends exactly once and in stream order:
// joined, or run here when its slice was stolen, a segment did not validate, a worker stopped at it or after it.
public sealed class FrontSliceCutterTests
{
    private sealed class FakeSlice
    {
        public int Start;
        public int Count;
        public readonly Queue<FrontSliceSegment> Segments = new();
        public int Resume;
        public bool Stolen;
        public readonly HashSet<int> Invalid = new();
        public bool Finished;
    }

    private sealed class Backend : FrontSliceCutter<int, FakeSlice>.IBackend
    {
        public int WorkerCount = 4;
        public readonly List<string> Log = new();
        public readonly List<FakeSlice> Slices = new();
        public readonly List<int> Ended = new();

        // How a worker records a slice: its segments (relative to the slice), where it stops, whether it was stolen.
        public Action<FakeSlice>? Script;

        public int Workers => WorkerCount;

        public FakeSlice Dispatch(List<int> items, int start, int count)
        {
            Assert.Equal(start, items[start] - items[0]);
            var slice = new FakeSlice { Start = start, Count = count, Resume = count };
            if (Script is { } script)
            {
                script(slice);
            }
            else
            {
                slice.Segments.Enqueue(new FrontSliceSegment(0, count, false, null, null));
            }

            Slices.Add(slice);
            Log.Add($"dispatch {start}+{count}");
            return slice;
        }

        public bool TryClaim(FakeSlice slice) => slice.Stolen;

        // Read on each segment wait: whether the cutter says the slice was handed out at the cut.
        public Func<bool>? JoiningRest;
        public readonly List<(int Start, bool AtCut)> Waits = new();

        public bool NextSegment(FakeSlice slice, out FrontSliceSegment segment)
        {
            if (JoiningRest is { } rest)
            {
                Waits.Add((slice.Start, rest()));
            }

            return slice.Segments.TryDequeue(out segment);
        }

        public bool JoinSegment(FakeSlice slice, in FrontSliceSegment segment) => segment.Count != 0 && !slice.Invalid.Contains(segment.First);

        public int Resume(FakeSlice slice) => slice.Resume;

        public void Finish(FakeSlice slice)
        {
            Assert.False(slice.Finished);
            slice.Finished = true;
        }

        public void Complete(int item)
        {
            Ended.Add(item);
            Log.Add($"joined {item}");
        }

        public void RunInline(int item, FrontSliceInline reason)
        {
            Ended.Add(item);
            Log.Add($"{reason.ToString().ToLowerInvariant()} {item}");
        }
    }

    private static (FrontSliceCutter<int, FakeSlice> Cutter, Backend Backend, FrontSliceStats Stats) Create(int sliceItems = 64, int minimumItems = 8)
    {
        var backend = new Backend();
        var stats = new FrontSliceStats();
        return (new FrontSliceCutter<int, FakeSlice>(backend, stats, sliceItems, minimumItems), backend, stats);
    }

    private static void AddRange(FrontSliceCutter<int, FakeSlice> cutter, int count) => AddRange(cutter, 0, count);

    private static void AddRange(FrontSliceCutter<int, FakeSlice> cutter, int from, int to)
    {
        for (var item = from; item < to; item++)
        {
            cutter.Add(item);
        }
    }

    [Theory]
    [InlineData(7, 4, 0)]
    [InlineData(8, 4, 1)]
    [InlineData(31, 4, 1)]
    [InlineData(32, 4, 2)]
    [InlineData(47, 4, 2)]
    [InlineData(48, 4, 3)]
    [InlineData(64, 4, 4)]
    [InlineData(200, 4, 4)]
    [InlineData(200, 1, 1)]
    [InlineData(200, 0, 0)]
    public void TheRestSplitsIntoAPartPerSixteenItemsUpToTheWorkers(int count, int workers, int parts) =>
        Assert.Equal(parts, FrontSliceCutter<int, FakeSlice>.PartsFor(count, workers, minimumItems: 8));

    [Fact]
    public void AFullSliceIsHandedOutAtOnceAndTheRestAtTheCut()
    {
        var (cutter, backend, _) = Create();
        AddRange(cutter, 64);
        Assert.Equal(new[] { "dispatch 0+64" }, backend.Log);

        AddRange(cutter, 64, 100);
        cutter.Flush(FrontSliceFlush.Item);

        Assert.Equal(new[] { "dispatch 0+64", "dispatch 64+18", "dispatch 82+18" }, backend.Log.Where(entry => entry.StartsWith("dispatch", StringComparison.Ordinal)));
        Assert.Equal(Enumerable.Range(0, 100), backend.Ended);
        Assert.All(backend.Log.Where(entry => !entry.StartsWith("dispatch", StringComparison.Ordinal)), entry => Assert.StartsWith("joined", entry));
        Assert.All(backend.Slices, slice => Assert.True(slice.Finished));
        Assert.False(cutter.HasOpen);
    }

    [Fact]
    public void OnlyTheSlicesHandedOutAtTheCutAreJoinedAsTheRest()
    {
        var (cutter, backend, _) = Create();
        backend.JoiningRest = () => cutter.JoiningRest;
        AddRange(cutter, 100);
        cutter.Flush(FrontSliceFlush.Item);

        Assert.Equal(new[] { (0, false), (0, false), (64, true), (64, true), (82, true), (82, true) }, backend.Waits);
        Assert.False(cutter.JoiningRest);
    }

    [Fact]
    public void AShortRunRunsHereWithoutASlice()
    {
        var (cutter, backend, stats) = Create();
        AddRange(cutter, 5);
        cutter.Flush(FrontSliceFlush.Exit);

        Assert.Equal(new[] { "short 0", "short 1", "short 2", "short 3", "short 4" }, backend.Log);
        Assert.Equal(5, stats.Inline[(int)FrontSliceInline.Short]);
        Assert.Equal(1, stats.Flushes[(int)FrontSliceFlush.Exit]);
        Assert.Equal(0, stats.Slices);
    }

    [Fact]
    public void AShortRestRunsHereAfterTheSlicesBeforeItAreJoined()
    {
        var (cutter, backend, _) = Create();
        AddRange(cutter, 70);
        cutter.Flush(FrontSliceFlush.Item);

        Assert.Equal(Enumerable.Range(0, 70), backend.Ended);
        Assert.Equal("joined 63", backend.Log[64]);
        Assert.Equal("short 64", backend.Log[65]);
    }

    [Fact]
    public void WithoutWorkersEveryItemRunsHereInOrder()
    {
        var (cutter, backend, _) = Create(sliceItems: 16);
        backend.WorkerCount = 0;
        AddRange(cutter, 40);
        cutter.Flush(FrontSliceFlush.Exit);

        Assert.Empty(backend.Slices);
        Assert.Equal(Enumerable.Range(0, 40), backend.Ended);
        Assert.All(backend.Log, entry => Assert.StartsWith("short", entry));
    }

    [Fact]
    public void TheItemAWorkerStoppedAtRunsHereAndTheNextSegmentIsJoined()
    {
        var (cutter, backend, stats) = Create(sliceItems: 20);
        backend.Script = slice =>
        {
            slice.Segments.Enqueue(new FrontSliceSegment(0, 10, true, "buffer lookup", null));
            slice.Segments.Enqueue(new FrontSliceSegment(11, 9, false, null, null));
        };
        AddRange(cutter, 20);
        cutter.Flush(FrontSliceFlush.Item);

        Assert.Equal(Enumerable.Range(0, 20), backend.Ended);
        Assert.Equal("serial 10", backend.Log[11]);
        Assert.Equal("joined 11", backend.Log[12]);
        Assert.Equal(1, stats.SerialReasons["buffer lookup"]);
    }

    [Fact]
    public void AfterAWorkerGaveUpTheRestOfItsSliceRunsHere()
    {
        var (cutter, backend, stats) = Create(sliceItems: 20);
        backend.Script = slice =>
        {
            slice.Segments.Enqueue(new FrontSliceSegment(0, 5, true, "stale color target", null));
            slice.Resume = 6;
        };
        AddRange(cutter, 20);
        cutter.Flush(FrontSliceFlush.Item);

        Assert.Equal(Enumerable.Range(0, 20), backend.Ended);
        Assert.Equal(new[] { "joined 0", "joined 1", "joined 2", "joined 3", "joined 4", "serial 5", "tail 6" }, backend.Log.Skip(1).Take(7));
        Assert.Equal(14, stats.Inline[(int)FrontSliceInline.Tail]);
    }

    [Fact]
    public void AnEmptyFirstSegmentStillRunsTheSerialItemFirst()
    {
        var (cutter, backend, _) = Create(sliceItems: 10);
        backend.Script = slice =>
        {
            slice.Segments.Enqueue(new FrontSliceSegment(0, 0, true, "unresolved draw", null));
            slice.Segments.Enqueue(new FrontSliceSegment(1, 9, false, null, null));
        };
        AddRange(cutter, 10);
        cutter.Flush(FrontSliceFlush.Item);

        Assert.Equal(new[] { "dispatch 0+10", "serial 0", "joined 1" }, backend.Log.Take(3));
        Assert.Equal(Enumerable.Range(0, 10), backend.Ended);
    }

    [Fact]
    public void ASegmentThatDoesNotValidateRunsHereAndTheSliceGoesOn()
    {
        var (cutter, backend, stats) = Create(sliceItems: 12);
        backend.Script = slice =>
        {
            slice.Segments.Enqueue(new FrontSliceSegment(0, 4, true, "texture acquire", null));
            slice.Segments.Enqueue(new FrontSliceSegment(5, 7, false, null, null));
            slice.Invalid.Add(0);
        };
        AddRange(cutter, 12);
        cutter.Flush(FrontSliceFlush.Item);

        Assert.Equal(new[] { "invalid 0", "invalid 1", "invalid 2", "invalid 3", "serial 4", "joined 5" }, backend.Log.Skip(1).Take(6));
        Assert.Equal(4, stats.Inline[(int)FrontSliceInline.Invalid]);
        Assert.Equal(Enumerable.Range(0, 12), backend.Ended);
    }

    [Fact]
    public void AStolenSliceRunsHereInOrderBetweenJoinedOnes()
    {
        var (cutter, backend, stats) = Create(sliceItems: 8);
        var dispatched = 0;
        backend.Script = slice =>
        {
            slice.Segments.Enqueue(new FrontSliceSegment(0, slice.Count, false, null, null));
            slice.Stolen = dispatched++ == 1;
        };
        AddRange(cutter, 24);
        cutter.Flush(FrontSliceFlush.Item);

        Assert.Equal(Enumerable.Range(0, 24), backend.Ended);
        Assert.Equal(new[] { "stolen 8", "stolen 15" }, new[] { backend.Log[11], backend.Log[18] });
        Assert.Equal(8, stats.Inline[(int)FrontSliceInline.Stolen]);
        Assert.All(backend.Slices, slice => Assert.True(slice.Finished));
    }

    [Fact]
    public void ASegmentOutOfOrderFailsTheJoin()
    {
        var (cutter, backend, _) = Create(sliceItems: 10);
        backend.Script = slice => slice.Segments.Enqueue(new FrontSliceSegment(2, 8, false, null, null));
        AddRange(cutter, 10);

        Assert.Throws<InvalidOperationException>(() => cutter.Flush(FrontSliceFlush.Item));
        Assert.True(backend.Slices[0].Finished);
        Assert.False(cutter.HasOpen);
    }

    [Fact]
    public void AResumeThatDisagreesWithTheSegmentsFailsTheJoin()
    {
        var (cutter, backend, _) = Create(sliceItems: 10);
        backend.Script = slice =>
        {
            slice.Segments.Enqueue(new FrontSliceSegment(0, 4, false, null, null));
            slice.Resume = 10;
        };
        AddRange(cutter, 10);

        Assert.Throws<InvalidOperationException>(() => cutter.Flush(FrontSliceFlush.Item));
    }

    [Fact]
    public void AFlushWhileJoiningDoesNothing()
    {
        var backend = new Backend();
        var stats = new FrontSliceStats();
        var reentered = 0;
        FrontSliceCutter<int, FakeSlice>? reentrant = null;
        var inner = new ReentrantBackend(backend, () =>
        {
            reentered++;
            Assert.True(reentrant!.Joining);
            reentrant.Flush(FrontSliceFlush.Read);
        });
        reentrant = new FrontSliceCutter<int, FakeSlice>(inner, stats);
        AddRange(reentrant, 40);
        reentrant.Flush(FrontSliceFlush.Item);

        Assert.Equal(40, reentered);
        Assert.Equal(Enumerable.Range(0, 40), backend.Ended);
        Assert.Equal(0, stats.Flushes[(int)FrontSliceFlush.Read]);
        Assert.Equal(1, stats.Flushes[(int)FrontSliceFlush.Item]);
    }

    // [local] SHARPEMU_FRONT_SLICE_DISPATCHES: draws, dispatches and barriers are one stream of items to the cutter. Across
    // full slices, a stolen one, a worker that stops at a dispatch and resumes, one that gives up, and the joins a
    // preparation check causes, every item ends once and in stream order.
    [Fact]
    public void MixedDrawsDispatchesAndBarriersEndInStreamOrderAcrossPrepJoins()
    {
        var (cutter, backend, stats) = Create(sliceItems: 16);
        var dispatched = 0;
        backend.Script = slice =>
        {
            switch (dispatched++)
            {
                case 0:
                    slice.Segments.Enqueue(new FrontSliceSegment(0, 6, true, "indirect thread dispatch", null));
                    slice.Segments.Enqueue(new FrontSliceSegment(7, slice.Count - 7, false, null, null));
                    break;
                case 1:
                    slice.Stolen = true;
                    break;
                case 2:
                    slice.Segments.Enqueue(new FrontSliceSegment(0, 3, true, "frontend image copy", null));
                    slice.Resume = 4;
                    break;
                default:
                    slice.Segments.Enqueue(new FrontSliceSegment(0, slice.Count, false, null, null));
                    break;
            }
        };

        // Items 0-39 (a draw, a dispatch, a barrier, repeating), a preparation join, then 40-99 and the exit.
        AddRange(cutter, 40);
        cutter.Flush(FrontSliceFlush.Prep);
        Assert.False(cutter.HasOpen);
        AddRange(cutter, 40, 100);
        cutter.Flush(FrontSliceFlush.Exit);

        Assert.Equal(Enumerable.Range(0, 100), backend.Ended);
        Assert.Equal(1, stats.Flushes[(int)FrontSliceFlush.Prep]);
        Assert.Equal(1, stats.Flushes[(int)FrontSliceFlush.Exit]);
        Assert.Contains("serial 6", backend.Log);
        Assert.Contains("stolen 16", backend.Log);
        Assert.Contains("serial 35", backend.Log);
        Assert.Contains("tail 36", backend.Log);
        Assert.All(backend.Slices, slice => Assert.True(slice.Finished));
    }

    [Fact]
    public void APreparationWithUnknownPagesJoins()
    {
        Assert.True(FrontSliceGuards.PreparationNeedsJoin(null, static (_, _) => false));
        Assert.True(FrontSliceGuards.PreparationNeedsJoin(new FrontPrepReads.Pages { Overflow = true }, static (_, _) => false));
        Assert.True(FrontSliceGuards.PreparationNeedsJoin(new FrontPrepReads.Pages(), null));
        Assert.False(FrontSliceGuards.PreparationNeedsJoin(new FrontPrepReads.Pages(), static (_, _) => true));
    }

    [Fact]
    public void APreparationJoinsOnlyWhenAPageItReadHasAPendingWrite()
    {
        var pages = new FrontPrepReads.Pages();
        pages.Runs.Add((0x10, 0x11));
        pages.Runs.Add((0x40, 0x40));
        var asked = new List<(ulong Address, ulong Size)>();
        bool PendingAt(ulong pendingAddress, ulong address, ulong size)
        {
            asked.Add((address, size));
            return address <= pendingAddress && pendingAddress < address + size;
        }

        Assert.False(FrontSliceGuards.PreparationNeedsJoin(pages, (address, size) => PendingAt(0x30000, address, size)));
        Assert.Equal(new[] { (0x10000UL, 0x2000UL), (0x40000UL, 0x1000UL) }, asked);
        Assert.True(FrontSliceGuards.PreparationNeedsJoin(pages, (address, size) => PendingAt(0x11FFF, address, size)));
        Assert.True(FrontSliceGuards.PreparationNeedsJoin(pages, (address, size) => PendingAt(0x40000, address, size)));
    }

    private sealed class ReentrantBackend(Backend inner, Action onEnd) : FrontSliceCutter<int, FakeSlice>.IBackend
    {
        public int Workers => inner.Workers;

        public FakeSlice Dispatch(List<int> items, int start, int count) => inner.Dispatch(items, start, count);

        public bool TryClaim(FakeSlice slice) => inner.TryClaim(slice);

        public bool NextSegment(FakeSlice slice, out FrontSliceSegment segment) => inner.NextSegment(slice, out segment);

        public bool JoinSegment(FakeSlice slice, in FrontSliceSegment segment) => inner.JoinSegment(slice, in segment);

        public int Resume(FakeSlice slice) => inner.Resume(slice);

        public void Finish(FakeSlice slice) => inner.Finish(slice);

        public void Complete(int item)
        {
            inner.Complete(item);
            onEnd();
        }

        public void RunInline(int item, FrontSliceInline reason)
        {
            inner.RunInline(item, reason);
            onEnd();
        }
    }
}
