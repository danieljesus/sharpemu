// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class TickTimeline
{
    private readonly IGpuTickDevice _device;
    private ulong _gpuTick;
    private ulong _currentTick = 1;

    public TickTimeline(IGpuTickDevice device) => _device = device;

    // [local] probe: which call sites make the CPU wait for the GPU, and for how long.
    private static readonly bool WaitProbe = Environment.GetEnvironmentVariable("SHARPEMU_WAIT_PROBE") == "1";
    private static readonly Dictionary<string, (int Count, double Ms, double Behind)> _waits = new();
    private static long _waitReport = System.Diagnostics.Stopwatch.GetTimestamp();

    private void RecordWait(ulong tick, long start)
    {
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
        var site = string.Join(" < ", frames.Take(4).Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?"));
        lock (_waits)
        {
            var entry = _waits.GetValueOrDefault(site);
            _waits[site] = (entry.Count + 1, entry.Ms + ms, entry.Behind + (CurrentTick - tick));
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_waitReport).TotalSeconds >= 10)
            {
                foreach (var (key, value) in _waits.OrderByDescending(pair => pair.Value.Ms))
                    Console.Error.WriteLine($"[WAIT_PROBE] count={value.Count} ms={value.Ms:F0} avg_ticks_behind={value.Behind / value.Count:F1} site={key}");
                _waits.Clear();
                _waitReport = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }
    }

    public ulong CurrentTick => Volatile.Read(ref _currentTick);

    public ulong CompletedTick => Volatile.Read(ref _gpuTick);

    public ulong Handle => _device.TimelineHandle;

    public bool IsTickComplete(ulong tick) => CompletedTick >= tick;

    public ulong ReserveTick() => Interlocked.Increment(ref _currentTick) - 1;

    public void RefreshCompletedTick()
    {
        var counter = _device.ReadTimeline();
        var known = Volatile.Read(ref _gpuTick);
        while (known < counter)
        {
            var seen = Interlocked.CompareExchange(ref _gpuTick, counter, known);
            if (seen == known)
            {
                return;
            }

            known = seen;
        }
    }

    public void Wait(ulong tick)
    {
        if (IsTickComplete(tick))
        {
            return;
        }

        RefreshCompletedTick();
        if (IsTickComplete(tick))
        {
            return;
        }

        var probeStart = WaitProbe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
        var waited = _device.TryWaitTimeline(tick, out var failure);
        if (WaitProbe) RecordWait(tick, probeStart); // [local]
        if (!waited)
        {
            throw SubmissionScheduler.Fatal($"vkWaitSemaphores failed: {failure}, tick={tick}");
        }

        RefreshCompletedTick();
    }
}
