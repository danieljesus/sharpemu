// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] Dormant detector of the GTA V "1-4 FPS" mode (d4G3, d4dg2: GPU 96-100 %, CPU 4-9 %, from ~95 s to the end).
// With SHARPEMU_FPS_LOG=1 and no render profiler, two consecutive 5 s windows under MinimumFps after StartSeconds (past
// the load and the player taking control, whose windows dip to ~6 FPS; a ~3.2 s pipeline compile makes one window of
// ~2-3 FPS, so one alone does not arm) arm GPU timestamps for ArmedWindows windows: the scheduler's command profile
// (created dormant, recording nothing until then) writes a marker after every draw and dispatch and prints
// [PERF][GPU_BUSY] and the top [PERF][GPU_INTERVAL] lines with the shader of each pipeline, which shows which pass holds
// the GPU. At most MaxArms per session.
// Off by default; SHARPEMU_SLOW_MODE_PROBE=1 turns it on (without it there is no profile object at all).
internal static class SlowModeProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_SLOW_MODE_PROBE") == "1";
    private const double MinimumFps = 4;
    private const double StartSeconds = 130;
    private const int ArmedWindows = 3;
    private const int MaxArms = 2;
    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime;
    private static volatile bool _armed;
    private static int _armedWindows;
    private static int _arms;
    private static int _slowWindows;

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

        _slowWindows = uptime >= StartSeconds && fps < MinimumFps ? _slowWindows + 1 : 0;
        if (_slowWindows < 2 || _arms >= MaxArms)
            return;
        _arms++;
        _armedWindows = 0;
        _armed = true;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GPU][SLOW_MODE] armed t={uptime:F1} fps={fps:F2} arm={_arms}"));
    }
}
