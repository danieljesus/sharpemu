// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

public sealed unsafe partial class GuestSpaceOwnerTests
{
    // A host region the owner never reserved blocks a fixed claim until the host releases it.
    [Fact]
    public void ReserveFreeRangeRefusesAForeignHostRegionAndDescribesIt()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var granularity = Math.Max(host.Granularity, Page);
        var address = ProbeGuestAddress(host, 3 * granularity);
        Assert.True(owner.TryReserveAddressRange(address, granularity));
        var foreign = address + granularity;
        Assert.Equal(foreign, host.ReserveHole(foreign, granularity));
        try
        {
            Assert.False(owner.TryReserveFreeRange(address, 3 * granularity));
            Assert.True(owner.ContainsFreeRange(address, granularity));
            Assert.Equal(granularity, owner.OwnedBytesIn(address, 3 * granularity));
            var description = owner.DescribeRange(address, 3 * granularity);
            Assert.Contains($"owned=[0x{address:X}+0x{granularity:X}]", description);
            Assert.Contains("single_free=False", description);
            Assert.Contains("mapped=[]", description);
        }
        finally
        {
            Assert.True(host.FreeHole(foreign, granularity));
        }

        Assert.True(owner.TryReserveFreeRange(address, 3 * granularity));
        Assert.Equal(3 * granularity, owner.OwnedBytesIn(address, 3 * granularity));
        Assert.Contains("single_free=True", owner.DescribeRange(address, 3 * granularity));
        owner.ReleaseAddressRanges();
    }

    [Theory]
    [InlineData(null, true, 0x10000UL)]
    [InlineData(null, false, 0x100_0000UL)]
    [InlineData("", true, 0x10000UL)]
    [InlineData("junk", false, 0x100_0000UL)]
    [InlineData("0", true, 0x10000UL)]
    [InlineData("1", false, 0x10000UL)]
    [InlineData("0x200000", true, 0x200000UL)]
    [InlineData("0x200000", false, 0x200000UL)]
    [InlineData("70000", true, 0x20000UL)]
    public void PreReserveMinimumDefaultsToTheGranularityOnWindows(string? configured, bool windows, ulong expected)
    {
        Assert.Equal(expected, GuestSpaceOwner.ResolvePreReserveMinimum(0x10000, configured, windows));
    }
}
