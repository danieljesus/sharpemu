// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// [local] Call sites of the GPU waits the render thread's profile counts (SHARPEMU_RENDER_WAIT_PROBE=1).
internal static class RenderWaitProbe
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_RENDER_WAIT_PROBE") == "1";
    private static readonly Dictionary<string, (int Count, double Ms)> Sites = new();
    private static long _report = System.Diagnostics.Stopwatch.GetTimestamp();

    public static void Record(string kind, long started)
    {
        if (!Enabled || !RenderPhaseProfile.DetailMeasurementsEnabled)
            return;
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
        var site = kind + ": " + string.Join(" < ", frames.Take(6).Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?"));
        lock (Sites)
        {
            var entry = Sites.GetValueOrDefault(site);
            Sites[site] = (entry.Count + 1, entry.Ms + ms);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_report).TotalSeconds < 10)
                return;
            foreach (var (key, value) in Sites.OrderByDescending(pair => pair.Value.Ms).Take(8))
                Console.Error.WriteLine($"[RENDER_WAIT] ms={value.Ms:F0} count={value.Count} site={key}");
            Sites.Clear();
            _report = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }
}
