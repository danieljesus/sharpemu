// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_LITE_OBTAIN (0 off by default, 1 on, 2 verify): A-1 of sharpemu-rediseno-draws.md. A read obtain
// (GuestBufferCache.ObtainBuffer with isWritten false) and a read V# of FindBuffers answer from the registry's page
// owner when the real path would do no work, and fall to it on the first special case.
//
// The read obtain answers (page owner, offset) when, at one instant: no pending publication overlaps the range (so the
// real path's CompleteGuestPublications and OverlayGuestPublications are no-ops), a registered buffer owns the first
// 16 KB page and contains the range, the range has no CPU-dirty page (MayHaveCpuDirtyPages: the stream ring needs
// HasCpuDirtyPages, which implies it, and a read upload needs it too), and a texel read has no image bytes under it
// (MayHaveImageBytes false: TrySynchronizeBufferFromImage returns at its first check). These are the conditions of the
// read memo in ObtainBufferCore, applied to the page owner instead of the remembered buffer. The real path's clean
// read does nothing else that later decisions read except TouchBuffer (the retirement recency), which the light path
// does too. Heat (hot pages, recent uploads, stream uses and shares) only moves when the range is CPU-dirty, a stream
// or an upload. Written obtains always take the real path (GPU-modified ranges, write ticks, pending-write marks).
//
// The read V# of FindBuffers answers (page owner, requested size, owner's extent as the ring-slot window) when the
// memo does not already answer it, the slot is not ring-reused, and the owner's whole extent was readable under the
// current mapping generation (kept per buffer): then ClampMappedSize returns the requested size, FindBuffer the page
// owner and RingSlotWindow the extent. The memo and the table are written as before.
//
// =2 runs the light answer and the real path on every read obtain and light V#, uses the real answer and counts
// mismatches (the first ones are printed as [GPU][LITE_OBTAIN_WRONG] kind=verify|find_verify). A light "clean" that a
// CPU write of the range's 4 MB blocks overtook between the two (TrackedRegion.CpuDirtyings moved) is counted as raced:
// that write was not ordered before the submission being recorded (CpuDirtySummary), so the real path answering a
// moment later sees it by timing only. The shadow probe (SHARPEMU_LITE_OBTAIN_PROBE) had the same window and printed
// its four races as fast_wrong. wrong (unraced) must be 0.
//
// [PERF][LITE_OBTAIN_USE] mode=1 obtains=N lite=N slow=pub:N,cpu:N,img:N,hot:N,cross:N,unmapped:N,guard:N finds=N
//     find_lite=N find_slow=N verified=N wrong=N raced=N wrong_total=N find_verified=N find_wrong=N find_wrong_total=N
// once per SHARPEMU_DRAWCOST_PROBE window, or every 5 s in =2 without it. obtains = read obtains, finds = read V# the
// memo and the ring slot did not answer (the light find's candidates); verified/find_verified count the light answers
// =2 compared.
internal static class LiteObtainUse
{
    internal static readonly int DefaultMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_LITE_OBTAIN"), out var mode) ? mode : 0;

    private const int DumpLimit = 20;

    // Why a read obtain took the real path, after the shadow probe's causes; guard = a diagnostic or a state the
    // real path rejects (read-only thread, invalid range or command buffer).
    internal enum Slow
    {
        Pub,
        Cpu,
        Img,
        Hot,
        Cross,
        Unmapped,
        Guard,
        Count,
    }

    private static readonly string[] SlowNames = ["pub", "cpu", "img", "hot", "cross", "unmapped", "guard"];

    // The window's counts. Plain increments on the recording thread: diagnostics only, except the wrong totals.
    internal static long Obtains;
    internal static long Lite;
    internal static readonly long[] SlowCounts = new long[(int)Slow.Count];
    internal static long Finds;
    internal static long FindLite;
    internal static long FindSlow;
    internal static long Verified;
    internal static long Wrong;
    internal static long Raced;
    internal static long FindVerified;
    internal static long FindWrong;
    private static long _wrongTotal;
    private static long _findWrongTotal;
    private static int _dumps;
    private static long _lastReport = Stopwatch.GetTimestamp();

    internal static long WrongTotal => Interlocked.Read(ref _wrongTotal);

    internal static long FindWrongTotal => Interlocked.Read(ref _findWrongTotal);

    internal static void NoteSlow(Slow cause) => SlowCounts[(int)cause]++;

    // A light "clean" the real path disagreed with: raced when a CPU write of the range's blocks came in between.
    internal static bool NoteMismatch(bool raced)
    {
        if (raced)
        {
            Raced++;
            return false;
        }

        Wrong++;
        Interlocked.Increment(ref _wrongTotal);
        return _dumps++ < DumpLimit;
    }

    internal static bool NoteFindMismatch()
    {
        FindWrong++;
        Interlocked.Increment(ref _findWrongTotal);
        return _dumps++ < DumpLimit;
    }

    // =2 without the DRAWCOST window: a line every 5 s from the verified obtains.
    internal static void MaybeReport()
    {
        if (Diagnostics.DrawCostProbe.Enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _lastReport >= 5 * Stopwatch.Frequency)
        {
            _lastReport = now;
            Report(DefaultMode);
        }
    }

    internal static void Report(int mode)
    {
        if (mode == 0)
        {
            return;
        }

        Console.Error.WriteLine(FormatAndClear(mode));
    }

    internal static string FormatAndClear(int mode)
    {
        var slow = string.Join(",", SlowNames.Select((name, index) => FormattableString.Invariant($"{name}:{SlowCounts[index]}")));
        var line = FormattableString.Invariant(
            $"[PERF][LITE_OBTAIN_USE] mode={mode} obtains={Obtains} lite={Lite} slow={slow} finds={Finds} find_lite={FindLite} find_slow={FindSlow} ") +
            FormattableString.Invariant(
            $"verified={Verified} wrong={Wrong} raced={Raced} wrong_total={WrongTotal} find_verified={FindVerified} find_wrong={FindWrong} find_wrong_total={FindWrongTotal}");
        Obtains = Lite = Finds = FindLite = FindSlow = Verified = Wrong = Raced = FindVerified = FindWrong = 0;
        Array.Clear(SlowCounts);
        return line;
    }

    // [local] For tests: clears the window, the totals and the dump budget.
    internal static void ResetForTests()
    {
        FormatAndClear(0);
        Interlocked.Exchange(ref _wrongTotal, 0);
        Interlocked.Exchange(ref _findWrongTotal, 0);
        _dumps = 0;
    }
}
