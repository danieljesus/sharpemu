// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_DRAW_PLAN (0 off by default, 1 on, 2 verify): stage 1 of the plan per draw shape (option B of
// sharpemu-rediseno-draws.md, §4.5): on the render thread, the state decisions of a draw are taken from the last draw
// of the same shape when the inputs they were made from are equal by value.
//
// The pipeline (RenderRecorder.TryResolvePipeline). The generic path keeps the previous draw's pipeline under its
// PipelineKey (the context and dynamic stamps, the target version, the bound slots, the vertex layout, the
// interpolators, the programs, the topology, restart and blending) with the rendering state, and otherwise asks
// ShaderPipelineCache, whose handle is a function of the GraphicsPipelineKey it fills from those same inputs. The
// plan keeps, per shape, the handle the generic path answered, and gives it again while the PipelineKey inputs are
// equal by value: the stamps become the value hashes of the context registers they cover (PlanGroupHashes, kept by
// RegisterBanks from the raw dwords written: ColorTargets, DepthTargets with the composite depth extent,
// ShaderInterface, PipelineState for the context stamp, ContextDynamic for the dynamic one); the target version is a
// function of the target, depth and shader-interface groups and of whether the pixel stage runs, which the shape
// holds; and the rendering state is compared in full, as the kept pipeline does. The shape (the dictionary key) is
// the gate's (PlanProbe.ShapeKey): the vertex and pixel program ids and hashes, the draw class (indexed, memory
// writes, pixel stage, depth target, blending off, unwritten targets dropped, solid clear), the color target count
// with the bound slot mask, the topology, restart, the vertex layout (PlanProbe.VertexLayout: strides, rates,
// attribute formats, registers, fetch slots and offsets, without bases and record counts) and the pixel interpolator
// settings, plus the pixel target output modes and sample shading (GraphicsPipelineKey reads both). The scalar parts
// are kept in the entry and compared again on a hit; the vertex layout and the pixel outputs by their 64-bit hashes.
// Graphics pipelines are never removed from the cache, so a kept handle stays valid.
//
// The target layout (RenderExecutor.ResolveTargetLayout). The executor keeps one resolved layout (the color target
// requests and slots, the depth target, whether the pixel stage runs) under the ColorTargets, DepthTargets and
// ShaderInterface stamps and the shader-program stamp. The plan also takes it when the stamps moved but the value
// hashes of those three groups (with the composite depth extent) are those it was resolved from and the pixel stage
// (HasActivePixelShader, which reads the shader-program registers) agrees. This one runs before the programs are
// resolved, so it is a single entry per value, not per shape.
//
// Covered: indexed and auto draws (record kind Draw) that are not mesh, tessellation or indirect, whose programs are
// not ray tracing or BVH builders (LiteObtainProbe.IsRtProgram). Everything else, and every other decision of a draw
// (target images, attachments, dynamic state, resources, descriptors, coherence), takes the generic path. The dynamic
// state is not planned: the front prepares it with the programs and the host sets only what changed since the last
// draw of its command buffer, so there is nothing exact to skip by shape. The shadow that keeps the hashes is on with
// this switch (PlanRegisterShadow.Enabled); register writes past the writers forget it (RegisterBanks.ForgetPlanHashes).
//
// =2 runs the generic path on every hit, uses its answer, and compares: the pipeline handle, and for the layout the
// requests, slots, depth target and pixel stage of a fresh resolution. A disagreement is printed as
// [GPU][DRAW_PLAN_WRONG] (the first 20) and counted; wrong must be 0.
//
// [PERF][DRAW_PLAN] mode=1 draws=N hits=N hit_pct=P miss=excluded:N,new:N,shape:N,targets:N,context:N,dynamic:N,
//     rendering:N shapes=N clears=N verified=N wrong=N wrong_total=N layout=stamp:N,value:N,resolve:N
//     layout_verified=N layout_wrong=N
// once per SHARPEMU_DRAWCOST_PROBE window, or every 5 s without it. draws = draws that reached the pipeline step;
// shapes = entries in the store (SHARPEMU_DRAW_PLAN_SHAPES, 4096 by default; a full store is cleared, counted as
// clears); layout counts the executor's layouts kept by stamp, kept by value, and resolved.
internal static class DrawPlan
{
    internal static readonly int DefaultMode = Environment.GetEnvironmentVariable("SHARPEMU_DRAW_PLAN") switch
    {
        "1" => 1,
        "2" => 2,
        _ => 0,
    };

