// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] SHARPEMU_SCOPE_BARRIER_PRUNE (0 by default): the global memory barrier every rendering scope records when it
// begins (DeferGlobalBarriers) orders what came before the scope ahead of its draws, because a guest cache flush inside
// the scope waits for the scope's end. It only matters when a memory write recorded since the last global barrier is
// not ordered yet. Writes: written buffer bindings and device-address ranges, programs that access memory through the
// device-address table (counted as stores, as RenderExecutor does), storage images, and every transfer except the
// buffer copies, fills and uploads that record their own barrier into all later commands. =1 counts the scopes that
// could skip the barrier; =2 skips it, and a draw that writes memory inside such a scope ends it first, so the next
// scope records the barrier.
internal static class ScopeBarrierTracker
{
    internal static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SCOPE_BARRIER_PRUNE"), out var mode) ? mode : 0;
    internal static bool Enabled => Mode != 0;

    private static volatile bool _unorderedWrites = true;
    [ThreadStatic] private static int t_selfOrdered;
    // [local] Set around the guest's own draws and dispatches, whose writes the stage path already notes.
    [ThreadStatic] internal static bool t_guestStage;
    private static long _emulatorCommands;
    private static long _scopes, _kept, _prunable, _forcedEnds, _writeNotes, _transferNotes, _fullBarriers;
    private static long _report = System.Diagnostics.Stopwatch.GetTimestamp();

    internal static bool UnorderedWrites => _unorderedWrites;

    internal static void NoteWrite()
    {
        _unorderedWrites = true;
        _writeNotes++;
    }

    internal static void NoteTransfer()
    {
        if (t_selfOrdered != 0)
            return;
        _unorderedWrites = true;
        _transferNotes++;
    }

    // Every draw and dispatch VkRecord records: one the emulator records for itself (detiling, blits to
    // depth, fault processing, presentation) counts as an unordered write unless it records its own barrier.
    internal static void NoteCommand()
    {
        if (t_guestStage || t_selfOrdered != 0)
            return;
        _unorderedWrites = true;
        _emulatorCommands++;
    }

    internal static void BeginSelfOrdered() => t_selfOrdered++;

    internal static void EndSelfOrdered() => t_selfOrdered--;

    internal static void NoteFullBarrier()
    {
        _unorderedWrites = false;
        _fullBarriers++;
    }

    // True when the scope beginning now may skip its barrier.
    internal static bool ScopeBegins(bool firstDrawWritesMemory)
    {
        _scopes++;
        var prunable = !_unorderedWrites && !firstDrawWritesMemory;
        if (prunable)
            _prunable++;
        else
            _kept++;
        Report();
        return prunable;
    }

    internal static void NoteForcedEnd() => _forcedEnds++;

    private static void Report()
    {
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_report).TotalSeconds < 5)
            return;
        _report = System.Diagnostics.Stopwatch.GetTimestamp();
        Console.Error.WriteLine($"[PERF][SCOPE_BARRIER] mode={Mode} scopes={_scopes} kept={_kept} prunable={_prunable} forced_ends={_forcedEnds} write_notes={_writeNotes} transfer_notes={_transferNotes} full_barriers={_fullBarriers} emulator_commands={_emulatorCommands}");
        _scopes = _kept = _prunable = _forcedEnds = _writeNotes = _transferNotes = _fullBarriers = _emulatorCommands = 0;
    }
}
