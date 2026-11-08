// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_BDA_WRITE_MEMO (see BdaWriteMemo): the per-cache table of the written device-address ranges obtained in
// the current tick, asked by the render thread's ObtainDeviceAddressRanges before it clamps and obtains a range.
public sealed unsafe partial class GuestBufferCache
{
    internal enum BdaRepeat
    {
        Miss,
        Hit,
        Verified,
    }

    // Per cache so tests set it.
    internal int BdaWriteMemoMode { get; set; } = BdaWriteMemo.DefaultMode;

    private const int BdaWriteMemoSlots = 4096;

    // One remembered written range: the key, the clamped size, the obtain's answer and the stamps it was valid under.
    private struct BdaWriteEntry
    {
        public ulong Address;
        public ulong Requested;
        public bool Conservative;
        public bool Valid;
        public ulong Size;
        public ulong Tick;
        public long Mapping;
        public long Resets;
        public long Epoch;
        public long Removals;
        public ulong CollectionTick;
        public long TrustSerial;
        public ulong Program;
        public ResourceSlotIdentifier Identifier;
        public GpuBuffer? Buffer;
        public ulong Offset;
    }

    private BdaWriteEntry[]? _bdaWrites;

    private static int BdaWriteSlot(ulong address, ulong requested, bool conservative) =>
        (int)(((address >> 4) ^ (address >> 19) ^ (requested * 0x9E3779B97F4A7C15UL >> 40) ^ (conservative ? 0x5A5UL : 0)) & (BdaWriteMemoSlots - 1));

    // What would make the real path do or observe something the memo cannot repeat: the diagnostics that watch every
    // written obtain, the switches that change what a written obtain does, the other memo, the read-only thread and a
    // command buffer that is not recording (the real path throws).
    private bool BdaWriteMemoGuarded =>
        WrittenObtainMemoMode != 0 || WrittenSyncCpuDirty || EagerReadbacksOn || ReadbackTickLog || DirtyWatch != 0 || EarlyWriterSubmit ||
        StoreTableVerify || CutPolicy.Words || CutPolicy.GuestWindows || WriteDiffProbe.Enabled || Diagnostics.TableReadCensus.Enabled ||
        Diagnostics.DccWriterTrace.Enabled || Diagnostics.TableDirtyProbe.Enabled || ImageClearTrace.Enabled || MeshDrawTrace.Active ||
        CpWriteDeferProbe.Enabled || SubmissionScheduler.ReadOnlyThread || _scheduler.Current.IsInvalid;

