// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// [local] The ranges a GPU binding has ever written, as the front's read check asks them (GuestBufferCache).
internal interface IEverGpuWrittenRanges
{
    // Bumped after every range is added; ranges are never removed.
    long EverGpuWrittenVersion { get; }

    bool EverGpuWrittenRanges(ulong guestAddress, ulong size);
}

// [local] SHARPEMU_FRONT_READ_MEMO (=1 on, =2 verify, off by default): every direct guest read the front makes (each
// descriptor word a materialization reads, the vertex and pixel inputs, the register tables and command reads) first
// asks NeedsBackRead whether the render thread must serve it: whether a queued WriteGuestNow it has not applied yet
// overlaps the read (a lock and a walk of the pending ranges whenever any is pending) and whether a GPU binding ever
// wrote the range (a reader lock on the ever-written set and a binary search over its spans). The front makes tens of
// such reads per draw. With the switch:
//   - the ever-written answer is kept per 4 KB granule, with the set's version when it was asked: while the version
//     is the same, a granule found with no written byte answers every read inside it at once. Ranges are only ever
//     added, and the version is bumped after each add, so an answer taken at a version still holds at that version
//     (the version is read before the set is asked). A granule that holds written bytes asks the exact range, as
//     before; a read that crosses granules asks the set as before.
//   - the pending-write ranges are published as an immutable array with their bounds on every add and retire (under
//     the overlay's lock), and the read check walks that array without the lock.
// Both give the answer the locked queries give at a moment between the last change they saw and the read: the same
// interleavings as before (a read could already run just before an add or a retire). =2 asks the old way as well
// and counts the answers that differ ([PERF][FRONT_READ_MEMO]); a range added between the version read and its bump
// can show as one.
internal sealed class FrontReadMemo
{
    internal static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONT_READ_MEMO"), out var mode) ? mode : 0;

    private const int GranuleShift = 12;
    private const ulong GranuleBytes = 1UL << GranuleShift;
    private const int Slots = 4096;

    private readonly ulong[] _tags = new ulong[Slots]; // granule + 1, 0 empty
    private readonly long[] _versions = new long[Slots];
    private readonly bool[] _written = new bool[Slots];
    private readonly int _mode;

    internal long Hits, WrittenHits, Misses, Spans, Verified, Mismatches;

    internal FrontReadMemo(int mode) => _mode = mode;

    // Whether a GPU binding ever wrote a byte of the range, as ranges.EverGpuWrittenRanges answers it.
    internal bool Overlaps(IEverGpuWrittenRanges ranges, ulong address, ulong size)
    {
        var last = address + size - 1;
        var granule = address >> GranuleShift;
        bool result;
        if (size == 0 || last < address || last >> GranuleShift != granule)
        {
            Spans++;
            result = ranges.EverGpuWrittenRanges(address, size);
        }
        else
        {
            var version = ranges.EverGpuWrittenVersion;
            var slot = (int)(granule & (Slots - 1));
            if (_tags[slot] == granule + 1 && _versions[slot] == version)
            {
                if (_written[slot])
                {
                    WrittenHits++;
                    result = ranges.EverGpuWrittenRanges(address, size);
                }
                else
                {
                    Hits++;
                    result = false;
                }
            }
            else
            {
                Misses++;
                var written = ranges.EverGpuWrittenRanges(granule << GranuleShift, GranuleBytes);
                _tags[slot] = granule + 1;
                _versions[slot] = version;
                _written[slot] = written;
                result = written && ranges.EverGpuWrittenRanges(address, size);
            }
        }

        if (_mode == 2)
        {
            Verified++;
            if (result != ranges.EverGpuWrittenRanges(address, size))
                Mismatches++;
        }

        if ((++_calls & 0xFFFF) == 0)
            MaybeReport();
        return result;
    }

    // The pending-write ranges as one immutable array, with the lowest start and the highest end.
    internal sealed class Published((ulong Start, ulong End)[] ranges)
    {
        internal static readonly Published Empty = new([]);

        internal readonly (ulong Start, ulong End)[] Ranges = ranges;
        internal readonly ulong Low = ranges.Length == 0 ? ulong.MaxValue : ranges.Min(range => range.Start);
        internal readonly ulong High = ranges.Length == 0 ? 0 : ranges.Max(range => range.End);

        internal bool Overlaps(ulong address, ulong size)
        {
            var end = address + size;
            if (address >= High || end <= Low)
                return false;
            foreach (var (start, stop) in Ranges)
            {
                if (address < stop && start < end)
                    return true;
            }

            return false;
        }
    }

    internal static long OverlayChecks, OverlayMismatches;

    private long _reported = Stopwatch.GetTimestamp();
    private long _calls;

    private void MaybeReport()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _reported < 5 * Stopwatch.Frequency)
            return;
        _reported = now;
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[PERF][FRONT_READ_MEMO] mode={_mode} hits={Hits} written_hits={WrittenHits} misses={Misses} spans={Spans} verified={Verified} mismatches={Mismatches} ") +
            FormattableString.Invariant($"overlay_checks={Interlocked.Exchange(ref OverlayChecks, 0)} overlay_mismatches={Interlocked.Exchange(ref OverlayMismatches, 0)}"));
        Hits = WrittenHits = Misses = Spans = Verified = Mismatches = 0;
    }
}
