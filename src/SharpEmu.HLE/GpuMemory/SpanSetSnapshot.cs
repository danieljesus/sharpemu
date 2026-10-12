// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// [local] The merged spans of a SpanSet at one moment, sorted by start. Never modified, so any thread may read it.
public sealed class SpanSetSnapshot
{
    private readonly ulong[] _starts;
    private readonly ulong[] _ends;

    public static readonly SpanSetSnapshot Empty = new([], []);

    internal SpanSetSnapshot(ulong[] starts, ulong[] ends)
    {
        _starts = starts;
        _ends = ends;
    }

    public int Count => _starts.Length;

    // The same answer as SpanSet.Contains: one span holds the whole range.
    public bool Contains(ulong address, ulong size)
    {
        if (size == 0 || address + size < address)
        {
            return false;
        }

        var low = 0;
        var high = _starts.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (_starts[mid] <= address)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low > 0 && _ends[low - 1] >= address + size;
    }
}
