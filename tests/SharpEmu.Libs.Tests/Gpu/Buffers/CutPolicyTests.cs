// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_CUT_POLICY: which write bindings end the open batch right after their dispatch. Each test uses its
// own instance; the static switches are not touched.
public sealed class CutPolicyTests
{
    [Fact]
    public void NothingHot_NeverCuts()
    {
        var policy = new CutPolicy(wordMax: 0x100, windowMax: 0x10000);
        Assert.False(policy.NoteWrite(0x1000, 0x100, serial: 1, words: true, windows: true));
    }

    [Fact]
    public void HotWord_CutsOnASmallCoveringWrite()
    {
        var policy = new CutPolicy(wordMax: 0x100, windowMax: 0x10000);
        policy.NoteWord(0x2028);
        Assert.False(policy.NoteWrite(0x2100, 0x100, serial: 1, words: true, windows: false));
        Assert.True(policy.NoteWrite(0x2000, 0x100, serial: 1, words: true, windows: false));
        Assert.Equal(1, policy.WordCuts);
    }

    [Fact]
    public void HotWord_AWideWriteIsRejected()
    {
        var policy = new CutPolicy(wordMax: 0x100, windowMax: 0x10000);
        policy.NoteWord(0x2028);
        Assert.False(policy.NoteWrite(0, 0x2800000, serial: 1, words: true, windows: false));
        Assert.Equal(1, policy.WordRejects);
        Assert.Equal(1, policy.RejectSizes[^1]);
    }

    [Fact]
    public void HotWord_OnlyWhenWordsAreOn()
    {
        var policy = new CutPolicy(wordMax: 0x100, windowMax: 0x10000);
        policy.NoteWord(0x2028);
        Assert.False(policy.NoteWrite(0x2000, 0x100, serial: 1, words: false, windows: true));
    }

    [Fact]
    public void HotWords_AreKeptOnceAndTheOldestIsReplaced()
    {
        var policy = new CutPolicy(wordMax: 0x100, windowMax: 0x10000);
        policy.NoteWord(0x10);
        policy.NoteWord(0x10);
        Assert.Equal(1, policy.HotWordCount);
        for (var index = 1; index < CutPolicy.MaxHotWords; index++)
            policy.NoteWord(0x10 + (ulong)index * 0x1000);
        Assert.Equal(CutPolicy.MaxHotWords, policy.HotWordCount);
        policy.NoteWord(0x900000);
        Assert.False(policy.NoteWrite(0, 0x20, serial: 1, words: true, windows: false));
        Assert.True(policy.NoteWrite(0x900000, 4, serial: 1, words: true, windows: false));
    }

    [Fact]
    public void GuestWindow_CutsOnAnOverlappingWriteWithinItsLifetime()
    {
        var policy = new CutPolicy(wordMax: 0x100, windowMax: 0x10000);
        policy.NoteWindow(0x5_1234, serial: 10);
        Assert.False(policy.NoteWrite(0x6_0000, 0x1000, serial: 11, words: false, windows: true));
        Assert.True(policy.NoteWrite(0x4_F000, 0x2000, serial: 11, words: false, windows: true));
        Assert.True(policy.NoteWrite(0x5_FFF0, 0x10, serial: 10 + CutPolicy.WindowLifetime, words: false, windows: true));
        Assert.False(policy.NoteWrite(0x5_0000, 0x10, serial: 11 + CutPolicy.WindowLifetime, words: false, windows: true));
    }

    [Fact]
    public void GuestWindow_AWideWriteIsRejected()
    {
        var policy = new CutPolicy(wordMax: 0x100, windowMax: 0x10000);
        policy.NoteWindow(0x5_1234, serial: 1);
        Assert.False(policy.NoteWrite(0, 0x100000, serial: 1, words: false, windows: true));
        Assert.Equal(1, policy.WindowRejects);
    }

    [Fact]
    public void GuestWindows_AHitRefreshesAndTheStalestIsReplaced()
    {
        var policy = new CutPolicy(wordMax: 0x100, windowMax: 0x10000);
        for (var index = 0; index < CutPolicy.MaxHotWindows; index++)
            policy.NoteWindow((ulong)index * CutPolicy.WindowBytes, serial: 100 + index);
        policy.NoteWindow(0, serial: 500);
        Assert.Equal(CutPolicy.MaxHotWindows, policy.HotWindowCount);
        policy.NoteWindow(0x1000_0000, serial: 501);
        Assert.True(policy.NoteWrite(0, 4, serial: 502, words: false, windows: true));
        Assert.False(policy.NoteWrite(CutPolicy.WindowBytes, 4, serial: 502, words: false, windows: true));
        Assert.True(policy.NoteWrite(0x1000_0000, 4, serial: 502, words: false, windows: true));
    }
}