    // A written range obtained earlier under the same key: Hit after the effects listed in BdaWriteMemo (size = the clamped
    // size of the first obtain; an entry of an earlier tick also marks the write at this tick, see BdaWriteMemo's re-tick),
    // Verified (=2) after the real path ran and was compared, Miss otherwise (the caller clamps and obtains through
    // ObtainBdaWrite). `mapping` and `resets` are the guest mapping generation and the
    // presenter's per-frame clamp resets, read before the clamp of the first obtain and now.
    internal BdaRepeat TryRepeatBdaWrite(ulong address, ulong requested, bool conservative, ulong traceShaderHash, long mapping, long resets,
        out ulong size)
    {
        size = 0;
        var mode = BdaWriteMemoMode;
        BdaWriteMemo.Obtains++;
        BdaWriteMemo.MaybeReport(mode);
        if (mapping == long.MinValue || BdaWriteMemoGuarded)
        {
            BdaWriteMemo.NoteMiss(BdaWriteMemo.Miss.Guard);
            return BdaRepeat.Miss;
        }

        var table = _bdaWrites;
        if (table is null)
        {
            BdaWriteMemo.NoteMiss(BdaWriteMemo.Miss.Cold);
            return BdaRepeat.Miss;
        }

        ref var entry = ref table[BdaWriteSlot(address, requested, conservative)];
        if (!entry.Valid || entry.Address != address || entry.Requested != requested || entry.Conservative != conservative)
        {
            BdaWriteMemo.NoteMiss(BdaWriteMemo.Miss.Cold);
            return BdaRepeat.Miss;
        }

        var tick = _scheduler.CurrentTick;
        if (tick != BdaWriteMemo.LastTick)
        {
            BdaWriteMemo.LastTick = tick;
            BdaWriteMemo.Ticks++;
        }

        var retick = entry.Tick != tick;
        if (retick)
            BdaWriteMemo.NoteRetick();
        if (retick && !BdaWriteMemo.Retick)
        {
            entry.Valid = false;
            BdaWriteMemo.NoteMiss(BdaWriteMemo.Miss.Tick);
            return BdaRepeat.Miss;
        }

        // A full answer stays while the generation does (only an unmap or a clear moves it, see ClampMappedSize); a
        // clamped one is kept per frame, until the presenter's next reset.
        if (entry.Mapping != mapping || (entry.Size != requested && entry.Resets != resets))
        {
            entry.Valid = false;
            BdaWriteMemo.NoteMiss(BdaWriteMemo.Miss.Mapping);
            return BdaRepeat.Miss;
        }

        size = entry.Size;
        // The real path's first effect; it may complete work that removes GPU-modified ranges or retires buffers, so the
        // checks below see the state the real path would see after it. A miss after it repeats it in the real path,
        // which then finds those publications completed.
        CompleteGuestPublications?.Invoke(address, size, PublicationAccess.Write);
        var buffer = entry.Buffer!;
        var epoch = _registry.Epoch;
        if (entry.Epoch != epoch)
        {
            var owner = _registry.FindContainingResource(address, size, out var identifier, out _);
            if (!ReferenceEquals(owner, buffer) || identifier != entry.Identifier)
            {
                entry.Valid = false;
                BdaWriteMemo.NoteMiss(BdaWriteMemo.Miss.Registry);
                return BdaRepeat.Miss;
            }

            entry.Epoch = epoch;
        }

        var removals = Volatile.Read(ref _gpuModifiedRemovals);
        if (entry.Removals != removals)
        {
            if (!_gpuModifiedRanges.Contains(address, size) || (size < BroadWriteBytes && !_preciseGpuModifiedRanges.Contains(address, size)))
            {
                entry.Valid = false;
                BdaWriteMemo.NoteMiss(BdaWriteMemo.Miss.Removed);
                return BdaRepeat.Miss;
            }

            entry.Removals = removals;
        }

        if (retick)
            BdaWriteMemo.HitsRetick++;
        else
            BdaWriteMemo.Hits++;
        if (mode == 2)
        {
            VerifyBdaWrite(ref entry, address, size, conservative, traceShaderHash, tick, retick);
            entry.Tick = tick;
            return BdaRepeat.Verified;
        }

        if (entry.CollectionTick != _retirementPolicy.CurrentTick)
        {
            TouchBuffer(entry.Identifier);
            entry.CollectionTick = _retirementPolicy.CurrentTick;
        }

        if (ScopeBarrierTracker.Enabled) ScopeBarrierTracker.NoteWrite();
        if (retick)
        {
            // The real call: the write ticks, LastGpuWriteTick and the WriteTickMap move to this tick (a chunk pruned since
            // is created again), with the count, the observer, the history entry and the mapped flag of the repeat.
            buffer.NoteGpuWrite(entry.Offset, size);
            entry.Tick = tick;
        }
        else
            buffer.NoteRepeatedGpuWrite(entry.Offset, size);
        if (TableWordMemo.Enabled && (entry.TrustSerial != TableWordMemo.TrustSerial || entry.Program != TableWordMemo.CurrentProgram))
        {
            entry.TrustSerial = TableWordMemo.TrustSerial;
            entry.Program = TableWordMemo.CurrentProgram;
            TableWordMemo.NoteGpuWrite(address, size);
            BdaWriteMemo.TableReplays++;
        }

        _obtainCounts[(int)ObtainOutcome.Written]++;
        Diagnostics.DrawCostProbe.Count(Diagnostics.DrawCostProbe.Counter.Obtain); // [local] SHARPEMU_DRAWCOST_PROBE
        Diagnostics.DrawCostProbe.Count(Diagnostics.DrawCostProbe.Counter.ObtainWritten);
        return BdaRepeat.Hit;
    }

    // The written obtain of a range the memo did not answer, remembered for the rest of the tick. The stamps are read
    // before the obtain: anything the obtain itself changes reads as a change at the next lookup, which checks again.
    internal void ObtainBdaWrite(ulong address, ulong requested, ulong size, bool conservative, ulong traceShaderHash, long mapping, long resets)
    {
        var remember = BdaWriteMemoMode != 0 && mapping != long.MinValue && !BdaWriteMemoGuarded;
        var epoch = _registry.Epoch;
        var removals = Volatile.Read(ref _gpuModifiedRemovals);
        var trust = TableWordMemo.TrustSerial;
        var (buffer, offset) = ObtainBuffer(address, size, isWritten: true, traceShaderHash: traceShaderHash, conservativeWrite: conservative);
        if (!remember)
        {
            return;
        }

        var owner = _registry.FindContainingResource(address, size, out var identifier, out _);
        if (!ReferenceEquals(owner, buffer))
        {
            return;
        }

        var table = _bdaWrites ??= new BdaWriteEntry[BdaWriteMemoSlots];
        table[BdaWriteSlot(address, requested, conservative)] = new BdaWriteEntry
        {
            Address = address,
            Requested = requested,
            Conservative = conservative,
            Valid = true,
            Size = size,
            Tick = _scheduler.CurrentTick,
            Mapping = mapping,
            Resets = resets,
            Epoch = epoch,
            Removals = removals,
            CollectionTick = _retirementPolicy.CurrentTick,
            TrustSerial = trust,
            Program = TableWordMemo.CurrentProgram,
            Identifier = identifier,
            Buffer = buffer,
            Offset = offset,
        };
    }

