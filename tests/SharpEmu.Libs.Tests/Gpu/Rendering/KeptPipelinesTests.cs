// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// [local] SHARPEMU_KEPT_PIPELINES: a draw that comes back to a program under unchanged registers takes the pipeline it
// was given before, and only while nothing it was resolved from has changed.
[Collection(SchedulingStateCollection.Name)]
public sealed class KeptPipelinesTests : IDisposable
{
    private readonly FatalScope _fatal = new();

    public void Dispose()
    {
        RenderRecorder.KeptPipelinesOverride.Value = null;
        _fatal.Dispose();
    }

    private static GraphicsPrograms WithVertexProgram(GraphicsPrograms programs, ulong id) =>
        new(programs.VertexInput, programs.PixelInput)
        {
            Vertex = new ShaderProgram(id),
            Pixel = programs.Pixel,
        };

    private static int PipelineCreations(FakePipelineProvider pipelines) =>
        pipelines.Calls.Count(call => call.StartsWith("create_graphics_pipeline", StringComparison.Ordinal));

    // Draws A, B, A from the same registers; returns how many pipelines were looked up.
    private static int AlternatingDraws(int? kept, bool writeBetween)
    {
        RenderRecorder.KeptPipelinesOverride.Value = kept;
        var host = new RecordingRenderHost();
        var pipelines = new FakePipelineProvider();
        var executor = new RenderExecutor(host, pipelines);
        var banks = Banks();
        var first = Programs();
        var second = WithVertexProgram(first, 0x44);

        pipelines.Graphics = first;
        executor.DrawAuto(1, banks, Auto(3));
        pipelines.Graphics = second;
        executor.DrawAuto(2, banks, Auto(3));
        if (writeBetween)
        {
            banks.MarkWritten(RegisterStamps.ContextBits);
        }

        pipelines.Graphics = first;
        executor.DrawAuto(3, banks, Auto(3));
        Assert.Equal(3, host.Calls.Count(call => call.StartsWith("draw ", StringComparison.Ordinal)));
        return PipelineCreations(pipelines);
    }

    [Fact]
    public void Default_KeepsOnlyThePreviousDrawsPipeline()
    {
        Assert.Equal(3, AlternatingDraws(kept: null, writeBetween: false));
        Assert.Equal(3, AlternatingDraws(kept: 1, writeBetween: false));
    }

    [Fact]
    public void SeveralKept_ADrawBackToAnEarlierProgramTakesItsPipeline()
    {
        Assert.Equal(2, AlternatingDraws(kept: 4, writeBetween: false));
        Assert.Equal(2, AlternatingDraws(kept: 2, writeBetween: false));
    }

    [Fact]
    public void SeveralKept_ARegisterWriteSinceMakesTheEarlierPipelineUnusable()
    {
        Assert.Equal(3, AlternatingDraws(kept: 4, writeBetween: true));
    }

    [Fact]
    public void SeveralKept_TheOldestEntryIsReplacedFirst()
    {
        RenderRecorder.KeptPipelinesOverride.Value = 2;
        var host = new RecordingRenderHost();
        var pipelines = new FakePipelineProvider();
        var executor = new RenderExecutor(host, pipelines);
        var banks = Banks();
        var a = Programs();
        var b = WithVertexProgram(a, 0x44);
        var c = WithVertexProgram(a, 0x55);

        foreach (var (programs, submit) in new[] { (a, 1ul), (b, 2ul), (c, 3ul), (b, 4ul), (a, 5ul) })
        {
            pipelines.Graphics = programs;
            executor.DrawAuto(submit, banks, Auto(3));
        }

        // a, b and c are looked up; b comes back from the two kept; a was pushed out by c.
        Assert.Equal(4, PipelineCreations(pipelines));
    }

    [Fact]
    public void ProbeOff_ItsHooksDoNothing()
    {
        Assert.False(DrawCostProbe.Enabled);
        DrawCostProbe.Begin(DrawCostProbe.Kind.Draw);
        DrawCostProbe.Lap(DrawCostProbe.Step.ExecPrelude);
        DrawCostProbe.Count(DrawCostProbe.Counter.Obtain);
        DrawCostProbe.End();
        Assert.False(DrawCostProbe.Timing);
    }

    [Fact]
    public void KeyShadow_RemembersTheLastEightKeys()
    {
        var shadow = new DrawCostProbe.KeyShadow<int>();
        for (var key = 0; key < 8; key++)
        {
            Assert.False(shadow.Contains(key));
            shadow.Add(key);
        }

        Assert.True(shadow.Contains(0));
        Assert.True(shadow.Contains(7));
        shadow.Add(8);
        Assert.False(shadow.Contains(0));
        Assert.True(shadow.Contains(1));
        Assert.True(shadow.Contains(8));
    }
}
