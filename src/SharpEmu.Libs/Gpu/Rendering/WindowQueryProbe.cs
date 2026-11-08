// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_WINDOW_QUERY_PROBE=1: what the window and HDR queries of every render-loop pass cost (minimized,
// drawable size, HDR state and the swapchain checks before taking a presentation), with the bytes the render thread
// allocated and the process's GC pause time in the same window. Render thread only; printed every 5 s.
internal static class WindowQueryProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_WINDOW_QUERY_PROBE") == "1";

    private static long _passes, _ticks, _allocated = -1;
    private static TimeSpan _paused = GC.GetTotalPauseDuration();
    private static long _window = Stopwatch.GetTimestamp();

    public static void Note(long started)
    {
        var now = Stopwatch.GetTimestamp();
        _passes++;
        _ticks += now - started;
        var seconds = (now - _window) / (double)Stopwatch.Frequency;
        if (seconds < 5)
            return;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var paused = GC.GetTotalPauseDuration();
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[WINDOW_QUERY_PROBE] window_s={seconds:F1} passes_per_s={_passes / seconds:F0} us_per_pass={_ticks * 1e6 / Stopwatch.Frequency / Math.Max(1, _passes):F2} ms_per_s={_ticks * 1000.0 / Stopwatch.Frequency / seconds:F2} render_alloc_mb_per_s={(_allocated < 0 ? 0 : allocated - _allocated) / 1048576.0 / seconds:F1} gc_pause_ms_per_s={(paused - _paused).TotalMilliseconds / seconds:F2} gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}"));
        _allocated = allocated;
        _paused = paused;
        _passes = _ticks = 0;
        _window = now;
    }
}
