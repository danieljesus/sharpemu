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

    // [local] SHARPEMU_FAULT_READ_EMULATE=1: the parked polls, every 5 s as [FAULT_READ_PARK]: per second by outcome (the
    // bytes changed, the deadline passed, a byte turned GPU-modified), the wakes per park and the parked ms.
    internal enum ParkOutcome { Changed, Deadline, Declined, Count }

    private static readonly object ParkGate = new();
    private static readonly long[] ParkOutcomes = new long[(int)ParkOutcome.Count];
    private static readonly List<double> ParkMs = new();
    private static long _parkWakes, _parkReportAt;

    public static void NotePark(ParkOutcome outcome, int wakes, long ticks)
    {
        lock (ParkGate)
        {
            ParkOutcomes[(int)outcome]++;
            _parkWakes += wakes;
            ParkMs.Add(ticks * 1000.0 / Stopwatch.Frequency);
            var now = Stopwatch.GetTimestamp();
            if (_parkReportAt == 0)
            {
                _parkReportAt = now + Stopwatch.Frequency * 5;
                return;
            }

            if (now < _parkReportAt)
            {
                return;
            }

            var seconds = (now - _parkReportAt) / (double)Stopwatch.Frequency + 5.0;
            _parkReportAt = now + Stopwatch.Frequency * 5;
            var count = ParkMs.Count;
            ParkMs.Sort();
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[FAULT_READ_PARK] window_s={seconds:F1} parks_per_s={count / seconds:F0} changed_per_s={ParkOutcomes[0] / seconds:F0} deadline_per_s={ParkOutcomes[1] / seconds:F0} declined_per_s={ParkOutcomes[2] / seconds:F0} wakes_per_park={_parkWakes / (double)Math.Max(1, count):F2} park_ms_mean/p50/p90/max={ParkMs.Average():F3}/{ParkMs[count / 2]:F3}/{ParkMs[(int)(count * 0.9)]:F3}/{ParkMs[count - 1]:F3} parked_ms_per_s={ParkMs.Sum() / seconds:F0}"));
            Array.Clear(ParkOutcomes);
            ParkMs.Clear();
            _parkWakes = 0;
        }
    }
}
