// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;
using Counter = SharpEmu.Libs.Diagnostics.DrawCostProbe.Counter;
using Kind = SharpEmu.Libs.Diagnostics.DrawCostProbe.Kind;
using PixelClass = SharpEmu.Libs.Diagnostics.DrawCostDepth.PixelClass;
using Same = SharpEmu.Libs.Diagnostics.DrawCostDepth.Same;
using Step = SharpEmu.Libs.Diagnostics.DrawCostProbe.Step;

namespace SharpEmu.Libs.Tests.Diagnostics;

// [local] SHARPEMU_DRAWCOST_DEPTH: a depth-only draw is classified when its targets are known, with what it counted
// before; the kind lines keep the draw format; the pixel class, the shape hashes and the shadow of the previous draw.
// A tick is a microsecond here (frequency 1,000,000).
public sealed class DrawCostDepthTests
{
    private const long Frequency = 1_000_000;

    private static List<string> Format(DrawCostTally tally, long frames = 1)
    {
        var lines = new List<string>();
        tally.Format(lines, frames, Frequency, allocPerStep: false);
        return lines;
    }

    // A timed draw: snapshots 2, exec_prelude 1, exec_targets 1, the classification, exec_programs 6 (10 µs).
    private static void TimedDraw(DrawCostTally tally, bool? depthOnly, long start = 0)
    {
        tally.Begin(Kind.Draw, timed: true, start);
        tally.Lap(Step.Snapshots, start + 2);
        tally.Count(Counter.Obtain, 2);
        tally.Lap(Step.ExecPrelude, start + 3);
        tally.Lap(Step.ExecTargets, start + 4);
        if (depthOnly is { } classified)
        {
            Assert.True(tally.Classify(classified));
        }

        tally.Count(Counter.PsNone, 1);
        tally.Lap(Step.ExecPrograms, start + 10);
        tally.End(allocatedBytes: 100, ringRecords: 1);
    }

    [Fact]
    public void ADepthOnlyDrawCountsItsWholeItemAsDrawDepth_TheStepsBeforeItsClassificationToo()
    {
        var tally = new DrawCostTally(depthSplit: true);
        TimedDraw(tally, depthOnly: true);

        var lines = Format(tally);
        Assert.Equal(
            "[PERF][DRAWCOST] kind=draw_depth items_per_frame=1 timed=1 us_per_item=10.00 ms_per_frame=0.01 " +
            "steps=snapshots=2.00/0.00/1.00,exec_prelude=1.00/0.00/1.00,exec_targets=1.00/0.00/1.00,exec_programs=6.00/0.01/1.00 " +
            "counts=obtain=2.00,ring_records=1.00,ps_none=1.00,allocated_bytes=100.00",
            Assert.Single(lines));
    }

    [Fact]
    public void AColorDrawKeepsTheDrawLine_TheSameFormat()
    {
        var tally = new DrawCostTally(depthSplit: true);
        TimedDraw(tally, depthOnly: false);

        var line = Assert.Single(Format(tally));
        Assert.StartsWith("[PERF][DRAWCOST] kind=draw items_per_frame=1 timed=1 us_per_item=10.00 ", line);
        Assert.Contains(" steps=snapshots=2.00/0.00/1.00,exec_prelude=1.00/0.00/1.00,exec_targets=1.00/0.00/1.00,exec_programs=6.00/0.01/1.00 ", line);
    }

    [Fact]
    public void ADrawNeverClassifiedStaysADraw()
    {
        var tally = new DrawCostTally(depthSplit: true);
        TimedDraw(tally, depthOnly: null);

        var line = Assert.Single(Format(tally));
        Assert.StartsWith("[PERF][DRAWCOST] kind=draw ", line);
        Assert.Contains("snapshots=2.00/0.00/1.00", line);
        Assert.Contains("counts=obtain=2.00,ring_records=1.00,ps_none=1.00,", line);
    }

