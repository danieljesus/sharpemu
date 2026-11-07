// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_FRONT_SLICES (docs/plan-parallel-resolution-open-world.md §10, increment 3a): the render thread
// resolves the front's draws in order and, instead of recording them, collects them with the global barriers between
// them into slices that upstream's render workers record into their own command buffers. A slice closes at
// SliceItems items and is handed to the workers at once; at a cut the rest is split into min(N, len/16) slices (one
// below 32 items, none below MinimumItems: those run on this thread), and every slice is joined in stream order before
// the item that cut runs. Each item ends exactly once, in order: joined (its segment validated and submitted) or run
// on this thread (stolen slice, invalid or empty segment, the item a worker could not record, the rest after it).
internal enum FrontSliceInline
{
    Short,
    Stolen,
    Invalid,
    Serial,
    Tail,
    Count,
}

internal enum FrontSliceFlush
{
    Item,
    Exit,
    Read,
    Record,
    After,
    Prep, // [local] SHARPEMU_FRONT_SLICE_DISPATCHES: a front preparation checked while a taken dispatch is open
    Count,
}

// [local] SHARPEMU_FRONT_SLICE_DISPATCHES: whether a front preparation checked while a taken dispatch is open must wait for
// everything taken to be joined: when its pages are not known (none noted, too many) or a run of them may be written by
// an open item (`pending`: a range has a pending write; null when the writes are not tracked).
internal static class FrontSliceGuards
{
    public static bool PreparationNeedsJoin(FrontPrepReads.Pages? pages, Func<ulong, ulong, bool>? pending)
    {
        if (pages is null || pages.Overflow || pending is null)
        {
            return true;
        }

        foreach (var (first, last) in pages.Runs)
        {
            if (pending(first << 12, (last - first + 1) << 12))
            {
                return true;
            }
        }

        return false;
    }
}

// One recorded run of a slice's items: [First, First + Count); Serial when the item at First + Count needs this thread.
internal readonly record struct FrontSliceSegment(int First, int Count, bool Serial, string? SerialReason, object? Handle);

// Counters of the cutter, render thread only; the probe takes them per window.
internal sealed class FrontSliceStats
{
    public static readonly FrontSliceStats Shared = new();

    public long Slices, Items, Joined, Segments;
    public readonly long[] Inline = new long[(int)FrontSliceInline.Count];
    public readonly long[] Flushes = new long[(int)FrontSliceFlush.Count];
    public readonly Dictionary<string, long> SerialReasons = new();

    // [local] SHARPEMU_PARRES_PROBE: the items that were not taken while something was open (each one joins everything),
    // by the queued work's method.
    public readonly Dictionary<string, long> Cuts = new();

    public void NoteCut(string name) => Cuts[name] = Cuts.GetValueOrDefault(name) + 1;

    public void NoteSerial(string? reason)
    {
        var key = reason ?? "unknown";
        SerialReasons[key] = SerialReasons.GetValueOrDefault(key) + 1;
    }
}

internal sealed class FrontSliceCutter<TItem, TSlice> where TSlice : class
{
    internal interface IBackend
    {
        // Render workers available now; 0 runs every item on this thread.
        int Workers { get; }

        // Hands items[start, start + count) to the workers as one slice.
        TSlice Dispatch(List<TItem> items, int start, int count);

        // True when no worker started the slice: this thread runs its items.
        bool TryClaim(TSlice slice);

        // [local] SHARPEMU_FRONT_SLICE_DEFER: the slice's worker is done with it, so joining it does not wait.
        bool IsDone(TSlice slice);

        // The slice's next finished segment in order; false once its worker is done with it.
        bool NextSegment(TSlice slice, out FrontSliceSegment segment);

        // Validates and submits the segment in stream order; false when its items must run on this thread.
        bool JoinSegment(TSlice slice, in FrontSliceSegment segment);

        // Where the worker stopped: items from here on were not recorded.
        int Resume(TSlice slice);

        void Finish(TSlice slice);

        // The item's recording was joined.
        void Complete(TItem item);

        // The item runs on this thread, in order.
        void RunInline(TItem item, FrontSliceInline reason);
    }

    private readonly IBackend _backend;
    private readonly FrontSliceStats _stats;
    private readonly int _sliceItems;
    private readonly int _minimumItems;
    private readonly List<TItem> _items = new();
    private readonly List<(TSlice Slice, int Start, int Count, bool AtCut)> _slices = new();
    private int _cutStart;
    private bool _joining;

    // [local] SHARPEMU_FRONT_SLICE_DEFER: the slices handed out at full size that were joined before the flush (JoinReady).
    private int _joined;

    public const int SplitItems = 32;
    public const int PartItems = 16;

    public FrontSliceCutter(IBackend backend, FrontSliceStats stats, int sliceItems = 64, int minimumItems = 8)
    {
        _backend = backend;
        _stats = stats;
        _sliceItems = Math.Max(sliceItems, 1);
        _minimumItems = Math.Max(minimumItems, 1);
    }

    // Items taken and not ended yet.
    public bool HasOpen => _items.Count != 0;

    // True while the open items are being joined or run; nothing may cut then.
    public bool Joining => _joining;

    // While joining: whether the slice being joined was handed out at the cut (the rest), not closed full.
    public bool JoiningRest { get; private set; }

    public int OpenItems => _items.Count;

