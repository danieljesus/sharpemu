// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// [local] SHARPEMU_DIRTY_LOG_PROBE=1: sizes an incremental CPU-dirty log for the BDA sweep before building one. Every
// transition of pages to CPU-dirty is recorded by its gate (a fault or marking through ChangeState, an emulator write
// through MarkCpuWriteKeepingProtection, a region born dirty), and each BDA sweep takes the ranges recorded since the
// previous one and counts how many overlap a registered buffer (GuestBufferCache.PrepareBda). Off by default.
public static class DirtyLogProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DIRTY_LOG_PROBE") == "1";

    public enum Gate { Change, KeepProtection, Birth, Count }

    private const int Capacity = 1 << 20;
    private static readonly object Gate_ = new();
    private static List<(ulong Address, ulong Size)> _ranges = new(4096);
    private static List<(ulong Address, ulong Size)> _spare = new(4096);
    private static readonly long[] Calls = new long[(int)Gate.Count];
    private static readonly long[] Pages = new long[(int)Gate.Count];
    private static long _overflows;

    public static void Note(Gate gate, ulong address, ulong size)
    {
        Interlocked.Increment(ref Calls[(int)gate]);
        Interlocked.Add(ref Pages[(int)gate], (long)((size + TrackerLayout.PageBytes - 1) / TrackerLayout.PageBytes));
        lock (Gate_)
        {
            if (_ranges.Count < Capacity)
                _ranges.Add((address, size));
            else
                _overflows++;
        }
    }

    // The ranges recorded since the last call; the list is the caller's until the next call.
    public static List<(ulong Address, ulong Size)> Take()
    {
        lock (Gate_)
        {
            (_ranges, _spare) = (_spare, _ranges);
            _ranges.Clear();
            return _spare;
        }
    }

    public static string TakeCounters()
    {
        var text = FormattableString.Invariant(
            $"change={Interlocked.Exchange(ref Calls[0], 0)}/{Interlocked.Exchange(ref Pages[0], 0)} keep={Interlocked.Exchange(ref Calls[1], 0)}/{Interlocked.Exchange(ref Pages[1], 0)} birth={Interlocked.Exchange(ref Calls[2], 0)}/{Interlocked.Exchange(ref Pages[2], 0)} overflows={Interlocked.Exchange(ref _overflows, 0)}");
        return text;
    }
}
