// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_FAULT_READ_EMULATE: the store side of an emulated faulting read (a label sharing its page with a
// GPU-written range), its parked poll and the =2 write watch.
[Collection(SchedulingStateCollection.Name)]
public sealed class FaultReadEmulationTests : IClassFixture<HeadlessVulkanFixture>
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private readonly HeadlessVulkan? _vulkan;

    public FaultReadEmulationTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    private static long Ticks(int milliseconds) => Stopwatch.Frequency * milliseconds / 1000;

    private static (CacheHarness Harness, ulong Label) LabelBesideGpuWrites(HeadlessVulkan vulkan)
    {
        var harness = new CacheHarness(vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address + 0x180, 0x80, isWritten: true);
            buffer.Fill(offset, 0x80, 0x11223344);
            harness.Cache.WriteHostMemory(address, BitConverter.GetBytes(5UL));
        });
        return (harness, address);
    }

    [Fact]
    public void UnmodifiedBytesAreServedAndGpuModifiedOnesDeclined()
    {
        if (_vulkan is null) return;
        var (harness, label) = LabelBesideGpuWrites(_vulkan!);
        using (harness)
        {
            Assert.True(harness.Cache.HasGpuDirtyPages(label, 8));
            Span<byte> bytes = stackalloc byte[8];
            Assert.True(harness.Store.TryReadUnmodified(label, bytes));
            Assert.Equal(5UL, BitConverter.ToUInt64(bytes));
            Assert.False(harness.Store.TryReadUnmodified(label + 0x1C0, bytes));
            Assert.False(harness.Store.TryReadUnmodifiedParked(label + 0x1C0, bytes, Stopwatch.GetTimestamp() + Ticks(50)));
            harness.Shutdown();
        }
    }

    [Fact]
    public void AParkedPollReturnsWhenTheCommandProcessorWritesItsBytes()
    {
        if (_vulkan is null) return;
        var (harness, label) = LabelBesideGpuWrites(_vulkan!);
        using (harness)
        {
            var writer = Task.Run(() =>
            {
                Thread.Sleep(50);
                harness.Worker.Run(() => harness.Cache.WriteHostMemory(label + 0x40, BitConverter.GetBytes(9UL)));
                Thread.Sleep(50);
                harness.Worker.Run(() => harness.Cache.WriteHostMemory(label, BitConverter.GetBytes(6UL)));
            });
            var bytes = BitConverter.GetBytes(5UL);
            var started = Stopwatch.GetTimestamp();
            Assert.True(harness.Store.TryReadUnmodifiedParked(label, bytes, started + Ticks(10_000)));
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            writer.Wait();
            Assert.Equal(6UL, BitConverter.ToUInt64(bytes));
            Assert.InRange(elapsed, 90, 5_000);
            harness.Shutdown();
        }
    }

    [Fact]
    public void AParkedPollWithNoWriteReturnsTheSameBytesAtItsDeadline()
    {
        if (_vulkan is null) return;
        var (harness, label) = LabelBesideGpuWrites(_vulkan!);
        using (harness)
        {
            var bytes = BitConverter.GetBytes(5UL);
            var started = Stopwatch.GetTimestamp();
            Assert.True(harness.Store.TryReadUnmodifiedParked(label, bytes, started + Ticks(30)));
            Assert.True(Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 29);
            Assert.Equal(5UL, BitConverter.ToUInt64(bytes));

            var changed = BitConverter.GetBytes(4UL);
            started = Stopwatch.GetTimestamp();
            Assert.True(harness.Store.TryReadUnmodifiedParked(label, changed, started + Ticks(5_000)));
            Assert.True(Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1_000);
            Assert.Equal(5UL, BitConverter.ToUInt64(changed));
            harness.Shutdown();
        }
    }

    [Fact]
    public void AWriteWatchCountsOnlyTheWritesThatTouchItsRange()
    {
        if (_vulkan is null) return;
        var (harness, label) = LabelBesideGpuWrites(_vulkan!);
        using (harness)
        {
            var watch = harness.Store.BeginWriteWatch(label, 8);
            Assert.True(watch >= 0);
            harness.Worker.Run(() => harness.Cache.WriteHostMemory(label + 8, BitConverter.GetBytes(1UL)));
            Assert.Equal(0, harness.Store.EndWriteWatch(watch));

            watch = harness.Store.BeginWriteWatch(label, 8);
            harness.Worker.Run(() => harness.Cache.WriteHostMemory(label + 4, BitConverter.GetBytes(1UL)));
            Assert.Equal(1, harness.Store.EndWriteWatch(watch));

            watch = harness.Store.BeginWriteWatch(label + 0x1C0, 8);
            harness.Worker.Run(() => harness.Cache.ReadMemory(label + 0x1C0, 8));
            Assert.True(harness.Store.EndWriteWatch(watch) >= 1);
            harness.Shutdown();
        }
    }
}
