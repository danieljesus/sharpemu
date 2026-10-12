// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_PROFILE_FRONT=1: where the front thread's time goes. The loop splits wall time into slices
// that progressed, slices and waits with no submission, and slices and waits with every head blocked; the
// blocked time is charged to the wait that ended it (kind and address of the head that ran next).
// SHARPEMU_PROFILE_FRONT=2 also splits progressed slices by phase (self time, scopes nest), which costs two
// timestamps per packet and per scope.
internal static class FrontProfile
{
    internal enum Phase
    {
        Interpret,
        StreamRead,
        RegisterPackets,
        DrawPackets,
        DispatchPackets,
        WaitPackets,
        OtherPackets,
        Snapshot,
        DrawRegisters,
        ProgramInputs,
        ProgramSource,
        ProgramGate,
        ProgramLookup,
        ProgramTail,
        PrepareCompute,
        Enqueue,
        Backpressure,
        SyncWait,
        CommandRead,
        WriteCopy,
        Count,
    }

    internal enum SuspendKind : byte
    {
        None,
        Memory,
        Semaphore,
        Atomic,
        ConstantEngine,
        DrawEngineDiff,
        Rewind,
        FlipSlot,
        FlipDone,
        RingChunk,
        Other,
    }

    internal static readonly int Level = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_FRONT"), out var level) ? level : 0;
    internal static readonly bool LoopEnabled = Level >= 1;
    internal static readonly bool PhasesEnabled = Level >= 2;

    [ThreadStatic] private static bool _active;
    [ThreadStatic] private static Phase _current;
    [ThreadStatic] private static long _last;
    [ThreadStatic] private static long _lastAllocated;

    private static readonly long[] _ticks = new long[(int)Phase.Count];
    private static readonly long[] _entries = new long[(int)Phase.Count];
    private static readonly long[] _allocated = new long[(int)Phase.Count];

    // Loop-level accounting (front thread writes, the render thread reports).
    internal static long ProgressTicks, NoWorkTicks, BlockedTicks, BlockedUnattributedTicks, Draws, Dispatches, Enqueues, Packets, DeferredPrepTicks;
    private static readonly Dictionary<(SuspendKind Kind, ulong Address, byte Writer), (long IdleTicks, long Count, long BlockedTicks)> _blocked = new();

    // Who last wrote each address the stream wrote (front thread only): 1 a queued CP write, 2 an immediate CP write,
    // 3 an end-of-pipe write. A memory wait on an address the stream never wrote waits on the guest CPU or the GPU's shaders.
    private static readonly Dictionary<ulong, byte> _writers = new();
    private static readonly string[] WriterNames = ["?", "cp", "cpnow", "eop"];

    internal static void NoteStreamWrite(ulong address, byte writer)
    {
        if (LoopEnabled && address != 0)
            _writers[address & ~3UL] = writer;
    }

    internal static bool Active => _active;

    // Called on the front thread once.
    internal static void BindFrontThread()
    {
        if (!PhasesEnabled)
            return;
        _active = true;
        _current = Phase.Interpret;
        _last = 0;
    }

    internal readonly ref struct Scope
    {
        private readonly Phase _previous;
        private readonly bool _on;

        internal Scope(Phase previous)
        {
            _previous = previous;
            _on = true;
        }

        public void Dispose()
        {
            if (_on)
                Charge(_previous);
        }
    }

    // A slice starts the clock; time between slices is not charged here.
    internal static void BeginSlice()
    {
        if (!PhasesEnabled || !_active)
            return;
        _last = Stopwatch.GetTimestamp();
        _lastAllocated = GC.GetAllocatedBytesForCurrentThread();
        _current = Phase.Interpret;
    }

    internal static void EndSlice()
    {
        if (!PhasesEnabled || !_active)
            return;
        Charge(Phase.Interpret);
        _last = 0;
    }

    internal static Scope Measure(Phase phase)
    {
        if (!PhasesEnabled || !_active || _last == 0)
            return default;
        var previous = Charge(phase);
        _entries[(int)phase]++;
        return new Scope(previous);
    }

    // Switches the running phase without nesting (sequential steps inside one scope).
    internal static void Switch(Phase phase)
    {
        if (!PhasesEnabled || !_active || _last == 0)
            return;
        Charge(phase);
        _entries[(int)phase]++;
    }

