// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_CHEAP_PROBE=1 (with SHARPEMU_FPS_LOG=1): one [PERF][CHEAP] line per FPS window with, per frame,
// the guest reads that found every readback slot busy (waits for a slot off the GPU queue thread, and synchronous
// downloads on it), the BDA fault scans and their GPU time, and the SET_PREDICATION packets by kind with the time of
// the ZPass flush-and-wait.
internal static class CheapProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_CHEAP_PROBE") == "1";

    private static long _slotWaits, _slotWaitTicks, _slotFallbacks, _slotFallbackTicks;
    private static long _bdaScans, _bdaTimed, _bdaGpuNs, _bdaGroups, _bdaDispatches, _bdaLost, _bdaOutside;
    private static long _predClear, _predZpassWait, _predZpassNoWait, _predZpassTicks;

    public static void NoteSlotWait(long ticks)
    {
        Interlocked.Increment(ref _slotWaits);
        Interlocked.Add(ref _slotWaitTicks, ticks);
    }

    public static void NoteSlotFallback(long ticks)
    {
        Interlocked.Increment(ref _slotFallbacks);
        Interlocked.Add(ref _slotFallbackTicks, ticks);
    }

    public static void NoteBdaScan(long groups, long dispatches)
    {
        Interlocked.Increment(ref _bdaScans);
        Interlocked.Add(ref _bdaGroups, groups);
        Interlocked.Add(ref _bdaDispatches, dispatches);
    }

    public static void NoteBdaGpu(long nanoseconds)
    {
        Interlocked.Increment(ref _bdaTimed);
        Interlocked.Add(ref _bdaGpuNs, nanoseconds);
    }

    public static void NoteBdaVerify(long lost, long outside)
    {
        Interlocked.Add(ref _bdaLost, lost);
        Interlocked.Add(ref _bdaOutside, outside);
    }

    public static void NotePredicationClear() => Interlocked.Increment(ref _predClear);

    public static void NotePredicationZpass(bool wait, long ticks)
    {
        if (!wait)
        {
            Interlocked.Increment(ref _predZpassNoWait);
            return;
        }

        Interlocked.Increment(ref _predZpassWait);
        Interlocked.Add(ref _predZpassTicks, ticks);
    }

    public static string TakeReport(long frames)
    {
        var f = Math.Max(frames, 1);
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / f;
        double PerFrame(long count) => (double)count / f;
        var timed = Interlocked.Exchange(ref _bdaTimed, 0);
        var gpuNs = Interlocked.Exchange(ref _bdaGpuNs, 0);
        var scans = Interlocked.Exchange(ref _bdaScans, 0);
        return FormattableString.Invariant(
            $"[PERF][CHEAP] frames={frames} slot_waits={PerFrame(Interlocked.Exchange(ref _slotWaits, 0)):F2} slot_wait_ms={Ms(Interlocked.Exchange(ref _slotWaitTicks, 0)):F3} ") +
            FormattableString.Invariant(
            $"slot_fallbacks={PerFrame(Interlocked.Exchange(ref _slotFallbacks, 0)):F2} slot_fallback_ms={Ms(Interlocked.Exchange(ref _slotFallbackTicks, 0)):F3} ") +
            FormattableString.Invariant(
            $"bda_scans={PerFrame(scans):F2} bda_gpu_ms={(timed == 0 ? 0 : gpuNs / 1e6 / timed * scans / f):F3} bda_scan_us={(timed == 0 ? 0 : gpuNs / 1e3 / timed):F1} ") +
            FormattableString.Invariant(
            $"bda_groups={(scans == 0 ? 0 : (double)Interlocked.Exchange(ref _bdaGroups, 0) / scans):F0} bda_dispatches={(scans == 0 ? 0 : (double)Interlocked.Exchange(ref _bdaDispatches, 0) / scans):F1} ") +
            FormattableString.Invariant(
            $"bda_lost={Interlocked.Exchange(ref _bdaLost, 0)} bda_outside={Interlocked.Exchange(ref _bdaOutside, 0)} ") +
            FormattableString.Invariant(
            $"pred_clear={PerFrame(Interlocked.Exchange(ref _predClear, 0)):F2} pred_zpass_wait={PerFrame(Interlocked.Exchange(ref _predZpassWait, 0)):F2} ") +
            FormattableString.Invariant(
            $"pred_zpass_nowait={PerFrame(Interlocked.Exchange(ref _predZpassNoWait, 0)):F2} pred_zpass_ms={Ms(Interlocked.Exchange(ref _predZpassTicks, 0)):F3}");
    }
}
