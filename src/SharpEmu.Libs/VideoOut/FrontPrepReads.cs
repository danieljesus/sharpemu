// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_FRONT_PREP_RANGES (0 off: the old rule; 1 on; 2 verify): a front preparation reads guest memory
// directly only where no GPU write has been recorded, and SHARPEMU_FRONT_PREP_CHECK discards it when any range
// became GPU-written before the render thread executes it. In pan streaming adds such ranges every frame, and each
// one discarded every preparation in flight (~11 per frame, all prepared again on the render thread). With this
// switch the front notes the 4 KB pages each preparation read directly (the reads NeedsBackRead lets through, and
// the code of the programs it names), and the preparation is discarded only when a range first written since it
// started overlaps one of them, or when the ranges since then are no longer known (ring wrapped, too many pages).
// =2 keeps the preparations the old rule would have discarded only after preparing them again on the render thread
// and comparing (FrontPrepVerify); the render thread's result is used on a mismatch.
internal static class FrontPrepReads
{
    public static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONT_PREP_RANGES"), out var mode) ? mode : 0;

    private const int MaxRanges = 128;

    // Coalesced runs of pages (first, last), in the order read.
    internal sealed class Pages
    {
        public readonly List<(ulong First, ulong Last)> Runs = new(16);
        public bool Overflow;
    }

    [ThreadStatic] private static Pages? _current;
    private static readonly ConcurrentQueue<Pages> _pool = new();

    internal static long Kept, Discarded, Unknown, Verified;

    public static void Begin()
    {
        if (Mode == 0)
            return;
        if (!_pool.TryDequeue(out var pages))
            pages = new Pages();
        pages.Runs.Clear();
        pages.Overflow = false;
        _current = pages;
    }

    public static Pages? End()
    {
        var pages = _current;
        _current = null;
        return pages;
    }

    public static void Release(Pages? pages)
    {
        if (pages is not null && _pool.Count < 4096)
            _pool.Enqueue(pages);
    }

    public static void Note(ulong address, ulong size)
    {
        var pages = _current;
        if (pages is null || size == 0 || pages.Overflow)
            return;
        var first = address >> 12;
        var last = (address + size - 1) >> 12;
        var runs = pages.Runs;
        for (var index = runs.Count - 1; index >= 0 && index >= runs.Count - 4; index--)
        {
            var run = runs[index];
            if (first <= run.Last + 1 && run.First <= last + 1)
            {
                runs[index] = (Math.Min(run.First, first), Math.Max(run.Last, last));
                return;
            }
        }

        if (runs.Count >= MaxRanges)
        {
            pages.Overflow = true;
            return;
        }

        runs.Add((first, last));
    }

    public static bool Overlaps(Pages pages, ulong address, ulong size)
    {
        var first = address >> 12;
        var last = (address + size - 1) >> 12;
        foreach (var run in pages.Runs)
        {
            if (run.First <= last && first <= run.Last)
                return true;
        }

        return false;
    }

    public static string TakeReport() =>
        FormattableString.Invariant(
            $" ranges_kept={Interlocked.Exchange(ref Kept, 0)} ranges_discarded={Interlocked.Exchange(ref Discarded, 0)} ranges_unknown={Interlocked.Exchange(ref Unknown, 0)} ranges_verified={Interlocked.Exchange(ref Verified, 0)}");
}
