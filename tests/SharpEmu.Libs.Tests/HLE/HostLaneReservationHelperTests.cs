// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class HostLaneReservationHelperTests
{
    private static List<(ulong Mask, int EfficiencyClass)> Cores(int count, int efficiencyClass = 0) =>
        Enumerable.Range(0, count).Select(index => (3UL << (2 * index), efficiencyClass)).ToList();

    [Fact]
    public void TheRenderCoreIsTheLastOfTheHighestClass()
    {
        var cores = Cores(8);
        Assert.Equal(0xC000UL, HostLaneReservation.SelectCore(cores));
        cores[2] = (cores[2].Mask, 1);
        Assert.Equal(0x30UL, HostLaneReservation.SelectCore(cores));
    }

    [Fact]
    public void RingAndFrontTakeTheNextCoresDownFromTheRenderCore()
    {
        var cores = Cores(8);
        var (ring, front) = HostLaneReservation.SelectHelperCores(cores, 0xC000);
        Assert.Equal(0x3000UL, ring);
        Assert.Equal(0x0C00UL, front);
    }

    [Fact]
    public void HelperCoresPreferTheHighestClass()
    {
        var cores = Cores(8);
        cores[0] = (cores[0].Mask, 1);
        cores[1] = (cores[1].Mask, 1);
        cores[2] = (cores[2].Mask, 1);
        var render = HostLaneReservation.SelectCore(cores);
        Assert.Equal(0x30UL, render);
        var (ring, front) = HostLaneReservation.SelectHelperCores(cores, render);
        Assert.Equal(0xCUL, ring);
        Assert.Equal(0x3UL, front);
    }

    [Fact]
    public void NoHelperCoresBelowSixPhysicalCores()
    {
        var cores = Cores(5);
        Assert.Equal((0UL, 0UL), HostLaneReservation.SelectHelperCores(cores, HostLaneReservation.SelectCore(cores)));
    }

    [Fact]
    public void HelperLanesStayOffTheRenderAndGuestLanes()
    {
        if (!HostLaneReservation.HelperLanesActive)
        {
            Assert.Equal(0UL, HostLaneReservation.FrontMask);
            return;
        }

        Assert.Equal(0UL, HostLaneReservation.RingMask & HostLaneReservation.FrontMask);
        Assert.Equal(0UL, (HostLaneReservation.RingMask | HostLaneReservation.FrontMask) &
            (HostLaneReservation.ReservedMask | HostLaneReservation.GuestMask));
    }
}
