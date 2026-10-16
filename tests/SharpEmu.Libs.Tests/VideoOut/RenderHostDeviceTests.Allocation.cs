// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.Tracing;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] Managed bytes the render thread allocates per warm draw and per warm PrepareBindings + BindResources
// (GC.GetAllocatedBytesForCurrentThread on the worker). SHARPEMU_ALLOC_SURVEY=<file> also samples the allocated types
// (GC AllocationTick, one sample per ~100 KB) and writes them there.
public sealed unsafe partial class RenderHostDeviceTests
{
    private const int AllocationWarmup = 512;
    // SHARPEMU_ALLOC_SURVEY_WAIT_S=<s> waits before the count (for dotnet-trace to attach) and counts ten times longer.
    private static readonly int AllocationSurveyWaitSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ALLOC_SURVEY_WAIT_S"), out var wait) ? wait : 0;
    private static readonly int AllocationCalls = AllocationSurveyWaitSeconds > 0 ? 40960 : 4096;
    private const int AllocationFlushEvery = 64;

    private sealed class AllocationSurvey : EventListener
    {
        private readonly Dictionary<string, long> _bytes = new();
        private volatile bool _sampling;

        public static AllocationSurvey? Start() =>
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHARPEMU_ALLOC_SURVEY")) ? null : new AllocationSurvey();

        public void Sample(bool on) => _sampling = on;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (!_sampling || data.EventName is null || !data.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) ||
                data.PayloadNames is null || data.Payload is null)
            {
                return;
            }

            var typeIndex = data.PayloadNames.IndexOf("TypeName");
            var amountIndex = data.PayloadNames.IndexOf("AllocationAmount64");
            if (typeIndex < 0)
            {
                return;
            }