    [Fact]
    public void AnUntimedDepthOnlyDrawMovesItsItemOnly()
    {
        var tally = new DrawCostTally(depthSplit: true);
        TimedDraw(tally, depthOnly: false);
        tally.Begin(Kind.Draw, timed: false, 0);
        Assert.True(tally.Classify(true));
        tally.End();
        tally.Begin(Kind.Draw, timed: false, 0);
        tally.End();

        Assert.Equal(2, tally.Items(Kind.Draw));
        Assert.Equal(1, tally.Items(Kind.DrawDepth));
        Assert.Equal(0, tally.Timed(Kind.DrawDepth));
        var lines = Format(tally);
        Assert.Equal(2, lines.Count);
        // No timed item: the time per item is 0 and no step is listed.
        Assert.Equal("[PERF][DRAWCOST] kind=draw_depth items_per_frame=1 timed=0 us_per_item=0.00 ms_per_frame=0.00 steps= counts=", lines[1]);
    }

    [Fact]
    public void TheFirstClassificationOfAnItemDecides()
    {
        var tally = new DrawCostTally(depthSplit: true);
        tally.Begin(Kind.Draw, timed: true, 0);
        Assert.True(tally.Classify(true));
        Assert.False(tally.Classify(false));
        Assert.Equal(Kind.DrawDepth, tally.CurrentKind);
        tally.Lap(Step.Exit, 5);
        tally.End();

        Assert.Equal(0, tally.Items(Kind.Draw));
        Assert.Equal(1, tally.Timed(Kind.DrawDepth));
    }

    [Fact]
    public void DispatchesAreNeverClassified()
    {
        var tally = new DrawCostTally(depthSplit: true);
        tally.Begin(Kind.Dispatch, timed: true, 0);
        tally.Lap(Step.DispatchProgram, 4);
        Assert.False(tally.Classify(true));
        tally.End();

        Assert.StartsWith("[PERF][DRAWCOST] kind=dispatch items_per_frame=1 timed=1 us_per_item=4.00 ", Assert.Single(Format(tally)));
    }

    [Fact]
    public void WithoutTheSwitchEveryDrawIsADraw()
    {
        var tally = new DrawCostTally(depthSplit: false);
        tally.Begin(Kind.Draw, timed: true, 0);
        tally.Lap(Step.ExecTargets, 1);
        Assert.False(tally.Classify(true));
        tally.End();

        Assert.Equal(1, tally.Items(Kind.Draw));
        Assert.StartsWith("[PERF][DRAWCOST] kind=draw ", Assert.Single(Format(tally)));
    }

    [Fact]
    public void TheKindLinesAreDrawThenDrawDepthThenDispatch()
    {
        var tally = new DrawCostTally(depthSplit: true);
        tally.Begin(Kind.Dispatch, timed: true, 0);
        tally.End();
        TimedDraw(tally, depthOnly: true);
        TimedDraw(tally, depthOnly: false);

        var lines = Format(tally);
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("[PERF][DRAWCOST] kind=draw ", lines[0]);
        Assert.StartsWith("[PERF][DRAWCOST] kind=draw_depth ", lines[1]);
        Assert.StartsWith("[PERF][DRAWCOST] kind=dispatch ", lines[2]);
    }

    [Fact]
    public void ClearEmptiesTheWindow()
    {
        var tally = new DrawCostTally(depthSplit: true);
        TimedDraw(tally, depthOnly: true);
        tally.Clear();
        Assert.Empty(Format(tally));
    }

    [Fact]
    public void TheNamesAndTheMasksFit()
    {
        Assert.True(DrawCostTally.Steps <= 64);
        Assert.True(DrawCostTally.Counters <= 64);
        Assert.Equal("depth_note", DrawCostTally.NameOf(Step.DepthNote));
        Assert.Equal("draw_depth", DrawCostTally.NameOf(Kind.DrawDepth));
        Assert.Equal("same_all", DrawCostTally.NameOf(Counter.SameAll));
        Assert.Equal("ps_kill", DrawCostTally.NameOf(Counter.PsKill));
        Assert.Equal("dispatch", DrawCostTally.NameOf(Kind.Dispatch));
    }

