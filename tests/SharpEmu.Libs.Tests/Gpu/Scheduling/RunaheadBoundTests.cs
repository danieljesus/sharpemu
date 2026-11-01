// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

// [local] SHARPEMU_RUNAHEAD_TICKS: when the render thread holds between back-queue items.
public sealed class RunaheadBoundTests
{
    [Fact]
    public void Off_NeverHolds() =>
        Assert.False(RunaheadBound.ShouldHold(sealedTick: 100, completedTick: 1, limit: 0, guestOnly: false, guestWaiters: 3));

    [Fact]
    public void WithinTheLimit_DoesNotHold()
    {
        Assert.False(RunaheadBound.ShouldHold(sealedTick: 14, completedTick: 10, limit: 4, guestOnly: false, guestWaiters: 0));
        Assert.False(RunaheadBound.ShouldHold(sealedTick: 9, completedTick: 10, limit: 4, guestOnly: false, guestWaiters: 0));
    }

    [Fact]
    public void BeyondTheLimit_Holds() =>
        Assert.True(RunaheadBound.ShouldHold(sealedTick: 15, completedTick: 10, limit: 4, guestOnly: false, guestWaiters: 0));

    [Fact]
    public void GuestOnly_HoldsOnlyWhileAGuestWaits()
    {
        Assert.False(RunaheadBound.ShouldHold(sealedTick: 15, completedTick: 10, limit: 4, guestOnly: true, guestWaiters: 0));
        Assert.True(RunaheadBound.ShouldHold(sealedTick: 15, completedTick: 10, limit: 4, guestOnly: true, guestWaiters: 1));
    }
}
