// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class GuestPollingTests
{
    [Theory]
    [InlineData(null, -1)]
    [InlineData("", -1)]
    [InlineData("x", -1)]
    [InlineData("-3", -1)]
    [InlineData("0", 0)]
    [InlineData("16", 16)]
    public void TheBackoffSwitchIsOffUnlessItIsACount(string? value, int expected) =>
        Assert.Equal(expected, GuestPolling.ReadBackoff(value));

    [Fact]
    public void ABackToBackStreakSleepsAfterTheThreshold()
    {
        var streak = new GuestPolling.Streak();
        var now = 1000L;
        for (var call = 1; call <= 3; call++)
        {
            Assert.False(streak.Next(now, gapTicks: 50, backoffAfter: 3));
            streak.Done(now += 5);
            now += 10;
        }

        Assert.True(streak.Next(now, gapTicks: 50, backoffAfter: 3));
        Assert.Equal(4, streak.Length);
    }

    [Fact]
    public void WorkBetweenPollsStartsANewStreak()
    {
        var streak = new GuestPolling.Streak();
        Assert.False(streak.Next(100, gapTicks: 50, backoffAfter: 1));
        streak.Done(110);
        Assert.True(streak.Next(120, gapTicks: 50, backoffAfter: 1));
        streak.Done(130);
        Assert.False(streak.Next(180, gapTicks: 50, backoffAfter: 1));
        Assert.Equal(1, streak.Length);
    }

    [Fact]
    public void ThresholdZeroSleepsOnEveryPollAndOffNever()
    {
        var streak = new GuestPolling.Streak();
        Assert.True(streak.Next(100, gapTicks: 50, backoffAfter: 0));
        streak.Done(101);
        Assert.False(streak.Next(102, gapTicks: 50, backoffAfter: -1));
    }
}
