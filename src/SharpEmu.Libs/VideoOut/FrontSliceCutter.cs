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
    Write, // [local] SHARPEMU_FRONT_SLICE_WRITES: a render-thread read of bytes a taken command write has not applied yet
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

    // [local] SHARPEMU_FRONT_SLICE_WRITES: why a command write met while something is open is not taken (null: taken).
    //  - "off": the switch is off or the workers are not running;
    //  - "tables": its bytes may hold a device store table (DeviceStoreTablePlanner.MayInvalidate). Applying it moves the
    //    planner's applied generation and drops its cached tables, which the resolution of the next items reads at once
    //    (TablesStillValid, the cached plans), so it must land where it lands in serial.
    public static string? WriteRefusal(bool enabled, bool mayInvalidateTables) =>
        !enabled ? "off" : mayInvalidateTables ? "tables" : null;

    // [local] Whether a render-thread read must join everything open first: it overlaps a pending GPU write of a taken
    // draw or dispatch (`pending`) or a taken command write not applied yet.
    public static FrontSliceFlush? ReadNeedsJoin(ulong address, ulong size, Func<ulong, ulong, bool> pending, FrontSliceWrites writes)
    {
        if (size == 0)
        {
            return null;
        }

        if (pending(address, size))
        {
            return FrontSliceFlush.Read;
        }

        return writes.Overlaps(address, size) ? FrontSliceFlush.Write : null;
    }
}

// [local] SHARPEMU_FRONT_SLICE_WRITES: the command writes (FrontWrite) the slices took and have not applied yet. A taken
// write is applied when the slice it sits in is joined, so until then a render-thread read of its bytes (the resolution
// of a later item) must join first, as for a pending GPU write. Pages give a cheap reject; the ranges decide. Render
// thread only.
internal sealed class FrontSliceWrites
{
    private readonly List<(ulong Address, ulong Size, bool Applied)> _writes = new();
    private readonly HashSet<ulong> _pages = new();
    private int _pending;

    // Taken writes not applied yet.
    public int Pending => _pending;

    public int Add(ulong address, ulong size)
    {
        _writes.Add((address, size, false));
        _pending++;
        if (size != 0)
        {
            for (var page = address >> 12; page <= (address + size - 1) >> 12; page++)
            {
                _pages.Add(page);
            }
        }

        return _writes.Count - 1;
    }

    public void Applied(int token)
    {
        if ((uint)token >= (uint)_writes.Count || _writes[token].Applied)
        {
            return;
        }

        _writes[token] = _writes[token] with { Applied = true };
        _pending--;
    }

    public bool Overlaps(ulong address, ulong size)
    {
        if (_pending == 0 || size == 0)
        {
            return false;
        }

        var first = address >> 12;
        var last = (address + size - 1) >> 12;
        var hit = last - first >= 64; // a long read: the ranges decide
        for (var page = first; !hit && page <= last; page++)
        {
            hit = _pages.Contains(page);
        }

        if (!hit)
        {
            return false;
        }

        foreach (var (start, length, applied) in _writes)
        {
            if (!applied && address < start + length && start < address + size)
            {
                return true;
            }
        }

        return false;
    }

    // Everything taken ended (the cutter has nothing open): every write was applied.
    public void Clear()
    {
        _writes.Clear();
        _pages.Clear();
        _pending = 0;
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

    // [local] SHARPEMU_PARRES_PROBE, gta/parres3: what the cutter would do if the command writes (FrontWrite) and the
    // end-of-pipe and interrupt posts that follow labels did not cut (rediseno-draws §10.5): every taken item counts in
    // a real run (ended by any flush) and in a virtual run (ended by any flush but those "transparent" ones). A run of L
    // items is replayed as the cutter splits it: L / SliceItems full slices, then the rest in PartsFor parts, or on this
    // thread when it is shorter than MinimumItems. The real replay checks the model against the real counters; reads
    // that would join on a pending command write are not counted (JOIN_PROBE: ~1-2 per frame).
    public int ModelSliceItems = 64, ModelMinimumItems = 32, ModelWorkers = 4;
    private long _realRun, _virtualRun;
    public long RealRuns, RealSlices, RealShort, VirtualRuns, VirtualSlices, VirtualShort, VirtualLong, TransparentWrites, TransparentPosts;

    public void NoteTaken()
    {
        _realRun++;
        _virtualRun++;
    }

    public void NoteBoundary(bool transparentWrite, bool transparentPost)
    {
        Replay(ref _realRun, ref RealRuns, ref RealSlices, ref RealShort, out _);
        if (transparentWrite) TransparentWrites++;
        else if (transparentPost) TransparentPosts++;
        else
        {
            Replay(ref _virtualRun, ref VirtualRuns, ref VirtualSlices, ref VirtualShort, out var length);
            if (length >= 256) VirtualLong += length;
        }
    }

    private void Replay(ref long run, ref long runs, ref long slices, ref long shortItems, out long length)
    {
        length = run;
        run = 0;
        if (length == 0) return;
        runs++;
        var full = length / ModelSliceItems;
        var rest = (int)(length - full * ModelSliceItems);
        var parts = FrontSliceCutter<object, object>.PartsFor(rest, ModelWorkers, ModelMinimumItems);
        slices += full + parts;
        if (parts == 0) shortItems += rest;
    }

    // [local] SHARPEMU_PARRES_PROBE: the command writes (FrontWrite) met while something was open: how many, taken
    // (SHARPEMU_FRONT_SLICE_WRITES), refused because a cached store table holds their bytes, inside the planner's table
    // bounds, over a registered buffer (a device copy is recorded), over a pending GPU write of an open item; and by the
    // interpreter method that made them and their size (WriteSites, "caller/size").
    public long WritesOpen, WritesTaken, WritesTables, WritesInBounds, WritesRegistered, WritesPending;
    public readonly Dictionary<string, long> WriteSites = new();
    private readonly Dictionary<(string, int), string> _siteNames = new();

    public void NoteWrite(string caller, int length, string? refusal, bool inBounds, bool registered, bool pending)
    {
        WritesOpen++;
        if (refusal is null) WritesTaken++;
        else if (refusal == "tables") WritesTables++;
        if (inBounds) WritesInBounds++;
        if (registered) WritesRegistered++;
        if (pending) WritesPending++;
        var size = length is 4 or 8 ? length : length <= 64 ? 64 : 65;
        if (!_siteNames.TryGetValue((caller, size), out var name))
        {
            name = $"{caller}/{(size == 64 ? "le64" : size == 65 ? "gt64" : size.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            _siteNames[(caller, size)] = name;
        }

        WriteSites[name] = WriteSites.GetValueOrDefault(name) + 1;
    }

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
        if (ParallelResolveProbe.Enabled) _stats.NoteTaken(); // [local] gta/parres3: the virtual cutter
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
