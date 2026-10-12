// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_PRECISE_WRITE_TICKS (0 off, 1 on, 2 off but each read checked against a precise copy, 3 on with each
// narrowed window checked against a copy after the block ticks): the last
// tick that recorded a GPU write over a guest range, from a ring of every recorded write in guest addresses.
// The 16 KB block ticks of GpuBuffer also move when a neighbour in the block is written (an upload, a 4-byte
// counter), so a table word written frames ago waited for the batch being recorded. Entries are appended in
// tick order from the thread that records commands, so the newest overlapping entry holds the answer; once
// the ring wraps, the tick of the newest evicted entry bounds anything older.
internal static class GpuWriteHistory
{
    public static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PRECISE_WRITE_TICKS"), out var mode) ? mode : 0;

    private const int Capacity = 1 << 16;
    private static readonly (ulong Begin, ulong End, ulong Tick)[] _ring = Mode != 0 ? new (ulong, ulong, ulong)[Capacity] : [];
    private static long _count;
    private static ulong _evictedTick;
    private static int _owner;
    private static volatile bool _poisoned;

    public static bool Enabled => Mode != 0 && !_poisoned;

    public static bool Narrows => (Mode == 1 || Mode == 3) && !_poisoned;

    public static void Note(ulong address, ulong size, ulong tick)
    {
        if (Mode == 0 || size == 0)
        {
            return;
        }

        var thread = Environment.CurrentManagedThreadId;
        if (_owner == 0)
        {
            _owner = thread;
        }
        else if (_owner != thread && !_poisoned)
        {
            _poisoned = true;
            Console.Error.WriteLine($"[PRECISE_WRITE_TICKS] write recorded from a second thread ({thread}, owner {_owner}); precise ticks off");
        }

        var index = (int)(_count & (Capacity - 1));
        if (_count >= Capacity && _ring[index].Tick > _evictedTick)
        {
            _evictedTick = _ring[index].Tick;
        }

        _ring[index] = (address, address + size, tick);
        _count++;
    }

    // The ranges inside [begin, end) written after tick `after`. False when the ring no longer reaches back to it.
    public static bool CollectWrittenAfter(ulong begin, ulong end, ulong after, List<(ulong Begin, ulong End)> output)
    {
        var count = Math.Min(_count, Capacity);
        for (long back = 1; back <= count; back++)
        {
            var entry = _ring[(int)((_count - back) & (Capacity - 1))];
            if (entry.Tick <= after)
            {
                return true;
            }

            if (entry.Begin < end && begin < entry.End)
            {
                output.Add((Math.Max(entry.Begin, begin), Math.Min(entry.End, end)));
            }
        }

        return _evictedTick <= after;
    }

    // An upper bound of the last tick that wrote any byte of [address, address + size); 0 when no write is known.
    public static ulong LastWriteTick(ulong address, ulong size)
    {
        var end = address + size;
        var count = Math.Min(_count, Capacity);
        for (long back = 1; back <= count; back++)
        {
            var entry = _ring[(int)((_count - back) & (Capacity - 1))];
            if (entry.Begin < end && address < entry.End)
            {
                return Math.Max(entry.Tick, _evictedTick);
            }
        }

        return _evictedTick;
    }
}
