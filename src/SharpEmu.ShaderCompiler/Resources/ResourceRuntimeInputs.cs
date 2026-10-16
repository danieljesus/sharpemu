// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// Reads one dword of guest memory; false when the address cannot be read.
public delegate bool GuestWordReader(ulong address, out uint word);

// [local] Brings GPU-owned guest ranges back to the CPU in one readback, as the word reader would one at a time:
// true when the first range is synchronized. The other ranges are best effort.
public delegate bool GuestRangeSynchronizer(ReadOnlySpan<(ulong Address, int Length)> ranges);

// What one draw supplies to materialise a plan: its user data, the shader base and
// the two memory readers. The clean reader refuses memory the GPU may still own.
// Nothing keeps the instance past the materialisation, so a caller may refill one per draw.
public sealed class ResourceRuntimeInputs
{
    public IReadOnlyList<uint> UserData { get; set; } = [];
    public ulong ShaderBase { get; set; }
    public GuestWordReader? ReadMemory { get; init; }
    public GuestWordReader? ReadCleanMemory { get; init; }
    public ComputeSelectorState? ComputeState { get; set; }

    // Told true before the flattened table's words are evaluated and false after, so a reader
    // wrapper can tell the words only the table reads from those the descriptors depend on.
    public Action<bool>? TablePhase { get; init; }

    public ResidentGuestBytesReader? ReadResidentMemory { get; init; }

    public bool ReadsClean { get; init; }

    public GuestRangeSynchronizer? SynchronizeRanges { get; init; } // [local] SHARPEMU_MATERIALIZE_SYNC_VALIDATE

    public ResourceRuntimeInputs WithReader(GuestWordReader? reader) => new()
    {
        UserData = UserData,
        ShaderBase = ShaderBase,
        ReadMemory = reader,
        ReadCleanMemory = ReadCleanMemory,
        ComputeState = ComputeState,
        TablePhase = TablePhase,
        ReadResidentMemory = ReadResidentMemory,
        ReadsClean = ReadsClean || ReferenceEquals(reader, ReadCleanMemory),
        SynchronizeRanges = SynchronizeRanges,
    };
}

public readonly record struct ComputeSelectorState(uint WaveSize, uint ThreadsX, uint ThreadsY, uint ThreadsZ,
    bool HasPartialWorkgroups, uint LocalDataShareDwords, int LocalInvocationIdComponents);
