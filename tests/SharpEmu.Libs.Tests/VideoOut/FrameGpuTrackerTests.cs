// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRAME_MISS_GPU's arithmetic on ns clocks: the instance under test has no static state. The GPU clock
// runs Offset ns ahead of the CPU clock.
public sealed class FrameGpuTrackerTests
{
    private const long Ms = 1_000_000;
    private const long Base = 100 * Ms;
    private const long Offset = 7_000 * Ms;

    // Previous flip tick 10 (GPU 0-5 ms), tick 11 sealed at 8, submitted at 9, started at 10 and ran A for 10 ms; flip
    // tick 12 sealed at 15, submitted at 16, ran B for 5 ms right behind it.
    private static (FrameGpuTracker Tracker, int A, int B, List<string> Lines) Frame(bool deliverLast = true)
    {
        var lines = new List<string>();
        var tracker = new FrameGpuTracker(lines.Add);
        var a = tracker.Program("A");
        var b = tracker.Program("B");
        tracker.Sealed(10, Base - Ms);
        tracker.Submitted(10, Base);
        tracker.Buffer(10, Base + Offset, [(b, 5)]);
        tracker.Sealed(11, Base + 8 * Ms);
        tracker.Submitted(11, Base + 9 * Ms);
        tracker.Buffer(11, Base + 10 * Ms + Offset, [(a, 10)]);
        tracker.Sealed(12, Base + 15 * Ms);
        tracker.Submitted(12, Base + 16 * Ms);
        if (deliverLast) tracker.Buffer(12, Base + 20 * Ms + Offset, [(b, 5)]);
        return (tracker, a, b, lines);
    }

    [Fact]
    public void TheWindowSplitsIntoBusyAndTheIdleCauses()
    {
        var (tracker, _, _, lines) = Frame();
        tracker.RenderSpan(FrameGpuTracker.SpanWait, Base + 5 * Ms, Base + 7 * Ms);
        tracker.Frame(10, 0);
        tracker.Frame(12, 1);
        tracker.Drain();

        Assert.Equal(Offset, tracker.Offset());
        Assert.Equal(1, tracker.MissFrames);
        Assert.Equal(15, tracker.MeanBusy(true), 6);
        Assert.Equal(5, tracker.MeanIdle(true), 6);
        Assert.Equal(3, tracker.MeanIdleSealLate(true), 6);
        Assert.Equal(2, tracker.MeanIdleSealRender(true, FrameGpuTracker.SpanWait), 6);
        Assert.Equal(1, tracker.MeanIdleSealRender(true, FrameGpuTracker.SpanKinds), 6);
        Assert.Equal(1, tracker.MeanIdleSubmitLate(true), 6);
        Assert.Equal(1, tracker.MeanIdleStartLate(true), 6);
        Assert.Equal(10, tracker.ProgramMs(true, "A"), 6);
        Assert.Equal(5, tracker.ProgramMs(true, "B"), 6);
        Assert.Equal(0, tracker.Saturated(true));
        Assert.Contains(lines, line => line.StartsWith("[FRAME_GPU_MISS]", StringComparison.Ordinal));
        Assert.Contains(tracker.Report(), line => line.StartsWith("[FRAME_GPU] class=miss", StringComparison.Ordinal));
    }

    [Fact]
    public void ARenderWaitGetsTheProgramsTheGpuRanMeanwhile()
    {
        var (tracker, _, _, _) = Frame();
        tracker.Wait(FrameGpuTracker.WaitRender, 11, 10, Base + 12 * Ms, Base + 18 * Ms);
        tracker.Wait(FrameGpuTracker.WaitOther, 11, 10, Base + 12 * Ms, Base + 18 * Ms);
        tracker.Frame(10, 0);
        tracker.Frame(12, 0);
        tracker.Drain();

        Assert.Equal(1, tracker.NormalFrames);
        Assert.Equal(6, tracker.MeanRenderWait(false), 6);
        Assert.Equal(6, tracker.MeanRenderWaitGpuBusy(false), 6);
        Assert.Equal(6, tracker.ProgramMs(false, "A", duringWait: true), 6);
        Assert.Equal(0, tracker.ProgramMs(false, "B", duringWait: true), 6);
    }

    [Fact]
    public void AFrameWaitsForItsLateTicks()
    {
        var (tracker, _, b, _) = Frame(deliverLast: false);
        tracker.Frame(10, 0);
        tracker.Frame(12, 0);
        tracker.Drain();
        Assert.Equal(0, tracker.NormalFrames);

        tracker.Buffer(12, Base + 20 * Ms + Offset, [(b, 5)]);
        tracker.Drain();
        Assert.Equal(1, tracker.NormalFrames);
        Assert.Equal(15, tracker.MeanBusy(false), 6);
    }

    [Fact]
    public void AFrameWithANeverProfiledTickFinishesAfterTheGrace()
    {
        var (tracker, _, b, _) = Frame();
        tracker.Sealed(13, Base + 25 * Ms);
        tracker.Sealed(14, Base + 26 * Ms);
        tracker.Submitted(14, Base + 26 * Ms);
        tracker.Buffer(14, Base + 30 * Ms + Offset, [(b, 40)]);
        tracker.Frame(10, 0);
        tracker.Frame(12, 0);
        tracker.Frame(14, 0);
        tracker.Drain();
        Assert.Equal(1, tracker.NormalFrames);

        tracker.Sealed(14 + FrameGpuTracker.TickGrace, Base + 900 * Ms);
        tracker.Drain();
        Assert.Equal(2, tracker.NormalFrames);
        Assert.Equal(1, tracker.Saturated(false));
    }
}
