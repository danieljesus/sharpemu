// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using Counter = SharpEmu.Libs.Diagnostics.DrawCostProbe.Counter;
using Kind = SharpEmu.Libs.Diagnostics.DrawCostProbe.Kind;
using Step = SharpEmu.Libs.Diagnostics.DrawCostProbe.Step;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_DRAWCOST_DEPTH=1 (off by default; only with SHARPEMU_DRAWCOST_PROBE=1): the gate X6 of the short
// path for depth-only draws. DRAWCOST reports the depth-only draws (no color target and a depth target:
// ColorCount == 0 && Depth.HasTarget) as their own kind, with the same steps and counts as the others:
//
//   [PERF][DRAWCOST] kind=draw items_per_frame=N timed=N us_per_item=U ms_per_frame=M steps=... counts=...
//   [PERF][DRAWCOST] kind=draw_depth items_per_frame=N timed=N us_per_item=U ms_per_frame=M steps=... counts=...
//   [PERF][DRAWCOST] kind=dispatch ...
//
// With the switch, kind=draw holds the other draws only (their sum is the old draw line). A draw is classified when
// the executor has resolved its targets (RenderExecutor.DrawIndexed and DrawAutoCore, right after the exec_targets
// lap), or, for a record resolved elsewhere, when the recorder records it (RenderRecorder.RecordResolved). Its steps
// before that (snapshots, exec_prelude, exec_targets) are kept on the side and added to the kind it is classified as
// (DrawCostTally); a draw that leaves before its targets are known (no targets, a metadata operation, a zero count,
// tessellation) stays a draw. The first classification of an item decides (a CPU-argument indirect draw resolves its
// targets again inside its item).
//
// Counts, per timed item of each draw kind:
//   ps_none / ps_kill / ps_export / ps_other: the pixel stage at the classification (PixelClassOf): it does not run
//     (RenderExecutor.HasActivePixelShader false); it runs only because kills are enabled (DB_SHADER_CONTROL
//     KILL_ENABLE: discard or alpha test, no color output, no Z/mask export, no execute-on-noop); it exports Z or the
//     coverage mask; it runs for another reason (a color export masked to a target that did not resolve, dual export,
//     execute-on-noop). ps_idle: it does not run although a pixel program is set.
//   pixel_bound: the pixel stage's bindings were prepared (PrepareBindings, BindResources, CommitBindings run for it);
//     pixel_resources: and its program has images, samplers or buffers.
//   shaped: the draw reached the end of RecordDrawCommands; there, writes_memory, and same_vertex (vertex program,
//     shader base, user data, the stage's descriptor words, vertex buffers and attributes), same_index (index source:
//     enabled, address, size, type, host data), same_targets (colors, depth target and its state, pixel stage on),
//     same_dynamic (viewport, scissor, bias, ...), same_pixel (pixel program and descriptor words, or none) and
//     same_all (all five) against the previous shaped draw of the same kind. Values are compared by their hashes
//     (XxHash3) over their raw bytes; padding or a moved reference reads as a difference, never as a sameness.
//
// The notes are timed apart, as the step depth_note (two passes per draw); with the switch off nothing reaches it, and
// every hook is behind DrawCostProbe.DepthSplit (static readonly, so the JIT drops them).
internal static class DrawCostDepth
{
    internal static readonly bool Requested = Environment.GetEnvironmentVariable("SHARPEMU_DRAWCOST_DEPTH") == "1";

    internal enum PixelClass
    {
        None,
        Kill,
        Export,
        Other,
    }

    [Flags]
    internal enum Same
    {
        None = 0,
        Vertex = 1,
        Index = 2,
        Targets = 4,
        Dynamic = 8,
        Pixel = 16,
        All = Vertex | Index | Targets | Dynamic | Pixel,
    }

    // The hashes of one draw's shape.
    internal readonly record struct Shape(ulong Vertex, ulong Index, ulong Targets, ulong Dynamic, ulong Pixel);

    internal static bool IsDepthOnly(uint colorCount, bool hasDepthTarget) => colorCount == 0 && hasDepthTarget;

