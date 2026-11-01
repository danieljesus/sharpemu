// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.Gpu.Scheduling;

// [local] SHARPEMU_SUBMIT_THREAD counters, reported with the render profile: the submit thread's time joining deferred
// uploads and in vkQueueSubmit2, the queue depth, and the recording thread's waits for submissions.
internal static class SubmitThreadStats
{
    private static long _submits;
    private static long _joinTicks;
    private static long _submitTicks;
    private static long _maxDepth;
    private static long _waits;
    private static long _waitTicks;

    public static void Sealed(int depth)
    {
        var seen = Volatile.Read(ref _maxDepth);
        while (depth > seen)
        {
            var previous = Interlocked.CompareExchange(ref _maxDepth, depth, seen);
            if (previous == seen)
            {
                break;
            }

            seen = previous;
        }
    }

    public static void Submitted(long joinTicks, long submitTicks)
    {
        Interlocked.Increment(ref _submits);
        Interlocked.Add(ref _joinTicks, joinTicks);
        Interlocked.Add(ref _submitTicks, submitTicks);
    }

    // [local] SHARPEMU_SUBMIT_BATCH: queue calls and the buffers they carried, reported every 4096 calls.
    private static long _batchCalls, _batchItems, _batchMulti, _batchMax;

    public static void Batched(int items)
    {
        _batchItems += items;
        if (items > 1) _batchMulti++;
        if (items > _batchMax) _batchMax = items;
        if ((++_batchCalls & 4095) != 0)
        {
            return;
        }

        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[GPU][SUBMIT_BATCH] calls={_batchCalls} buffers={_batchItems} multi_calls={_batchMulti} buffers_per_call={_batchItems / (double)_batchCalls:F3} max={_batchMax}"));
        _batchCalls = _batchItems = _batchMulti = _batchMax = 0;
    }

    public static void Waited(long ticks)
    {
        Interlocked.Increment(ref _waits);
        Interlocked.Add(ref _waitTicks, ticks);
    }

    public static string? TakeReport(double seconds, long frames)
    {
        var submits = Interlocked.Exchange(ref _submits, 0);
        var joinTicks = Interlocked.Exchange(ref _joinTicks, 0);
        var submitTicks = Interlocked.Exchange(ref _submitTicks, 0);
        var maxDepth = Interlocked.Exchange(ref _maxDepth, 0);
        var waits = Interlocked.Exchange(ref _waits, 0);
        var waitTicks = Interlocked.Exchange(ref _waitTicks, 0);
        if (submits == 0 && waits == 0)
        {
            return null;
        }

        var perFrame = frames > 0 ? frames : 1;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / perFrame;
        return string.Create(CultureInfo.InvariantCulture,
            $"[PERF][SUBMIT_THREAD] window_s={seconds:F1} frames={frames} submits={submits} " +
            $"join_ms_per_frame={Ms(joinTicks):F2} submit_ms_per_frame={Ms(submitTicks):F2} max_depth={maxDepth} " +
            $"render_waits={waits} render_wait_ms_per_frame={Ms(waitTicks):F2}");
    }
}
