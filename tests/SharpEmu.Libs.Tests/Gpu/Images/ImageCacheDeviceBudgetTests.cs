// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// [local] SHARPEMU_IMAGE_DEVICE_BUDGET: the image cache's collection thresholds sized to the device. DarthPotato 5a4bf244.
public sealed class ImageCacheDeviceBudgetTests
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [Fact]
    public void BudgetLeavesAnEighthOfWhatIsAvailable()
    {
        Assert.Equal(7 * GiB, GuestImageCache.ComputeImageCacheBudget(8 * GiB, hasMemoryBudget: true, marginDivisor: 8));
        Assert.Equal(14 * GiB, GuestImageCache.ComputeImageCacheBudget(16 * GiB, hasMemoryBudget: true, marginDivisor: 8));
        Assert.Equal(6 * GiB, GuestImageCache.ComputeImageCacheBudget(8 * GiB, hasMemoryBudget: true, marginDivisor: 4));
    }

    [Fact]
    public void MarginHasAFloor()
    {
        Assert.Equal(512 * MiB, GuestImageCache.ComputeImageCacheBudget(GiB, hasMemoryBudget: true, marginDivisor: 8));
        Assert.Equal(200 * MiB, GuestImageCache.ComputeImageCacheBudget(400 * MiB, hasMemoryBudget: true, marginDivisor: 8));
        Assert.Equal(MiB, GuestImageCache.ComputeImageCacheBudget(0, hasMemoryBudget: true, marginDivisor: 8));
    }

    [Fact]
    public void WithoutAMemoryBudgetTheHeapSizeIsCapped()
    {
        Assert.Equal(4 * GiB, GuestImageCache.ComputeImageCacheBudget(16 * GiB, hasMemoryBudget: false, marginDivisor: 8));
        Assert.Equal(7 * GiB / 2, GuestImageCache.ComputeImageCacheBudget(4 * GiB, hasMemoryBudget: false, marginDivisor: 8));
    }

    [Fact]
    public void ThresholdsAreShareOfTheBudget()
    {
        var thresholds = GuestImageCache.ComputeCollectionThresholds(14 * GiB, (60, 85, 92));
        Assert.Equal(14 * GiB / 100 * 60, thresholds.Start);
        Assert.Equal(14 * GiB / 100 * 85, thresholds.Pressure);
        Assert.Equal(14 * GiB / 100 * 92, thresholds.Critical);
        Assert.Equal((MiB, MiB, MiB), GuestImageCache.ComputeCollectionThresholds(MiB, (60, 85, 92)));
    }

    [Fact]
    public void ThresholdsParseOrFallBack()
    {
        Assert.Equal((60UL, 85UL, 92UL), GuestImageCache.ParseThresholds(null));
        Assert.Equal((50UL, 60UL, 70UL), GuestImageCache.ParseThresholds("50,60,70"));
        Assert.Equal((60UL, 85UL, 92UL), GuestImageCache.ParseThresholds("70,60,80"));
        Assert.Equal((60UL, 85UL, 92UL), GuestImageCache.ParseThresholds("50,60,101"));
        Assert.Equal((60UL, 85UL, 92UL), GuestImageCache.ParseThresholds("50,60"));
    }

    [Fact]
    public void SwitchIsOffByDefault()
    {
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_DEVICE_BUDGET") == "1", GuestImageCache.DeviceBudget);
    }
}
