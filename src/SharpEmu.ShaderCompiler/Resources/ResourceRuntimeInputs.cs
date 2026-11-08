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
    public Action<uint, uint, ulong, ulong, ReadOnlyMemory<uint>>? TraceImageCandidate { get; init; }

    // Told true before the flattened table's words are evaluated and false after, so a reader
    // wrapper can tell the words only the table reads from those the descriptors depend on.
    public Action<bool>? TablePhase { get; init; }

    public ResidentGuestBytesReader? ReadResidentMemory { get; init; }

    // [local] SHARPEMU_PAGE_SPAN_READS: the words of one page ReadMemory and ReadResidentMemory would answer, as one span.
    // Not carried by WithReader or WithCallArguments, nor by any wrapper: a span would bypass a replaced reader.
    public ResidentGuestSpanReader? ReadResidentSpan { get; init; }

    // [local] SHARPEMU_STORE_TABLE_COMPARE: what ReadResidentMemory would read, compared in place. Only on inputs whose
    // resident reads are not recorded (or whose store-table scans bypass the recorder), since a compare records nothing.
    public ResidentTableChunkComparer? CompareTableChunk { get; init; }

    public bool ReadsClean { get; init; }

    public GuestRangeSynchronizer? SynchronizeRanges { get; init; } // [local] SHARPEMU_MATERIALIZE_SYNC_VALIDATE

    // [local] SHARPEMU_STORE_TABLE_SYNC: brings one GPU-owned range of a store table back in one readback that waits only
    // for the range's writers, so a refused chunk is read whole instead of one synchronizing word at a time.
    public GuestRangeSynchronizer? SynchronizeTableRange { get; init; }

    public ResourceRuntimeInputs WithReader(GuestWordReader? reader) => new()
    {
        UserData = UserData,
        ShaderBase = ShaderBase,
        ReadMemory = reader,
        ReadCleanMemory = ReadCleanMemory,
        ComputeState = ComputeState,
        TraceImageCandidate = TraceImageCandidate,
        TablePhase = TablePhase,
        ReadResidentMemory = ReadResidentMemory,
        ReadsClean = ReadsClean || ReferenceEquals(reader, ReadCleanMemory),
        SynchronizeRanges = SynchronizeRanges,
        SynchronizeTableRange = SynchronizeTableRange,
    };

    // The inputs of a linked plan whose call entries read their arguments from user data
    // (ScalarValueGraph.CallArgumentRegisterBase): this draw's user data, then two registers per argument.
    public ResourceRuntimeInputs WithCallArguments(uint userDataBase, IReadOnlyList<ulong> arguments)
    {
        if (userDataBase > ScalarValueGraph.CallArgumentRegisterBase ||
            UserData.Count > ScalarValueGraph.CallArgumentRegisterBase - userDataBase)
        {
            throw new ArgumentOutOfRangeException(nameof(userDataBase), "The user data reaches the call argument registers.");
        }

        var first = (int)(ScalarValueGraph.CallArgumentRegisterBase - userDataBase);
        var userData = new uint[first + arguments.Count * 2];
        for (var index = 0; index < UserData.Count; index++)
        {
            userData[index] = UserData[index];
        }

        var offset = first;
        foreach (var argument in arguments)
        {
            userData[offset++] = (uint)argument;
            userData[offset++] = (uint)(argument >> 32);
        }

        return new()
        {
            UserData = userData,
            ShaderBase = ShaderBase,
            ReadMemory = ReadMemory,
            ReadCleanMemory = ReadCleanMemory,
            ComputeState = ComputeState,
            TraceImageCandidate = TraceImageCandidate,
            TablePhase = TablePhase,
            ReadResidentMemory = ReadResidentMemory,
            CompareTableChunk = CompareTableChunk, // [local] same resident reader, so its comparer still applies
            ReadsClean = ReadsClean,
            SynchronizeRanges = SynchronizeRanges, // [local]
            SynchronizeTableRange = SynchronizeTableRange, // [local]
        };
    }
}

public readonly record struct ComputeSelectorState(uint WaveSize, uint ThreadsX, uint ThreadsY, uint ThreadsZ,
    bool HasPartialWorkgroups, uint LocalDataShareDwords, int LocalInvocationIdComponents);
