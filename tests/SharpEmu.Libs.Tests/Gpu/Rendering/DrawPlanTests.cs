// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// [local] SHARPEMU_DRAW_PLAN: a draw takes the pipeline of the last draw of its shape while the inputs of its
// PipelineKey are equal by value, and the target layout while the registers it was resolved from are; any input that
// changes (a register of a group, the rendering state through a replaced image or a clear) misses, =2 counts a
// disagreement with the generic path, and off is the generic path.
[Collection(SchedulingStateCollection.Name)]
public sealed class DrawPlanTests : IDisposable
{
    private readonly FatalScope _fatal = new();

    public DrawPlanTests()
    {
        Assert.True(PlanRegisterShadow.Enabled); // the test module initializer turns the shadow on
        DrawPlan.ResetForTests();
    }

    public void Dispose()
    {
        DrawPlan.ModeOverride.Value = null;
        DrawPlan.ResetForTests();
        _fatal.Dispose();
    }

    private sealed class Rig
    {
        public readonly RecordingRenderHost Host = new();
        public readonly FakePipelineProvider Pipelines = new();
        public readonly RenderExecutor Executor;
        public readonly RegisterBanks Banks = Banks();
        private ulong _submit;

        public Rig(int mode)
        {
            DrawPlan.ModeOverride.Value = mode;
            Executor = new RenderExecutor(Host, Pipelines);
            DrawPlan.ModeOverride.Value = null;
            // The registers the tests write again, written once first: a register's first write moves its group's
            // hash (a written value differs from an unwritten register), even with the typed value it already had.
            Write(CbBlend0Control, 0);
            Write(CbColor0Info, RegisterWords.Color(ColorBase, 64, 64).Info);
            Write(PaClViewportXScale, BitConverter.SingleToUInt32Bits(32f));
        }

        public void Draw() => Executor.DrawAuto(++_submit, Banks, Auto(3));

        // A register written as the command stream writes it: typed state, value hash and stamp.
        public void Write(uint offset, uint value) => RegisterWriteTable.WriteContextEntry(Banks, offset, value, 0);

        public int Creations => Pipelines.Calls.Count(call => call.StartsWith("create_graphics_pipeline", StringComparison.Ordinal));