    [Fact]
    public void ProbeOff_TheDepthHooksDoNothing()
    {
        Assert.False(DrawCostProbe.DepthSplit);
        Assert.False(DrawCostProbe.Classify(true));
        Assert.False(DrawCostProbe.DepthTiming);
    }

    [Theory]
    [InlineData(0u, true, true)]
    [InlineData(1u, true, false)]
    [InlineData(0u, false, false)]
    [InlineData(2u, false, false)]
    public void DepthOnlyIsNoColorTargetAndADepthTarget(uint colors, bool depth, bool expected)
    {
        Assert.Equal(expected, DrawCostDepth.IsDepthOnly(colors, depth));
    }

    [Fact]
    public void DepthOnlyFollowsTheDrawState()
    {
        var state = DrawState.Create();
        Assert.False(state.Depth.HasTarget);
        Assert.False(DrawCostDepth.IsDepthOnly(state.ColorCount, state.Depth.HasTarget));
    }

    private static DepthShaderControlRegisters Control(bool kill = false, bool z = false, bool mask = false, bool dual = false, bool noop = false) =>
        new() { KillEnable = kill, DepthExportEnable = z, MaskExportEnable = mask, DualExportEnable = dual, ExecuteOnNoop = noop };

    [Fact]
    public void PixelClass_FollowsTheShaderControl()
    {
        Assert.Equal(PixelClass.None, DrawCostDepth.PixelClassOf(false, 0xF, 0xF, Control(kill: true, z: true)));
        Assert.Equal(PixelClass.Kill, DrawCostDepth.PixelClassOf(true, 0, 0xF, Control(kill: true)));
        Assert.Equal(PixelClass.Export, DrawCostDepth.PixelClassOf(true, 0, 0, Control(z: true)));
        Assert.Equal(PixelClass.Export, DrawCostDepth.PixelClassOf(true, 0, 0, Control(kill: true, mask: true)));
        // A color export enabled for a slot (the target did not resolve), dual export or execute-on-noop: not kill only.
        Assert.Equal(PixelClass.Other, DrawCostDepth.PixelClassOf(true, 0xF, 0xF, Control(kill: true)));
        Assert.Equal(PixelClass.Other, DrawCostDepth.PixelClassOf(true, 0, 0, Control(kill: true, dual: true)));
        Assert.Equal(PixelClass.Other, DrawCostDepth.PixelClassOf(true, 0, 0, Control(noop: true)));
        Assert.Equal(PixelClass.Other, DrawCostDepth.PixelClassOf(true, 0, 0, Control()));
        Assert.Equal(Counter.PsKill, DrawCostDepth.CounterOf(PixelClass.Kill));
        Assert.Equal(Counter.PsOther, DrawCostDepth.CounterOf(PixelClass.Other));
    }

    [Fact]
    public void PixelClass_DecodedFromTheRegister()
    {
        // DB_SHADER_CONTROL: Z export bit 0, kill bit 6.
        Assert.Equal(PixelClass.Kill, DrawCostDepth.PixelClassOf(true, 0, 0, DepthShaderControlRegisters.Decode(1u << 6)));
        Assert.Equal(PixelClass.Export, DrawCostDepth.PixelClassOf(true, 0, 0, DepthShaderControlRegisters.Decode(1u | 1u << 6)));
    }

    private static VertexInputInfo Vertex(ulong bufferAddress = 0x1000, uint userData = 7, ulong programHash = 0xAB) => new()
    {
        Buffers = [new VertexInputBuffer(bufferAddress, 16, 100)],
        Stage = new ShaderStageResources(new ShaderProgramInfo { Hash = programHash }, new ResourceSnapshot { UserData = [userData, 2], Buffers = [[1, 2, 3, 4]] }),
    };

