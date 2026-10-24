// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_RENDER2_PROBE=1: what the render thread spends on the GPU wait spins (by caller and outcome), on
// relay commands (by delegate) and on the mapped-size memo, printed with the render profile's window.
internal static class Render2Probe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_RENDER2_PROBE") == "1";

    public enum SpinSite { Readback, Tick, Count }

    private static readonly long[] SpinCalls = new long[(int)SpinSite.Count];
    private static readonly long[] SpinHits = new long[(int)SpinSite.Count];
    private static readonly long[] SpinHitTicks = new long[(int)SpinSite.Count];
    private static readonly long[] SpinMissTicks = new long[(int)SpinSite.Count];
    private static readonly long[] BlockedTicks = new long[(int)SpinSite.Count];
    private static long _mappedHits, _mappedMisses, _mappedResets;
    internal static long MappedKeepHits, MappedKeepChecked, MappedKeepMismatches; // SHARPEMU_MAPPED_SIZE_KEEP
    internal static long SizeReuseChecked, SizeReuseMismatches; // SHARPEMU_BUFFER_SIZE_REUSE=2
    internal static long MappedGenerationChanges; // generation moves the mapped-size memo saw
    private static readonly Dictionary<string, (long Count, long Ticks)> Relay = new();

    [ThreadStatic] private static int t_render;

    // Only the render thread's waits are counted.
    private static bool OnRender()
    {
        if (t_render == 0)
            t_render = Thread.CurrentThread.Name == "SharpEmu Vulkan VideoOut" ? 1 : 2;
        return t_render == 1;
    }

    public static void NoteSpin(SpinSite site, bool hit, long ticks)
    {
        if (!OnRender())
            return;
        Interlocked.Increment(ref SpinCalls[(int)site]);
        if (hit)
        {
            Interlocked.Increment(ref SpinHits[(int)site]);
            Interlocked.Add(ref SpinHitTicks[(int)site], ticks);
        }
        else
        {
            Interlocked.Add(ref SpinMissTicks[(int)site], ticks);
        }
    }

    public static void NoteBlocked(SpinSite site, long ticks)
    {
        if (OnRender())
            Interlocked.Add(ref BlockedTicks[(int)site], ticks);
    }

    public static void NoteMapped(bool hit)
    {
        if (hit) _mappedHits++;
        else _mappedMisses++;
    }

    public static void NoteMappedReset() => _mappedResets++;

    public static void NoteRelay(Delegate command, long ticks)
    {
        var name = $"{command.Method.DeclaringType?.Name}.{command.Method.Name}";
        lock (Relay)
        {
            var entry = Relay.GetValueOrDefault(name);
            Relay[name] = (entry.Count + 1, entry.Ticks + ticks);
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static string? TakeReport(long frames)
    {
        if (!Enabled)
            return null;
        var f = (double)Math.Max(frames, 1);
        var text = new System.Text.StringBuilder("[PERF][RENDER2]");
        for (var site = 0; site < (int)SpinSite.Count; site++)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" {(SpinSite)site}: spins={Interlocked.Exchange(ref SpinCalls[site], 0) / f:F2} hits={Interlocked.Exchange(ref SpinHits[site], 0) / f:F2} hit_ms={Ms(Interlocked.Exchange(ref SpinHitTicks[site], 0)) / f:F3} miss_ms={Ms(Interlocked.Exchange(ref SpinMissTicks[site], 0)) / f:F3} blocked_ms={Ms(Interlocked.Exchange(ref BlockedTicks[site], 0)) / f:F3}");
        }

        text.Append(CultureInfo.InvariantCulture,
            $" mapped_hits={_mappedHits / f:F0} mapped_misses={_mappedMisses / f:F0} mapped_resets={_mappedResets / f:F1}");
        _mappedHits = _mappedMisses = _mappedResets = 0;
        text.Append(CultureInfo.InvariantCulture,
            $" mapped_keep_hits={Interlocked.Exchange(ref MappedKeepHits, 0) / f:F0} mapped_keep_checked={Interlocked.Exchange(ref MappedKeepChecked, 0) / f:F0} mapped_keep_mismatches={Interlocked.Read(ref MappedKeepMismatches)}");
        text.Append(CultureInfo.InvariantCulture,
            $" size_reuse_checked={Interlocked.Exchange(ref SizeReuseChecked, 0) / f:F0} size_reuse_mismatches={Interlocked.Read(ref SizeReuseMismatches)} mapped_generation_changes={Interlocked.Exchange(ref MappedGenerationChanges, 0) / f:F2}");
        lock (Relay)
        {
            text.Append(" relay=[");
            text.AppendJoin(",", Relay.OrderByDescending(pair => pair.Value.Ticks).Take(12)
                .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}:{pair.Value.Count / f:F1}/{Ms(pair.Value.Ticks) / f:F3}ms")));
            text.Append(']');
            Relay.Clear();
        }

        return text.ToString();
    }
}
