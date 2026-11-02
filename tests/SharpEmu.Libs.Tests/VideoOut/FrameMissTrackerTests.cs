// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRAME_MISS_PROBE's arithmetic on a 1 MHz clock: the instance under test has no static state.
public sealed class FrameMissTrackerTests
{
    private const long Frequency = 1_000_000;
    private const long Slot = 33_333;

    private static FrameMissTracker Create(List<string> lines, double startSeconds = 0) =>
        new(Frequency, 0, startSeconds, 1000, ["Wait", "Compiles"], [false, true], lines.Add);

    private static FrameMissTracker.FlipTimes At(long presented) => new()
    {
        Reserved = presented - 20_000,
        Captured = presented - 10_000,
        Presented = presented,
    };

    [Fact]
    public void GridSlotCountsTheSlotsWithoutAPresentation()
    {
        Assert.Equal((3L, 0), FrameMissTracker.GridSlot(1_000 + 3 * Slot + 5, 1_000, Slot, 2));
        Assert.Equal((5L, 2), FrameMissTracker.GridSlot(1_000 + 5 * Slot, 1_000, Slot, 2));
        Assert.Equal((5L, 0), FrameMissTracker.GridSlot(1_000 + 5 * Slot, 1_000, Slot, -1));
    }

    [Fact]
    public void BinsAreHalfMillisecondsAndLateEdgesAreOrdered()
    {
        Assert.Equal(66, FrameMissTracker.IntervalBin(33.4));
        Assert.Equal(67, FrameMissTracker.IntervalBin(33.6));
        Assert.Equal(159, FrameMissTracker.IntervalBin(500));
        Assert.Equal(0, FrameMissTracker.LateBin(0.2));
        Assert.Equal(1, FrameMissTracker.LateBin(0.7));
        Assert.Equal(11, FrameMissTracker.LateBin(150));
    }

    [Fact]
    public void ALateFlipInsideItsSlotIsNotAMiss()
    {
        var lines = new List<string>();
        var tracker = Create(lines);
        tracker.Presented(At(Slot), 0, Slot, [0, 0]);
        // 1.5 ms past its boundary: still within the slot it was due in.
        tracker.Presented(At(2 * Slot + 1_500), 0, Slot, [0, 0]);
        tracker.Presented(At(3 * Slot + 100), 0, Slot, [0, 0]);
        Assert.Equal(0, tracker.Misses);
        Assert.Equal(2, tracker.Presentations);
        Assert.Equal(1, tracker.IntervalHistogram[FrameMissTracker.IntervalBin(34.8)]);
        Assert.DoesNotContain(lines, line => line.StartsWith("[FRAME_MISS]", StringComparison.Ordinal));
    }

    [Fact]
    public void ASkippedSlotIsAMissWithItsLatenessAndCounters()
    {
        var lines = new List<string>();
        var tracker = Create(lines);
        tracker.Presented(At(Slot + 100), 0, Slot, [0, 0]);
        tracker.Presented(At(2 * Slot + 100), 0, Slot, [1_000, 0]);
        // Due in slot 3 (ending at 4 * Slot), shown 4 ms into slot 4: one skipped slot, 4 ms late, 12 ms of wait.
        tracker.Presented(At(4 * Slot + 4_000), 0, Slot, [13_000, 2]);
        Assert.Equal(1, tracker.Misses);
        Assert.Equal(1, tracker.SkippedSlots);
        Assert.Equal(1, tracker.LateHistogram[FrameMissTracker.LateBin(4.0)]);
        Assert.Equal(12.0, tracker.MissSum[0], 3);
        Assert.Equal(1.0, tracker.MissPreviousSum[0], 3);
        Assert.Equal(2.0, tracker.MissSum[1], 3);
        var miss = Assert.Single(lines, line => line.StartsWith("[FRAME_MISS]", StringComparison.Ordinal));
        Assert.Contains("skipped=1 late_ms=4.00", miss);
        Assert.Contains("d0: Wait=12.00 Compiles=2", miss);
        Assert.Contains("captured=-6.0", miss);
    }

    [Fact]
    public void SlackIsTheTimeLeftAfterTheCaptureBeforeTheDeadline()
    {
        var lines = new List<string>();
        var tracker = Create(lines);
        tracker.Presented(At(Slot), 0, Slot, [0, 0]);
        // Due in slot 2 (ending at 3 * Slot), captured 5.2 ms before that end.
        tracker.Presented(new FrameMissTracker.FlipTimes { Captured = 3 * Slot - 5_200, Presented = 3 * Slot - 5_000 }, 0, Slot, [0, 0]);
        Assert.Equal(1, tracker.SlackHistogram[5]);
        var report = tracker.Report(3 * Slot).ToList();
        Assert.Equal("[FRAME_LEAD] normal presentations, ms before the end of their slot, p10/p50/p90: captured=5/5/5 presented=5/5/5", report[0]);
        Assert.Contains("slack_ms_1=[5:1]", report[1]);
    }

    [Fact]
    public void PresentationsBeforeTheStartAreLeftOut()
    {
        var lines = new List<string>();
        var tracker = Create(lines, startSeconds: 1);
        tracker.Presented(At(Slot), 0, Slot, [0, 0]);
        tracker.Presented(At(29 * Slot), 0, Slot, [0, 0]);
        Assert.Equal(0, tracker.Presentations);
        Assert.Empty(lines);
        tracker.Presented(At(30 * Slot + 10), 0, Slot, [0, 0]);
        tracker.Presented(At(32 * Slot + 100), 0, Slot, [0, 0]);
        Assert.Equal(2, tracker.Presentations);
        Assert.Equal(1, tracker.Misses);
    }

    [Fact]
    public void TheAttributionRanksTimeCountersByTheirExcessOnMisses()
    {
        var lines = new List<string>();
        var tracker = Create(lines);
        tracker.Presented(At(Slot), 0, Slot, [0, 0]);
        tracker.Presented(At(2 * Slot), 0, Slot, [2_000, 0]);
        tracker.Presented(At(4 * Slot), 0, Slot, [30_000, 1]);
        var report = tracker.Report(4 * Slot).ToList();
        Assert.StartsWith("[FRAME_HIST]", report[1]);
        Assert.Contains("misses=1 skipped_slots=1", report[1]);
        Assert.Contains("[FRAME_ATTR] miss_frames=1 normal_frames=1 per_interval miss/before/normal: Wait=28.00/2.00/2.00 Compiles=1.00/0.00/0.00", report[2]);
    }
}
