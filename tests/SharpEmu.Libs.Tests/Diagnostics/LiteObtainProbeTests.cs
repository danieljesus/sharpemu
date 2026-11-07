// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiteObtainProbeCollection
{
    public const string Name = "LiteObtainProbe";
}

// [local] SHARPEMU_LITE_OBTAIN_PROBE: the comparison of the light answer with the real one, the item ownership and
// the report.
[Collection(LiteObtainProbeCollection.Name)]
public sealed class LiteObtainProbeTests : IDisposable
{
    public LiteObtainProbeTests() => LiteObtainProbe.ResetForTests();

    public void Dispose() => LiteObtainProbe.ResetForTests();

    [Theory]
    [InlineData((int)LiteObtainProbe.Cause.Fast, true, (int)LiteObtainProbe.Work.None, (int)LiteObtainProbe.Outcome.Agree)]
    [InlineData((int)LiteObtainProbe.Cause.Fast, false, (int)LiteObtainProbe.Work.None, (int)LiteObtainProbe.Outcome.FastWrong)]
    [InlineData((int)LiteObtainProbe.Cause.Fast, true, (int)LiteObtainProbe.Work.Upload, (int)LiteObtainProbe.Outcome.FastWrong)]
    [InlineData((int)LiteObtainProbe.Cause.Fast, true, (int)LiteObtainProbe.Work.Stream, (int)LiteObtainProbe.Outcome.FastWrong)]
    [InlineData((int)LiteObtainProbe.Cause.Fast, true, (int)LiteObtainProbe.Work.Publication, (int)LiteObtainProbe.Outcome.FastWrong)]
    [InlineData((int)LiteObtainProbe.Cause.Cpu, false, (int)LiteObtainProbe.Work.Upload, (int)LiteObtainProbe.Outcome.Slow)]
    [InlineData((int)LiteObtainProbe.Cause.Unmapped, false, (int)LiteObtainProbe.Work.Registry, (int)LiteObtainProbe.Outcome.Slow)]
    [InlineData((int)LiteObtainProbe.Cause.Pub, true, (int)LiteObtainProbe.Work.None, (int)LiteObtainProbe.Outcome.Slow)]
    public void ClassifiesTheLightAnswerAgainstTheRealOne(int cause, bool same, int work, int expected) => // the enums are internal
        Assert.Equal((LiteObtainProbe.Outcome)expected, LiteObtainProbe.Classify((LiteObtainProbe.Cause)cause, same, (LiteObtainProbe.Work)work));

    [Fact]
    public void WindowSplitsFastSlowAndWrong()
    {
        var window = new LiteObtainProbe.Window();
        window.Items[0] = 2;
        window.AddObtain(0, LiteObtainProbe.Cause.Fast, true, LiteObtainProbe.Work.None, 10, 100);
        window.AddObtain(0, LiteObtainProbe.Cause.Fast, true, LiteObtainProbe.Work.None, 10, 100);
        window.AddObtain(0, LiteObtainProbe.Cause.Fast, false, LiteObtainProbe.Work.None, 10, 100);
        window.AddObtain(0, LiteObtainProbe.Cause.Hot, false, LiteObtainProbe.Work.Stream, 10, 500);
        window.AddObtain(0, LiteObtainProbe.Cause.Cpu, true, LiteObtainProbe.Work.None, 10, 100);
        window.AddFind(0, LiteObtainProbe.Cause.Fast, true, false, 5);
        window.AddFind(0, LiteObtainProbe.Cause.Fast, true, true, 5); // a lookup that created a buffer
        window.AddSlot(0, rt: false, raw: true);
        window.AddSlot(0, rt: false, raw: false);
        window.AddSlot(0, rt: true, raw: true);
        Assert.Equal(2, window.Agree[0]);
        Assert.Equal(1, window.FastWrong[0]);
        Assert.Equal(1, window.SlowClean[0]);
        Assert.Equal(1, window.FindWrong[0]);

        var lines = LiteObtainProbe.Report(window, 5, 0, 3);
        var line = Assert.Single(lines);
        Assert.StartsWith("[PERF][LITE_OBTAIN] window_s=5.0 frames=0 kind=draw items_per_frame=2 obtains_per_item=2.50 fast=60.0 fast_wrong=1 fast_wrong_total=3 ", line);
        Assert.Contains(" slow=cpu:20.0,pub:0.0,img:0.0,hot:20.0,cross:0.0,unmapped:0.0 slow_clean=20.0 ", line);
        Assert.Contains(" finds_per_item=1.00 find_fast=100.0 find_wrong=1 ", line);
        Assert.Contains(" slots_per_item=1.50 eligible_raw=50.0 rt_excluded=33.3 ", line);
    }

    [Fact]
    public void ItemsOwnTheNotesOfTheirThreadOnly()
    {
        Assert.False(LiteObtainProbe.Owns);
        LiteObtainProbe.Begin(dispatch: true);
        Assert.True(LiteObtainProbe.Owns);
        LiteObtainProbe.Begin(dispatch: false); // nested: still the dispatch
        LiteObtainProbe.End();
        Assert.True(LiteObtainProbe.Owns);
        var other = true;
        var thread = new Thread(() => other = LiteObtainProbe.Owns); // its own thread: a task can run inline on this one
        thread.Start();
        thread.Join();
        Assert.False(other);
        LiteObtainProbe.End();
        Assert.False(LiteObtainProbe.Owns);
        Assert.Equal(1, LiteObtainProbe.WindowForTests.Items[1]);
        Assert.Equal(0, LiteObtainProbe.WindowForTests.Items[0]);
    }

    [Fact]
    public void RtProgramsAreTheBvhPasses()
    {
        Assert.True(LiteObtainProbe.IsRtProgram(0x8395E43F382309DF));
        Assert.True(LiteObtainProbe.IsRtProgram(0x5981037B07E391D5));
        Assert.False(LiteObtainProbe.IsRtProgram(0xD44E000000000000));
    }
}
