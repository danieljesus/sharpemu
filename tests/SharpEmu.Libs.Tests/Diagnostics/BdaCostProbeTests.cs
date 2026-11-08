// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using Xunit;
using Step = SharpEmu.Libs.Diagnostics.BdaCostProbe.Step;
using Counter = SharpEmu.Libs.Diagnostics.BdaCostProbe.Counter;

namespace SharpEmu.Libs.Tests.Diagnostics;

// [local] SHARPEMU_BDA_COST_PROBE: the tally's sums per program slot, the size buckets, the top programs and the report
// lines. A tick is a microsecond here (frequency 1,000,000).
public sealed class BdaCostProbeTests
{
    private const long Frequency = 1_000_000;

    private static List<string> Format(BdaCostTally tally, long frames = 10)
    {
        var lines = new List<string>();
        tally.Format(lines, 5.0, frames, Frequency);
        return lines;
    }

    [Fact]
    public void EmptyWindowPrintsNothing()
    {
        Assert.Empty(Format(new BdaCostTally()));
    }

    [Fact]
    public void HeaderSumsStepsAndRangesOverDispatches()
    {
        var tally = new BdaCostTally();
        for (var dispatch = 0; dispatch < 4; dispatch++)
        {
            var slot = tally.Begin(0xAAAA);
            tally.AddStep(slot, Step.BindHost, 10);
            tally.AddStep(slot, Step.BdaPrelude, 2);
            tally.Count(Counter.UsesBda);
            tally.Count(Counter.Gated);
            tally.AddStep(slot, Step.RangeCheck, 1);
            tally.AddStep(slot, Step.WObtain, 3);
            tally.AddRange(slot, written: true, 2UL << 20, 4);
        }

        var lines = Format(tally);
        var header = lines[0];
        Assert.StartsWith("[PERF][BDA_COST] window_s=5.0 frames=10 dispatches=4 dispatches_per_frame=0 ", header);
        Assert.Contains(" uses_bda=1.00 gated=1.00 prepared=0.00 ", header);
        Assert.Contains(" ranges_per=1.00 written_per=1.00 read_per=0.00 skipped_per=0.00 ", header);
        Assert.Contains(" mb_written_per=2.00 mb_read_per=0.00 ", header);
        // 16 µs per dispatch, 64 µs in 10 frames.
        Assert.Contains(" us_per_dispatch=16.00 ms_per_frame=0.01 programs=1 ", header);
        // <us per pass>/<ms per frame>/<passes per dispatch>; steps no dispatch reached are left out.
        Assert.Contains(" steps=bind_host=10.00/0.00/1.00,bda_prelude=2.00/0.00/1.00,range_check=1.00/0.00/1.00,w_obtain=3.00/0.00/1.00", header);
        Assert.DoesNotContain("bind_note", header);
    }

    [Fact]
    public void SizeLinesBucketRangesByKind()
    {
        Assert.Equal(0, BdaCostTally.BucketOf(64UL << 10));
        Assert.Equal(1, BdaCostTally.BucketOf((64UL << 10) + 1));
        Assert.Equal(2, BdaCostTally.BucketOf(16UL << 20));
        Assert.Equal(3, BdaCostTally.BucketOf(40UL << 20));

        var tally = new BdaCostTally();
        var slot = tally.Begin(1);
        tally.AddRange(slot, written: false, 4096, 2);
        tally.AddRange(slot, written: false, 4096, 4);
        tally.AddRange(slot, written: false, 40UL << 20, 30);
        tally.AddRange(slot, written: true, 1UL << 20, 5);

        var lines = Format(tally);
        Assert.Equal("[PERF][BDA_COST_SIZE] kind=written le64k=0/0.00/0.00,le1m=1/5.00/1.00,le16m=0/0.00/0.00,gt16m=0/0.00/0.00", lines[1]);
        Assert.Equal("[PERF][BDA_COST_SIZE] kind=read le64k=2/3.00/0.00,le1m=0/0.00/0.00,le16m=0/0.00/0.00,gt16m=1/30.00/40.00", lines[2]);
    }

    [Fact]
    public void ProgramLinesAreTheTopProgramsByTimeSorted()
    {
        var tally = new BdaCostTally();
        for (var program = 1; program <= BdaCostTally.TopPrograms + 3; program++)
        {
            var slot = tally.Begin((ulong)program);
            tally.AddStep(slot, Step.RSync, program * 100);
        }

        var lines = Format(tally).Where(line => line.StartsWith("[PERF][BDA_COST_PROG]", StringComparison.Ordinal)).ToList();
        Assert.Equal(BdaCostTally.TopPrograms, lines.Count);
        Assert.Equal($"[PERF][BDA_COST_PROG] hash=0x{BdaCostTally.TopPrograms + 3:X16} dispatches=1 ranges_per=0.00 mb_per=0.00 us_per=1500.00 ms_per_frame=0.15 r_sync=1500", lines[0]);
        Assert.StartsWith("[PERF][BDA_COST_PROG] hash=0x0000000000000004 ", lines[^1]);
    }

    [Fact]
    public void ProgramLineCarriesItsRangesAndStepTotals()
    {
        var tally = new BdaCostTally();
        for (var dispatch = 0; dispatch < 2; dispatch++)
        {
            var slot = tally.Begin(0x5981037B07E391D5);
            tally.AddStep(slot, Step.BdaGate, 1);
            tally.AddStep(slot, Step.RPublications, 7);
            tally.AddRange(slot, written: false, 1UL << 20, 7);
            tally.AddRange(slot, written: true, 1UL << 20, 0);
        }

        var line = Format(tally)[3];
        Assert.Equal("[PERF][BDA_COST_PROG] hash=0x5981037B07E391D5 dispatches=2 ranges_per=2.00 mb_per=2.00 us_per=8.00 ms_per_frame=0.00 bda_gate=2 r_publications=14", line);
    }

    [Fact]
    public void ProgramsPastTheCapShareTheOverflowSlotAndResetClearsTheWindow()
    {
        var tally = new BdaCostTally();
        for (var program = 0; program < BdaCostTally.MaxPrograms; program++)
        {
            tally.Begin((ulong)program + 1);
        }

        var first = tally.Begin(0xF00D);
        var second = tally.Begin(0xBEEF);
        Assert.Equal(first, second);
        tally.AddStep(first, Step.BindNote, 1_000_000);
        var lines = Format(tally);
        Assert.Contains($" programs={BdaCostTally.MaxPrograms}+ ", lines[0]);
        Assert.StartsWith("[PERF][BDA_COST_PROG] hash=other dispatches=2 ", lines[3]);

        tally.Reset();
        Assert.Empty(Format(tally));
        Assert.Equal(0, tally.Begin(0xF00D));
    }

    [Fact]
    public void ProbeIsOffByDefaultAndItsCallsAreNoOps()
    {
        Assert.False(BdaCostProbe.Enabled);
        BdaCostProbe.Begin(1);
        BdaCostProbe.Lap(Step.BindHost);
        BdaCostProbe.Count(Counter.Gated);
        BdaCostProbe.NoteRange(written: true, 4096);
        BdaCostProbe.End();
        BdaCostProbe.NoteFrame();
        BdaCostProbe.Report();
    }

    [Fact]
    public void StepNamesFollowTheEnum()
    {
        Assert.Equal("bind_host", BdaCostTally.NameOf(Step.BindHost));
        Assert.Equal("w_invalidate", BdaCostTally.NameOf(Step.WInvalidate));
        Assert.Equal("range_tail", BdaCostTally.NameOf(Step.RangeTail));
    }
}
