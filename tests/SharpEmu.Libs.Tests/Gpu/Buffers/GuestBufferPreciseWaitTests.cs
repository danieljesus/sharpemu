// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_READBACK_PRECISE_WAIT: a small render-thread read whose bytes were last written in a finished batch
// downloads only those bytes without submitting the batch being recorded; any other read keeps the ordinary path.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferPreciseWaitTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan = fixture.Vulkan;

    private static CacheHarness Start(HeadlessVulkan vulkan)
    {
        GpuWriteHistory.ResetForTests();
        var harness = new CacheHarness(vulkan);
        GpuWriteHistory.ForceRecordingThread = harness.Worker.Run(() => Environment.CurrentManagedThreadId);
        harness.Cache.AsyncReadback = new VulkanAsyncReadback(vulkan.DeviceInfo, harness.Scheduler, vulkan.Queue, vulkan.QueueFamily);
        harness.Cache.ForcePreciseWait = true;
        return harness;
    }

    private static void Stop(CacheHarness harness)
    {
        harness.Worker.Run(() =>
        {
            harness.Cache.AsyncReadback?.Dispose();
            harness.Cache.AsyncReadback = null;
        });
        harness.Shutdown();
        harness.Dispose();
        GpuWriteHistory.ForceRecordingThread = 0;
        GpuWriteHistory.ResetForTests();
    }

    private static void GpuWrite(CacheHarness harness, ulong address, uint value) => harness.Worker.Run(() =>
    {
        var (buffer, offset) = harness.Cache.ObtainBuffer(address, 0x100, isWritten: true);
        buffer.Fill(offset, 0x100, value);
    });

    private static void ShaderRead(CacheHarness harness, ulong address) => harness.Worker.Run(() =>
        Assert.True(harness.Cache.TrySynchronizeCpuRead(address, 4, GuestMemoryProfile.ReadbackSource.ShaderResourceRead)));

    [Fact]
    public void BytesWrittenInAFinishedBatchAreReadWithoutSubmittingTheBatchBeingRecorded()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        var harness = Start(_vulkan);
        try
        {
            using var fatal = new FatalScope();
            var address = harness.MapBacked(0x10000, ReadWrite);
            GpuWrite(harness, address, 0x11111111);
            harness.Worker.Run(() => harness.Scheduler.Finish());
            // A neighbour in the same 16 KB block, written in the batch being recorded.
            GpuWrite(harness, address + 0x1000, 0x22222222);
            var recording = harness.Worker.Run(() => harness.Scheduler.CurrentTick);

            ShaderRead(harness, address + 8);

            Assert.Equal((1L, 0L), (harness.Cache.PreciseWaitReads, harness.Cache.PreciseWaitFallbacks));
            Assert.Equal(recording, harness.Worker.Run(() => harness.Scheduler.CurrentTick));
            // The finished piece of the window comes down whole; the neighbour still being written stays GPU-modified.
            Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
            Assert.True(harness.Cache.HasGpuDirtyBytes(address + 0x1000, 0x100));
            Assert.Equal(0x11111111u, BitConverter.ToUInt32(harness.Read(address + 8, 4)));
        }
        finally
        {
            Stop(harness);
        }
    }

    [Fact]
    public void TheVerificationCapturesThePreciseBytesThenTakesTheOrdinaryPathAndMatches()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        var harness = Start(_vulkan);
        try
        {
            using var fatal = new FatalScope();
            harness.Cache.ForcePreciseVerify = true;
            var address = harness.MapBacked(0x10000, ReadWrite);
            GpuWrite(harness, address, 0x55555555);
            harness.Worker.Run(() => harness.Scheduler.Finish());
            GpuWrite(harness, address + 0x1000, 0x66666666);

            ShaderRead(harness, address + 8);

            Assert.Equal((0L, 0L), (harness.Cache.PreciseWaitReads, harness.Cache.PreciseWaitFallbacks));
            Assert.Equal((1L, 0L), (harness.Cache.PreciseVerifyChecks, harness.Cache.PreciseVerifyMismatches));
            Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
            Assert.False(harness.Cache.HasGpuDirtyBytes(address + 0x1000, 0x100));
            Assert.Equal(0x55555555u, BitConverter.ToUInt32(harness.Read(address + 8, 4)));
        }
        finally
        {
            Stop(harness);
        }
    }

    [Fact]
    public void BytesWrittenInTheBatchBeingRecordedTakeTheOrdinaryPath()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        var harness = Start(_vulkan);
        try
        {
            using var fatal = new FatalScope();
            var address = harness.MapBacked(0x10000, ReadWrite);
            GpuWrite(harness, address, 0x11111111);
            harness.Worker.Run(() => harness.Scheduler.Finish());
            GpuWrite(harness, address, 0x33333333);

            ShaderRead(harness, address + 8);

            Assert.Equal((0L, 1L), (harness.Cache.PreciseWaitReads, harness.Cache.PreciseWaitFallbacks));
            Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
            Assert.Equal(0x33333333u, BitConverter.ToUInt32(harness.Read(address + 8, 4)));
        }
        finally
        {
            Stop(harness);
        }
    }

    [Fact]
    public void AReadOtherThanAShaderOrCommandReadTakesTheOrdinaryPath()
    {
        if (_vulkan is null || !GatePrerequisites.Ready(_vulkan)) return;
        var harness = Start(_vulkan);
        try
        {
            using var fatal = new FatalScope();
            var address = harness.MapBacked(0x10000, ReadWrite);
            GpuWrite(harness, address, 0x44444444);
            harness.Worker.Run(() => harness.Scheduler.Finish());

            harness.Worker.Run(() =>
                Assert.True(harness.Cache.TrySynchronizeCpuRead(address + 8, 4, GuestMemoryProfile.ReadbackSource.CpuReadSynchronization)));

            Assert.Equal((0L, 0L), (harness.Cache.PreciseWaitReads, harness.Cache.PreciseWaitFallbacks));
            Assert.False(harness.Cache.HasGpuDirtyBytes(address, 0x100));
            Assert.Equal(0x44444444u, BitConverter.ToUInt32(harness.Read(address + 8, 4)));
        }
        finally
        {
            Stop(harness);
        }
    }

    [Fact]
    public void ABoundedLookupNeverReportsAnEarlierTickThanTheLastWrite()
    {
        GpuWriteHistory.ResetForTests();
        GpuWriteHistory.ForceRecordingThread = Environment.CurrentManagedThreadId;
        try
        {
            GpuWriteHistory.Note(0x1000, 0x10, tick: 5);
            for (ulong tick = 6; tick < 40; tick++)
            {
                GpuWriteHistory.Note(0x9000 + tick * 0x10, 0x10, tick);
            }

            Assert.Equal(5UL, GpuWriteHistory.LastWriteTick(0x1000, 4));
            Assert.Equal(5UL, GpuWriteHistory.LastWriteTick(0x1000, 4, scan: 64));
            // Not reached within 8 entries: the oldest entry looked at bounds the answer from above.
            Assert.Equal(32UL, GpuWriteHistory.LastWriteTick(0x1000, 4, scan: 8));
            Assert.Equal(0UL, GpuWriteHistory.LastWriteTick(0x100000, 4, scan: 64));
        }
        finally
        {
            GpuWriteHistory.ForceRecordingThread = 0;
            GpuWriteHistory.ResetForTests();
        }
    }
}
