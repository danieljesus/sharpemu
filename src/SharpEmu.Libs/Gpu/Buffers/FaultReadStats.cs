// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_FAULT_READ_EMULATE: the relay round trips of the emulated faulting reads (time to reach the GPU
// queue and time there), served or declined (a GPU-modified byte), every 5 s as [FAULT_READ_RELAY] per second.
internal static class FaultReadStats
{
    private static long _served, _declined, _waitTicks, _workTicks, _maxWaitTicks, _reportAt;

    public static void NoteRelay(bool served, long waitTicks, long workTicks)
    {
        if (served) Interlocked.Increment(ref _served); else Interlocked.Increment(ref _declined);
        Interlocked.Add(ref _waitTicks, waitTicks);
        Interlocked.Add(ref _workTicks, workTicks);
        long max;
        while (waitTicks > (max = Volatile.Read(ref _maxWaitTicks)) && Interlocked.CompareExchange(ref _maxWaitTicks, waitTicks, max) != max)
        {
        }

        var now = Stopwatch.GetTimestamp();
        var due = Volatile.Read(ref _reportAt);
        if (due == 0)
        {
            Interlocked.CompareExchange(ref _reportAt, now + Stopwatch.Frequency * 5, 0);
            return;
        }

        if (now < due || Interlocked.CompareExchange(ref _reportAt, now + Stopwatch.Frequency * 5, due) != due)
        {
            return;
        }

        var seconds = (now - due) / (double)Stopwatch.Frequency + 5.0;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var servedNow = Interlocked.Exchange(ref _served, 0);
        var declinedNow = Interlocked.Exchange(ref _declined, 0);
        var calls = Math.Max(1, servedNow + declinedNow);
        var wait = Interlocked.Exchange(ref _waitTicks, 0);
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[FAULT_READ_RELAY] window_s={seconds:F1} served_per_s={servedNow / seconds:F0} declined_per_s={declinedNow / seconds:F0} relay_wait_ms_per_s={Ms(wait) / seconds:F2} relay_wait_us_mean={Ms(wait) * 1000 / calls:F0} relay_wait_ms_max={Ms(Interlocked.Exchange(ref _maxWaitTicks, 0)):F2} queue_work_ms_per_s={Ms(Interlocked.Exchange(ref _workTicks, 0)) / seconds:F2}"));
    }
}