    // [local] A test sets the mode for the recorders and executors it creates.
    internal static readonly AsyncLocal<int?> ModeOverride = new();

    // The mode a recorder or executor takes when it is created: off without the shadow the plans compare.
    internal static int CurrentMode => PlanRegisterShadow.Enabled ? ModeOverride.Value ?? DefaultMode : 0;

    internal static readonly int StoreCapacity =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DRAW_PLAN_SHAPES"), out var shapes) && shapes > 0 ? shapes : 4096;

    private const int DumpLimit = 20;

    internal enum Miss
    {
        Excluded,
        New,
        Shape,
        Targets,
        Context,
        Dynamic,
        Rendering,
        Count,
    }

    private static readonly string[] MissNames = ["excluded", "new", "shape", "targets", "context", "dynamic", "rendering"];

    // The window's counts: plain increments on the render thread (diagnostics), except the wrong total.
    internal static long Draws;
    internal static long Hits;
    internal static readonly long[] Misses = new long[(int)Miss.Count];
    internal static long Shapes;
    internal static long Clears;
    internal static long Verified;
    internal static long Wrong;
    internal static long LayoutStamp;
    internal static long LayoutValue;
    internal static long LayoutResolve;
    internal static long LayoutVerified;
    internal static long LayoutWrong;
    private static long _wrongTotal;
    private static int _dumps;
    private static long _lastReportTick = Environment.TickCount64;

    internal static long WrongTotal => Interlocked.Read(ref _wrongTotal);

    // A disagreement of the plan with the generic path; true for the first ones, which the caller prints.
    internal static bool NoteWrong(bool layout)
    {
        if (layout) LayoutWrong++;
        else Wrong++;
        Interlocked.Increment(ref _wrongTotal);
        return Interlocked.Increment(ref _dumps) <= DumpLimit;
    }

    // ---- The shape of a draw.

    internal struct Shape
    {
        public ulong Key;
        public ulong VertexProgram;
        public ulong PixelProgram;
        public ulong VertexHash;
        public ulong PixelHash;
        public ulong VertexLayout;
        public ulong PixelOutputs;
        public PlanProbe.DrawClass DrawClass;
        public uint ColorCount;
        public uint BoundSlots;
        public int Topology;
        public bool PrimitiveRestart;
    }

    // The pixel stage parts of GraphicsPipelineKey that are not context registers or the program: its interpolator
    // settings, target output modes and sample shading.
    internal static ulong PixelOutputs(PixelInputInfo? pixelInput)
    {
        if (pixelInput is null)
        {
            return 0;
        }

        var hash = XxHash3.HashToUInt64(MemoryMarshal.AsBytes(pixelInput.InterpolatorSettings.AsSpan())) ^ (ulong)pixelInput.InterpolatorSettings.Length;
        RepeatProbe.Add(ref hash, XxHash3.HashToUInt64(pixelInput.TargetOutputModes) ^ ((ulong)pixelInput.TargetOutputModes.Length << 32));
        RepeatProbe.Add(ref hash, pixelInput.SampleShading ? 0x5Au : 0xA5u);
        return hash;
    }

