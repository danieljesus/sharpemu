// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

public sealed class RelayProbeTests
{
    [Theory]
    [InlineData(10UL, 10UL, 5UL, 0)]
    [InlineData(11UL, 10UL, 5UL, 0)]
    [InlineData(9UL, 10UL, 5UL, 1)]
    [InlineData(5UL, 10UL, 5UL, 2)]
    [InlineData(0UL, 10UL, 5UL, 2)]
    public void TheWriterOfACopyIsTheBatchBeingRecordedInFlightOrCompleted(ulong waitTick, ulong current, ulong completed, int expected) =>
        Assert.Equal((RelayProbe.Writer)expected, RelayProbe.ClassifyWriter(waitTick, current, completed));

    [Fact]
    public void TheReportIsPerFrameAndSplitsTheCommandsByHop()
    {
        var counters = new RelayProbe.Counters
        {
            Drains = 40, Drain1 = 30, Drain2 = 8, Drain3Plus = 2, Commands = 56, CommandTicks = Stopwatch.Frequency / 100,
            Begins = 20, BeginTicks = Stopwatch.Frequency / 1000, Completes = 20, CompleteTicks = Stopwatch.Frequency / 500,
            Pieces = 30, Bytes = 20 * 4096, GuestSubmits = 18, GuestFallbacks = 2, Applied = 19, NotClean = 1,
        };
        counters.SourceBegins[(int)GuestMemoryProfile.ReadbackSource.StoreDownload] = 16;
        counters.SourceBegins[(int)GuestMemoryProfile.ReadbackSource.CpuWriteInvalidation] = 4;
        counters.Outcomes[(int)RelayProbe.Outcome.OffQueue] = 20;
        counters.Writers[(int)RelayProbe.Writer.CurrentBatch] = 15;
        counters.Writers[(int)RelayProbe.Writer.InFlight] = 5;

        var line = RelayProbe.Format(counters, 5.0, 10);

        Assert.StartsWith("[PERF][RELAY] window_s=5.0 frames=10 drains=4.0 drain_depth=1:3.0,2:0.8,3+:0.2", line);
        Assert.Contains("commands=5.6/1.000ms begin=2.0/0.100ms/50.0us", line);
        Assert.Contains("complete=2.0/0.200ms/100.0us rest=1.6/0.700ms", line);
        Assert.Contains("sources=StoreDownload:1.6/0.000ms,CpuWriteInvalidation:0.4/0.000ms", line);
        Assert.Contains("OffQueue:2.0", line);
        Assert.Contains("writer=CurrentBatch:1.5,InFlight:0.5,Completed:0.0 pieces_per_copy=1.50 kb_per_copy=4.0", line);
        Assert.Contains("guest:1.8/", line);
        Assert.Contains("handed_back:0.20", line);
        Assert.Contains("completes=applied:1.9,not_clean:0.10", line);
    }
}
