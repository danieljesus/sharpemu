// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.GpuMemory;

// [local] SHARPEMU_UPLOAD_JOB_PROBE=1 (diagnostic, off by default): splits the deferred upload jobs' time (queued,
// region-lock wait, protection decided under the lock, kernel call, copy) and counts the contended waits on the
// regions' protect gate (SHARPEMU_PROTECT_OUTSIDE_LOCK), by the deferred upload workers and by every other thread.
public static class DeferredProtectProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_UPLOAD_JOB_PROBE") == "1";

    public enum Part
    {
        Queued,
        LockWait,
        UnderLock,
        Call,
        Copy,
        Verify,
        Count,
    }

    private static readonly string[] Names = ["queued", "lock_wait", "under_lock", "call", "copy", "verify"];
    private static readonly long[] Ticks = new long[(int)Part.Count];
    private static long _jobs, _regions, _workerGateWaits, _workerGateTicks, _otherGateWaits, _otherGateTicks;

    [ThreadStatic] private static bool t_worker;

    public static void MarkWorkerThread() => t_worker = true;

    public static void Add(Part part, long ticks)
    {
        if (Enabled)
            Interlocked.Add(ref Ticks[(int)part], ticks);
    }

    public static void NoteJob(int regions)
    {
        if (!Enabled)
            return;
        Interlocked.Increment(ref _jobs);
        Interlocked.Add(ref _regions, regions);
    }

    public static void NoteGateWait(long ticks)
    {
        if (!Enabled)
            return;
        if (t_worker)
        {
            Interlocked.Increment(ref _workerGateWaits);
            Interlocked.Add(ref _workerGateTicks, ticks);
        }
        else
        {
            Interlocked.Increment(ref _otherGateWaits);
            Interlocked.Add(ref _otherGateTicks, ticks);
        }
    }

    // Per frame (frames > 0) over the window; resets the counters.
    public static string TakeReport(long frames)
    {
        var perFrame = 1000.0 / Stopwatch.Frequency / Math.Max(frames, 1);
        var parts = new string[(int)Part.Count];
        for (var index = 0; index < parts.Length; index++)
            parts[index] = FormattableString.Invariant($"{Names[index]}={Interlocked.Exchange(ref Ticks[index], 0) * perFrame:F3}");
        var jobs = Interlocked.Exchange(ref _jobs, 0);
        return FormattableString.Invariant(
            $"[GPU][UPLOAD_JOB_PROBE] frames={frames} jobs={jobs / (double)Math.Max(frames, 1):F1} regions={Interlocked.Exchange(ref _regions, 0) / (double)Math.Max(jobs, 1):F2} ms_per_frame: {string.Join(" ", parts)} gate_worker={Interlocked.Exchange(ref _workerGateWaits, 0) / (double)Math.Max(frames, 1):F1}/{Interlocked.Exchange(ref _workerGateTicks, 0) * perFrame:F3} gate_other={Interlocked.Exchange(ref _otherGateWaits, 0) / (double)Math.Max(frames, 1):F1}/{Interlocked.Exchange(ref _otherGateTicks, 0) * perFrame:F3}");
    }
}