    // The shape of a draw the plan covers; false (excluded) otherwise. pixelInput is the active pixel stage's.
    internal static bool TryShape(ResolvedDraw record, in DrawState state, VertexInputInfo vertexInput, PixelInputInfo? pixelInput, out Shape shape)
    {
        shape = default;
        var programs = state.Programs;
        if (record.Kind != ResolvedDrawKind.Draw || programs.MeshInput is not null || programs.Tessellation is not null ||
            vertexInput.Tessellation is not null || record.Emission.IndirectArgumentsAddress != 0)
        {
            return false;
        }

        var vertexHash = vertexInput.Stage.Program?.Hash ?? 0;
        var pixelHash = pixelInput?.Stage.Program?.Hash ?? 0;
        if (LiteObtainProbe.IsRtProgram(vertexHash) || LiteObtainProbe.IsRtProgram(pixelHash))
        {
            return false;
        }

        var drawClass = (record.Emission.Indexed ? PlanProbe.DrawClass.Indexed : 0) | (record.WritesMemory ? PlanProbe.DrawClass.WritesMemory : 0) |
            (state.PixelActive ? PlanProbe.DrawClass.PixelActive : 0) | (state.Depth.HasTarget ? PlanProbe.DrawClass.DepthTarget : 0) |
            (programs.DisableBlending ? PlanProbe.DrawClass.DisableBlending : 0) |
            (programs.DropUnwrittenColorTargets ? PlanProbe.DrawClass.DropUnwrittenTargets : 0) |
            (programs.SolidClear is not null ? PlanProbe.DrawClass.SolidClear : 0);
        var boundSlots = 0u;
        for (var index = 0; index < state.ColorCount; index++)
        {
            boundSlots |= 1u << (int)state.Colors[index].Slot;
        }

        shape.VertexProgram = programs.Vertex.Id;
        shape.PixelProgram = programs.Pixel.Id;
        shape.VertexHash = vertexHash;
        shape.PixelHash = pixelHash;
        shape.VertexLayout = PlanProbe.VertexLayout(vertexInput);
        shape.PixelOutputs = PixelOutputs(pixelInput);
        shape.DrawClass = drawClass;
        shape.ColorCount = state.ColorCount;
        shape.BoundSlots = boundSlots;
        shape.Topology = (int)record.Topology;
        shape.PrimitiveRestart = record.PrimitiveRestart;
        var key = PlanProbe.ShapeKey(shape.VertexProgram, shape.PixelProgram, vertexHash, pixelHash, 0, drawClass, shape.ColorCount, boundSlots,
            shape.Topology, shape.PrimitiveRestart, shape.VertexLayout, []);
        RepeatProbe.Add(ref key, shape.PixelOutputs);
        shape.Key = key;
        return true;
    }

    // ---- The store: one entry per shape, on the render thread.

    internal sealed class Entry
    {
        public bool Valid;
        public Shape Shape;
        public PlanGroupHashes Groups;
        public RenderingState Rendering;
        public PipelineHandle Handle;
        public ContextRegisters? Context; // =2 only: the dumps' context diff
    }

    internal static bool SameShape(in Shape left, in Shape right) =>
        left.Key == right.Key && left.VertexProgram == right.VertexProgram && left.PixelProgram == right.PixelProgram &&
        left.VertexHash == right.VertexHash && left.PixelHash == right.PixelHash && left.VertexLayout == right.VertexLayout &&
        left.PixelOutputs == right.PixelOutputs && left.DrawClass == right.DrawClass && left.ColorCount == right.ColorCount &&
        left.BoundSlots == right.BoundSlots && left.Topology == right.Topology && left.PrimitiveRestart == right.PrimitiveRestart;

    // Why an entry's pipeline does not answer a draw of its shape; null when it does.
    internal static Miss? Compare(Entry entry, in Shape shape, in PlanGroupHashes groups, in RenderingState rendering)
    {
        if (!entry.Valid)
        {
            return Miss.New;
        }

        if (!SameShape(in entry.Shape, in shape))
        {
            return Miss.Shape;
        }

        ref readonly var kept = ref entry.Groups;
        if (kept.ColorTargets != groups.ColorTargets || kept.DepthTargets != groups.DepthTargets)
        {
            return Miss.Targets;
        }

        if (kept.ShaderInterface != groups.ShaderInterface || kept.PipelineState != groups.PipelineState)
        {
            return Miss.Context;
        }

        if (kept.ContextDynamic != groups.ContextDynamic)
        {
            return Miss.Dynamic;
        }

        return entry.Rendering.Equals(rendering) ? null : Miss.Rendering;
    }

    internal sealed class Store
    {
        private readonly Dictionary<ulong, Entry> _entries;
        private readonly int _capacity;

