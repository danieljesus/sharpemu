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

    // [local] SHARPEMU_READBACK_PRECISE_WAIT=1 also records the ring, without narrowing any tick or window: only the
    // render thread's small reads ask it (GuestBufferCache.TryPreciseRead).
    public static readonly bool PreciseWaitRequested = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_PRECISE_WAIT") is "1" or "2";

    // [local] Lets the precise-read tests record the writes of one thread without the environment switches.
    internal static int ForceRecordingThread;

    public static bool Recording => Mode != 0 || PreciseWaitRequested || ConservativeRecords.PreciseRecording; // [local] SHARPEMU_CONSERVATIVE_RECORDS=3/4, SHARPEMU_PRECISE_TABLE_READ

    public static bool Queryable => (Recording || ForceRecordingThread != 0) && !_poisoned;

    private const int Capacity = 1 << 16;
    private static (ulong Begin, ulong End, ulong Tick)[] _ring = Mode != 0 || PreciseWaitRequested ? new (ulong, ulong, ulong)[Capacity] : [];
    private static long _count;
    private static ulong _evictedTick;
    private static int _owner;
    private static volatile bool _poisoned;

    public static bool Enabled => Mode != 0 && !_poisoned;

    public static bool Narrows => (Mode == 1 || Mode == 3) && !_poisoned;

    public static void Note(ulong address, ulong size, ulong tick)
    {
        if (size == 0 || (!Recording && (ForceRecordingThread == 0 || ForceRecordingThread != Environment.CurrentManagedThreadId)))
        {
            return;
        }

        if (_ring.Length == 0)
        {
            _ring = new (ulong, ulong, ulong)[Capacity];
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

        if (Diagnostics.TableDirtyProbe.Overlaps(address, size)) // [local] SHARPEMU_TABLE_DIRTY_PROBE: who stamps a watched word's precise tick
            Diagnostics.TableDirtyProbe.Note($"history program={TableWordMemo.CurrentProgram:X16}", address, size, tick);
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

    // [local] Set by the scan-limited LastWriteTick when it stopped before finding a write over the range.
    public static bool LastScanExhausted;

    // [local] As LastWriteTick, looking back at most `scan` entries: entries are in tick order, so a range with no
    // write among them was last written no later than the oldest one looked at.
    public static ulong LastWriteTick(ulong address, ulong size, int scan)
    {
        LastScanExhausted = false;
        var end = address + size;
        var count = Math.Min(Math.Min(_count, Capacity), scan);
        for (long back = 1; back <= count; back++)
        {
            var entry = _ring[(int)((_count - back) & (Capacity - 1))];
            if (entry.Begin < end && address < entry.End)
            {
                return Math.Max(entry.Tick, _evictedTick);
            }
        }

        if (count < Math.Min(_count, Capacity))
        {
            LastScanExhausted = true;
            return Math.Max(_ring[(int)((_count - count) & (Capacity - 1))].Tick, _evictedTick);
        }

        return _evictedTick;
    }

    // [local] Tests only: forget the recorded writes and the recording thread.
    internal static void ResetForTests()
    {
        Array.Clear(_ring);
        _count = 0;
        _evictedTick = 0;
        _owner = 0;
        _poisoned = false;
    }
}
