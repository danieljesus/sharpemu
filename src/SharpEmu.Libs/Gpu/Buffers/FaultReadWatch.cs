// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_FAULT_READ_EMULATE: guest ranges a faulting thread is watching for writes. A parked poll (=1) waits
// on its slot until a write touches the range; the =2 check counts the writes that touched it while the ordinary
// download ran, so a changed value is told apart from a wrong one. Writers note after their bytes are in the backing:
// the command-processor host writes, every backing write of the buffer cache (downloads, fills), ranges turning
// GPU-modified and guest CPU write faults. With no slot in use a note is one fence and one read.
internal static class FaultReadWatch
{
    private const int SlotCount = 16;

    private sealed class Slot
    {
        public ulong Begin, End;
        public int Hits, Used;
    }

    private static readonly Slot[] Slots = CreateSlots();
    private static int _active;

    private static Slot[] CreateSlots()
    {
        var slots = new Slot[SlotCount];
        for (var index = 0; index < SlotCount; index++)
        {
            slots[index] = new Slot();
        }

        return slots;
    }

    // -1 when every slot is taken (the caller then neither parks nor checks).
    public static int Begin(ulong address, ulong size)
    {
        for (var index = 0; index < SlotCount; index++)
        {
            var slot = Slots[index];
            if (Interlocked.CompareExchange(ref slot.Used, 1, 0) != 0)
            {
                continue;
            }

            slot.Begin = address;
            Volatile.Write(ref slot.End, address + size);
            Volatile.Write(ref slot.Hits, 0);
            Interlocked.Increment(ref _active);
            return index;
        }

        return -1;
    }

    public static int Hits(int index) => Volatile.Read(ref Slots[index].Hits);

    public static void End(int index)
    {
        var slot = Slots[index];
        Volatile.Write(ref slot.End, 0);
        Interlocked.Decrement(ref _active);
        Volatile.Write(ref slot.Used, 0);
    }

    // Until a write touches the range after seen was read, or the deadline (Stopwatch ticks); true when woken by a write.
    public static bool Wait(int index, int seen, long deadline)
    {
        var slot = Slots[index];
        lock (slot)
        {
            while (Volatile.Read(ref slot.Hits) == seen)
            {
                var remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    return false;
                }

                var milliseconds = (int)Math.Min(1000, Math.Max(1, (remaining * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency));
                Monitor.Wait(slot, milliseconds);
            }
        }

        return true;
    }

    // After the written bytes are in the backing. The fence orders that store before the read of _active against a
    // watcher's Begin (a locked increment) before its own read of the backing: one of the two sees the other.
    public static void Note(ulong address, ulong size)
    {
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _active) == 0)
        {
            return;
        }

        var end = address + size;
        foreach (var slot in Slots)
        {
            if (Volatile.Read(ref slot.Used) == 0 || address >= Volatile.Read(ref slot.End) || slot.Begin >= end)
            {
                continue;
            }

            Interlocked.Increment(ref slot.Hits);
            lock (slot)
            {
                Monitor.PulseAll(slot);
            }
        }
    }
}
