// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] Dormant detector of the GTA V "1-4 FPS" mode (d4G3, d4dg2: GPU 96-100 %, CPU 4-9 %, from ~95 s to the end).
// With SHARPEMU_FPS_LOG=1 and no render profiler, a 5 s window under MinimumFps after StartSeconds arms GPU timestamps
// for ArmedWindows windows: the scheduler's command profile (created dormant, recording nothing until then) writes a
// marker after every draw and dispatch and prints [PERF][GPU_BUSY] and the top [PERF][GPU_INTERVAL] lines with the
// shader of each pipeline, which shows which pass holds the GPU. At most MaxArms per session.
// SHARPEMU_SLOW_MODE_PROBE=0 turns it off (no profile object at all).
internal static class SlowModeProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_SLOW_MODE_PROBE") != "0";
    private const double MinimumFps = 8;
    private const double StartSeconds = 60;
    private const int ArmedWindows = 3;
    private const int MaxArms = 2;
    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime;
    private static volatile bool _armed;
    private static int _armedWindows;
    private static int _arms;

    internal static bool Armed => _armed;

    internal static void NoteWindow(double seconds, long frames)
    {
        if (!Enabled || seconds <= 0)
            return;
        var uptime = (DateTime.Now - Started).TotalSeconds;
        var fps = frames / seconds;
        if (_armed)
        {
            if (++_armedWindows < ArmedWindows)
                return;
            _armed = false;
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GPU][SLOW_MODE] disarmed t={uptime:F1} fps={fps:F2}"));
            return;
        }

        if (uptime < StartSeconds || fps >= MinimumFps || _arms >= MaxArms)
            return;
        _arms++;
        _armedWindows = 0;
        _armed = true;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GPU][SLOW_MODE] armed t={uptime:F1} fps={fps:F2} arm={_arms}"));
    }
}