    [Fact]
    public void VertexHash_IsTheValuesNotTheObjects()
    {
        Assert.Equal(DrawCostDepth.VertexHash(Vertex()), DrawCostDepth.VertexHash(Vertex()));
        Assert.NotEqual(DrawCostDepth.VertexHash(Vertex()), DrawCostDepth.VertexHash(Vertex(bufferAddress: 0x2000)));
        Assert.NotEqual(DrawCostDepth.VertexHash(Vertex()), DrawCostDepth.VertexHash(Vertex(userData: 8)));
        Assert.NotEqual(DrawCostDepth.VertexHash(Vertex()), DrawCostDepth.VertexHash(Vertex(programHash: 0xAC)));
    }

    [Fact]
    public void StageHash_CoversTheDescriptorWords()
    {
        var program = new ShaderProgramInfo { Hash = 1 };
        var first = new ShaderStageResources(program, new ResourceSnapshot { Images = [[1, 2, 3, 4, 5, 6, 7, 8]] });
        var same = new ShaderStageResources(program, new ResourceSnapshot { Images = [[1, 2, 3, 4, 5, 6, 7, 8]] });
        var other = new ShaderStageResources(program, new ResourceSnapshot { Images = [[1, 2, 3, 4, 5, 6, 7, 9]] });
        Assert.Equal(DrawCostDepth.StageHash(first), DrawCostDepth.StageHash(same));
        Assert.NotEqual(DrawCostDepth.StageHash(first), DrawCostDepth.StageHash(other));
    }

    [Fact]
    public void IndexHash_CoversTheSource()
    {
        var index = new IndexSource(true, 0x4000, null, 300, IndexType.Uint16);
        Assert.Equal(DrawCostDepth.IndexHash(index), DrawCostDepth.IndexHash(index with { }));
        Assert.NotEqual(DrawCostDepth.IndexHash(index), DrawCostDepth.IndexHash(index with { Address = 0x4100 }));
        Assert.NotEqual(DrawCostDepth.IndexHash(index), DrawCostDepth.IndexHash(index with { Type = IndexType.Uint32 }));
        Assert.NotEqual(DrawCostDepth.IndexHash(index), DrawCostDepth.IndexHash(index with { HostData = new byte[4] }));
    }

    [Fact]
    public void TargetsHash_CoversThePixelStageAndTheColorCount()
    {
        var state = DrawState.Create();
        state.Begin(-1);
        var first = DrawCostDepth.TargetsHash(ref state);
        Assert.Equal(first, DrawCostDepth.TargetsHash(ref state));
        state.PixelActive = false;
        Assert.NotEqual(first, DrawCostDepth.TargetsHash(ref state));
    }

    [Fact]
    public void Shadow_ComparesWithThePreviousDrawOfTheSameKind()
    {
        var shadow = new DrawCostDepth.ShapeShadow();
        var shape = new DrawCostDepth.Shape(1, 2, 3, 4, 5);
        Assert.Equal(Same.None, shadow.Compare(Kind.DrawDepth, in shape));
        Assert.Equal(Same.All, shadow.Compare(Kind.DrawDepth, in shape));
        // Another kind has its own previous draw.
        Assert.Equal(Same.None, shadow.Compare(Kind.Draw, shape with { Vertex = 9 }));
        Assert.Equal(Same.All & ~Same.Index, shadow.Compare(Kind.DrawDepth, shape with { Index = 7 }));
        Assert.Equal(Same.All & ~Same.Dynamic, shadow.Compare(Kind.DrawDepth, shape with { Index = 7, Dynamic = 8 }));
        shadow.Reset();
        Assert.Equal(Same.None, shadow.Compare(Kind.DrawDepth, in shape));
    }
}
