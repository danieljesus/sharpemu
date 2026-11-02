// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// [local] SHARPEMU_DEFERRED_PROTECT_VERIFY (diagnostic): the last writes made through the backing alias, numbered in
// the order they landed. They bypass the guest view's protection and may leave a page clean on purpose (command-
// processor writes that also update the device buffer, downloads), so the deferred upload verify does not count a
// page one of them touched after the job began.
public static class BackingWriteLog
{
    public static bool Enabled { get; set; }

    private const int Capacity = 1 << 16;
    private static readonly (ulong Begin, ulong End)[] Entries = new (ulong, ulong)[Capacity];
    private static long _sequence;
    private static readonly object Gate = new();

    public static long Sequence => Volatile.Read(ref _sequence);

    public static void Note(ulong address, ulong size)
    {
        lock (Gate)
        {
            Entries[(int)(_sequence & (Capacity - 1))] = (address, address + size);
            Volatile.Write(ref _sequence, _sequence + 1);
        }
    }

    // Whether a write numbered at or after since overlaps the range; true also when the log no longer holds them all.
    public static bool WrittenSince(long since, ulong address, ulong size)
    {
        lock (Gate)
        {
            if (_sequence - since > Capacity)
            {
                return true;
            }

            for (var number = since; number < _sequence; number++)
            {
                var (begin, end) = Entries[(int)(number & (Capacity - 1))];
                if (begin < address + size && address < end)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