    // [local] SHARPEMU_FRONT_SLICE_DEFER: slices handed out and not joined yet.
    public int PendingSlices => _slices.Count - _joined;

    // [local] SHARPEMU_FRONT_SLICE_DEFER: joins, in stream order and before any later slice, the oldest slices handed out at
    // full size whose workers are done, and the oldest one even if its worker is not when more than `ahead` are pending.
    // The items after them stay open: they are joined by a later JoinReady or the flush. Nothing else is touched, so the
    // order every item ends in is the same as with a flush at the end. Returns the number of slices joined.
    public int JoinReady(int ahead)
    {
        if (_joining || _joined == _slices.Count)
        {
            return 0;
        }

        var joined = 0;
        _joining = true;
        try
        {
            while (_joined < _slices.Count)
            {
                var (slice, start, count, _) = _slices[_joined];
                if (_slices.Count - _joined <= ahead && !_backend.IsDone(slice))
                {
                    break;
                }

                _joined++;
                Join(slice, start, count);
                joined++;
            }
        }
        finally
        {
            _joining = false;
        }

        return joined;
    }

    public void Add(TItem item)
    {
        _items.Add(item);
        if (_items.Count - _cutStart >= _sliceItems && DispatchRange(_cutStart, _items.Count - _cutStart, rest: false))
        {
            _cutStart = _items.Count;
        }
    }

    // The number of slices a cut splits a rest of `count` items into, 0 when they run on this thread.
    public static int PartsFor(int count, int workers, int minimumItems)
    {
        if (workers <= 0 || count < minimumItems)
        {
            return 0;
        }

        return count < SplitItems ? 1 : Math.Clamp(count / PartItems, 1, workers);
    }

    // Ends every open item in stream order: the rest is handed out, then each slice is joined.
    public void Flush(FrontSliceFlush reason)
    {
        if (_items.Count == 0 || _joining)
        {
            return;
        }

        _stats.Flushes[(int)reason]++;
        _joining = true;
        try
        {
            var rest = _items.Count - _cutStart;
            var parts = PartsFor(rest, _backend.Workers, _minimumItems);
            var inlineFrom = _items.Count;
            if (parts == 0)
            {
                inlineFrom = _cutStart;
            }
            else
            {
                var start = _cutStart;
                for (var part = 0; part < parts; part++)
                {
                    var end = _cutStart + (int)((long)rest * (part + 1) / parts);
                    _ = DispatchRange(start, end - start, rest: true);
                    start = end;
                }
            }

            for (var index = _joined; index < _slices.Count; index++)
            {
                var (slice, start, count, isRest) = _slices[index];
                JoiningRest = isRest;
                Join(slice, start, count);
            }

            for (var index = inlineFrom; index < _items.Count; index++)
            {
                RunInline(_items[index], FrontSliceInline.Short);
            }
        }
        finally
        {
            _slices.Clear();
            _items.Clear();
            _cutStart = 0;
            _joined = 0;
            _joining = false;
            JoiningRest = false;
        }
    }

    private bool DispatchRange(int start, int count, bool rest)
    {
        if (_backend.Workers <= 0)
        {
            return false;
        }

        _slices.Add((_backend.Dispatch(_items, start, count), start, count, rest));
        _stats.Slices++;
        _stats.Items += count;
        return true;
    }

    private void RunInline(TItem item, FrontSliceInline reason)
    {
        _stats.Inline[(int)reason]++;
        _backend.RunInline(item, reason);
    }

    // As CommandThread.JoinSlice: segments in order, the item a worker stopped at and the rest on this thread.
    private void Join(TSlice slice, int start, int count)
    {
        if (_backend.TryClaim(slice))
        {
            try
            {
                for (var index = 0; index < count; index++)
                {
                    RunInline(_items[start + index], FrontSliceInline.Stolen);
                }
            }
            finally
            {
                _backend.Finish(slice);
            }

            return;
        }

        var next = 0;
        try
        {
            while (_backend.NextSegment(slice, out var segment))
            {
                if (segment.First != next || segment.First + segment.Count > count || (segment.Serial && segment.First + segment.Count >= count))
                {
                    throw new InvalidOperationException(
                        $"A front slice segment is out of order: first={segment.First} count={segment.Count} serial={segment.Serial} expected={next} items={count}.");
                }

                _stats.Segments++;
                var end = segment.First + segment.Count;
                if (_backend.JoinSegment(slice, in segment))
                {
                    for (var index = segment.First; index < end; index++)
                    {
                        _backend.Complete(_items[start + index]);
                        _stats.Joined++;
                    }
                }
                else
                {
                    for (var index = segment.First; index < end; index++)
                    {
                        RunInline(_items[start + index], FrontSliceInline.Invalid);
                    }
                }

                next = end;
                if (segment.Serial)
                {
                    _stats.NoteSerial(segment.SerialReason);
                    RunInline(_items[start + end], FrontSliceInline.Serial);
                    next = end + 1;
                }
            }
        }
        finally
        {
            _backend.Finish(slice);
        }

        var resume = _backend.Resume(slice);
        if (resume != next)
        {
            throw new InvalidOperationException($"A front slice resumes at {resume}, but its segments ended at {next}.");
        }

        for (var index = resume; index < count; index++)
        {
            RunInline(_items[start + index], FrontSliceInline.Tail);
        }
    }
}