    // The registers HasActivePixelShader read, for a draw whose pixel stage runs or not.
    internal static PixelClass PixelClassOf(bool pixelActive, uint renderTargetMask, uint colorShaderMask, in DepthShaderControlRegisters control)
    {
        if (!pixelActive)
        {
            return PixelClass.None;
        }

        if (control.DepthExportEnable || control.MaskExportEnable)
        {
            return PixelClass.Export;
        }

        var colorOutput = (renderTargetMask & colorShaderMask) != 0;
        return control.KillEnable && !colorOutput && !control.DualExportEnable && !control.ExecuteOnNoop ? PixelClass.Kill : PixelClass.Other;
    }

    internal static Counter CounterOf(PixelClass pixel) => pixel switch
    {
        PixelClass.None => Counter.PsNone,
        PixelClass.Kill => Counter.PsKill,
        PixelClass.Export => Counter.PsExport,
        _ => Counter.PsOther,
    };

    // The executor resolved the draw's targets (state.PixelActive is the layout's).
    internal static void NoteTargets(RegisterBanks banks, in DrawState state)
    {
        if (!DrawCostProbe.Classify(IsDepthOnly(state.ColorCount, state.Depth.HasTarget)) || !DrawCostProbe.DepthTiming)
        {
            return;
        }

        CountPixelClass(banks.Context, in state);
        if (!state.PixelActive && banks.Shader.Pixel.Address != 0)
        {
            DrawCostProbe.Count(Counter.PsIdle);
        }

        DrawCostProbe.Lap(Step.DepthNote);
    }

    // The recorder records a draw: classified here when the executor did not (a record resolved elsewhere).
    internal static void NoteRecorded(ResolvedDraw record, in DrawState state)
    {
        if (!DrawCostProbe.Classify(IsDepthOnly(state.ColorCount, state.Depth.HasTarget)) || !DrawCostProbe.DepthTiming)
        {
            return;
        }

        if (record.Context is { } context)
        {
            CountPixelClass(context, in state);
        }

        DrawCostProbe.Lap(Step.DepthNote);
    }

    private static void CountPixelClass(ContextRegisters context, in DrawState state)
    {
        var shaderInterface = context.ShaderInterface;
        DrawCostProbe.Count(CounterOf(PixelClassOf(state.PixelActive, context.RenderTargetMask, shaderInterface.ColorShaderMask,
            in shaderInterface.DepthShaderControl)));
    }

    // The recorder prepared the draw's bindings; prepared is whether the pixel stage's were.
    internal static void NotePixelBindings(bool prepared, in ShaderStageResources pixelStage)
    {
        if (!prepared || !DrawCostProbe.DepthTiming)
        {
            return;
        }

        DrawCostProbe.Count(Counter.PixelBound);
        if (pixelStage.Program is { } program && program.Images.Length + program.Buffers.Length + program.SamplerCount != 0)
        {
            DrawCostProbe.Count(Counter.PixelResources);
        }
    }

    // The recorder emitted the draw (after the draw_tail lap).
    internal static void NoteShape(ResolvedDraw record, ref DrawState state, VertexInputInfo vertexInput)
    {
        if (!DrawCostProbe.DepthTiming)
        {
            return;
        }

        var shape = ShapeOf(record, ref state, vertexInput);
        var same = Shadow.Compare(DrawCostProbe.CurrentKind, in shape);
        DrawCostProbe.Count(Counter.Shaped);
        if (record.WritesMemory) DrawCostProbe.Count(Counter.WritesMemory);
        if ((same & Same.Vertex) != 0) DrawCostProbe.Count(Counter.SameVertex);
        if ((same & Same.Index) != 0) DrawCostProbe.Count(Counter.SameIndex);
        if ((same & Same.Targets) != 0) DrawCostProbe.Count(Counter.SameTargets);
        if ((same & Same.Dynamic) != 0) DrawCostProbe.Count(Counter.SameDynamic);
        if ((same & Same.Pixel) != 0) DrawCostProbe.Count(Counter.SamePixel);
        if (same == Same.All) DrawCostProbe.Count(Counter.SameAll);
        DrawCostProbe.Lap(Step.DepthNote);
    }

    internal static Shape ShapeOf(ResolvedDraw record, ref DrawState state, VertexInputInfo vertexInput) => new(
        VertexHash(vertexInput),
        IndexHash(in record.Index),
        TargetsHash(ref state),
        Raw(in record.Dynamic),
        state.PixelActive && state.Programs?.PixelInput is { } pixelInput ? StageHash(pixelInput.Stage) : 0);

