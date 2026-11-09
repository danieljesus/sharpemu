// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Diagnostics.DgcRunProbe;

namespace SharpEmu.Libs.Tests.Diagnostics;

// [local] SHARPEMU_DGC_RUN_PROBE: the runs a generated-commands call could cover, their cuts and causes, per scenario.
public sealed class DgcRunProbeTests
{
    private static readonly PipeInfo A = new() { Stages = 1, Frag = 1, State = 1, Layout = 1, Family = 1 };
    private static readonly PipeInfo ShadersOnly = new() { Stages = 1, Frag = 1, State = 1, Layout = 1, Family = 2 };
    private static readonly PipeInfo OtherLayout = new() { Stages = 1, Frag = 1, State = 1, Layout = 9, Family = 3 };
    private static readonly PipeInfo OtherState = new() { Stages = 1, Frag = 1, State = 7, Layout = 1, Family = 4 };

    private static long Main(Scenario scenario, Cut cause) => scenario.Main[MainCause(cause)];

    private static long Any(Scenario scenario, Cut cause) => scenario.Any[MainCause(cause)];

    [Fact]
    public void PipelinesOfOneExecutionSetAndPushedStateDoNotCut()
    {
        var splitter = new Splitter();
        splitter.BeginRendering(0x10, allLoad: true);
        splitter.Draw(DrawType.Indexed, A, 100);
        splitter.Draw(DrawType.Indexed, ShadersOnly, 101); // other shaders, same state: one IES
        splitter.DescriptorsWritten = true;
        splitter.Draw(DrawType.Indexed, A, 100);
        splitter.Dyn0.Viewport.Width = 640; // S1 moves it to push constants
        splitter.Draw(DrawType.Indexed, A, 100);
        splitter.Draw(DrawType.Indexed, OtherLayout, 102); // S0 only
        splitter.Dyn1.DepthWrite = 1; // cuts both
        splitter.Draw(DrawType.Indexed, OtherLayout, 102);

        Assert.Equal(3, splitter.S0.Runs); // [3 | 1 | 1 | ...]: viewport, then layout, then depth write
        Assert.Equal(1, Main(splitter.S0, Cut.DynState0));
        Assert.Equal(1, Main(splitter.S0, Cut.Layout));
        Assert.Equal(1, Main(splitter.S0, Cut.DynState));
        Assert.Equal(1, splitter.S1.Runs); // [5 | ...]: depth write
        Assert.Equal(1, Main(splitter.S1, Cut.DynState));
        Assert.Equal(5, splitter.S1.Draws);
        Assert.Equal(1, splitter.DescriptorDraws);
        Assert.Equal(5, splitter.ComparedDraws);
    }

    [Fact]
    public void ScopeBoundariesTellFusablePassesFromOtherTargets()
    {
        var splitter = new Splitter();
        splitter.BeginRendering(0x10, allLoad: false);
        splitter.Draw(DrawType.Draw, A, 100);
        splitter.EndRendering();
        splitter.Barrier(BarrierSource.Global);
        splitter.BeginRendering(0x10, allLoad: true); // same views, loaded: a pass split
        splitter.Draw(DrawType.Draw, A, 100);
        splitter.EndRendering();
        splitter.BeginRendering(0x10, allLoad: false); // same views, cleared
        splitter.Draw(DrawType.Draw, A, 100);
        splitter.EndRendering();
        splitter.BeginRendering(0x20, allLoad: true); // other views
        splitter.Draw(DrawType.Draw, A, 100);

        Assert.Equal(3, splitter.S1.Runs);
        Assert.Equal(1, Main(splitter.S1, Cut.Pass)); // the pass outranks the barrier at its boundary
        Assert.Equal(1, Any(splitter.S1, Cut.BarrierGame));
        Assert.Equal(2, Main(splitter.S1, Cut.Targets));
        Assert.Equal(2, splitter.S1.Sole[MainCause(Cut.Targets)]);
        Assert.Equal(0, splitter.S1.Sole[MainCause(Cut.Pass)]);
        Assert.Equal(1, splitter.Barriers[(int)BarrierSource.Global]);
    }

    [Fact]
    public void AStoringDrawsBreakIsADependencyAndHardCutsComeFirst()
    {
        var splitter = new Splitter();
        splitter.BeginRendering(0x10, allLoad: true);
        splitter.Draw(DrawType.Draw, A, 100);
        splitter.Storing = true;
        splitter.EndRendering();
        splitter.Barrier(BarrierSource.Other);
        splitter.Storing = false;
        splitter.BeginRendering(0x10, allLoad: true);
        splitter.Draw(DrawType.Draw, A, 100);
        splitter.Note(Cut.Dispatch);
        splitter.Barrier(BarrierSource.Image);
        splitter.Draw(DrawType.Indexed, OtherState, 103);
        splitter.Draw(DrawType.Indexed, OtherState, 103);

        Assert.Equal(2, splitter.S1.Runs);
        Assert.Equal(1, splitter.S1.Sole[MainCause(Cut.Dependency)]);
        Assert.Equal(1, Main(splitter.S1, Cut.Dispatch));
        Assert.Equal(1, Any(splitter.S1, Cut.BarrierEmu));
        Assert.Equal(1, Any(splitter.S1, Cut.PipeState));
        Assert.Equal(1, Any(splitter.S1, Cut.DrawType));
        Assert.Equal(0, splitter.S1.PipeVariants);
    }

    [Fact]
    public void RunLengthsGiveTheMeanPercentilesAndTheDrawsInLongRuns()
    {
        var splitter = new Splitter();
        foreach (var length in new[] { 1, 1, 10, 2 })
        {
            for (var index = 0; index < length; index++)
            {
                splitter.Draw(DrawType.Draw, A, 100);
            }

            splitter.Note(Cut.Transfer);
        }

        splitter.Draw(DrawType.Draw, A, 100); // closes the last run
        var s1 = splitter.S1;
        Assert.Equal(4, s1.Runs);
        Assert.Equal(14, s1.Draws);
        Assert.Equal(10, s1.DrawsIn8);
        Assert.Equal(1, s1.Percentile(0.5));
        Assert.Equal(10, s1.Percentile(0.9));
        var line = Report(splitter, 5.0, 2, 0, 30);
        Assert.Contains("S1=runs_pf:2.0,mean:3.50,p50:1,p90:10,in8:71.4 ", line);
        Assert.Contains("S1_main=xfer:100.0 ", line);
        Assert.Contains("draws_per_frame=8 ", line);
    }

    [Fact]
    public void AnUnknownPipelineCutsOnlyWhenTheHandleChanges()
    {
        var splitter = new Splitter();
        splitter.Draw(DrawType.Draw, null, 500);
        splitter.Draw(DrawType.Draw, null, 500);
        splitter.Draw(DrawType.Draw, null, 501);
        Assert.Equal(1, splitter.S0.Runs);
        Assert.Equal(1, Main(splitter.S0, Cut.PipeState));
        Assert.Equal(3, splitter.UnknownPipe);
    }
}
