// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed class DrawResolution
{
    public ulong TargetContextStamp;
    public ulong TargetProgramsStamp;
    public uint TargetVersion;
    public int ColorCount;
    public readonly ColorTargetResolution[] Colors = new ColorTargetResolution[RenderingState.ColorAttachmentCapacity];
    public readonly uint[] ColorSlots = new uint[RenderingState.ColorAttachmentCapacity];
    public DepthTargetState? Depth;
    public bool PixelActive;

    // [local] SHARPEMU_DRAW_PLAN: the value hashes of the groups the layout was resolved from (PlanTargetsKey).
    public bool PlanTargetsValid;
    public PlanTargetsKey PlanTargets;

    public ulong ValidatedContextStamp;
    public ulong ValidatedUserConfigStamp;

    public DynamicStateKey DynamicKey;
    public DynamicDrawState Dynamic;
}

// [local] SHARPEMU_DRAW_PLAN: what a target layout is a function of, by value: the ColorTargets, DepthTargets and
// ShaderInterface register groups (the stamps ResolveTargetLayout keys on) and the composite depth extent, which moves
// the DepthTargets stamp too. The shader-program stamp's part, whether the pixel stage runs, is checked apart.
internal readonly record struct PlanTargetsKey(ulong ColorTargets, ulong DepthTargets, ulong ShaderInterface, uint? CompositeDepthSizeXy)
{
    public static PlanTargetsKey Of(GpuCommands.Registers.RegisterBanks banks)
    {
        var hashes = banks.PlanHashes;
        return new PlanTargetsKey(hashes.ColorTargets, hashes.DepthTargets, hashes.ShaderInterface, banks.CompositeDepthSizeXy);
    }
}

internal readonly record struct DynamicStateKey(
    ulong ContextStamp,
    ulong DynamicStamp,
    uint TargetVersion,
    uint ColorCount,
    uint BoundSlots,
    bool FirstColorFound,
    bool DepthFound);

internal sealed class KeptPipeline
{
    public bool Valid;
    public PipelineKey Key;
    public RenderingState Rendering;
    public PipelineHandle Handle;
}

internal readonly record struct PipelineKey(
    ulong ContextStamp,
    ulong DynamicStamp,
    uint TargetVersion,
    uint ColorCount,
    uint BoundSlots,
    bool DepthBound,
    VertexAttributeResource[] VertexAttributes,
    VertexInputBuffer[] VertexBuffers,
    uint[]? PixelInterpolators,
    ulong VertexProgram,
    ulong PixelProgram,
    int Topology,
    bool PrimitiveRestart,
    bool DisableBlending);
