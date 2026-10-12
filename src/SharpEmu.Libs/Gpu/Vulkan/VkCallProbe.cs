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

    // SHARPEMU_VKCALL_PROBE_CALLERS=1 also samples the handle's callers: one stack trace per this many accesses, the
    // counts scaled back. It costs ~16 ms/frame and inflates the call times, so take the call times without it.
    private static readonly bool SampleCallers = Environment.GetEnvironmentVariable("SHARPEMU_VKCALL_PROBE_CALLERS") == "1";
    private const int HandleSampleEvery = 32;

    // SHARPEMU_VKCALL_PROBE_BARRIERS=1 (with the probe) counts vkCmdPipelineBarrier2 per call site and how many came
    // right after another barrier on the same command buffer with no probed command in between (an upper bound on
    // what merging adjacent barriers could save: unprobed commands such as image copies and clears are not seen).
    public static readonly bool CountBarrierSites = Enabled &&
        Environment.GetEnvironmentVariable("SHARPEMU_VKCALL_PROBE_BARRIERS") == "1";

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
    private static readonly Dictionary<(string File, int Line), long[]> _barrierSites = new();
    [ThreadStatic]
    private static nint _lastBarrierCommand;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Stop(Kind kind, long start)
    {
        if (Enabled)
        {
            Interlocked.Add(ref _ticks[(int)kind], Stopwatch.GetTimestamp() - start);
            Interlocked.Increment(ref _calls[(int)kind]);
            if (kind != Kind.Barrier)
            {
                _lastBarrierCommand = 0;
            }
        }
    }

    // Per site: calls, calls adjacent to the previous barrier, memory, buffer and image barriers.
    public static void CountBarrier(nint command, uint memory, uint buffer, uint image,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!CountBarrierSites)
        {
            return;
        }

        var adjacent = _lastBarrierCommand == command ? 1 : 0;
        _lastBarrierCommand = command;
        lock (_barrierSites)
        {
            if (!_barrierSites.TryGetValue((file, line), out var counts))
            {
                counts = new long[5];
                _barrierSites[(file, line)] = counts;
            }

            counts[0]++;
            counts[1] += adjacent;
            counts[2] += memory;
            counts[3] += buffer;
            counts[4] += image;
        }
    }

    public static void CountHandle()
    {
        if (Interlocked.Increment(ref _handles) % HandleSampleEvery != 0 || !SampleCallers)
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

        if (CountBarrierSites)
        {
            List<KeyValuePair<(string File, int Line), long[]>> sites;
            lock (_barrierSites)
            {
                sites = _barrierSites.Where(pair => pair.Value[0] != 0).OrderByDescending(pair => pair.Value[0])
                    .Select(pair => new KeyValuePair<(string File, int Line), long[]>(pair.Key, (long[])pair.Value.Clone())).ToList();
                foreach (var counts in _barrierSites.Values)
                {
                    Array.Clear(counts);
                }
            }

            var total = sites.Sum(pair => pair.Value[0]);
            var adjacentTotal = sites.Sum(pair => pair.Value[1]);
            yield return string.Create(CultureInfo.InvariantCulture,
                $"[PERF][VKCALL_BARRIER] window_s={seconds:F1} calls_per_frame={total / (double)perFrame:F0} adjacent_per_frame={adjacentTotal / (double)perFrame:F0}");
            foreach (var (site, counts) in sites)
            {
                yield return string.Create(CultureInfo.InvariantCulture,
                    $"[PERF][VKCALL_BARRIER] per_frame={counts[0] / (double)perFrame:F1} adjacent={counts[1] / (double)perFrame:F1} mem={counts[2] / (double)perFrame:F1} buf={counts[3] / (double)perFrame:F1} img={counts[4] / (double)perFrame:F1} site={Path.GetFileName(site.File)}:{site.Line}");
            }
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