    internal static Phase PacketPhase(uint opcode) => opcode switch
    {
        Gpu.GpuCommands.PacketOpcode.SetContextRegister or Gpu.GpuCommands.PacketOpcode.SetShaderRegister or
            Gpu.GpuCommands.PacketOpcode.SetUserConfigRegister or Gpu.GpuCommands.PacketOpcode.SetUserConfigRegisterIndex or
            Gpu.GpuCommands.PacketOpcode.SetContextRegisterIndirect or Gpu.GpuCommands.PacketOpcode.SetShaderRegisterIndirect or
            Gpu.GpuCommands.PacketOpcode.SetUserConfigRegisterIndirect or Gpu.GpuCommands.PacketOpcode.ContextControl or
            Gpu.GpuCommands.PacketOpcode.ClearState => Phase.RegisterPackets,
        Gpu.GpuCommands.PacketOpcode.DrawIndex2 or Gpu.GpuCommands.PacketOpcode.DrawIndexAuto or Gpu.GpuCommands.PacketOpcode.DrawIndexOffset2 or
            Gpu.GpuCommands.PacketOpcode.DrawIndexIndirect or Gpu.GpuCommands.PacketOpcode.DrawIndirect or
            Gpu.GpuCommands.PacketOpcode.DrawIndexIndirectMulti or Gpu.GpuCommands.PacketOpcode.DrawIndirectMulti or
            Gpu.GpuCommands.PacketOpcode.DrawIndexMultiAuto or Gpu.GpuCommands.PacketOpcode.DispatchDrawPreamble => Phase.DrawPackets,
        Gpu.GpuCommands.PacketOpcode.DispatchDirect or Gpu.GpuCommands.PacketOpcode.DispatchIndirect => Phase.DispatchPackets,
        Gpu.GpuCommands.PacketOpcode.WaitRegisterMemory or Gpu.GpuCommands.PacketOpcode.WaitRegisterMemory64 or
            Gpu.GpuCommands.PacketOpcode.MemorySemaphore or Gpu.GpuCommands.PacketOpcode.WaitOnCeCounter or
            Gpu.GpuCommands.PacketOpcode.WaitOnDeCounterDiff => Phase.WaitPackets,
        _ => Phase.OtherPackets,
    };

    private static Phase Charge(Phase next)
    {
        var now = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var previous = _current;
        _ticks[(int)previous] += now - _last;
        _allocated[(int)previous] += allocated - _lastAllocated;
        _last = now;
        _lastAllocated = allocated;
        _current = next;
        return previous;
    }

    internal static void ChargeBlocked(SuspendKind kind, ulong address, long idleTicks, long blockedTicks)
    {
        var writer = kind is SuspendKind.Memory or SuspendKind.Semaphore or SuspendKind.Atomic ? _writers.GetValueOrDefault(address & ~3UL) : (byte)0;
        lock (_blocked)
        {
            var entry = _blocked.GetValueOrDefault((kind, address, writer));
            _blocked[(kind, address, writer)] = (entry.IdleTicks + idleTicks, entry.Count + 1, entry.BlockedTicks + blockedTicks);
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    internal static IEnumerable<string> TakeReport(int frames)
    {
        if (!LoopEnabled)
            yield break;
        var perFrame = Math.Max(frames, 1);
        yield return FormattableString.Invariant(
            $"[PERF][FRONT_SPLIT] frames={frames} progress_ms={Ms(Interlocked.Exchange(ref ProgressTicks, 0)):F1} nowork_ms={Ms(Interlocked.Exchange(ref NoWorkTicks, 0)):F1} blocked_ms={Ms(Interlocked.Exchange(ref BlockedTicks, 0)):F1} blocked_unattributed_ms={Ms(Interlocked.Exchange(ref BlockedUnattributedTicks, 0)):F1} draws={Interlocked.Exchange(ref Draws, 0)} dispatches={Interlocked.Exchange(ref Dispatches, 0)} enqueues={Interlocked.Exchange(ref Enqueues, 0)} packets={Interlocked.Exchange(ref Packets, 0)} deferred_prep_ms={Ms(Interlocked.Exchange(ref DeferredPrepTicks, 0)):F1}");
        (SuspendKind Kind, ulong Address, byte Writer, long IdleTicks, long Count, long BlockedTicks)[] blocked;
        lock (_blocked)
        {
            blocked = _blocked.Select(pair => (pair.Key.Kind, pair.Key.Address, pair.Key.Writer, pair.Value.IdleTicks, pair.Value.Count, pair.Value.BlockedTicks))
                .OrderByDescending(entry => entry.IdleTicks).ToArray();
            _blocked.Clear();
        }

        if (blocked.Length != 0)
        {
            yield return "[PERF][FRONT_BLOCKED] " + string.Join(" ", blocked.Take(8).Select(entry => FormattableString.Invariant(
                $"{entry.Kind}@0x{entry.Address:X}:{WriterNames[entry.Writer]}=idle{Ms(entry.IdleTicks):F1}ms/n{entry.Count}/blocked{Ms(entry.BlockedTicks):F1}ms")));
        }

        if (!PhasesEnabled)
            yield break;
        var parts = new List<string>();
        for (var index = 0; index < (int)Phase.Count; index++)
        {
            var ticks = Interlocked.Exchange(ref _ticks[index], 0);
            var entries = Interlocked.Exchange(ref _entries[index], 0);
            var bytes = Interlocked.Exchange(ref _allocated[index], 0);
            if (ticks != 0)
                parts.Add(FormattableString.Invariant($"{(Phase)index}={Ms(ticks):F2}ms/n{entries}/kb{bytes / 1024}"));
        }

        yield return $"[PERF][FRONT_PHASES] frames={frames} " + string.Join(" ", parts);
    }
}
