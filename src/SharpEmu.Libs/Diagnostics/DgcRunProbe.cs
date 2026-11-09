// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_DGC_RUN_PROBE=1 (off by default): how many draws in a row VK_EXT_device_generated_commands could
// generate on the GPU from one vkCmdExecuteGeneratedCommandsEXT (sharpemu-rediseno-draws.md §13.6), in shadow. The
// commands the render thread records into its scheduler command buffer go through VkRecord in recording order; this
// splits the guest draws among them into maximal runs, the longest a single generated-commands call could cover, under
// two scenarios:
//
//   S0 strict (per the spec): within a run only the pipeline (one indirect execution set: identical static and dynamic
//      state, compatible layout, same stages and fragment output interface, VUID-11040/11039/11147/11152/11098/11086/
//      11085), push constants, vertex and index buffers and the draw parameters vary. Any dynamic state change, a
//      layout change (structural; descriptors assumed bindless), a different draw command (one indirect commands layout
//      has one action token), a rendering scope boundary, a barrier, a transfer, a dispatch, a query, an emulator draw
//      or the end of the command buffer cuts the run.
//   S1 optimistic: as S0, but viewport, scissor, depth bias (and its enable), stencil reference and blend constants go
//      to push constants or the shader and no longer cut, nor does a layout change.
//   S2 ceiling (not a DGC scenario): as S1, but no pipeline, dynamic state or draw command difference cuts either; only
//      what no generated-commands design removes (scopes, barriers, transfers, dispatches, queries, flushes).
//
// A cut carries every cause present (the events since the previous guest draw, and what differs from the run's first
// draw); the line reports the main cause by priority (dispatch, transfer, readback, flush, dependency, targets, pass,
// game barrier, emulator barrier, query, other, pipeline stages / fragment interface / state, dynamic state, S0-only
// dynamic state, layout, draw command), every cause present, and the cuts whose only cause it is. A dependency is the
// scope end and shader-write barrier RenderRecorder records after a draw with WriteStages. "pass" is a scope that ends
// and begins again on the same views and area with every attachment loaded (fusable); a scope on other views, or one
// that clears, is "targets". Barriers are split by BarrierSource: Global and Requested are the game's (ACQUIRE_MEM,
// cache EVENT_WRITE, RELEASE_MEM), the rest the emulator's. Vertex strides are left out of the pipeline state: the
// vertex buffer token needs VK_DYNAMIC_STATE_VERTEX_INPUT_BINDING_STRIDE, an assumption the line does not check.
//
// One [PERF][DGC_RUN] line per window (SHARPEMU_DGC_RUN_REPORT_S, 5 by default):
//
//   [PERF][DGC_RUN] window_s=5.0 frames=34 draws_per_frame=4012 S0=runs_pf:2000,mean:2.0,p50:1,p90:4,in8:12.0
//       S0_main=... S0_any=... S0_sole=... S1=... S1_main=... S1_any=... S1_sole=... pd_changed=99.9 unknown_pipe=0.0
//       pipe_variant=1.2 emu_draws_pf=3 barriers_pf=Global:10,Image:30 off_main=0 ns_per_event=40
//
// runs_pf = runs closed per frame, mean = draws per closed run, in8 = % of the draws of closed runs in runs of 8 or more,
// *_main/*_any/*_sole = % of the cuts. pd_changed = % of the draws after the first whose push descriptors or descriptor
// sets were written since the previous draw (a cut without bindless); pipe_variant = % of the pipeline cuts between two
// pipelines of one description (the rectangle list variants); *_pstate = % of the pipeline state cuts (both pipelines
// known) where each piece differs (StatePartNames).
internal static class DgcRunProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DGC_RUN_PROBE") == "1";

    private static readonly long ReportTicks = (long)((double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DGC_RUN_REPORT_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 5.0) * Stopwatch.Frequency);

    [Flags]
    internal enum Cut : uint
    {
        None = 0,
        Dispatch = 1u << 0,
        Transfer = 1u << 1,
        Readback = 1u << 2,
        Flush = 1u << 3,
        Dependency = 1u << 4,
        Targets = 1u << 5,
        Pass = 1u << 6,
        BarrierGame = 1u << 7,
        BarrierEmu = 1u << 8,
        Query = 1u << 9,
        Other = 1u << 10,
        PipeStages = 1u << 11,
        PipeFrag = 1u << 12,
        PipeState = 1u << 13,
        DynState = 1u << 14,
        DynState0 = 1u << 15,
        Layout = 1u << 16,
        DrawType = 1u << 17,
    }

    internal const int CutCount = 18;
    internal const Cut AnyPipe = Cut.PipeStages | Cut.PipeFrag | Cut.PipeState;
    internal const Cut S1Ignored = Cut.DynState0 | Cut.Layout;
    internal const Cut S2Ignored = S1Ignored | AnyPipe | Cut.DynState | Cut.DrawType;

    internal static readonly string[] CutNames =
        ["disp", "xfer", "rdbk", "flush", "dep", "tgt", "pass", "bgame", "bemu", "query", "other", "pstage", "pfrag", "pstate", "dyn", "dyn0", "layout", "dtype"];

    internal enum DrawType : byte
    {
        Draw,
        Indexed,
        Indirect,
        IndexedIndirect,
        Mesh,
        MeshIndirect,
    }

    // ---- Pipelines and layouts, registered when they are created (any thread).

    internal sealed class PipeInfo
    {
        public ulong Stages;
        public ulong Frag;
        public ulong State;
        public ulong Layout;
        public ulong Family;
        public ulong[] Parts = []; // the pieces of State, StatePartNames
    }

    // What a pipeline state cut is made of: blend and color masks, depth and stencil, rasterization (clip, samples,
    // cull, face), topology and restart, attachment formats, the dynamic state list, the vertex input.
    internal static readonly string[] StatePartNames = ["blend", "ds", "rast", "topo", "fmt", "dynlist", "vtx"];

    private static readonly ConcurrentDictionary<ulong, PipeInfo> _pipelines = new();
    private static readonly ConcurrentDictionary<ulong, ulong> _layouts = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(ref ulong hash, ulong value) => RepeatProbe.Add(ref hash, value);

    private static ulong HashOf<T>(ReadOnlySpan<T> values) where T : unmanaged =>
        XxHash3.HashToUInt64(MemoryMarshal.AsBytes(values)) ^ (ulong)values.Length;

    // The structure of a pipeline layout: its bindings, push constant stages and sets (handles differ per pipeline).
    internal static void RegisterLayout(ulong layout, ReadOnlySpan<DescriptorSetLayoutBinding> bindings, ShaderStageFlags pushStages, bool bindlessHeap,
        bool pushDescriptors)
    {
        var hash = RepeatProbe.Mix(0x1A70);
        Add(ref hash, (ulong)bindings.Length);
        foreach (var binding in bindings)
        {
            Add(ref hash, binding.Binding | ((ulong)(uint)binding.DescriptorType << 32));
            Add(ref hash, binding.DescriptorCount | ((ulong)(uint)binding.StageFlags << 32));
        }

        Add(ref hash, (ulong)(uint)pushStages | (bindlessHeap ? 1UL << 32 : 0) | (pushDescriptors ? 1UL << 33 : 0));
        _layouts[layout] = hash;
    }

    internal static void RegisterPipeline(ulong pipeline, ulong layout, ulong stages, ulong frag, ulong state, ulong family, ulong[]? parts = null)
    {
        _pipelines[pipeline] = new PipeInfo
        {
            Stages = stages,
            Frag = frag,
            State = state,
            Layout = _layouts.TryGetValue(layout, out var structure) ? structure : layout,
            Family = family,
            Parts = parts ?? [],
        };
    }

    // The keys of one graphics pipeline as CreateRenderPipeline builds it.
    internal static void RegisterGraphicsPipeline(ulong pipeline, ulong layout, Gpu.Pipelines.GraphicsPipelineDescription description, PrimitiveTopology topology,
        PolygonMode polygonMode, bool geometryStage, bool dynamicDepthBounds, ReadOnlySpan<DynamicState> dynamicStates,
        ReadOnlySpan<VertexInputAttributeDescription> attributes, ReadOnlySpan<VertexInputBindingDescription> bindings)
    {
        var mesh = description.VertexStage.Stage == Gpu.Rendering.ShaderStageKind.Mesh;
        var pixel = description.PixelStage is not null;
        var stages = RepeatProbe.Mix(0x57A6);
        Add(ref stages, (mesh ? 1UL : 0) | (description.Tessellation is not null ? 2UL : 0) | (geometryStage ? 4UL : 0) | (pixel ? 8UL : 0));

        Span<byte> parameters = stackalloc byte[Gpu.Pipelines.PipelineStaticParameters.ByteSize];
        description.StaticParameters.Bytes.CopyTo(parameters);
        Normalize(parameters, dynamicDepthBounds);
        var colorCount = Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(parameters[55..]), 8u);

        var frag = RepeatProbe.Mix(0xF4A6);
        if (pixel && description.PixelInfo is { } info)
        {
            for (var slot = 0; slot < colorCount && slot < info.TargetOutputModes.Length; slot++)
            {
                // only the slots written: the output interface of a slot without a write mask can be declared the same
                Add(ref frag, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(parameters[(59 + slot * 4)..]) == 0 ? 0xFFUL : info.TargetOutputModes[slot]);
            }

            Add(ref frag, (info.DepthExportEnable ? 1UL : 0) | (info.SampleMaskExportEnable ? 2UL : 0));
        }

        var parts = new ulong[StatePartNames.Length];
        parts[0] = HashOf<byte>(parameters.Slice(55, 36)) ^ RepeatProbe.Mix(HashOf<byte>(parameters.Slice(94, 72))); // count, masks; blend
        parts[1] = HashOf<byte>(parameters.Slice(12, 43));
        parts[2] = HashOf<byte>(parameters.Slice(0, 2)) ^ RepeatProbe.Mix(HashOf<byte>(parameters.Slice(7, 5))) ^
            RepeatProbe.Mix(RepeatProbe.Mix(HashOf<byte>(parameters.Slice(91, 3))) ^ (ulong)(uint)polygonMode);
        parts[3] = HashOf<byte>(parameters.Slice(2, 5)) ^ RepeatProbe.Mix((uint)topology);
        var rendering = description.Rendering;
        var formats = RepeatProbe.Mix(0xF0F0);
        Add(ref formats, rendering.ColorCount | ((ulong)(uint)rendering.DepthFormat << 32));
        Add(ref formats, (uint)rendering.StencilFormat);
        Add(ref formats, HashOf<Format>(rendering.ColorFormats.AsSpan(0, (int)Math.Min(rendering.ColorCount, (uint)rendering.ColorFormats.Length))));
        parts[4] = formats;
        parts[5] = HashOf(dynamicStates);
        var vertex = RepeatProbe.Mix(0x7E57);
        Add(ref vertex, (ulong)attributes.Length);
        foreach (var attribute in attributes)
        {
            Add(ref vertex, attribute.Location | ((ulong)attribute.Binding << 32));
            Add(ref vertex, (uint)attribute.Format | ((ulong)attribute.Offset << 32));
        }

        Add(ref vertex, (ulong)bindings.Length);
        foreach (var binding in bindings)
        {
            Add(ref vertex, binding.Binding | ((ulong)(uint)binding.InputRate << 32)); // no stride: dynamic with the VB token
        }

        parts[6] = vertex;
        var state = RepeatProbe.Mix(0x57A7);
        foreach (var part in parts)
        {
            Add(ref state, part);
        }

        RegisterPipeline(pipeline, layout, stages, frag, state, (ulong)(uint)RuntimeHelpers.GetHashCode(description), parts);
    }

    // Zeroes what the pipeline ignores, so two pipelines that differ only there count as one state (the emulator builds
    // them and could write the same values): bounds while dynamic or off, stencil operations with the test off, the
    // slots past the color count, the blend of a slot not written or not blended, the alpha blend that follows color.
    internal static void Normalize(Span<byte> parameters, bool dynamicDepthBounds)
    {
        if (dynamicDepthBounds || parameters[13] == 0)
        {
            parameters.Slice(14, 8).Clear();
        }

        if (parameters[22] == 0)
        {
            parameters.Slice(23, 32).Clear();
        }

        var colorCount = Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(parameters[55..]), 8u);
        for (var slot = 0; slot < 8; slot++)
        {
            var mask = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(parameters[(59 + slot * 4)..]);
            if (slot >= colorCount)
            {
                parameters.Slice(59 + slot * 4, 4).Clear();
                mask = 0;
            }

            var blended = parameters[150 + slot] != 0 && parameters[158 + slot] == 0;
            if (mask == 0 || !blended)
            {
                for (var field = 94; field <= 158; field += 8)
                {
                    parameters[field + slot] = 0;
                }
            }
            else if (parameters[142 + slot] == 0)
            {
                parameters[118 + slot] = parameters[126 + slot] = parameters[134 + slot] = 0;
            }
        }
    }

    // ---- The dynamic state shadow.

    // The state S1 moves to push constants or the shader.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Dyn0
    {
        public Viewport Viewport;
        public Rect2D Scissor;
        public float BiasConstant;
        public float BiasClamp;
        public float BiasSlope;
        public uint BiasEnable;
        public uint StencilReferenceFront;
        public uint StencilReferenceBack;
        public float Blend0;
        public float Blend1;
        public float Blend2;
        public float Blend3;
    }

    // The rest of the dynamic state: it cuts in both scenarios.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Dyn1
    {
        public float LineWidth;
        public uint DepthTest;
        public uint DepthWrite;
        public uint DepthCompare;
        public float BoundsMin;
        public float BoundsMax;
        public uint StencilCompareFront;
        public uint StencilCompareBack;
        public uint StencilWriteFront;
        public uint StencilWriteBack;
        public uint ColorWriteCount;
        public uint ColorWriteMask;
    }

    private static bool Same<T>(in T left, in T right) where T : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in left)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in right)));

    // ---- Runs of one scenario.

    internal sealed class Scenario(Cut allowed)
    {
        public const int MaxLength = 4096;
        public readonly Cut Allowed = allowed;
        public int Length;
        public PipeInfo? AnchorPipe;
        public ulong AnchorHandle;
        public DrawType AnchorType;
        public readonly long[] Lengths = new long[MaxLength + 1];
        public long Runs;
        public long Draws;
        public long DrawsIn8;
        public long PipeCuts;
        public long PipeVariants;
        public long StateCuts;
        public readonly long[] StateParts = new long[StatePartNames.Length];
        public readonly long[] Main = new long[CutCount];
        public readonly long[] Any = new long[CutCount];
        public readonly long[] Sole = new long[CutCount];

        // The draw's causes against the run's first draw (pipeline, draw command), with the common ones; true when it cuts.
        public bool Draw(Cut common, DrawType type, PipeInfo? pipe, ulong handle)
        {
            if (Length == 0)
            {
                Start(type, pipe, handle);
                return false;
            }

            var mask = common;
            if (type != AnchorType)
            {
                mask |= Cut.DrawType;
            }

            if (handle != AnchorHandle)
            {
                if (pipe is null || AnchorPipe is null)
                {
                    mask |= Cut.PipeState;
                }
                else
                {
                    if (pipe.Stages != AnchorPipe.Stages) mask |= Cut.PipeStages;
                    if (pipe.Frag != AnchorPipe.Frag) mask |= Cut.PipeFrag;
                    if (pipe.State != AnchorPipe.State) mask |= Cut.PipeState;
                    if (pipe.Layout != AnchorPipe.Layout) mask |= Cut.Layout;
                }
            }

            mask &= Allowed;
            if (mask == Cut.None)
            {
                Length++;
                return false;
            }

            if ((mask & AnyPipe) != 0)
            {
                PipeCuts++;
                if (pipe is not null && AnchorPipe is not null && pipe.Family == AnchorPipe.Family)
                {
                    PipeVariants++;
                }
            }

            if ((mask & Cut.PipeState) != 0 && pipe is not null && AnchorPipe is not null &&
                pipe.Parts.Length == StatePartNames.Length && AnchorPipe.Parts.Length == StatePartNames.Length)
            {
                StateCuts++;
                for (var index = 0; index < StatePartNames.Length; index++)
                {
                    if (pipe.Parts[index] != AnchorPipe.Parts[index]) StateParts[index]++;
                }
            }

            Close(mask);
            Start(type, pipe, handle);
            return true;
        }

        private void Start(DrawType type, PipeInfo? pipe, ulong handle)
        {
            Length = 1;
            AnchorType = type;
            AnchorPipe = pipe;
            AnchorHandle = handle;
        }

        private void Close(Cut mask)
        {
            Lengths[Math.Min(Length, MaxLength)]++;
            Runs++;
            Draws += Length;
            if (Length >= 8)
            {
                DrawsIn8 += Length;
            }

            Main[MainCause(mask)]++;
            var bits = (uint)mask;
            for (var index = 0; index < CutCount; index++)
            {
                if ((bits & (1u << index)) != 0)
                {
                    Any[index]++;
                }
            }

            if ((bits & (bits - 1)) == 0)
            {
                Sole[BitIndex(bits)]++;
            }

            Length = 0;
        }

        // The smallest run length with at least fraction of the closed runs at or under it.
        public int Percentile(double fraction)
        {
            if (Runs == 0)
            {
                return 0;
            }

            var wanted = Math.Max((long)Math.Ceiling(Runs * fraction), 1);
            var seen = 0L;
            for (var length = 1; length <= MaxLength; length++)
            {
                seen += Lengths[length];
                if (seen >= wanted)
                {
                    return length;
                }
            }

            return MaxLength;
        }

        public void ClearWindow()
        {
            Array.Clear(Lengths);
            Array.Clear(Main);
            Array.Clear(Any);
            Array.Clear(Sole);
            Array.Clear(StateParts);
            Runs = Draws = DrawsIn8 = PipeCuts = PipeVariants = StateCuts = 0;
        }

        public void Reset()
        {
            ClearWindow();
            Length = 0;
            AnchorPipe = null;
            AnchorHandle = 0;
        }
    }

    // The bits are numbered in priority order: the lowest set one is the main cause.
    internal static int MainCause(Cut mask) => BitIndex((uint)mask);

    private static int BitIndex(uint bits) => System.Numerics.BitOperations.TrailingZeroCount(bits);

    // ---- The splitter: the events of the render thread's command buffer, in recording order.

    internal sealed class Splitter
    {
        public Cut Gap;
        public bool Storing;
        public bool DescriptorsWritten;
        public ulong BoundPipeline;
        public Dyn0 Dyn0;
        public Dyn1 Dyn1;
        private Dyn0 _previousDyn0;
        private Dyn1 _previousDyn1;
        private bool _haveDraw;
        private bool _haveScope;
        private bool _endedByStoring;
        private ulong _scopeHash;
        public readonly Scenario S0 = new((Cut)uint.MaxValue);
        public readonly Scenario S1 = new((Cut)uint.MaxValue & ~S1Ignored);
        public readonly Scenario S2 = new((Cut)uint.MaxValue & ~S2Ignored);
        public long Draws;
        public long ComparedDraws;
        public long DescriptorDraws;
        public long UnknownPipe;
        public long EmulatorDraws;
        public readonly long[] Barriers = new long[(int)Gpu.Vulkan.BarrierSource.Count];

        public void Note(Cut cause) => Gap |= Storing ? Cut.Dependency : cause;

        public void Barrier(Gpu.Vulkan.BarrierSource source)
        {
            Barriers[(int)source]++;
            Note(source is Gpu.Vulkan.BarrierSource.Global or Gpu.Vulkan.BarrierSource.Requested ? Cut.BarrierGame : Cut.BarrierEmu);
        }

        public void EndRendering()
        {
            if (Storing)
            {
                _endedByStoring = true;
            }
        }

        // A render pass (the emulator's only VkRenderPass): the next scope is on other targets.
        public void RenderPass()
        {
            Note(Cut.Other);
            _scopeHash = 0;
        }

        public void BeginRendering(ulong hash, bool allLoad)
        {
            if (_haveScope)
            {
                if (hash != _scopeHash || !allLoad)
                {
                    Gap |= Cut.Targets;
                }
                else
                {
                    Gap |= _endedByStoring ? Cut.Dependency : Cut.Pass;
                }
            }

            _haveScope = true;
            _scopeHash = hash;
            _endedByStoring = false;
        }

        public void Draw(DrawType type, PipeInfo? pipe, ulong handle)
        {
            Draws++;
            if (pipe is null)
            {
                UnknownPipe++;
            }

            var common = Gap;
            if (_haveDraw)
            {
                ComparedDraws++;
                if (DescriptorsWritten) DescriptorDraws++;
                if (!Same(in Dyn1, in _previousDyn1)) common |= Cut.DynState;
                if (!Same(in Dyn0, in _previousDyn0)) common |= Cut.DynState0;
            }

            S0.Draw(common, type, pipe, handle);
            S1.Draw(common, type, pipe, handle);
            S2.Draw(common, type, pipe, handle);
            _previousDyn0 = Dyn0;
            _previousDyn1 = Dyn1;
            _haveDraw = true;
            Gap = Cut.None;
            DescriptorsWritten = false;
        }

        public void ClearWindow()
        {
            S0.ClearWindow();
            S1.ClearWindow();
            S2.ClearWindow();
            Draws = ComparedDraws = DescriptorDraws = UnknownPipe = EmulatorDraws = 0;
            Array.Clear(Barriers);
        }

        public void Reset()
        {
            S0.Reset();
            S1.Reset();
            S2.Reset();
            ClearWindow();
            Gap = Cut.None;
            Storing = DescriptorsWritten = _haveDraw = _haveScope = _endedByStoring = false;
            BoundPipeline = _scopeHash = 0;
            Dyn0 = default;
            Dyn1 = default;
            _previousDyn0 = default;
            _previousDyn1 = default;
        }
    }

    // ---- The render thread's state.

    private static readonly Splitter _splitter = new();
    private static int _owner;
    private static nint _main;
    private static bool _guestDraw;
    private static long _frames;
    private static long _windowStart;
    private static long _offMain;
    private static long _events;
    private static long _eventTicks;
    [ThreadStatic] internal static int t_barrierSource; // BarrierSource + 1 while VulkanSynchronization records one

    internal static Splitter SplitterForTests => _splitter;

    internal static void NoteFrame()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _frames);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Mine(CommandBuffer command) =>
        _owner != 0 && command.Handle == _main && Environment.CurrentManagedThreadId == _owner;

    // The scheduler's command buffer begins (VulkanTickDevice) on the render thread.
    internal static void NoteBufferBegin(nint buffer)
    {
        if (_owner == 0 || Environment.CurrentManagedThreadId == _owner)
        {
            _main = buffer;
        }
    }

    internal static void NoteBufferEnd(nint buffer, bool hostRead)
    {
        if (_owner != 0 && buffer == _main && Environment.CurrentManagedThreadId == _owner)
        {
            _splitter.Gap |= hostRead ? Cut.Readback : Cut.Flush;
        }
    }

    // RenderRecorder around the emission of one guest draw: the draw commands in between are the guest's.
    internal static void BeginGuestDraw()
    {
        if (_owner == 0)
        {
            _owner = Environment.CurrentManagedThreadId;
            _windowStart = Stopwatch.GetTimestamp();
        }

        if (Environment.CurrentManagedThreadId == _owner)
        {
            _guestDraw = true;
        }
    }

    internal static void EndGuestDraw()
    {
        if (Environment.CurrentManagedThreadId == _owner)
        {
            _guestDraw = false;
        }
    }

    // RenderRecorder around the scope end and shader-write barrier after a draw that stores to memory.
    internal static void BeginStoring()
    {
        if (Environment.CurrentManagedThreadId == _owner)
        {
            _splitter.Storing = true;
        }
    }

    internal static void EndStoring()
    {
        if (Environment.CurrentManagedThreadId == _owner)
        {
            _splitter.Storing = false;
        }
    }

    internal static void Note(CommandBuffer command, Cut cause)
    {
        if (Mine(command))
        {
            _splitter.Note(cause);
        }
    }

    internal static void Barrier(CommandBuffer command)
    {
        if (Mine(command))
        {
            var source = t_barrierSource;
            _splitter.Barrier(source == 0 ? Gpu.Vulkan.BarrierSource.Other : (Gpu.Vulkan.BarrierSource)(source - 1));
        }
    }

    internal static void BindPipeline(CommandBuffer command, PipelineBindPoint bindPoint, Pipeline pipeline)
    {
        if (bindPoint == PipelineBindPoint.Graphics && Mine(command))
        {
            _splitter.BoundPipeline = pipeline.Handle;
        }
    }

    internal static void DescriptorsWritten(CommandBuffer command, PipelineBindPoint bindPoint)
    {
        if (bindPoint == PipelineBindPoint.Graphics && Mine(command))
        {
            _splitter.DescriptorsWritten = true;
        }
    }

    internal static unsafe void BeginRendering(CommandBuffer command, RenderingInfo* info)
    {
        if (!Mine(command))
        {
            return;
        }

        var hash = RepeatProbe.Mix(0xBE61);
        Add(ref hash, (ulong)(uint)info->RenderArea.Offset.X | ((ulong)(uint)info->RenderArea.Offset.Y << 32));
        Add(ref hash, info->RenderArea.Extent.Width | ((ulong)info->RenderArea.Extent.Height << 32));
        Add(ref hash, info->LayerCount | ((ulong)info->ViewMask << 32));
        Add(ref hash, (uint)info->Flags | ((ulong)info->ColorAttachmentCount << 32));
        var allLoad = true;
        for (var index = 0u; index < info->ColorAttachmentCount; index++)
        {
            Attachment(ref hash, ref allLoad, info->PColorAttachments + index);
        }

        Add(ref hash, 0xD0);
        Attachment(ref hash, ref allLoad, info->PDepthAttachment);
        Add(ref hash, 0x5E);
        Attachment(ref hash, ref allLoad, info->PStencilAttachment);
        _splitter.BeginRendering(hash, allLoad);
    }

    private static unsafe void Attachment(ref ulong hash, ref bool allLoad, RenderingAttachmentInfo* attachment)
    {
        if (attachment == null || attachment->ImageView.Handle == 0)
        {
            Add(ref hash, 0);
            return;
        }

        Add(ref hash, attachment->ImageView.Handle);
        Add(ref hash, (uint)attachment->ImageLayout | ((ulong)(uint)attachment->ResolveMode << 32));
        Add(ref hash, attachment->ResolveImageView.Handle);
        if (attachment->LoadOp != AttachmentLoadOp.Load && (int)attachment->LoadOp != 1000400000) // 1000400000: VK_ATTACHMENT_LOAD_OP_NONE
        {
            allLoad = false;
        }
    }

    internal static void EndRendering(CommandBuffer command)
    {
        if (Mine(command))
        {
            _splitter.EndRendering();
        }
    }

    internal static void RenderPass(CommandBuffer command)
    {
        if (Mine(command))
        {
            _splitter.RenderPass();
        }
    }

    // A draw command: the guest's inside the RenderRecorder window, the emulator's otherwise.
    internal static void Draw(CommandBuffer command, DrawType type)
    {
        if (_owner == 0 || Environment.CurrentManagedThreadId != _owner)
        {
            return;
        }

        if (!_guestDraw)
        {
            if (command.Handle == _main)
            {
                _splitter.EmulatorDraws++;
                _splitter.Note(Cut.Other);
            }

            return;
        }

        var started = Stopwatch.GetTimestamp();
        if (command.Handle != _main)
        {
            _offMain++;
            _main = command.Handle; // the guest's buffer is the one its draws go to
            _splitter.Gap |= Cut.Flush;
        }

        var pipeline = _splitter.BoundPipeline;
        _pipelines.TryGetValue(pipeline, out var pipe);
        _splitter.Draw(type, pipe, pipeline);
        var now = Stopwatch.GetTimestamp();
        _events++;
        _eventTicks += now - started;
        if (now - _windowStart >= ReportTicks)
        {
            Report(now);
        }
    }

    // ---- Dynamic state.

    internal static unsafe void Viewport(CommandBuffer command, uint first, uint count, Viewport* viewports)
    {
        if (first == 0 && count != 0 && Mine(command)) _splitter.Dyn0.Viewport = viewports[0];
    }

    internal static unsafe void Scissor(CommandBuffer command, uint first, uint count, Rect2D* scissors)
    {
        if (first == 0 && count != 0 && Mine(command)) _splitter.Dyn0.Scissor = scissors[0];
    }

    internal static void LineWidth(CommandBuffer command, float width)
    {
        if (Mine(command)) _splitter.Dyn1.LineWidth = width;
    }

    internal static unsafe void BlendConstants(CommandBuffer command, float* constants)
    {
        if (!Mine(command)) return;
        ref var dyn = ref _splitter.Dyn0;
        dyn.Blend0 = constants[0];
        dyn.Blend1 = constants[1];
        dyn.Blend2 = constants[2];
        dyn.Blend3 = constants[3];
    }

    internal static void DepthTest(CommandBuffer command, uint enable)
    {
        if (Mine(command)) _splitter.Dyn1.DepthTest = enable;
    }

    internal static void DepthWrite(CommandBuffer command, uint enable)
    {
        if (Mine(command)) _splitter.Dyn1.DepthWrite = enable;
    }

    internal static void DepthCompare(CommandBuffer command, CompareOp compare)
    {
        if (Mine(command)) _splitter.Dyn1.DepthCompare = (uint)compare;
    }

    internal static void DepthBiasEnable(CommandBuffer command, uint enable)
    {
        if (Mine(command)) _splitter.Dyn0.BiasEnable = enable;
    }

    internal static void DepthBias(CommandBuffer command, float constant, float clamp, float slope)
    {
        if (!Mine(command)) return;
        ref var dyn = ref _splitter.Dyn0;
        dyn.BiasConstant = constant;
        dyn.BiasClamp = clamp;
        dyn.BiasSlope = slope;
    }

    internal static void DepthBounds(CommandBuffer command, float min, float max)
    {
        if (!Mine(command)) return;
        _splitter.Dyn1.BoundsMin = min;
        _splitter.Dyn1.BoundsMax = max;
    }

    internal static void StencilCompare(CommandBuffer command, StencilFaceFlags faces, uint mask)
    {
        if (!Mine(command)) return;
        if ((faces & StencilFaceFlags.FaceFrontBit) != 0) _splitter.Dyn1.StencilCompareFront = mask;
        if ((faces & StencilFaceFlags.FaceBackBit) != 0) _splitter.Dyn1.StencilCompareBack = mask;
    }

    internal static void StencilWrite(CommandBuffer command, StencilFaceFlags faces, uint mask)
    {
        if (!Mine(command)) return;
        if ((faces & StencilFaceFlags.FaceFrontBit) != 0) _splitter.Dyn1.StencilWriteFront = mask;
        if ((faces & StencilFaceFlags.FaceBackBit) != 0) _splitter.Dyn1.StencilWriteBack = mask;
    }

    internal static void StencilReference(CommandBuffer command, StencilFaceFlags faces, uint reference)
    {
        if (!Mine(command)) return;
        if ((faces & StencilFaceFlags.FaceFrontBit) != 0) _splitter.Dyn0.StencilReferenceFront = reference;
        if ((faces & StencilFaceFlags.FaceBackBit) != 0) _splitter.Dyn0.StencilReferenceBack = reference;
    }

    internal static unsafe void ColorWrite(CommandBuffer command, uint count, Silk.NET.Core.Bool32* enables)
    {
        if (!Mine(command)) return;
        var mask = 0u;
        for (var index = 0; index < count && index < 32; index++)
        {
            if (enables[index].Value != 0) mask |= 1u << index;
        }

        _splitter.Dyn1.ColorWriteCount = count;
        _splitter.Dyn1.ColorWriteMask = mask;
    }

    // ---- The report.

    private static void Report(long now)
    {
        var frames = Interlocked.Exchange(ref _frames, 0);
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (_splitter.Draws != 0)
        {
            var nsPerEvent = _events == 0 ? 0 : _eventTicks * 1e9 / Stopwatch.Frequency / _events;
            Console.Error.WriteLine(Report(_splitter, seconds, frames, _offMain, nsPerEvent));
        }

        _splitter.ClearWindow();
        _offMain = _events = _eventTicks = 0;
        _windowStart = Stopwatch.GetTimestamp();
    }

    internal static string Report(Splitter splitter, double seconds, long frames, long offMain, double nsPerEvent)
    {
        var perFrame = Math.Max(frames, 1);
        var line = new StringBuilder(1536);
        line.Append(FormattableString.Invariant(
            $"[PERF][DGC_RUN] window_s={seconds:F1} frames={frames} draws_per_frame={splitter.Draws / (double)perFrame:F0} "));
        AppendScenario(line, "S0", splitter.S0, perFrame);
        AppendScenario(line, "S1", splitter.S1, perFrame);
        AppendScenario(line, "S2", splitter.S2, perFrame);
        line.Append(FormattableString.Invariant(
            $"pd_changed={Percent(splitter.DescriptorDraws, splitter.ComparedDraws):F1} unknown_pipe={Percent(splitter.UnknownPipe, splitter.Draws):F1} "));
        line.Append(FormattableString.Invariant(
            $"pipe_variant_s0={Percent(splitter.S0.PipeVariants, splitter.S0.PipeCuts):F1} pipe_variant_s1={Percent(splitter.S1.PipeVariants, splitter.S1.PipeCuts):F1} "));
        line.Append(FormattableString.Invariant($"emu_draws_pf={splitter.EmulatorDraws / (double)perFrame:F1} barriers_pf="));
        var first = true;
        for (var index = 0; index < splitter.Barriers.Length; index++)
        {
            if (splitter.Barriers[index] == 0) continue;
            if (!first) line.Append(',');
            first = false;
            line.Append(((Gpu.Vulkan.BarrierSource)index).ToString()).Append(':')
                .Append((splitter.Barriers[index] / (double)perFrame).ToString("F1", CultureInfo.InvariantCulture));
        }

        if (first) line.Append('0');
        line.Append(FormattableString.Invariant($" off_main={offMain} ns_per_draw={nsPerEvent:F0}"));
        return line.ToString();
    }

    private static void AppendScenario(StringBuilder line, string name, Scenario scenario, long frames)
    {
        line.Append(FormattableString.Invariant(
            $"{name}=runs_pf:{scenario.Runs / (double)frames:F1},mean:{(scenario.Runs == 0 ? 0 : scenario.Draws / (double)scenario.Runs):F2},"));
        line.Append(FormattableString.Invariant(
            $"p50:{scenario.Percentile(0.5)},p90:{scenario.Percentile(0.9)},in8:{Percent(scenario.DrawsIn8, scenario.Draws):F1} "));
        AppendCauses(line, name + "_main", scenario.Main, scenario.Runs);
        AppendCauses(line, name + "_any", scenario.Any, scenario.Runs);
        AppendCauses(line, name + "_sole", scenario.Sole, scenario.Runs);
        line.Append(name).Append("_pstate=");
        for (var index = 0; index < StatePartNames.Length; index++)
        {
            if (index != 0) line.Append(',');
            line.Append(StatePartNames[index]).Append(':')
                .Append(Percent(scenario.StateParts[index], scenario.StateCuts).ToString("F1", CultureInfo.InvariantCulture));
        }

        line.Append(' ');
    }

    private static void AppendCauses(StringBuilder line, string name, long[] counts, long runs)
    {
        line.Append(name).Append('=');
        var first = true;
        for (var index = 0; index < CutCount; index++)
        {
            if (counts[index] == 0) continue;
            if (!first) line.Append(',');
            first = false;
            line.Append(CutNames[index]).Append(':').Append(Percent(counts[index], runs).ToString("F1", CultureInfo.InvariantCulture));
        }

        if (first) line.Append('0');
        line.Append(' ');
    }

    private static double Percent(long part, long whole) => whole <= 0 ? 0 : part * 100.0 / whole;

    // [local] For tests: the render thread's state and the window.
    internal static void ResetForTests()
    {
        _splitter.Reset();
        _owner = 0;
        _main = 0;
        _guestDraw = false;
        _frames = _offMain = _events = _eventTicks = 0;
        _pipelines.Clear();
        _layouts.Clear();
    }
}
