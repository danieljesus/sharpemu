// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_IDLE_PROBE=1: the render thread's idle waits in WaitForRenderWork without the phase profiler
// (SHARPEMU_PROFILE_RENDER inflates the render thread enough to hide them): time and count per kind (blocked
// commands, pending presentation, no queued work), split by GPU work in flight and by signaled or timed out.
// One Stopwatch read per wait; printed every 5 s with the frames presented in the window.
internal static class IdleProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_IDLE_PROBE") == "1";

    private static readonly long[] Ticks = new long[12];
    private static readonly long[] Counts = new long[12];
    private static long _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
    private static long _frames;

    public static void NoteFrame()
    {
        if (Enabled)
            _frames++;
    }

    public static void Note(RenderPhaseProfile.Phase phase, bool gpuInFlight, bool signaled, long ticks)
    {
        var kind = phase switch
        {
            RenderPhaseProfile.Phase.IdleBlockedCommands => 0,
            RenderPhaseProfile.Phase.IdlePendingPresentation => 1,
            _ => 2,
        };
        var index = kind * 4 + (gpuInFlight ? 2 : 0) + (signaled ? 1 : 0);
        Ticks[index] += ticks;
        Counts[index]++;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = (now - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (seconds < 5)
            return;
        var frames = Math.Max(1, _frames);
        string[] names = ["blocked", "presentation", "no_work"];
        var parts = new List<string>();
        for (var slot = 0; slot < Ticks.Length; slot++)
        {
            if (Counts[slot] == 0)
                continue;
            parts.Add(FormattableString.Invariant(
                $"{names[slot / 4]}{((slot & 2) != 0 ? "_gpu" : "_idlegpu")}{((slot & 1) != 0 ? "_sig" : "_timeout")}={Ticks[slot] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames:F3}ms/n{Counts[slot] / (double)frames:F1}"));
        }

        Console.Error.WriteLine(FormattableString.Invariant($"[IDLE_PROBE] window_s={seconds:F1} frames={_frames} ") + string.Join(" ", parts));
        Array.Clear(Ticks);
        Array.Clear(Counts);
        _frames = 0;
        _windowStart = now;
    }
}
