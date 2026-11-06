// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// [local] SHARPEMU_ASYNC_GRAPHICS_PIPELINES: a draw whose pipeline compiles is left out only when it writes no memory.
[Collection(SchedulingStateCollection.Name)]
public sealed class AsyncGraphicsPipelineTests : IDisposable
{
    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public AsyncGraphicsPipelineTests()
    {
        _executor = new RenderExecutor(_host, _pipelines);
        // [local] These draws use a small target: the exemptions are covered by their own tests below.
        RenderRecorder.AsyncGraphicsExemptOverride.Value = false;
    }

    public void Dispose()
    {
        RenderRecorder.AsyncGraphicsPipelinesOverride.Value = null;
        RenderRecorder.AsyncGraphicsExemptOverride.Value = null;
        _fatal.Dispose();
    }

    // DB_COUNT_CONTROL with the zero-pass increment disabled: the draws count no occlusion samples.
    private static RegisterBanks Uncounted(uint primitiveType = PrimitiveTriangleList)
    {
        var banks = Banks(primitiveType);
        banks.Context.DepthCountControl |= 1u;
        return banks;
    }

    private int Draws => _host.Calls.Count(call => call.StartsWith("draw ", StringComparison.Ordinal));

    [Fact]
    public void SwitchOff_CreatesThePipelineSynchronously()
    {
        RenderRecorder.AsyncGraphicsPipelinesOverride.Value = 0;
        _pipelines.PendingGraphicsCompiles = 1;

        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.Equal(0, _pipelines.GraphicsPipelineAttempts);
        Assert.Contains(_pipelines.Calls, call => call.StartsWith("create_graphics_pipeline", StringComparison.Ordinal));
        Assert.Contains("draw 3 1 0 0", _host.Calls);
    }

    [Fact]
    public void SwitchOn_SkipsTheDrawWhileItsPipelineCompilesAndDrawsOnceReady()
    {
        RenderRecorder.AsyncGraphicsPipelinesOverride.Value = 1;
        _pipelines.PendingGraphicsCompiles = 1;

        _executor.DrawAuto(1, Uncounted(), Auto(3));
        Assert.Equal(1, _pipelines.GraphicsPipelineAttempts);
        Assert.Equal(0, Draws);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("begin_rendering", StringComparison.Ordinal));

        _executor.DrawAuto(2, Uncounted(), Auto(3));
        Assert.Equal(2, _pipelines.GraphicsPipelineAttempts);
        Assert.Equal(1, Draws);

