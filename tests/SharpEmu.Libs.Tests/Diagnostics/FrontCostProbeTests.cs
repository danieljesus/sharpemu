// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

// [local] SHARPEMU_FRONTCOST_PROBE: the steps of a packet add up to it, sampling, the bound thread, packets that end
// early (an indirect buffer) or never return, and the packet kinds.
public sealed class FrontCostProbeTests
{
    private static readonly long Microsecond = Math.Max(Stopwatch.Frequency / 1_000_000, 1);

    private sealed class Clock
    {
        public long Microseconds;

        public long Now() => Microseconds * Microsecond;
    }

    private static FrontCostProbe.Ledger Ledger(Clock clock, int sampleEvery = 1)
    {
        var ledger = new FrontCostProbe.Ledger(sampleEvery, long.MaxValue / 2, allocPerStep: false, clock.Now);
        ledger.BindThread(Environment.CurrentManagedThreadId);
        return ledger;
    }

    private static Dictionary<string, string> Fields(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);

    private static string KindLine(List<string> lines, string kind) =>
        Assert.Single(lines, line => line.Contains($" kind={kind} ", StringComparison.Ordinal));

    private static Dictionary<string, double[]> Steps(string line) =>
        Fields(line)["steps"].Split(',').Select(step => step.Split('='))
            .ToDictionary(pair => pair[0], pair => pair[1].Split('/').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray());

    private static double Value(string line, string field) => double.Parse(Fields(line)[field], CultureInfo.InvariantCulture);

    private static long Expected(double microseconds) => (long)Math.Round(microseconds * Microsecond * 1_000_000.0 / Stopwatch.Frequency);