    internal static ulong VertexHash(VertexInputInfo vertexInput)
    {
        var hash = new XxHash3();
        Add(ref hash, StageHash(vertexInput.Stage));
        hash.Append(MemoryMarshal.AsBytes(vertexInput.Buffers.AsSpan()));
        hash.Append(MemoryMarshal.AsBytes(vertexInput.Attributes.AsSpan()));
        Add(ref hash, (vertexInput.FetchEmbedded ? 1UL : 0) | ((ulong)(uint)vertexInput.FetchAttributeRegister << 8) |
            ((ulong)(uint)vertexInput.FetchBufferRegister << 24) | ((ulong)(uint)vertexInput.StartInstanceUserData << 40));
        return hash.GetCurrentHashAsUInt64();
    }

    // A stage: its program, shader base, user data and descriptor words.
    internal static ulong StageHash(in ShaderStageResources stage)
    {
        var hash = new XxHash3();
        Add(ref hash, stage.Program?.Hash ?? 0);
        Add(ref hash, stage.ShaderBase);
        if (stage.Resources is { } resources)
        {
            Add(ref hash, (ulong)resources.UserData.Length);
            hash.Append(MemoryMarshal.AsBytes(resources.UserData.AsSpan()));
            AddWords(ref hash, resources.Buffers);
            AddWords(ref hash, resources.Images);
            AddWords(ref hash, resources.Samplers);
        }

        return hash.GetCurrentHashAsUInt64();
    }

    internal static ulong IndexHash(in IndexSource index)
    {
        var hash = new XxHash3();
        Add(ref hash, (index.Enabled ? 1UL : 0) | ((ulong)index.Type << 8));
        Add(ref hash, index.Address);
        Add(ref hash, index.Size);
        Add(ref hash, index.HostData is { } host ? (ulong)(uint)RuntimeHelpers.GetHashCode(host) | 1UL << 40 : 0);
        return hash.GetCurrentHashAsUInt64();
    }

    internal static ulong TargetsHash(ref DrawState state)
    {
        var hash = new XxHash3();
        Add(ref hash, state.ColorCount | (state.PixelActive ? 1UL << 32 : 0) | (state.DepthResolved ? 1UL << 33 : 0));
        foreach (ref readonly var color in DrawState.BoundColors(ref state))
        {
            Add(ref hash, Raw(in color.Resolution));
            Add(ref hash, color.Slot);
        }

        if (state.Depth.HasTarget)
        {
            Add(ref hash, Raw(in state.Depth.Target));
        }

        return hash.GetCurrentHashAsUInt64();
    }

    private static void AddWords(ref XxHash3 hash, uint[][] words)
    {
        Add(ref hash, (ulong)words.Length);
        foreach (var entry in words)
        {
            Add(ref hash, (ulong)entry.Length);
            hash.Append(MemoryMarshal.AsBytes(entry.AsSpan()));
        }
    }

    private static void Add(ref XxHash3 hash, ulong value) =>
        hash.Append(MemoryMarshal.AsBytes(new ReadOnlySpan<ulong>(in value)));

    // A value's raw bytes, references included (as SameTargetLayout compares them).
    internal static ulong Raw<T>(in T value) where T : struct =>
        XxHash3.HashToUInt64(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)), Unsafe.SizeOf<T>()));

    // The previous shaped draw of each kind (the recording thread only).
    internal sealed class ShapeShadow
    {
        private readonly Shape[] _previous = new Shape[(int)Kind.Count];
        private readonly bool[] _valid = new bool[(int)Kind.Count];

        public Same Compare(Kind kind, in Shape shape)
        {
            var index = (int)kind;
            var same = Same.None;
            if (_valid[index])
            {
                ref readonly var previous = ref _previous[index];
                if (previous.Vertex == shape.Vertex) same |= Same.Vertex;
                if (previous.Index == shape.Index) same |= Same.Index;
                if (previous.Targets == shape.Targets) same |= Same.Targets;
                if (previous.Dynamic == shape.Dynamic) same |= Same.Dynamic;
                if (previous.Pixel == shape.Pixel) same |= Same.Pixel;
            }

            _previous[index] = shape;
            _valid[index] = true;
            return same;
        }

        public void Reset() => Array.Clear(_valid);
    }

    internal static readonly ShapeShadow Shadow = new();
}
