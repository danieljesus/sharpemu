// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] Muting of read-ahead windows whose downloads keep going stale.
public sealed class EagerWindowMuteTests
{
    [Fact]
    public void ThirdWastedDownloadInARowMutesTheWindow()
    {
        var mute = new EagerWindowMute(strikeLimit: 3, muteBatches: 100);
        Assert.False(mute.Note(0x80000, wasted: true, batch: 10));
        Assert.False(mute.Note(0x80000, wasted: true, batch: 11));
        Assert.False(mute.IsMuted(0x80000, 12));
        Assert.True(mute.Note(0x80000, wasted: true, batch: 12));
        Assert.True(mute.IsMuted(0x80000, 12));
        Assert.True(mute.IsMuted(0x80000, 111));
        Assert.False(mute.IsMuted(0x80000, 112));
        Assert.False(mute.IsMuted(0x100000, 12));
    }

    [Fact]
    public void AUsedDownloadClearsTheStrikes()
    {
        var mute = new EagerWindowMute(strikeLimit: 3, muteBatches: 100);
        mute.Note(0x80000, wasted: true, batch: 1);
        mute.Note(0x80000, wasted: true, batch: 2);
        Assert.False(mute.Note(0x80000, wasted: false, batch: 3));
        Assert.False(mute.Note(0x80000, wasted: true, batch: 4));
        Assert.False(mute.Note(0x80000, wasted: true, batch: 5));
        Assert.False(mute.IsMuted(0x80000, 6));
    }

    [Fact]
    public void PruneForgetsOnlyExpiredMutes()
    {
        var mute = new EagerWindowMute(strikeLimit: 1, muteBatches: 10);
        mute.Note(0x80000, wasted: true, batch: 0);
        var striking = new EagerWindowMute(strikeLimit: 2, muteBatches: 10);
        striking.Note(0x80000, wasted: true, batch: 0);
        mute.Prune(5);
        Assert.Equal(1, mute.Count);
        mute.Prune(10);
        Assert.Equal(0, mute.Count);
        striking.Prune(50);
        Assert.Equal(1, striking.Count);
    }
}