    [Fact]
    public void StepsAddUpToThePacket()
    {
        var clock = new Clock();
        var ledger = Ledger(clock);
        ledger.Begin();
        clock.Microseconds = 2;
        ledger.Classify(FrontCostProbe.Kind.DrawIndexed); // decode 2
        clock.Microseconds = 5;
        ledger.LapCore(FrontCostProbe.Step.Snapshot); // 3
        clock.Microseconds = 9;
        ledger.LapCore(FrontCostProbe.Step.StageMaterialize); // 4
        clock.Microseconds = 12;
        ledger.LapCore(FrontCostProbe.Step.StageMaterialize); // 3 more, a second pass
        ledger.CountCore(FrontCostProbe.Counter.Stages, 2);
        clock.Microseconds = 13;
        ledger.End(); // handler 1

        var lines = ledger.TakeReport(clock.Now());
        var line = KindLine(lines, "draw_indexed");
        Assert.Equal(13, Expected(Value(line, "us_per_item")));
        var steps = Steps(line);
        Assert.Equal(2, Expected(steps["decode"][0]));
        Assert.Equal(3, Expected(steps["snapshot"][0]));
        Assert.Equal(3.5, steps["stage_materialize"][0] * 1_000_000.0 / Stopwatch.Frequency * Microsecond, 3);
        Assert.Equal(2.0, steps["stage_materialize"][2], 3); // two passes per packet
        Assert.Equal(1, Expected(steps["handler"][0]));
        Assert.True(Math.Abs(Value(line, "ms_per_frame") - steps.Values.Sum(step => step[1])) <= 0.05);
        Assert.Contains("stages=2.00", Fields(line)["counts"], StringComparison.Ordinal);
        Assert.StartsWith("[PERF][FRONTCOST] window_s=", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SamplingTimesOneInNButCountsEvery()
    {
        var clock = new Clock();
        var ledger = Ledger(clock, sampleEvery: 2);
        for (var packet = 0; packet < 6; packet++)
        {
            ledger.Begin();
            clock.Microseconds++;
            ledger.Classify(FrontCostProbe.Kind.ShaderRegisters);
            ledger.LapCore(FrontCostProbe.Step.RegWrite);
            ledger.End();
        }

        var line = KindLine(ledger.TakeReport(clock.Now()), "shader_registers");
        Assert.Equal("3", Fields(line)["timed"]);
        Assert.Equal("6", Fields(line)["items_per_frame"]); // no frame noted: per window
    }

    [Fact]
    public void OtherThreadsAreNotMeasured()
    {
        var clock = new Clock();
        var ledger = Ledger(clock);
        ledger.Begin();
        clock.Microseconds = 1;
        ledger.Classify(FrontCostProbe.Kind.Dispatch);
        var other = new Thread(() =>
        {
            clock.Microseconds = 50;
            ledger.LapCore(FrontCostProbe.Step.Enqueue);
            ledger.CountCore(FrontCostProbe.Counter.Enqueues, 1);
            ledger.Begin();
            ledger.End();
        });
        other.Start();
        other.Join();
        clock.Microseconds = 51;
        ledger.End();

        var line = KindLine(ledger.TakeReport(clock.Now()), "dispatch");
        Assert.DoesNotContain("enqueue", Fields(line)["steps"], StringComparison.Ordinal);
        Assert.DoesNotContain("enqueues", Fields(line).GetValueOrDefault("counts", string.Empty), StringComparison.Ordinal);
        Assert.Equal(51, Expected(Value(line, "us_per_item")));
    }

    [Fact]
    public void AnIndirectBufferEndsAtItsDecodeAndItsPacketsAreItemsOfTheirOwn()
    {
        var clock = new Clock();
        var ledger = Ledger(clock);
        ledger.Begin();
        clock.Microseconds = 1;
        ledger.Classify(FrontCostProbe.Kind.IndirectBuffer);
        ledger.End();
        for (var packet = 0; packet < 2; packet++)
        {
            clock.Microseconds += 10; // the nested buffer's push, outside any packet
            ledger.Begin();
            clock.Microseconds += 4;
            ledger.Classify(FrontCostProbe.Kind.DrawAuto);
            clock.Microseconds += 1;
            ledger.End();
        }

        var lines = ledger.TakeReport(clock.Now());
        Assert.Equal(1, Expected(Value(KindLine(lines, "indirect_buffer"), "us_per_item")));
        var draws = KindLine(lines, "draw_auto");
        Assert.Equal("2", Fields(draws)["timed"]);
        Assert.Equal(5, Expected(Value(draws, "us_per_item")));
    }

    [Fact]
    public void APacketThatNeverReturnedEndsAtTheNextOne()
    {
        var clock = new Clock();
        var ledger = Ledger(clock);
        ledger.Begin();
        clock.Microseconds = 1;
        ledger.Classify(FrontCostProbe.Kind.Wait);
        clock.Microseconds = 7; // the handler threw; the next packet begins
        ledger.Begin();
        clock.Microseconds = 8;
        ledger.Classify(FrontCostProbe.Kind.Label);
        ledger.End();

        var lines = ledger.TakeReport(clock.Now());
        Assert.Equal(7, Expected(Value(KindLine(lines, "wait"), "us_per_item")));
        Assert.Equal(1, Expected(Value(KindLine(lines, "label"), "us_per_item")));
    }

    [Fact]
    public void ABeginWithoutAPacketCostsNothing()
    {
        var clock = new Clock();
        var ledger = Ledger(clock);
        ledger.Begin(); // a filler dword: no header, no handler
        clock.Microseconds = 5;
        ledger.End();
        ledger.Begin();
        clock.Microseconds = 6;
        ledger.Classify(FrontCostProbe.Kind.Other);
        ledger.End();

        var lines = ledger.TakeReport(clock.Now());
        var line = Assert.Single(lines, entry => entry.Contains(" kind=", StringComparison.Ordinal));
        Assert.Equal(1, Expected(Value(line, "us_per_item")));
    }

    [Fact]
    public void TimersAreReportedPerCallAndPerItem()
    {
        var clock = new Clock();
        var ledger = Ledger(clock);
        clock.Microseconds = 1;
        ledger.Begin();
        ledger.Classify(FrontCostProbe.Kind.DrawIndexed);
        for (var read = 0; read < 3; read++)
        {
            var started = ledger.TimerStartCore();
            Assert.NotEqual(0, started);
            clock.Microseconds += 2;
            ledger.TimerEndCore(FrontCostProbe.Timer.ReadCheck, started);
        }

        ledger.End();
        var line = KindLine(ledger.TakeReport(clock.Now()), "draw_indexed");
        var timer = Fields(line)["timers"].Split('=')[1].Split('/');
        Assert.Equal("read_check", Fields(line)["timers"].Split('=')[0]);
        Assert.Equal(2000.0, double.Parse(timer[0], CultureInfo.InvariantCulture) * Microsecond * 1_000_000.0 / Stopwatch.Frequency, 0);
        Assert.Equal("3.00", timer[1]);
    }

    [Fact]
    public void PacketKinds()
    {
        Assert.Equal(FrontCostProbe.Kind.DrawIndexed, FrontCostProbe.KindOf(PacketOpcode.DrawIndex2, PacketHeader.Make(6, PacketOpcode.DrawIndex2)));
        Assert.Equal(FrontCostProbe.Kind.DrawAuto, FrontCostProbe.KindOf(PacketOpcode.Nop, PacketHeader.Make(3, PacketOpcode.Nop, PacketCustomCode.DrawIndexAuto)));
        Assert.Equal(FrontCostProbe.Kind.ContextTable, FrontCostProbe.KindOf(PacketOpcode.SetContextRegisterIndirect, 0));
        Assert.Equal(FrontCostProbe.Kind.Label, FrontCostProbe.KindOf(PacketOpcode.Nop, PacketHeader.Make(8, PacketOpcode.Nop, PacketCustomCode.ReleaseMemory)));
        Assert.Equal(FrontCostProbe.Kind.Marker, FrontCostProbe.KindOf(PacketOpcode.Nop, PacketHeader.Make(2, PacketOpcode.Nop, PacketCustomCode.Zero)));
        Assert.Equal(FrontCostProbe.Kind.IndirectBuffer, FrontCostProbe.KindOf(PacketOpcode.IndirectBuffer, 0));
        Assert.Equal(FrontCostProbe.Kind.Dispatch, FrontCostProbe.KindOf(PacketOpcode.DispatchIndirect, 0));
    }
}