        public int Draws => Host.Calls.Count(call => call.StartsWith("draw ", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_TheGenericPathAnswersEveryMovedStamp()
    {
        var rig = new Rig(0);
        rig.Write(CbBlend0Control, 0x10);
        rig.Draw();
        rig.Write(CbBlend0Control, 0x10); // the value repeats, the stamp moves
        rig.Draw();
        Assert.Equal(2, rig.Creations);
        Assert.Equal(0, DrawPlan.Draws + DrawPlan.Hits);
    }

    [Fact]
    public void On_ARepeatedValueKeepsThePipeline()
    {
        var rig = new Rig(1);
        rig.Write(CbBlend0Control, 0x10);
        rig.Draw();
        rig.Write(CbBlend0Control, 0x10);
        rig.Draw();
        rig.Write(PaClViewportXScale, BitConverter.SingleToUInt32Bits(32f));
        rig.Draw();
        Assert.Equal(1, rig.Creations);
        Assert.Equal(3, rig.Draws);
        Assert.Equal(2, DrawPlan.Hits);
        Assert.Equal(1, DrawPlan.Misses[(int)DrawPlan.Miss.New]);
        Assert.Equal(3, rig.Host.Calls.Count(call => call == "bind_pipeline Graphics A1"));
    }

    [Theory]
    [InlineData(CbBlend0Control, 0x10u, 0x20u, (int)DrawPlan.Miss.Context)]
    [InlineData(PaSuScModeCntl, 0x0u, 0x4u, (int)DrawPlan.Miss.Context)]
    [InlineData(PaClViewportXScale, 0x4200_0000u, 0x4180_0000u, (int)DrawPlan.Miss.Dynamic)]
    [InlineData(CbTargetMask, 0xFu, 0x3u, (int)DrawPlan.Miss.Targets)]
    public void On_ARegisterThatChangesOneInputMisses_AndItsValueComingBackHits(uint register, uint first, uint changed, int reason)
    {
        var rig = new Rig(1);
        rig.Write(register, first);
        rig.Draw();
        rig.Write(register, changed);
        rig.Draw();
        Assert.Equal(2, rig.Creations);
        Assert.Equal(1, DrawPlan.Misses[reason]);
        rig.Write(register, first); // back to the first value: the draws of the shape keep the latest answer
        rig.Draw();
        Assert.Equal(3, rig.Creations);
        rig.Write(register, first);
        rig.Draw();
        Assert.Equal(3, rig.Creations);
        Assert.Equal(1, DrawPlan.Hits);
    }

    [Fact]
    public void On_AReplacedTargetImageMisses()
    {
        var rig = new Rig(1);
        rig.Draw();
        rig.Host.ReplaceImage(ColorBase); // another image at the target's address: another view in the rendering state
        rig.Write(CbBlend0Control, 0);
        rig.Draw();
        Assert.Equal(2, rig.Creations);
        Assert.Equal(1, DrawPlan.Misses[(int)DrawPlan.Miss.Rendering]);
        Assert.Equal(0, DrawPlan.Hits);
    }

    [Fact]
    public void On_AClearOfTheTargetMisses()
    {
        var rig = new Rig(1);
        rig.Draw();
        rig.Host.ColorMetadataClears[ColorBase] = default; // the attachment clears: the rendering state differs
        rig.Write(CbBlend0Control, 0);
        rig.Draw();
        Assert.Equal(2, rig.Creations);
        Assert.Equal(1, DrawPlan.Misses[(int)DrawPlan.Miss.Rendering]);
    }

    [Fact]
    public void On_AnotherProgramIsAnotherShape()
    {
        var rig = new Rig(1);
        rig.Draw();
        var other = rig.Pipelines.Graphics;
        rig.Pipelines.Graphics = new GraphicsPrograms(other.VertexInput, other.PixelInput) { Vertex = new ShaderProgram(0x44), Pixel = other.Pixel };
        rig.Write(CbBlend0Control, 0);
        rig.Draw();
        Assert.Equal(2, rig.Creations);
        Assert.Equal(2, DrawPlan.Misses[(int)DrawPlan.Miss.New]);
    }

    [Fact]
    public void On_ARayTracingProgramIsLeftToTheGenericPath()
    {
        var rig = new Rig(1);
        var programs = rig.Pipelines.Graphics;
        programs.VertexInput.Stage = Stage(new ShaderProgramInfo { Stage = ShaderStageKind.Vertex, Hash = 0x8395_0000_0000_0001, ParameterExportMask = 1 });
        rig.Draw();
        rig.Write(CbBlend0Control, 0);
        rig.Draw();
        Assert.Equal(2, rig.Creations);
        Assert.Equal(2, DrawPlan.Misses[(int)DrawPlan.Miss.Excluded]);
    }

    [Fact]
    public void Verify_ADisagreementIsCountedAndTheGenericAnswerUsed()
    {
        var rig = new Rig(2);
        rig.Draw();
        rig.Write(CbBlend0Control, 0);
        rig.Draw();
        Assert.Equal(1, DrawPlan.Verified);
        Assert.Equal(0, DrawPlan.Wrong);

        rig.Pipelines.GraphicsHandle = new PipelineHandle(0xA2, 0xB1, false); // the generic path answers otherwise
        rig.Write(CbBlend0Control, 0);
        rig.Draw();
        Assert.Equal(2, DrawPlan.Verified);
        Assert.Equal(1, DrawPlan.Wrong);
        Assert.Equal(1, DrawPlan.WrongTotal);
        Assert.Equal("bind_pipeline Graphics A2", rig.Host.Calls.Last(call => call.StartsWith("bind_pipeline", StringComparison.Ordinal)));
        Assert.Equal(3, rig.Creations); // =2 always runs the generic path
    }

    [Fact]
    public void Layout_KeptByValueWhenTheStampsMoved()
    {
        var rig = new Rig(1);
        rig.Write(CbColor0Info, RegisterWords.Color(ColorBase, 64, 64).Info);
        rig.Draw();
        Assert.Equal(1, DrawPlan.LayoutResolve);
        rig.Write(CbColor0Info, RegisterWords.Color(ColorBase, 64, 64).Info);
        rig.Draw();
        Assert.Equal(1, DrawPlan.LayoutValue);
        Assert.Equal(1, DrawPlan.LayoutResolve);
        rig.Write(CbColor0Base, (uint)(SecondColorBase >> 8)); // another target
        rig.Draw();
        Assert.Equal(2, DrawPlan.LayoutResolve);
        Assert.Contains(rig.Host.Calls, call => call.StartsWith($"find_image {SecondColorBase:X} ", StringComparison.Ordinal));
    }

    [Fact]
    public void Layout_ForgottenHashesResolveAgain()
    {
        var rig = new Rig(1);
        rig.Draw();
        rig.Banks.ForgetPlanHashes();
        rig.Banks.MarkWritten(RegisterStamps.ContextBits);
        rig.Draw();
        Assert.Equal(2, DrawPlan.LayoutResolve);
        Assert.Equal(0, DrawPlan.LayoutValue);
        Assert.Equal(1, DrawPlan.Misses[(int)DrawPlan.Miss.Targets]);
    }

    [Fact]
    public void Layout_VerifyCatchesAWriteThePlanCannotSee()
    {
        var rig = new Rig(2);
        rig.Draw();
        // A typed write past the register writers that does not forget the hashes: what a missing dependency would be.
        rig.Banks.Context.ColorTargets[0] = RegisterWords.Color(SecondColorBase, 64, 64);
        rig.Banks.MarkWritten(RegisterStamps.ContextBits);
        rig.Draw();
        Assert.Equal(1, DrawPlan.LayoutVerified);
        Assert.Equal(1, DrawPlan.LayoutWrong);
        Assert.Contains(rig.Host.Calls, call => call.StartsWith($"find_image {SecondColorBase:X} ", StringComparison.Ordinal));
    }

    [Fact]
    public void Layout_VerifyAgreesOnARepeatedValue()
    {
        var rig = new Rig(2);
        rig.Draw();
        rig.Write(CbBlend0Control, 0);
        rig.Draw();
        rig.Write(CbColor0Info, RegisterWords.Color(ColorBase, 64, 64).Info);
        rig.Banks.MarkWritten(RegisterStamps.ContextBits);
        rig.Draw();
        Assert.True(DrawPlan.LayoutVerified >= 1);
        Assert.Equal(0, DrawPlan.LayoutWrong);
        Assert.Equal(0, DrawPlan.Wrong);
    }

    [Fact]
    public void Compare_NamesTheFirstGroupThatDiffers()
    {
        var shape = new DrawPlan.Shape { Key = 1, VertexProgram = 2, Topology = 3 };
        var groups = new PlanGroupHashes { ColorTargets = 1, DepthTargets = 2, ShaderInterface = 3, PipelineState = 4, ContextDynamic = 5 };
        var rendering = new RenderingState { Width = 64, Height = 64, Layers = 1, Samples = 1 };
        var entry = new DrawPlan.Entry();
        Assert.Equal(DrawPlan.Miss.New, DrawPlan.Compare(entry, in shape, in groups, in rendering));
        DrawPlan.Keep(entry, in shape, in groups, in rendering, new PipelineHandle(1, 2, false), null);
        Assert.Null(DrawPlan.Compare(entry, in shape, in groups, in rendering));
        Assert.Equal(DrawPlan.Miss.Shape, DrawPlan.Compare(entry, shape with { Topology = 4 }, in groups, in rendering));
        Assert.Equal(DrawPlan.Miss.Targets, DrawPlan.Compare(entry, in shape, groups with { DepthTargets = 9 }, in rendering));
        Assert.Equal(DrawPlan.Miss.Context, DrawPlan.Compare(entry, in shape, groups with { PipelineState = 9 }, in rendering));
        Assert.Equal(DrawPlan.Miss.Context, DrawPlan.Compare(entry, in shape, groups with { ShaderInterface = 9 }, in rendering));
        Assert.Equal(DrawPlan.Miss.Dynamic, DrawPlan.Compare(entry, in shape, groups with { ContextDynamic = 9 }, in rendering));
        Assert.Equal(DrawPlan.Miss.Rendering, DrawPlan.Compare(entry, in shape, in groups, rendering with { Width = 32 }));
    }

    [Fact]
    public void Store_AFullStoreStartsOver()
    {
        var store = new DrawPlan.Store(2);
        var first = store.Take(1);
        Assert.Same(first, store.Take(1));
        store.Take(2);
        Assert.Equal(2, store.Count);
        store.Take(3);
        Assert.Equal(1, store.Count);
        Assert.NotSame(first, store.Take(1));
        Assert.Equal(1, DrawPlan.Clears);
    }

    [Fact]
    public void ForgetPlanHashes_EqualsNoOtherState()
    {
        var banks = new RegisterBanks(message => new InvalidOperationException(message));
        banks.NotePlanContextWrite(CbColor0Info, [0x10]);
        var known = banks.PlanHashes;
        banks.ForgetPlanHashes();
        var forgotten = banks.PlanHashes;
        Assert.NotEqual(known, forgotten);
        Assert.NotEqual(default, forgotten);
        banks.NotePlanContextWrite(CbColor0Info, [0x10]); // the same write again does not bring the known state back
        Assert.NotEqual(known, banks.PlanHashes);
        banks.ApplyContextState(ContextStateOperation.Clear);
        Assert.Equal(default, banks.PlanHashes);
    }

    [Fact]
    public void ReportLineHasTheCountsAndClearsThem()
    {
        DrawPlan.Draws = 10;
        DrawPlan.Hits = 7;
        DrawPlan.Misses[(int)DrawPlan.Miss.Targets] = 2;
        DrawPlan.Misses[(int)DrawPlan.Miss.Excluded] = 1;
        DrawPlan.LayoutValue = 4;
        var line = DrawPlan.FormatAndClear(1);
        Assert.StartsWith("[PERF][DRAW_PLAN] mode=1 draws=10 hits=7 hit_pct=70.0 miss=excluded:1,new:0,shape:0,targets:2,", line);
        Assert.Contains(" wrong=0 wrong_total=0 ", line);
        Assert.Contains(" layout=stamp:0,value:4,resolve:0 ", line);
        Assert.Equal(0, DrawPlan.Draws);
        Assert.Equal(0, DrawPlan.Misses[(int)DrawPlan.Miss.Targets]);
    }
}