    // =2: the preconditions of the skipped steps, then the real path, then what it changed.
    // A re-tick hit skips the write-tick precondition (the real path moves the ticks, as the memo's NoteGpuWrite does) and
    // checks after it that the real path left them at this tick.
    private void VerifyBdaWrite(ref BdaWriteEntry entry, ulong address, ulong size, bool conservative, ulong traceShaderHash, ulong tick,
        bool retick)
    {
        BdaWriteMemo.Verified++;
        var buffer = entry.Buffer!;
        string? reason = null;
        var owner = _registry.FindContainingResource(address, size, out var identifier, out _);
        bool everWritten;
        _everGpuWrittenLock.EnterReadLock();
        try
        {
            everWritten = _everGpuWritten.Contains(address, size);
        }
        finally
        {
            _everGpuWrittenLock.ExitReadLock();
        }

        if (!ReferenceEquals(owner, buffer) || identifier != entry.Identifier)
            reason = "owner";
        else if (!_gpuModifiedRanges.Contains(address, size))
            reason = "not_modified";
        else if (size < BroadWriteBytes && !_preciseGpuModifiedRanges.Contains(address, size))
            reason = "not_precise";
        else if (!everWritten)
            reason = "not_ever_written";
        else if (!retick && !buffer.WriteTicksHold(entry.Offset, size, tick))
            reason = "write_ticks";
        else if (entry.CollectionTick == _retirementPolicy.CurrentTick && _registry.UseTickForTests(identifier) < _retirementPolicy.CurrentTick)
            reason = "use_tick";

        if (retick && _tracker.MayHaveCpuDirtyPages(address, size))
            BdaWriteMemo.RetickCpuDirty++;
        var skipsTable = TableWordMemo.Enabled && entry.TrustSerial == TableWordMemo.TrustSerial && entry.Program == TableWordMemo.CurrentProgram;
        var uploads = _syncUploads;
        var version = Volatile.Read(ref _gpuModifiedVersion);
        var removals = Volatile.Read(ref _gpuModifiedRemovals);
        var epoch = _registry.Epoch;
        var drops = TableWordMemo.GpuDrops;
        var writes = buffer.GpuWrites;
        var (real, offset) = ObtainBuffer(address, size, isWritten: true, traceShaderHash: traceShaderHash, conservativeWrite: conservative);
        if (reason is null)
        {
            if (!ReferenceEquals(real, buffer) || offset != entry.Offset)
                reason = "buffer";
            else if (_syncUploads != uploads)
                reason = "upload";
            else if (Volatile.Read(ref _gpuModifiedVersion) != version || Volatile.Read(ref _gpuModifiedRemovals) != removals)
                reason = "modified";
            else if (_registry.Epoch != epoch)
                reason = "registry";
            else if (skipsTable && TableWordMemo.GpuDrops != drops)
                reason = "table";
            else if (buffer.GpuWrites != writes + 1)
                reason = "writes";
            else if (!buffer.WriteTicksHold(entry.Offset, size, tick))
                reason = retick ? "retick_ticks" : "ticks_after";
        }

        if (reason is not null)
            BdaWriteMemo.NoteWrong(reason, address, size, FormattableString.Invariant(
                $"retick={retick} entry_tick={entry.Tick} conservative={conservative} buffer=0x{buffer.CpuAddress:X}+0x{buffer.Size:X}@0x{entry.Offset:X} real=0x{real.CpuAddress:X}+0x{real.Size:X}@0x{offset:X} tick={tick} program=0x{TableWordMemo.CurrentProgram:X16}"));

        entry.CollectionTick = _retirementPolicy.CurrentTick;
        entry.TrustSerial = TableWordMemo.TrustSerial;
        entry.Program = TableWordMemo.CurrentProgram;
        entry.Removals = Volatile.Read(ref _gpuModifiedRemovals);
        entry.Epoch = _registry.Epoch;
    }

    // =2: the presenter clamped the size of a verified hit again.
    internal static void NoteBdaWriteClamp(ulong address, ulong requested, ulong remembered, ulong clamped)
    {
        if (remembered != clamped)
            BdaWriteMemo.NoteWrong("clamp", address, requested, FormattableString.Invariant($"remembered=0x{remembered:X} clamped=0x{clamped:X}"));
    }
}
