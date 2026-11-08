// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_BDA_WALK_PROBE=1: what the bda_walk step of a device-address sweep spends. Per 5 s: sweeps, the
// possibly CPU-dirty ranges the tracker hands over, the registered buffers visited for them, the SynchronizeBuffer
// calls on a buffer with dirty pages and their bytes, and the sweep time split into SynchronizeBuffer and the rest
// (tracker iteration and registry lookups). Render thread only.
internal static class BdaWalkProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_BDA_WALK_PROBE") == "1";

    public static long Ranges, Buffers;
    private static long _sweeps, _syncs, _syncBytes, _syncTicks, _sweepTicks, _callbackTicks, _rangeBytes, _emptyRanges;
    private static long _window = Stopwatch.GetTimestamp();

    public static void NoteSync(long started, ulong bytes)
    {
        _syncs++;
        _syncBytes += (long)bytes;
        _syncTicks += Stopwatch.GetTimestamp() - started;
    }

    public static void NoteRange(long started, ulong bytes, bool noBuffers)
    {
        Ranges++;
        _rangeBytes += (long)bytes;
        if (noBuffers)
            _emptyRanges++;
        _callbackTicks += Stopwatch.GetTimestamp() - started;
    }

    public static void NoteSweep(long started)
    {
        var now = Stopwatch.GetTimestamp();
        _sweeps++;
        _sweepTicks += now - started;
        var seconds = (now - _window) / (double)Stopwatch.Frequency;
        if (seconds < 5)
            return;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / seconds;
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[BDA_WALK_PROBE] window_s={seconds:F1} sweeps_per_s={_sweeps / seconds:F1} ranges_per_sweep={Ranges / (double)Math.Max(1, _sweeps):F0} buffers_per_sweep={Buffers / (double)Math.Max(1, _sweeps):F0} syncs_per_sweep={_syncs / (double)Math.Max(1, _sweeps):F0} sync_kb_per_sweep={_syncBytes / 1024.0 / Math.Max(1, _sweeps):F0} walk_ms_per_s={Ms(_sweepTicks):F2} sync_ms_per_s={Ms(_syncTicks):F2} lookup_ms_per_s={Ms(_callbackTicks - _syncTicks):F2} tracker_ms_per_s={Ms(_sweepTicks - _callbackTicks):F2} empty_ranges_per_sweep={_emptyRanges / (double)Math.Max(1, _sweeps):F0} range_mb_per_sweep={_rangeBytes / 1048576.0 / Math.Max(1, _sweeps):F0} us_per_sync={_syncTicks * 1e6 / Stopwatch.Frequency / Math.Max(1, _syncs):F2}"));
        Ranges = Buffers = _sweeps = _syncs = _syncBytes = _syncTicks = _sweepTicks = _callbackTicks = _rangeBytes = _emptyRanges = 0;
        _window = now;
    }
}
