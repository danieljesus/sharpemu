// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] SHARPEMU_VKCALL_PROBE=1 (step 1, day 2 of plan-grabador-lineal-2026-10-12.md): wall time of the driver call
// alone at the hot vkCmd* sites and in vkQueueSubmit2, per kind and per presented frame, and how often the recording
// buffer's native handle is taken, by caller (each would be a drain of a linear recorder). Reported with the render
// profile every window as [PERF][VKCALL] and [PERF][VKCALL_HANDLE].
internal static class VkCallProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_VKCALL_PROBE") == "1";

    // One stack trace per this many handle accesses; the per-caller counts are scaled back.
    private const int HandleSampleEvery = 32;

    internal enum Kind
    {
        Draw,
        DrawIndexed,
        DrawIndirect,
        Dispatch,
        DispatchIndirect,
        BindPipeline,
        BindVertexBuffers,
        BindIndexBuffer,
        DynamicState,
        BeginRendering,
        EndRendering,
        PushConstants,
        PushDescriptors,
        BindDescriptorSets,
        Barrier,
        CopyBuffer,
        FillBuffer,
        UpdateBuffer,
        Submit,
        Count,
    }

    private static readonly long[] _ticks = new long[(int)Kind.Count];
    private static readonly long[] _calls = new long[(int)Kind.Count];
    private static long _handles;
    private static readonly Dictionary<string, long> _handleCallers = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Stop(Kind kind, long start)
    {
        if (Enabled)
        {
            Interlocked.Add(ref _ticks[(int)kind], Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref _calls[(int)kind]);
        }
    }

    public static void CountHandle()
    {
        if (Interlocked.Increment(ref _handles) % HandleSampleEvery != 0)
        {
            return;
        }

        var frames = new StackTrace(2, false).GetFrames();
        var site = string.Join(" < ", frames.Take(3).Select(frame => frame.GetMethod() is { } method
            ? $"{method.DeclaringType?.Name}.{method.Name}"
            : "?"));
        lock (_handleCallers)
        {
            _handleCallers[site] = _handleCallers.GetValueOrDefault(site) + HandleSampleEvery;
        }
    }

    public static IEnumerable<string> TakeReport(double seconds, long frames)
    {
        if (!Enabled)
        {
            yield break;
        }

        var perFrame = frames > 0 ? frames : 1;
        var line = new StringBuilder();
        var outside = 0.0;
        var submit = 0.0;
        var parts = new List<string>();
        for (var index = 0; index < (int)Kind.Count; index++)
        {
            var ticks = Interlocked.Exchange(ref _ticks[index], 0);
            var calls = Interlocked.Exchange(ref _calls[index], 0);
            if (calls == 0)
            {
                continue;
            }

            var ms = ticks * 1000.0 / Stopwatch.Frequency / perFrame;
            if ((Kind)index == Kind.Submit)
            {
                submit = ms;
            }
            else
            {
                outside += ms;
            }

            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{(Kind)index}={ms:F3}ms/n{calls / (double)perFrame:F0}"));
        }

        line.Append(string.Create(CultureInfo.InvariantCulture,
            $"[PERF][VKCALL] window_s={seconds:F1} frames={frames} outside_submit={outside:F2}ms submit={submit:F2}ms "));
        line.Append(string.Join(' ', parts));
        yield return line.ToString();

        var handles = Interlocked.Exchange(ref _handles, 0);
        List<KeyValuePair<string, long>> callers;
        lock (_handleCallers)
        {
            callers = _handleCallers.OrderByDescending(pair => pair.Value).Take(15).ToList();
            _handleCallers.Clear();
        }

        yield return string.Create(CultureInfo.InvariantCulture,
            $"[PERF][VKCALL_HANDLE] window_s={seconds:F1} accesses_per_frame={handles / (double)perFrame:F0}");
        foreach (var (site, count) in callers)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"[PERF][VKCALL_HANDLE] per_frame~{count / (double)perFrame:F1} site={site}");
        }
    }
}