            var type = data.Payload[typeIndex]?.ToString() ?? "?";
            var amount = amountIndex >= 0 ? Convert.ToInt64(data.Payload[amountIndex]) : 100_000;
            lock (_bytes)
            {
                _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
            }
        }

        public void Write(string scenario, double bytesPerCall)
        {
            var path = Environment.GetEnvironmentVariable("SHARPEMU_ALLOC_SURVEY")!;
            KeyValuePair<string, long>[] rows;
            lock (_bytes)
            {
                rows = _bytes.OrderByDescending(static row => row.Value).ToArray();
                _bytes.Clear();
            }

            var total = Math.Max(1L, rows.Sum(static row => row.Value));
            var lines = new List<string> { $"== {scenario}: {bytesPerCall:F1} B/call" };
            lines.AddRange(rows.Take(40).Select(row => $"  {100.0 * row.Value / total,5:F1}%  {bytesPerCall * row.Value / total,7:F1} B/call  {row.Key}"));
            lock (typeof(AllocationSurvey))
            {
                File.AppendAllLines(path, lines);
            }
        }
    }

    // Bytes per call of work(), warm, minimum over two rounds; between rounds of AllocationFlushEvery calls the
    // batch is flushed outside the count.
    private static double MeasureAllocatedBytes(PresenterUnderTest presenter, Action work, AllocationSurvey? survey)
    {
        return presenter.Run(() =>
        {
            for (var index = 0; index < AllocationWarmup; index++)
            {
                work();
                if (index % AllocationFlushEvery == AllocationFlushEvery - 1) presenter.InvokeMethod("FlushBatchedGuestCommands");
            }

            if (AllocationSurveyWaitSeconds > 0) Thread.Sleep(AllocationSurveyWaitSeconds * 1000);
            var best = double.MaxValue;
            for (var round = 0; round < 2; round++)
            {
                long bytes = 0;
                survey?.Sample(true);
                for (var index = 0; index < AllocationCalls; index++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    work();
                    bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    if (index % AllocationFlushEvery == AllocationFlushEvery - 1)
                    {
                        survey?.Sample(false);
                        presenter.InvokeMethod("FlushBatchedGuestCommands");
                        survey?.Sample(true);
                    }
                }

                survey?.Sample(false);
                best = Math.Min(best, bytes / (double)AllocationCalls);
            }

            presenter.InvokeMethod("FlushBatchedGuestCommands");
            if (AllocationSurveyWaitSeconds > 0) Thread.Sleep(AllocationSurveyWaitSeconds * 1000);
            return best;
        });
    }

    // The first programs of the fixed provider, returned again on every draw: the cache hands back the same objects
    // for an unchanged draw, so the provider's own allocations stay out of the count.
    private sealed class CachedProgramProvider(IShaderPipelineProvider inner) : IShaderPipelineProvider
    {
        private GraphicsPrograms? _programs;
        private PipelineHandle? _pipeline;

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface,
            ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive, bool depthBound) =>
            _programs ??= inner.GetGraphicsPrograms(vertex, pixel, shaderInterface, context, targetExportMapping, pixelActive, depthBound);

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth, VertexInputInfo vertexInput,
            PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering, PrimitiveTopology topology, bool primitiveRestartEnabled,
            bool disableBlending, ShaderProgram vertexProgram, ShaderProgram pixelProgram) =>
            _pipeline ??= inner.CreateGraphicsPipeline(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled,
                disableBlending, vertexProgram, pixelProgram);

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator,
            uint dimensionX, uint dimensionY, uint dimensionZ) =>
            inner.GetComputeProgram(compute, shaderInterface, dispatchInitiator, dimensionX, dimensionY, dimensionZ);

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) => inner.CreateComputePipeline(input, program);
    }

    [Fact]
    public void WarmDraw_AllocatesLittlePerDraw()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var executor = new RenderExecutor(presenter.RenderHost,
            new CachedProgramProvider(new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices)));
        var banks = Banks(RegisterWords.Color(target, Size, Size));
        var draw = Draw();
        using var survey = AllocationSurvey.Start();
        GuestGpuMemoryHook.Attach(harness.Gpu);
        double bytes;
        try
        {
            ulong submit = 1;
            bytes = MeasureAllocatedBytes(presenter, () => executor.DrawAuto(submit++, banks, draw), survey);
            harness.Finish();
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        survey?.Write("draw (fixed programs, one color target, one vertex buffer)", bytes);
        harness.Shutdown();
        Console.Error.WriteLine($"[ALLOC] draw {bytes:F1} B/call");
        // Before the pools: 952 B per draw. What stays is the stream ring's retention handle (32 B).
        Assert.True(bytes <= 64, $"{bytes:F1} B per draw");
    }

    // dirty: the CPU rewrites every bound buffer before each preparation (the dirty-page uploads); formatted: the
    // buffers are texel buffers.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WarmPrepareAndBind_AllocatesLittlePerStage(bool dirty, bool formatted)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var textures = harness.MapBacked(0x20000, ReadWrite);
        var buffers = harness.MapBacked(0x10000, ReadWrite);
        var sampled = new ImageResource
        {
            Dimension = ImageDimension.Dim2D,
            ResourceClass = global::SharpEmu.ShaderCompiler.Resources.ImageResourceClass.Sampled,
            NumericClass = ImageNumericClass.Float,
            Read = true,
        };
        var info = new ShaderResourceInfo
        {
            Images = [sampled, sampled],
            Samplers = [new SamplerResource()],
            Buffers =
            [
                new BufferResource { Read = true, Formatted = formatted, MaxByteExtent = 0x100 },
                new BufferResource { Read = true, Formatted = formatted, MaxByteExtent = 0x100 },
                new BufferResource { Read = true, Formatted = formatted, MaxByteExtent = 0x100 },
                new BufferResource { Read = true, Written = true, MaxByteExtent = 0x100 },
            ],
        };
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Pixel, 0x5A5A, info, userDataRegisters: [0, 1, 2, 3]);
        uint[] Buffer(ulong offset) => [(uint)(buffers + offset), (uint)((buffers + offset) >> 32), 0x100, 0];
        var snapshot = new ResourceSnapshot
        {
            Images =
            [
                RegisterWords.Texture(textures, GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64),
                RegisterWords.Texture(textures + 0x10000, GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64),
            ],
            Samplers = [[0, 0, 0, 0]],
            Buffers = [Buffer(0), Buffer(0x1000), Buffer(0x2000), Buffer(0x3000)],
            UserData = [1, 2, 3, 4],
        };
        var stage = new ShaderStageResources(program, snapshot);
        var payload = new byte[0x100];
        using var survey = AllocationSurvey.Start();
        GuestGpuMemoryHook.Attach(harness.Gpu);
        double bytes;
        try
        {
            bytes = MeasureAllocatedBytes(presenter, () =>
            {
                if (dirty)
                {
                    payload[0]++;
                    for (var index = 0; index < 3; index++)
                    {
                        harness.Memory.TryWrite(buffers + (ulong)index * 0x1000, payload);
                    }
                }

                using (presenter.RenderHost.BeginPreparation())
                {
                    var prepared = presenter.RenderHost.PrepareBindings(stage);
                    presenter.RenderHost.BindResources(prepared);
                }

                presenter.RenderHost.ResetBindings();
            }, survey);
            harness.Finish();
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        survey?.Write($"prepare + bind (2 textures, 1 sampler, 4 buffers, 4 user data) dirty={dirty} formatted={formatted}", bytes);
        harness.Shutdown();
        Console.Error.WriteLine($"[ALLOC] prepare+bind dirty={dirty} formatted={formatted} {bytes:F1} B/call");
        // Before the pools: ~2090 B per stage (684 B per image binding). What stays is the retention handle and the
        // release closure of a preparation closed without a commit (the test commits nothing; a draw does).
        Assert.True(bytes <= 160, $"{bytes:F1} B per prepare + bind");
    }
}
