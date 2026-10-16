// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The page granularity and address width every emitter uses for device addresses.
public static class DeviceAddressPaging
{
    // Guest pages are mapped to device buffers at this granularity; the host's cache uses the same.
    public const int PageBits = 14;
    public const ulong PageSize = 1UL << PageBits;
    public const ulong PageOffsetMask = PageSize - 1;

    // Address handles carry 48 bits; the upper bits are ignored.
    public const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    // The fault buffer starts with one bit per page of the 40-bit guest GPU address space.
    public const int AddressSpaceBits = 40;
    public const ulong FaultBitmapBytes = (1UL << (AddressSpaceBits - PageBits)) / 8;

    // [local] SHARPEMU_RT_STACK_SPLIT=<rows> (8 by default, 0 = off): the workgroup rows of the 0x8395
    // BLAS stack. An area after the fault bitmap holds the deeper entries: a bitmap of warp slots (each
    // 32-lane subgroup claims one for the dispatch and frees it at the end), then per slot
    // RtStackSlotEntries entries of 32 lanes.
    public static readonly uint RtStackSplitRows =
        Environment.GetEnvironmentVariable("SHARPEMU_RT_STACK_SPLIT") is { Length: > 0 } text
            ? uint.TryParse(text, out var rows) && rows < RtStackSlotEntries ? rows : 0
            : 8;
    public static bool RtStackAreaEnabled => RtStackSplitRows != 0;
    public const uint RtStackWarpSlots = 8192;
    public const uint RtStackSlotEntries = 58;
    public const ulong RtStackSlotBitmapBytes = RtStackWarpSlots / 8;
    public static ulong RtStackAreaBytes => RtStackAreaEnabled ? RtStackSlotBitmapBytes + (ulong)RtStackWarpSlots * RtStackSlotStride * 32 * 4 : 0;

    // [local] SHARPEMU_RT_SPILL_SCALARS (on by default; =0 turns it off): 0x8395 keeps up to RtSpillMaxScalars scalar registers that
    // the BVH walk neither reads nor writes in the warp slot while it runs. Each slot then has RtSpillRows more
    // 32-dword rows after the deep stack entries: two rows for values equal in the whole subgroup (one dword each),
    // then one row per value for subgroups where they differ.
    // =2 (verify) keeps the values in registers as well and counts restores that differ ([GPU][RT_STACK]).
    public static readonly bool RtSpillScalars = Environment.GetEnvironmentVariable("SHARPEMU_RT_SPILL_SCALARS") != "0";
    public static readonly bool RtSpillVerify = Environment.GetEnvironmentVariable("SHARPEMU_RT_SPILL_SCALARS") == "2";
    public const uint RtSpillMaxScalars = 40;
    public const uint RtSpillUniformRows = (RtSpillMaxScalars + 31) / 32;
    public const uint RtSpillRows = RtSpillUniformRows + RtSpillMaxScalars;
    public static uint RtStackSlotStride => RtStackSlotEntries + (RtSpillScalars ? RtSpillRows : 0);
}
