// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_FPS_LOG=1 without SHARPEMU_PROFILE_RENDER: frames presented per 5 s window, in the
// [PERF][RENDER_MS] format the measurement scripts read, without timing any render phase.
internal static class FpsLog
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_FPS_LOG") == "1" && !RenderPhaseProfile.Enabled;

    private static long _windowStart;
    private static long _frames;

    public static void FramePresented()
    {
        IdleProbe.NoteFrame(); // [local]
        if (!Enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_windowStart == 0)
        {
            _windowStart = now;
        }

        _frames++;
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (seconds < 5.0)
        {
            return;
        }

        Console.Error.WriteLine(FormattableString.Invariant($"[PERF][RENDER_MS] window_s={seconds:F1} frames={_frames}"));
        _windowStart = now;
        _frames = 0;
    }
}
