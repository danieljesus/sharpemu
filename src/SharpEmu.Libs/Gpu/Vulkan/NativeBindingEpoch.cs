// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] Advances on every pipeline, descriptor, push-constant, vertex-buffer and index-buffer bind recorded through
// VkRecord, so a cache of what a command buffer has bound can tell that someone else bound something since.
// Adapted from ae97ac95 by ParantezTech (#1082), which advanced it in each helper pass; here VkRecord advances it.
internal static class NativeBindingEpoch
{
    private static long _value;

    public static long Value => Volatile.Read(ref _value);

    public static void Advance() => Interlocked.Increment(ref _value);
}
