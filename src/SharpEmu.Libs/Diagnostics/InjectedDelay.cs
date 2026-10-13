// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

// [local] Sensitivity probe for the critical path: SHARPEMU_INJECT_RENDER_US / SHARPEMU_INJECT_FRONT_US busy-spin that
// many microseconds once per guest flip on the render thread (when it executes the flip) or on the front (when it
// interprets the flip). The FPS change per injected millisecond tells how much of each thread's time is on the frame's
// critical path. Diagnostic only: off unless set.
internal static class InjectedDelay
{
    internal static readonly long RenderTicks = Read("SHARPEMU_INJECT_RENDER_US");
    internal static readonly long FrontTicks = Read("SHARPEMU_INJECT_FRONT_US");

    private static long Read(string name) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var microseconds) && microseconds > 0
            ? microseconds * Stopwatch.Frequency / 1_000_000
            : 0;

    internal static void Spin(long ticks)
    {
        if (ticks <= 0)
        {
            return;
        }

        var end = Stopwatch.GetTimestamp() + ticks;
        while (Stopwatch.GetTimestamp() < end)
        {
            Thread.SpinWait(16);
        }
    }
}
