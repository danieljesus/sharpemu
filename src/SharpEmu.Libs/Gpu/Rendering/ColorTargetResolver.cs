// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// Selects the color target slot of a draw and builds its image request from the context bank.
public static class ColorTargetResolver
{
    public const uint FirstBoundSlot = uint.MaxValue;

    // The first slot with a target mask nibble and a base address; slot 0 when none is bound.
    public static uint FirstBound(ContextRegisters context)
    {
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (context.RenderTargetMaskForSlot(slot) != 0 && context.ColorTargets[slot].BaseAddress != 0)
            {
                return slot;
            }
        }

        return 0;
    }

    // Building a slot's request decodes the tiling of the surface, with its own arrays, and the
    // registers rarely change between draws: the last resolution of each slot is kept on the
    // resolving thread while its inputs stay the same. The result is a value with immutable
    // tables, so sharing it between draws is safe.
    private readonly record struct ResolutionKey(ColorTargetWords Words, uint TargetMask, uint DrawLayerOffset, bool IgnoreTargetMask);

    [ThreadStatic] private static ResolutionKey[]? _lastKeys;
    [ThreadStatic] private static ColorTargetResolution?[]? _lastResolutions;
    [ThreadStatic] private static bool[]? _lastValid;

    // Null means the slot carries no color output.
    public static ColorTargetResolution? Resolve(ContextRegisters context, uint slot, uint drawLayerOffset, bool ignoreTargetMask, out uint resolvedSlot)
    {
        resolvedSlot = slot == FirstBoundSlot ? FirstBound(context) : slot;
        var key = new ResolutionKey(context.ColorTargets[resolvedSlot], context.RenderTargetMaskForSlot(resolvedSlot), drawLayerOffset, ignoreTargetMask);
        var keys = _lastKeys ??= new ResolutionKey[ContextRegisters.ColorTargetCount];
        var resolutions = _lastResolutions ??= new ColorTargetResolution?[ContextRegisters.ColorTargetCount];
        var valid = _lastValid ??= new bool[ContextRegisters.ColorTargetCount];
        if (valid[resolvedSlot] && keys[resolvedSlot] == key)
        {
            return resolutions[resolvedSlot];
        }

        var resolution = ImageRequestBuilders.ColorTarget(in context.ColorTargets[resolvedSlot], key.TargetMask, drawLayerOffset, ignoreTargetMask);
        keys[resolvedSlot] = key;
        resolutions[resolvedSlot] = resolution;
        valid[resolvedSlot] = true;
        return resolution;
    }
}