        _executor.DrawAuto(3, Uncounted(), Auto(3));
        Assert.Equal(2, Draws);
    }

    [Fact]
    public void ModeOne_ADrawThatCountsOcclusionWaitsAndModeTwoSkipsIt()
    {
        RenderRecorder.AsyncGraphicsPipelinesOverride.Value = 1;
        _pipelines.PendingGraphicsCompiles = 1;
        _executor.DrawAuto(1, Banks(), Auto(3));
        Assert.Equal(2, _pipelines.GraphicsPipelineAttempts);
        Assert.Equal(1, Draws);

        RenderRecorder.AsyncGraphicsPipelinesOverride.Value = 2;
        _pipelines.PendingGraphicsCompiles = 1;
        _executor.DrawAuto(2, Banks(PrimitiveTriangleStrip), Auto(4));
        Assert.Equal(3, _pipelines.GraphicsPipelineAttempts);
        Assert.Equal(1, Draws);
    }

    [Fact]
    public void SwitchOn_ADrawThatWritesMemoryWaitsForItsPipeline()
    {
        RenderRecorder.AsyncGraphicsPipelinesOverride.Value = 2;
        _pipelines.Graphics = Programs(vertexStage: Stage(Program(ShaderStageKind.Vertex, usesDeviceAddresses: true)));
        _pipelines.PendingGraphicsCompiles = 2;

        _executor.DrawAuto(1, Uncounted(), Auto(3));

        Assert.Equal(3, _pipelines.GraphicsPipelineAttempts);
        Assert.Equal(1, Draws);
    }

    [Fact]
    public void ProgramsThatMayStore_AreNeverSkipped()
    {
        var plain = new VertexInputInfo { Stage = Stage(Program(ShaderStageKind.Vertex)) };
        var record = new ResolvedDraw { Kind = ResolvedDrawKind.Draw };
        Assert.True(RenderRecorder.MaySkipWhileCompiling(record, plain, null, mesh: false));
        Assert.False(RenderRecorder.MaySkipWhileCompiling(record, plain, null, mesh: true));

        var writtenBuffer = new VertexInputInfo
        {
            Stage = Stage(Program(ShaderStageKind.Vertex, buffers: [new BufferResourceInfo(false, true, false, false, false, 16, 16)])),
        };
        Assert.False(RenderRecorder.MaySkipWhileCompiling(record, writtenBuffer, null, mesh: false));

        var storageImage = new PixelInputInfo
        {
            Stage = Stage(Program(ShaderStageKind.Pixel, images: [new ImageResourceInfo(ImageResourceClass.Storage, Written: true)])),
        };
        Assert.False(RenderRecorder.MaySkipWhileCompiling(record, plain, storageImage, mesh: false));

        record.WritesMemory = true;
        Assert.False(RenderRecorder.MaySkipWhileCompiling(record, plain, null, mesh: false));
        record.WritesMemory = false;

        // DB_COUNT_CONTROL with the zero-pass counter on: the game reads the samples back.
        record.Dynamic = record.Dynamic with { DepthCountControl = 1u << 8 };
        Assert.False(RenderRecorder.MaySkipWhileCompiling(record, plain, null, mesh: false));
        Assert.True(RenderRecorder.MaySkipWhileCompiling(record, plain, null, mesh: false, occlusionSkippable: true));
        Assert.False(RenderRecorder.MaySkipWhileCompiling(record, writtenBuffer, null, mesh: false, occlusionSkippable: true));
    }

    [Fact]
    public void Cache_HandsOverTheHostCompileOnceAndKeepsIt()
    {
        RenderRecorder.AsyncGraphicsPipelinesOverride.Value = 1;
        var guest = new PipelineTestGuest();
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var executor = new RenderExecutor(_host, new CachingProvider(cache));
        guest.Host.PendingGraphicsCompiles = 2;

        executor.DrawAuto(1, Uncounted(), Auto(3));
        executor.DrawAuto(2, Uncounted(), Auto(3));
        Assert.Empty(guest.Host.GraphicsPipelines);
        Assert.Equal(0, cache.GraphicsPipelineCount);
        Assert.Equal(0, Draws);

        executor.DrawAuto(3, Uncounted(), Auto(3));
        executor.DrawAuto(4, Uncounted(PrimitiveTriangleStrip), Auto(4));
        executor.DrawAuto(5, Uncounted(), Auto(3));
        Assert.Equal(2, guest.Host.GraphicsPipelines.Count);
        Assert.Equal(2, cache.GraphicsPipelineCount);
        Assert.Equal(3, Draws);
    }

    // Programs from the fixtures, pipelines from the real cache over the fake host.
    private sealed class CachingProvider(ShaderPipelineCache cache) : IShaderPipelineProvider
    {
        private readonly GraphicsPrograms _programs = Programs();

        public GraphicsPrograms GetGraphicsPrograms(RegisterBanks banks, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive, bool depthBound) => _programs;

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth, VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering, PrimitiveTopology topology, bool primitiveRestartEnabled, bool disableBlending, ShaderProgram vertexProgram, ShaderProgram pixelProgram) =>
            cache.CreateGraphicsPipeline(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending, vertexProgram, pixelProgram);

        public bool TryCreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth, VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering, PrimitiveTopology topology, bool primitiveRestartEnabled, bool disableBlending, ShaderProgram vertexProgram, ShaderProgram pixelProgram, out PipelineHandle handle, out Task? completion) =>
            cache.TryCreateGraphicsPipeline(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending, vertexProgram, pixelProgram, out handle, out completion);

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ) =>
            throw new InvalidOperationException("The caching provider has no compute program.");

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) => cache.CreateComputePipeline(input, program);
    }

    // [local] SHARPEMU_ASYNC_GRAPHICS_EXEMPT: linear targets and all-small targets keep their draws while compiling.
    private static ColorTargetState Target(uint width, uint height, GuestTileMode tileMode)
    {
        var description = new ImageDescription { TileMode = tileMode };
        var request = new ImageRequest(description, ImageViewDescription.Default, default);
        return new ColorTargetState(new ColorTargetResolution(request, 0, 0, new Extent2D(width, height), 0, 0, 1, default, false, false, default), 0, default);
    }

    [Fact]
    public void Exemptions_KeepLinearAndSmallTargetsAndLeaveLargeTiledOnes()
    {
        Assert.False(RenderRecorder.KeepsTargetsWhileCompiling([]));
        Assert.True(RenderRecorder.KeepsTargetsWhileCompiling([Target(1024, 1024, GuestTileMode.Standard64KB)]));
        Assert.True(RenderRecorder.KeepsTargetsWhileCompiling([Target(256, 128, GuestTileMode.Standard64KB), Target(512, 512, GuestTileMode.Standard64KB)]));
        Assert.False(RenderRecorder.KeepsTargetsWhileCompiling([Target(1920, 1080, GuestTileMode.Standard64KB)]));
        Assert.False(RenderRecorder.KeepsTargetsWhileCompiling([Target(256, 256, GuestTileMode.Standard64KB), Target(3840, 2160, GuestTileMode.Standard64KB)]));
        Assert.True(RenderRecorder.KeepsTargetsWhileCompiling([Target(3840, 2160, GuestTileMode.Linear)]));
    }
}
