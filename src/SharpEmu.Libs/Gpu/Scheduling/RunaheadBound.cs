// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Scheduling;

// [local] SHARPEMU_RUNAHEAD_TICKS=<n> (off by default; 0 off): between two items of its back queue, the render thread
// does not run more than n sealed ticks ahead of the GPU while a guest thread waits for a readback
// (SHARPEMU_RUNAHEAD_GUEST=0: always). It runs the relay and polls the timeline until the GPU catches up, the guest
// wait ends or SHARPEMU_RUNAHEAD_MAX_US (default 4000) passes. It only delays recording, so it is exact by
// construction. With SHARPEMU_TABLE_WORD_MEMO_LEARN=8 the render no longer waits for 994A, the GPU queue is deeper
// when the game reads its results, and the front waits longer on REWIND (2026-11-02, gta/runahead).
internal static class RunaheadBound
{
    public static readonly int Ticks = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RUNAHEAD_TICKS"), out var ticks) && ticks > 0 ? ticks : 0;
    public static readonly bool GuestOnly = Environment.GetEnvironmentVariable("SHARPEMU_RUNAHEAD_GUEST") != "0";
    public static readonly long MaxWaitTicks = (long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RUNAHEAD_MAX_US"), out var us) && us > 0 ? us : 4000) *
        Stopwatch.Frequency / 1_000_000;

    public static bool Enabled => Ticks != 0;

    private static int _guestWaiters;
    private static long _holds, _holdTicks, _timeouts, _checks, _report = Stopwatch.GetTimestamp();

    public static int GuestWaiters => Volatile.Read(ref _guestWaiters);

    public static void EnterGuestWait() => Interlocked.Increment(ref _guestWaiters);

    public static void ExitGuestWait() => Interlocked.Decrement(ref _guestWaiters);

    // True while the render thread should hold: more than limit sealed ticks not complete, and (guestOnly) a guest
    // thread waiting for a readback.
    public static bool ShouldHold(ulong sealedTick, ulong completedTick, int limit, bool guestOnly, int guestWaiters) =>
        limit > 0 && sealedTick > completedTick + (ulong)limit && (!guestOnly || guestWaiters > 0);

    // Holds the calling (render) thread as above; runRelay services guest requests meanwhile. Returns the ticks held.
    public static long Hold(TickTimeline timeline, Func<ulong> sealedTick, Func<bool> runRelay)
    {
        _checks++;
        if (!ShouldHold(sealedTick(), timeline.CompletedTick, Ticks, GuestOnly, GuestWaiters))
        {
            timeline.RefreshCompletedTick();
            if (!ShouldHold(sealedTick(), timeline.CompletedTick, Ticks, GuestOnly, GuestWaiters))
            {
                Report();
                return 0;
            }
        }

        var started = Stopwatch.GetTimestamp();
        var deadline = started + MaxWaitTicks;
        var spin = new SpinWait();
        while (true)
        {
            runRelay();
            timeline.RefreshCompletedTickNow();
            if (!ShouldHold(sealedTick(), timeline.CompletedTick, Ticks, GuestOnly, GuestWaiters))
            {
                break;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                _timeouts++;
                break;
            }

            spin.SpinOnce(-1);
        }

        var held = Stopwatch.GetTimestamp() - started;
        _holds++;
        _holdTicks += held;
        Report();
        return held;
    }

    private static void Report()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _report < 20 * Stopwatch.Frequency)
        {
            return;
        }

        Console.Error.WriteLine(FormattableString.Invariant(
            $"[GPU][RUNAHEAD] window_s={(now - _report) / (double)Stopwatch.Frequency:F1} checks={_checks} holds={_holds} held_ms={_holdTicks * 1000.0 / Stopwatch.Frequency:F0} timeouts={_timeouts} limit={Ticks} guest_only={GuestOnly}"));
        _report = now;
        _holds = _holdTicks = _timeouts = _checks = 0;
    }
}
