// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_LITE_OBTAIN: the light read obtain and find against the real path. The two fast_wrong kinds the gate
// printed (a stream copy for a small read, an upload for a large one, after a light "clean") are reproduced by a CPU
// write between the light answer and the real one, which =2 counts as raced; without it both answer alike.
[Collection(SchedulingStateCollection.Name)]
public sealed class LiteObtainTests : IClassFixture<HeadlessVulkanFixture>, IDisposable
{
    private const ulong Page = GuestBufferCache.CachingPageSize;
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    private readonly HeadlessVulkan? _vulkan;

    public LiteObtainTests(HeadlessVulkanFixture fixture)
    {
        _vulkan = fixture.Vulkan;
        LiteObtainUse.ResetForTests();
    }

    public void Dispose() => LiteObtainUse.ResetForTests();

    // A clean 4-page buffer over a fresh mapping: the first read uploads it and leaves its pages clean.
    private static (CacheHarness Harness, ulong Address, GpuBuffer Buffer) CleanBuffer(HeadlessVulkan vulkan, ulong mapped = 0x10000)
    {
        var harness = new CacheHarness(vulkan);
        var address = harness.MapBacked(mapped, ReadWrite);
        harness.Write(address, Pattern(0x10000, 1));
        var (buffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x10000, isWritten: false));
        Assert.False(harness.Cache.HasCpuDirtyPages(address, 0x10000));
        return (harness, address, buffer);
    }

    [Theory]
    [InlineData(0x17E0UL, 0x800UL, false)]
    [InlineData(0xA250UL, 0x4F60UL, false)]
    [InlineData(0x40UL, 0x100UL, true)]
    public void CleanReadAnswersThePageOwnerLikeTheRealPath(ulong offset, ulong size, bool texel)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address, buffer) = CleanBuffer(_vulkan!);
        using var disposer = harness;
        var (cause, lite, liteOffset) = harness.Worker.Run(() =>
        {
            var answer = harness.Cache.LiteObtain(address + offset, size, texel, out var owner, out var ownerOffset);
            return (answer, owner, ownerOffset);
        });
        Assert.Equal(LiteObtainProbe.Cause.Fast, cause);
        Assert.Same(buffer, lite);
        Assert.Equal(offset, liteOffset);

        var uploads = harness.Cache.SyncUploadsForTests;
        var real = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + offset, size, false, texel));
        Assert.Same(buffer, real.Buffer);
        Assert.Equal(offset, real.Offset);
        Assert.Equal(uploads, harness.Cache.SyncUploadsForTests);
        harness.Shutdown();
    }

    [Fact]
    public void LightObtainTouchesTheBufferAndCountsAsLite()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address, buffer) = CleanBuffer(_vulkan!);
        using var disposer = harness;
        harness.Cache.LiteObtainMode = 1;
        var identifier = harness.Cache.FindBuffer(address, 8);
        var before = harness.Cache.BufferUseTickForTests(identifier);
        harness.Worker.Run(() => harness.Cache.RunGarbageCollector());
        var uploads = harness.Cache.SyncUploadsForTests;

        var (lite, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x100, 0x200, isWritten: false));

        Assert.Same(buffer, lite);
        Assert.Equal(0x100UL, offset);
        Assert.Equal(uploads, harness.Cache.SyncUploadsForTests);
        Assert.True(harness.Cache.BufferUseTickForTests(identifier) > before);
        Assert.Equal(1, LiteObtainUse.Lite);
        Assert.Equal(1, LiteObtainUse.Obtains);
        harness.Shutdown();
    }

    // The gate's two kinds: after a light "clean", a CPU write makes the real path stream a small read and upload a large one.
    [Theory]
    [InlineData(0x17E0UL, 0x800UL, true)]
    [InlineData(0xA250UL, 0x4F60UL, false)]
    public void CpuWriteBetweenTheLightAnswerAndTheRealPathIsRaced(ulong offset, ulong size, bool streams)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address, buffer) = CleanBuffer(_vulkan!);
        using var disposer = harness;
        harness.Cache.LiteObtainMode = 2;
        var latest = Pattern((int)size, 7);
        harness.Cache.LiteVerifyBetweenForTests = () =>
        {
            Assert.True(harness.Store.MarkCpuWrite(address + offset, size));
            harness.Write(address + offset, latest);
        };
        var uploads = harness.Cache.SyncUploadsForTests;

        var (real, realOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + offset, size, isWritten: false));

        var stream = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream);
        if (streams)
        {
            Assert.Equal(stream.Handle.Handle, real.Handle.Handle);
            Assert.Equal(latest, real.Mapped.Slice((int)realOffset, (int)size).ToArray());
        }
        else
        {
            Assert.Same(buffer, real);
            Assert.True(harness.Cache.SyncUploadsForTests > uploads);
            Assert.Equal(latest, harness.ReadBack(buffer, offset, size));
        }

        Assert.Equal(1, LiteObtainUse.Verified);
        Assert.Equal(1, LiteObtainUse.Raced);
        Assert.Equal(0, LiteObtainUse.Wrong);
        Assert.Equal(0, LiteObtainUse.WrongTotal);

        // The next read sees the dirty page and takes the real path, as ObtainBufferCore alone would.
        harness.Cache.LiteVerifyBetweenForTests = null;
        harness.Cache.LiteObtainMode = 1;
        var next = harness.Worker.Run(() => harness.Cache.LiteObtain(address + offset, size, false, out _, out _));
        Assert.Equal(streams ? LiteObtainProbe.Cause.Cpu : LiteObtainProbe.Cause.Fast, next);
        harness.Shutdown();
    }

    // The lo2 case (0x141E297E0+0x800, prefound, work=Stream, counted unraced): a write fault is marking the range on
    // another thread when the light check runs. CpuDirtyings has already moved but the bits and the block summary are
    // not published, so the lock-free check reads clean; the real path then waits for the region lock, sees the page
    // dirty and streams. CpuDirtyPublications moves only after the light check, so =2 counts it raced.
    [Fact]
    public void DirtyingInFlightAtTheLightCheckIsRaced()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address, buffer) = CleanBuffer(_vulkan!);
        using var disposer = harness;
        const ulong offset = 0x17E0, size = 0x800;
        var identifier = harness.Cache.FindBuffer(address, 8);
        var latest = Pattern((int)size, 9);
        using var inFlight = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        using var marked = new ManualResetEventSlim();
        var marker = new Thread(() =>
        {
            TrackedRegionHook(() =>
            {
                inFlight.Set();
                go.Wait(TimeSpan.FromSeconds(30));
            });
            try
            {
                harness.Store.MarkCpuWrite(address + offset, size);
                harness.Write(address + offset, latest);
            }
            finally
            {
                TrackedRegionHook(null);
                marked.Set();
            }
        }) { IsBackground = true };
        marker.Start();
        Assert.True(inFlight.Wait(TimeSpan.FromSeconds(30)));
        harness.Cache.LiteObtainMode = 2;
        harness.Cache.LiteVerifyBetweenForTests = () =>
        {
            go.Set();
            marked.Wait(TimeSpan.FromSeconds(30));
        };

        var (real, realOffset) = harness.Worker.Run(() =>
            harness.Cache.ObtainBuffer(address + offset, size, isWritten: false, bufferIdentifier: identifier));

        marker.Join();
        Assert.Equal(harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream).Handle.Handle, real.Handle.Handle);
        Assert.NotSame(buffer, real);
        Assert.Equal(latest, real.Mapped.Slice((int)realOffset, (int)size).ToArray());
        Assert.Equal(1, LiteObtainUse.Verified);
        Assert.Equal(1, LiteObtainUse.Raced);
        Assert.Equal(0, LiteObtainUse.Wrong);
        Assert.Equal(0, LiteObtainUse.WrongTotal);
        harness.Shutdown();

        static void TrackedRegionHook(Action? hook) => SharpEmu.HLE.GpuMemory.TrackedRegion.AfterCpuDirtyingCountedForTests = hook;
    }

    [Fact]
    public void VerifyWithoutAnInterveningWriteAgrees()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address, buffer) = CleanBuffer(_vulkan!);
        using var disposer = harness;
        harness.Cache.LiteObtainMode = 2;
        for (var index = 0UL; index < 8; index++)
        {
            var (real, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + index * 0x1000, 0x800, isWritten: false));
            Assert.Same(buffer, real);
            Assert.Equal(index * 0x1000, offset);
        }

        Assert.Equal(8, LiteObtainUse.Verified);
        Assert.Equal(0, LiteObtainUse.Wrong);
        Assert.Equal(0, LiteObtainUse.Raced);
        harness.Shutdown();
    }

    // A change between the two that is not a CPU write (here a read that merges the buffer into a larger one) is wrong.
    [Fact]
    public void VerifyCountsAnUnracedDisagreementAsWrong()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address, buffer) = CleanBuffer(_vulkan!, mapped: 0x20000);
        using var disposer = harness;
        harness.Cache.LiteObtainMode = 2;
        harness.Cache.LiteVerifyBetweenForTests = () =>
        {
            harness.Cache.LiteVerifyBetweenForTests = null;
            harness.Cache.LiteObtainMode = 0;
            _ = harness.Cache.ObtainBuffer(address + 0x8000, 0x10000, isWritten: false);
            harness.Cache.LiteObtainMode = 2;
        };

        var (real, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x100, 0x200, isWritten: false));

        Assert.NotSame(buffer, real);
        Assert.Equal(1, LiteObtainUse.Wrong);
        Assert.Equal(1, LiteObtainUse.WrongTotal);
        Assert.Equal(0, LiteObtainUse.Raced);
        harness.Shutdown();
    }

    [Fact]
    public void ReadsTheLightPathCannotProveCleanTakeTheRealPath()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address, buffer) = CleanBuffer(_vulkan!, mapped: 0x20000);
        using var disposer = harness;
        harness.Cache.LiteObtainMode = 1;
        harness.Worker.Run(() =>
        {
            // Past the buffer, across its end, a written binding, and a pending publication.
            Assert.Equal(LiteObtainProbe.Cause.Unmapped, harness.Cache.LiteObtain(address + 0x10000, 0x100, false, out _, out _));
            Assert.Equal(LiteObtainProbe.Cause.Cross, harness.Cache.LiteObtain(address + 0xFF00, 0x200, false, out _, out _));
            var written = harness.Cache.ObtainBuffer(address + 0x200, 0x100, isWritten: true);
            Assert.Same(buffer, written.Buffer);
            harness.Cache.MayHaveGuestPublications = (start, size) => start < address + 0x400 && address + 0x300 < start + size;
            Assert.Equal(LiteObtainProbe.Cause.Pub, harness.Cache.LiteObtain(address + 0x380, 0x10, false, out _, out _));
            Assert.Equal(LiteObtainProbe.Cause.Fast, harness.Cache.LiteObtain(address + 0x800, 0x10, false, out _, out _));
            harness.Cache.MayHaveGuestPublications = null;
        });

        Assert.Equal(0, LiteObtainUse.Lite);
        Assert.True(harness.Store.MarkCpuWrite(address + 0x8040, 8));
        Assert.Equal(LiteObtainProbe.Cause.Cpu, harness.Worker.Run(() => harness.Cache.LiteObtain(address + 0x8000, 0x80, false, out _, out _)));
        harness.Shutdown();
    }

    [Fact]
    public void LightFindAnswersTheOwnerOnceItsRangeIsReadable()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var (harness, address, buffer) = CleanBuffer(_vulkan!);
        using var disposer = harness;
        var memory = new CountingMemory { Readable = true, Generation = 5 };
        var identifier = harness.Cache.FindBuffer(address, 8);

        Assert.True(harness.Cache.TryLiteFind(address + 0x100, 0x200, memory.Generation, memory, out var found, out var start, out var end));
        Assert.Equal(identifier, found);
        Assert.Equal(buffer.CpuAddress, start);
        Assert.Equal(buffer.CpuAddress + buffer.Size, end);
        Assert.True(harness.Cache.TryLiteFind(address + 0x4000, 0x200, memory.Generation, memory, out _, out _, out _));
        Assert.Equal(1, memory.CanReads);

        // A new generation asks again; an unreadable range is kept as such for the generation.
        memory.Readable = false;
        memory.Generation = 6;
        Assert.False(harness.Cache.TryLiteFind(address + 0x100, 0x200, memory.Generation, memory, out _, out _, out _));
        Assert.False(harness.Cache.TryLiteFind(address + 0x100, 0x200, memory.Generation, memory, out _, out _, out _));
        Assert.Equal(2, memory.CanReads);
        Assert.False(harness.Cache.TryLiteFind(address + 0x10000, 0x10, memory.Generation, memory, out _, out _, out _));
        harness.Shutdown();
    }

    [Fact]
    public void UseLineReportsTheWindowAndClearsIt()
    {
        LiteObtainUse.Obtains = 10;
        LiteObtainUse.Lite = 7;
        LiteObtainUse.NoteSlow(LiteObtainUse.Slow.Cpu);
        LiteObtainUse.NoteSlow(LiteObtainUse.Slow.Guard);
        LiteObtainUse.Finds = 4;
        LiteObtainUse.FindLite = 3;
        LiteObtainUse.FindSlow = 1;
        Assert.False(LiteObtainUse.NoteMismatch(raced: true));
        Assert.True(LiteObtainUse.NoteMismatch(raced: false));

        var line = LiteObtainUse.FormatAndClear(2);

        Assert.Equal("[PERF][LITE_OBTAIN_USE] mode=2 obtains=10 lite=7 slow=pub:0,cpu:1,img:0,hot:0,cross:0,unmapped:0,guard:1 finds=4 find_lite=3 find_slow=1 " +
            "verified=0 wrong=1 raced=1 wrong_total=1 find_verified=0 find_wrong=0 find_wrong_total=0", line);
        Assert.StartsWith("[PERF][LITE_OBTAIN_USE] mode=2 obtains=0 lite=0 ", LiteObtainUse.FormatAndClear(2));
    }

    private sealed class CountingMemory : ICpuMemory
    {
        public bool Readable { get; set; }

        public long Generation { get; set; }

        public int CanReads { get; private set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;

        public bool CanRead(ulong address, ulong size)
        {
            CanReads++;
            return Readable;
        }

        public long MappingGeneration => Generation;
    }

    private static byte[] Pattern(int size, byte seed)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }
}
