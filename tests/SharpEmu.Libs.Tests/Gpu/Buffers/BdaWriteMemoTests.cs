// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_BDA_WRITE_MEMO: a written device-address range obtained again in the same tick answers from the memo
// while the real path would do nothing new, and misses after a new tick, a mapping change, a GPU-modified removal over
// it or a new page owner. =2 runs the real path on every hit and finds nothing the memo skipped.
[Collection(SchedulingStateCollection.Name)]
public sealed class BdaWriteMemoTests : IClassFixture<HeadlessVulkanFixture>, IDisposable
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private const ulong Offset = 0x1000;
    private const ulong Size = 0x4000;

    private readonly HeadlessVulkan? _vulkan;

    public BdaWriteMemoTests(HeadlessVulkanFixture fixture)
    {
        _vulkan = fixture.Vulkan;
        BdaWriteMemo.ResetForTests();
    }

    public void Dispose() => BdaWriteMemo.ResetForTests();

    private static (CacheHarness Harness, ulong Address) Mapped(HeadlessVulkan vulkan, int mode)
    {
        var harness = new CacheHarness(vulkan);
        harness.Cache.BdaWriteMemoMode = mode;
        var address = harness.MapBacked(0x40000, ReadWrite);
        return (harness, address);
    }

    private static void Obtain(CacheHarness harness, ulong address, ulong size = Size, bool conservative = false, long mapping = 1, long resets = 0) =>
        harness.Worker.Run(() => harness.Cache.ObtainBdaWrite(address, size, size, conservative, 0, mapping, resets));

    private static (GuestBufferCache.BdaRepeat Repeat, ulong Size) Repeat(CacheHarness harness, ulong address, ulong size = Size,
        bool conservative = false, long mapping = 1, long resets = 0) => harness.Worker.Run(() =>
    {
        var repeat = harness.Cache.TryRepeatBdaWrite(address, size, conservative, 0, mapping, resets, out var repeated);
        return (repeat, repeated);
    });

    private static GpuBuffer Owner(CacheHarness harness, ulong address) =>
        harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 4, isWritten: false).Buffer);

    private static long Missed(BdaWriteMemo.Miss cause) => BdaWriteMemo.MissCounts[(int)cause];

    [Fact]
    public void RepeatedWrittenRangeHitsAndStillCountsTheWrite()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 1);
        using var disposer = harness;
        Obtain(harness, address + Offset);
        var buffer = Owner(harness, address + Offset);
        var writes = buffer.GpuWrites;
        var uploads = harness.Cache.SyncUploadsForTests;

        var (repeat, size) = Repeat(harness, address + Offset);

        Assert.True(repeat == GuestBufferCache.BdaRepeat.Hit, string.Join(",", BdaWriteMemo.MissCounts) + " tick=" + harness.Scheduler.CurrentTick);
        Assert.Equal(Size, size);
        Assert.Equal(writes + 1, buffer.GpuWrites);
        Assert.Equal(uploads, harness.Cache.SyncUploadsForTests);
        Assert.True(harness.Cache.HasGpuDirtyBytes(address + Offset, Size));
        Assert.Equal(1, BdaWriteMemo.Hits);

        // Another key (the conservative flag, the requested size) is not the remembered one.
        Assert.Equal(GuestBufferCache.BdaRepeat.Miss, Repeat(harness, address + Offset, conservative: true).Repeat);
        Assert.Equal(GuestBufferCache.BdaRepeat.Miss, Repeat(harness, address + Offset, Size / 2).Repeat);
        Assert.Equal(2, Missed(BdaWriteMemo.Miss.Cold));
        harness.Shutdown();
    }

    [Fact]
    public void ANewTickMisses()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 1);
        using var disposer = harness;
        Obtain(harness, address + Offset);
        harness.Worker.Run(() => harness.Scheduler.Flush());

        Assert.Equal(GuestBufferCache.BdaRepeat.Miss, Repeat(harness, address + Offset).Repeat);
        Assert.Equal(1, Missed(BdaWriteMemo.Miss.Tick));

        Obtain(harness, address + Offset);
        Assert.Equal(GuestBufferCache.BdaRepeat.Hit, Repeat(harness, address + Offset).Repeat);
        harness.Shutdown();
    }

    [Fact]
    public void AnUnmapAndRemapMovesTheGenerationAndMisses()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 1);
        using var disposer = harness;
        var other = harness.MapBacked(0x10000, ReadWrite);
        var before = harness.Memory.MappingGeneration;
        Obtain(harness, address + Offset, mapping: before);
        Assert.Equal(GuestBufferCache.BdaRepeat.Hit, Repeat(harness, address + Offset, mapping: harness.Memory.MappingGeneration).Repeat);

        Assert.True(harness.Memory.TryUnmapBacked(other, 0x10000));
        _ = harness.MapBacked(0x10000, ReadWrite);
        var after = harness.Memory.MappingGeneration;

        Assert.NotEqual(before, after);
        Assert.Equal(GuestBufferCache.BdaRepeat.Miss, Repeat(harness, address + Offset, mapping: after).Repeat);
        Assert.Equal(1, Missed(BdaWriteMemo.Miss.Mapping));
        harness.Shutdown();
    }

    [Fact]
    public void AClampedAnswerMissesAfterTheFrameReset()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 1);
        using var disposer = harness;
        harness.Worker.Run(() => harness.Cache.ObtainBdaWrite(address + Offset, 2 * Size, Size, false, 0, 1, 0));

        var (repeat, size) = Repeat(harness, address + Offset, 2 * Size);
        Assert.Equal(GuestBufferCache.BdaRepeat.Hit, repeat);
        Assert.Equal(Size, size);
        Assert.Equal(GuestBufferCache.BdaRepeat.Miss, Repeat(harness, address + Offset, 2 * Size, resets: 1).Repeat);
        Assert.Equal(1, Missed(BdaWriteMemo.Miss.Mapping));
        harness.Shutdown();
    }

    [Fact]
    public void AGpuModifiedRemovalOverTheRangeMissesAndOneElsewhereHits()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 1);
        using var disposer = harness;
        var elsewhere = address + 0x20000;
        Obtain(harness, address + Offset);
        Obtain(harness, elsewhere);

        harness.Worker.Run(() => harness.Cache.NoteKnownGpuContents(elsewhere + 0x100, [0x11111111u, 0x22222222u]));
        Assert.Equal(GuestBufferCache.BdaRepeat.Hit, Repeat(harness, address + Offset).Repeat);

        harness.Worker.Run(() => harness.Cache.NoteKnownGpuContents(address + Offset + 0x100, [0x33333333u]));
        Assert.False(harness.Cache.HasGpuDirtyBytes(address + Offset + 0x100, 4));
        Assert.Equal(GuestBufferCache.BdaRepeat.Miss, Repeat(harness, address + Offset).Repeat);
        Assert.Equal(1, Missed(BdaWriteMemo.Miss.Removed));

        // The real obtain marks the bytes again, and the next repeat hits.
        Obtain(harness, address + Offset);
        Assert.True(harness.Cache.HasGpuDirtyBytes(address + Offset + 0x100, 4));
        Assert.Equal(GuestBufferCache.BdaRepeat.Hit, Repeat(harness, address + Offset).Repeat);
        harness.Shutdown();
    }

    [Fact]
    public void ACpuWriteOverTheRangeMissesAndOneOutsideItHits()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 1);
        using var disposer = harness;
        Obtain(harness, address + Offset);

        // A page the range does not touch: the written path ignores CPU-dirty pages of other bytes.
        Assert.True(harness.Store.MarkCpuWrite(address + 0x30000, 4));
        harness.Write(address + 0x30000, [1, 2, 3, 4]);
        Assert.Equal(GuestBufferCache.BdaRepeat.Hit, Repeat(harness, address + Offset).Repeat);

        // A CPU write over GPU-written bytes reads them back first: they leave the GPU-modified set.
        Assert.True(harness.Store.MarkCpuWrite(address + Offset + 0x200, 4));
        Assert.Equal(GuestBufferCache.BdaRepeat.Miss, Repeat(harness, address + Offset).Repeat);
        harness.Shutdown();
    }

    [Fact]
    public void ANewPageOwnerMissesAndAnUnrelatedRegistrationHits()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 1);
        using var disposer = harness;
        Obtain(harness, address + Offset);
        var first = Owner(harness, address + Offset);

        // A buffer in another mapping bumps the registry epoch; the range's owner stays.
        var other = harness.MapBacked(0x10000, ReadWrite);
        _ = harness.Worker.Run(() => harness.Cache.FindBuffer(other, 4));
        Assert.Equal(GuestBufferCache.BdaRepeat.Hit, Repeat(harness, address + Offset).Repeat);

        // A range across the owner's end merges both into a new buffer.
        var end = first.CpuAddress + first.Size;
        _ = harness.Worker.Run(() => harness.Cache.FindBuffer(end - 0x100, 0x8000));
        Assert.NotSame(first, Owner(harness, address + Offset));
        Assert.Equal(GuestBufferCache.BdaRepeat.Miss, Repeat(harness, address + Offset).Repeat);
        Assert.Equal(1, Missed(BdaWriteMemo.Miss.Registry));
        harness.Shutdown();
    }

    [Fact]
    public void AWordStoredSinceTheFirstObtainIsDroppedByTheHit()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 1);
        using var disposer = harness;
        var word = address + Offset + 0x40;
        Obtain(harness, address + Offset);
        TableWordMemo.Store(word, 0x1234);

        Assert.Equal(GuestBufferCache.BdaRepeat.Hit, Repeat(harness, address + Offset).Repeat);

        Assert.False(TableWordMemo.TryGet(word, out _));
        Assert.Equal(1, BdaWriteMemo.TableReplays);
        harness.Shutdown();
    }

    [Fact]
    public void VerifyFindsNothingTheMemoSkipped()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address) = Mapped(_vulkan!, 2);
        using var disposer = harness;
        var elsewhere = address + 0x20000;
        Obtain(harness, address + Offset);
        Obtain(harness, address + Offset + 0x8000, 0x10000, conservative: true);
        Obtain(harness, elsewhere);
        Assert.Equal(GuestBufferCache.BdaRepeat.Verified, Repeat(harness, address + Offset).Repeat);
        Assert.Equal(GuestBufferCache.BdaRepeat.Verified, Repeat(harness, address + Offset).Repeat);

        harness.Worker.Run(() => harness.Cache.NoteKnownGpuContents(elsewhere + 0x100, [0x11111111u]));
        Assert.Equal(GuestBufferCache.BdaRepeat.Verified, Repeat(harness, address + Offset).Repeat);
        TableWordMemo.Store(address + Offset + 0x40, 7);
        Assert.Equal(GuestBufferCache.BdaRepeat.Verified, Repeat(harness, address + Offset).Repeat);
        var other = harness.MapBacked(0x10000, ReadWrite);
        _ = harness.Worker.Run(() => harness.Cache.FindBuffer(other, 4));
        Assert.Equal(GuestBufferCache.BdaRepeat.Verified, Repeat(harness, address + Offset).Repeat);
        Assert.Equal(GuestBufferCache.BdaRepeat.Verified, Repeat(harness, address + Offset + 0x8000, 0x10000, conservative: true).Repeat);

        Assert.Equal(6, BdaWriteMemo.Verified);
        Assert.Equal(0, BdaWriteMemo.Wrong);
        Assert.Equal(0, BdaWriteMemo.WrongTotal);
        harness.Shutdown();
    }
}