        public Store(int capacity)
        {
            _capacity = capacity;
            _entries = new Dictionary<ulong, Entry>(Math.Min(capacity, 1024));
        }

        public int Count => _entries.Count;

        // The entry of a shape, new (not valid) when the store has none; a full store is cleared first.
        public Entry Take(ulong key)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                return entry;
            }

            if (_entries.Count >= _capacity)
            {
                _entries.Clear();
                Clears++;
            }

            entry = new Entry();
            _entries[key] = entry;
            Shapes = _entries.Count;
            return entry;
        }
    }

    // The generic path's answer becomes the entry's.
    internal static void Keep(Entry entry, in Shape shape, in PlanGroupHashes groups, in RenderingState rendering, PipelineHandle handle,
        ContextRegisters? context)
    {
        entry.Valid = true;
        entry.Shape = shape;
        entry.Groups = groups;
        entry.Rendering = rendering;
        entry.Handle = handle;
        entry.Context = context;
    }

    internal static string WrongPipeline(Entry entry, in Shape shape, in PlanGroupHashes groups, PipelineHandle generic, ContextRegisters? context) =>
        FormattableString.Invariant(
            $"[GPU][DRAW_PLAN_WRONG] kind=pipeline total={WrongTotal} shape=0x{shape.Key:X16} vs=0x{shape.VertexHash:X16} ps=0x{shape.PixelHash:X16} ") +
        FormattableString.Invariant(
            $"planned=0x{entry.Handle.Pipeline:X}/0x{entry.Handle.Layout:X} generic=0x{generic.Pipeline:X}/0x{generic.Layout:X} ") +
        FormattableString.Invariant(
            $"groups=0x{groups.ColorTargets:X16}/0x{groups.DepthTargets:X16}/0x{groups.ShaderInterface:X16}/0x{groups.PipelineState:X16}/0x{groups.ContextDynamic:X16} ") +
        $"context_diff={PlanProbe.ContextDiff(entry.Context, context)}";

    // ---- The report.

    // Without the DRAWCOST window: a line every 5 s, from the render thread's plan lookups.
    internal static void MaybeReport(int mode)
    {
        if (DrawCostProbe.Enabled || Environment.TickCount64 - _lastReportTick < 5000)
        {
            return;
        }

        _lastReportTick = Environment.TickCount64;
        Console.Error.WriteLine(FormatAndClear(mode));
    }

    // Called by DrawCostProbe at its own report when it is on, so the line shares its window.
    internal static void Report()
    {
        if (DefaultMode != 0 && PlanRegisterShadow.Enabled)
        {
            Console.Error.WriteLine(FormatAndClear(DefaultMode));
        }
    }

    internal static string FormatAndClear(int mode)
    {
        var line = new StringBuilder(384);
        line.Append(FormattableString.Invariant(
            $"[PERF][DRAW_PLAN] mode={mode} draws={Draws} hits={Hits} hit_pct={(Draws == 0 ? 0 : Hits * 100.0 / Draws):F1} miss="));
        for (var index = 0; index < (int)Miss.Count; index++)
        {
            line.Append(FormattableString.Invariant($"{(index == 0 ? "" : ",")}{MissNames[index]}:{Misses[index]}"));
        }

        line.Append(FormattableString.Invariant(
            $" shapes={Shapes} clears={Clears} verified={Verified} wrong={Wrong} wrong_total={WrongTotal} "));
        line.Append(FormattableString.Invariant(
            $"layout=stamp:{LayoutStamp},value:{LayoutValue},resolve:{LayoutResolve} layout_verified={LayoutVerified} layout_wrong={LayoutWrong}"));
        Draws = Hits = Clears = Verified = Wrong = LayoutStamp = LayoutValue = LayoutResolve = LayoutVerified = LayoutWrong = 0;
        Array.Clear(Misses);
        return line.ToString();
    }

    // [local] For tests: the window, the totals and the dumps.
    internal static void ResetForTests()
    {
        FormatAndClear(0);
        Shapes = 0;
        Interlocked.Exchange(ref _wrongTotal, 0);
        _dumps = 0;
    }
}
