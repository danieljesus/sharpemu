// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_RING_SLOT_REUSE: a slot that moves inside its owner's window takes the owner the lookup would find,
// and the window refuses what lies outside it or an owner that was replaced.
[Collection(SchedulingStateCollection.Name)]
public sealed class RingSlotReuseTests : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Page = GuestBufferCache.CachingPageSize;
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private readonly HeadlessVulkan? _vulkan;

    public RingSlotReuseTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Theory]
    [InlineData(0x1000UL, 0x2000UL, 0x1000UL, 0x1000UL, true)]
    [InlineData(0x1000UL, 0x2000UL, 0x1800UL, 0x800UL, true)]
    [InlineData(0x1000UL, 0x2000UL, 0x1800UL, 0x801UL, false)]
    [InlineData(0x1000UL, 0x2000UL, 0x0FFCUL, 0x10UL, false)]
    [InlineData(0x1000UL, 0x2000UL, 0x2000UL, 0x4UL, false)]
    [InlineData(0x1000UL, 0x2000UL, 0x1000UL, 0UL, false)]
    [InlineData(0UL, 0UL, 0UL, 0x10UL, false)]
    [InlineData(0x1000UL, 0x2000UL, 0x1FFCUL, ulong.MaxValue, false)]
    public void TheWindowHoldsOnlyRangesInsideIt(ulong start, ulong end, ulong address, ulong size, bool covered) =>
        Assert.Equal(covered, GuestBufferCache.WindowCovers(start, end, address, size));

    [Fact]
    public void ARangeInsideTheOwnersExtentIsFoundInTheOwner()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var owner = harness.Cache.FindBuffer(address, 4 * Page);
            Assert.True(harness.Cache.TryGetRegisteredExtent(owner, out var start, out var size));
            Assert.True(start <= address && size >= 4 * Page);
            for (var offset = 0UL; offset + 0x100 <= 4 * Page; offset += 0x340)
            {
                Assert.True(GuestBufferCache.WindowCovers(start, start + size, address + offset, 0x100));
                Assert.Equal(owner, harness.Cache.FindContainingBuffer(address + offset, 0x100));
                Assert.Equal(owner, harness.Cache.FindBuffer(address + offset, 0x100));
            }
        });
        harness.Shutdown();
    }

    [Fact]
    public void AReplacedOwnerIsNoLongerFound()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var original = harness.Cache.FindBuffer(address, Page);
            Assert.True(harness.Cache.TryGetRegisteredExtent(original, out _, out _));
            var replacement = harness.Cache.FindBuffer(address, 2 * Page);
            Assert.NotEqual(original, replacement);
            Assert.False(harness.Cache.TryGetRegisteredExtent(original, out _, out _));
            Assert.NotEqual(original, harness.Cache.FindContainingBuffer(address, 0x100));
            Assert.Equal(replacement, harness.Cache.FindContainingBuffer(address, 0x100));
        });
        harness.Shutdown();
    }
}
