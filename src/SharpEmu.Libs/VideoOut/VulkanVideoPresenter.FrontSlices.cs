// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Diagnostics;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Scheduling;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // [local] SHARPEMU_FRONT_SLICES: null unless it is on.
        private FrontSlices? _frontSlicer;

        // [local] SHARPEMU_FRONT_SLICES: a guest read on the render thread while it resolves ahead of the draws it took.
        private void BeforeFrontSliceRead(ulong address, ulong size)
        {
            if (_frontSlicer is { HasOpen: true } slices)
            {
                slices.BeforeRead(address, size);
            }
        }

        // [local] SHARPEMU_FRONT_SLICE_DISPATCHES: before a front preparation is checked at its item (FrontPreparationStillValid).
        // Always true; see FrontSlices.BeforePreparationCheck.
        internal bool BeforeFrontPreparationCheck(FrontPrepReads.Pages? pages)
        {
            if (_frontSlicer is { OpenWriters: true } slices)
            {
                slices.BeforePreparationCheck(pages);
            }

            return true;
        }

        internal void RunFrontSliceWork(AgcExports.DeferredWork work) => RunDeferredWork(work);

        // [local] SHARPEMU_PARRES_PROBE: whether a command write there records a device copy (WriteCommandData).
        internal bool IsCommandWriteRegistered(ulong address, ulong size) => size != 0 && _bufferCache.IsRegionRegistered(address, size);

        internal Action FrontSliceRelay => RunRelayBetweenCommandItems;
    }

    // [local] SHARPEMU_FRONT_SLICE_DEFER: a queued item the slices take without joining (FrontPost): the render thread runs
    // `Run` in stream order when it joins the slice the item sits in, between the segment before it and the one after it.
    // A worker never records it: it ends its segment there (Serial "render unit") and goes on with the next item.
    internal sealed record FrontPostWork(Action Run) : AgcExports.DeferredWork
    {
        public override int CostKind => Diagnostics.ItemCostTally.Other;
    }

    // [local] SHARPEMU_FRONT_SLICES: the queued item whose banks a captured record reads.
    internal interface IFrontSliceOwner
    {
        void ReleaseSnapshot();
    }

    // [local] SHARPEMU_FRONT_SLICES: a draw or dispatch record or global barrier the render thread resolved and a worker records.
    private sealed class FrontSliceItem
    {
        public readonly CommandThread.Item Item = new() { Kind = CommandThread.Kind.Deferred };

        // The draw whose banks the record reads; set on the last record of the draw.
        public IFrontSliceOwner? Owner;

        // [local] A taken dispatch: it writes memory (FrontSlices.OpenWriters until it ends).
        public bool Writer;
    }

    // [local] SHARPEMU_FRONT_SLICES=1 (increment 3a of docs/plan-parallel-resolution-open-world.md §10), on the render
    // thread, between the back queue and upstream's render workers:
    //  - Take: a front draw that passes the resolve pool's gate (DrawHasTargets, shaders known not to write memory) and
    //    a global barrier while something is taken. Any other item (dispatch, write, fill, copy, label, flip, synchronous
    //    read, draw that fails the gate) first joins everything taken, so it runs exactly where it ran before.
    //  - Run: the draw runs as before (the front's preparation checked at its position, the stream state recorded, the
    //    executor resolves it), but its record is captured instead of recorded, with a stream position that keeps its
    //    pending writes (render targets) marked until its slice is joined. A record that is not a plain draw, or that
    //    writes memory, joins everything taken and is recorded here.
    //  - A render-thread guest read that overlaps a pending write of a taken draw joins everything taken first
    //    (BeforeRead), as the command thread waits for the render thread in upstream's pipeline.
    //  - Slices are joined in stream order by FrontSliceCutter with upstream's NextSegment/JoinSegment/FinishSlice; what a
    //    worker could not record runs here in order (RunDeferredWork). The front's banks a record reads go back to the
    //    front only once the record was joined or recorded.
    private sealed class FrontSlices : BackQueue.ISlicer, FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend
    {
        internal static readonly int SliceItems =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SLICE_ITEMS"), out var items) && items > 0 ? Math.Min(items, 1024) : 64;

        internal static readonly int MinimumItems =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SLICE_MIN"), out var minimum) && minimum > 0 ? minimum : 32;

        // [local] SHARPEMU_FRONT_SLICE_DISPATCHES=1 (step 3b, off by default): the front's direct dispatches (FrontDispatch) are
        // taken too, unless a local dispatch hook needs the render thread (NeedsSerialDispatch). A captured dispatch writes
        // memory, so what the render thread does after it, before its slice is joined, must not see stale memory:
        //  - every guest read on the render thread (TryReadGuest, the command memory, the table words of the materializer,
        //    clean and resident reads) joins everything taken first when it overlaps a pending write (BeforeRead), as for
        //    the draws' render targets; the pending writes are the dispatch's own marks (MarkPendingWrites);
        //  - a front preparation is checked at its item against the ranges first GPU-written since it was made
        //    (FrontPreparationStillValid), which a dispatch adds only once it is joined: while a taken dispatch is open, a
        //    preparation that read a page with a pending write, or whose pages are not known, joins everything first
        //    (BeforePreparationCheck, flush `prep`), so the check sees what it sees in serial;
        //  - any other item, and a draw or dispatch that is not taken, joins everything before it runs, as before.
        // A worker records the dispatch as upstream's slices do (Serial for what needs the render thread); ordering in the
        // command buffers is the stream order, segment by segment.
        internal static readonly bool Dispatches = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SLICE_DISPATCHES") == "1";

        // [local] SHARPEMU_FRONT_SLICE_DEFER=1 (step 3c, off by default): the render thread joins only where it must.
        //  - Posts (FrontCommandStreamHost.FrontPost: Flush, FlushFrame, the queue context of BeginSubmission, end-of-pipe
        //    writes and interrupts) no longer join what is open: they are added to the open run as render units. The
        //    render thread runs one in stream order when it joins the slice it sits in, after the segment before it and
        //    before the one after it (the worker ends its segment there, Serial "render unit"), so what it records lands in
        //    the command buffers exactly where it would in serial. What a post changes that the resolution of the items
        //    after it reads runs at once instead (BeginSubmission's translation state), as upstream's command thread runs
        //    it before it posts the rest (VulkanVideoPresenter.CommandStream.cs, OnCommandThread). The posts are the ones
        //    upstream's command thread posts to the render thread without waiting (Post); the ranged ones (PostRange:
        //    WriteGuest, FillBuffer, CopyBuffer) and everything else still join first.
        //  - Full slices are joined as soon as their workers are done (JoinReady after each taken item), and the oldest
        //    one is waited for when more than SHARPEMU_FRONT_SLICE_AHEAD (2 x workers) are pending, instead of all of them
        //    at the next item that is not taken.
        //  - Every guard of 3a/3b stays: a guest read overlapping a pending write, a preparation check with a taken writer
        //    open, an item not taken, the end of RunPending and AfterItem join everything first.
        internal static readonly bool Defer = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SLICE_DEFER") == "1";

        // [local] SHARPEMU_FRONT_SLICE_WRITES=1 (off by default; the last step of the slices line, §10): the front's WriteGuest
        // (FrontCommandStreamHost.FrontWrite: labels, end-of-pipe and event stores, occlusion and GDS dumps), 93 % of the
        // cuts in GTA V (`sbt`), no longer joins what is open. It is taken as a render unit, as SHARPEMU_FRONT_SLICE_DEFER
        // takes the posts (and it takes them too: a label is followed by its end-of-pipe post, which would cut instead):
        //  - the render thread applies it (WriteCommandData: guest memory, or both copies of a registered range with the
        //    device copy recorded then) when it joins the slice it sits in, after the segment before it was validated,
        //    its prologue uploaded what it reads from guest memory and it was submitted, and before the segment after it.
        //    So the GPU sees it, and the guest and the front read it, exactly where they would in serial, and an upload
        //    of an earlier item never sees it, nor a later item's upload miss it;
        //  - the resolution of the items after it runs before it is applied: a render-thread read of its bytes (every read
        //    BeforeRead guards: the command memory, TryReadGuest, the materializer's table words before TABLE_WORD_MEMO,
        //    clean and resident reads) joins everything first (flush `write`), as for a pending GPU write;
        //  - the device store table planner: what the write would do there at its position runs at once (its generations
        //    move, InvalidateAhead), unless a cached table holds its bytes (refusal `tables`: it cuts, as before);
        //  - a CP write moves no first-GPU-write version, so the front preparations' check (FrontPreparationStillValid)
        //    sees in serial what it sees here; TABLE_WORD_MEMO is updated when it is applied, and only read after BeforeRead.
        // JoinReady stays with SHARPEMU_FRONT_SLICE_DEFER: without it the writes are applied at the next cut or flush.
        internal static readonly bool Writes = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SLICE_WRITES") == "1";

        // [local] The posts are taken as render units (SHARPEMU_FRONT_SLICE_DEFER or SHARPEMU_FRONT_SLICE_WRITES).
        internal static readonly bool TakesPosts = Defer || Writes;

        internal static readonly int Ahead =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SLICE_AHEAD"), out var ahead) && ahead > 0 ? ahead : 0;

        private readonly Presenter _presenter;
        private readonly Action _barrier;
        private readonly FrontSliceCutter<FrontSliceItem, RenderSlice> _cutter;
        private readonly List<AgcExports.DeferredWork> _captured = new();
        private readonly Stack<FrontSliceItem> _spare = new();
        private readonly Action _betweenItems;
        private int _thread = -1;
        private int _openWriters;
        private readonly FrontSliceWrites _writes = new(); // [local] SHARPEMU_FRONT_SLICE_WRITES

        public FrontSlices(Presenter presenter, FrontCommandStreamHost front)
        {
            _presenter = presenter;
            _barrier = front.BarrierItem;
            _betweenItems = presenter.FrontSliceRelay;
            _cutter = new FrontSliceCutter<FrontSliceItem, RenderSlice>(this, FrontSliceStats.Shared, SliceItems, MinimumItems);
            FrontSliceStats.Shared.ModelSliceItems = SliceItems; // [local] gta/parres3: the virtual cutter replays with these
            FrontSliceStats.Shared.ModelMinimumItems = MinimumItems;
        }

        public bool HasOpen => _cutter.HasOpen;

        // A taken dispatch is open (SHARPEMU_FRONT_SLICE_DISPATCHES).
        public bool OpenWriters => _openWriters != 0;

        public bool Take(Action work)
        {
            if (work.Target is FrontCommandStreamHost.FrontDraw draw && _presenter.SlicesEnabled && _presenter._translation.DrawPassesSliceGate(draw.Banks))
            {
                return true;
            }

            if (Dispatches && work.Target is FrontCommandStreamHost.FrontDispatch && _presenter.SlicesEnabled && !Gpu.Rendering.RenderRecorder.NeedsSerialDispatch)
            {
                return true;
            }

            if (ReferenceEquals(work, _barrier) && _cutter.HasOpen)
            {
                return true;
            }

            if (TakesPosts && work.Target is FrontCommandStreamHost.FrontPost && _cutter.HasOpen && _presenter.SlicesEnabled)
            {
                return true;
            }

            if (work.Target is FrontCommandStreamHost.FrontWrite write && _cutter.HasOpen && TakeWrite(write))
            {
                return true;
            }

            if (ParallelResolveProbe.Enabled && _cutter.HasOpen) // [local] SHARPEMU_PARRES_PROBE: slice_cuts
            {
                NoteCut(work);
            }

            if (ParallelResolveProbe.Enabled) // [local] gta/parres3: the cut, as the virtual cutter sees it
            {
                _cutWrite = work.Target is FrontCommandStreamHost.FrontWrite;
                _cutPost = !_cutWrite && IsLabelPost(work);
            }

            Flush(FrontSliceFlush.Item);
            _cutWrite = _cutPost = false;
            return false;
        }

        // [local] gta/parres3 (SHARPEMU_PARRES_PROBE): the end-of-pipe and interrupt posts that follow a label.
        private bool _cutWrite, _cutPost;
        private readonly Dictionary<System.Reflection.MethodInfo, bool> _labelPosts = new();

        private bool IsLabelPost(Action work)
        {
            var target = work.Target is FrontCommandStreamHost.FrontPost post ? post.Work.Run : work;
            if (!_labelPosts.TryGetValue(target.Method, out var label))
            {
                var name = target.Method.Name;
                label = name.Contains("RecordEndOfPipe", StringComparison.Ordinal) || name.Contains("TriggerInterrupt", StringComparison.Ordinal);
                _labelPosts[target.Method] = label;
            }

            return label;
        }

        // [local] SHARPEMU_FRONT_SLICE_WRITES: whether a command write met while something is open is taken; with the probe, the
        // census of those writes (slice_writes, slice_write_sites).
        private bool TakeWrite(FrontCommandStreamHost.FrontWrite write)
        {
            var size = (ulong)write.Length;
            var enabled = Writes && _presenter.SlicesEnabled;
            var inTables = enabled && SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.MayInvalidate(write.Address, size);
            var refusal = FrontSliceGuards.WriteRefusal(enabled,
                inTables && !SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidateAhead(write.Address, size,
                    SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidationSource.CommandRender));
            if (ParallelResolveProbe.Enabled)
            {
                FrontSliceStats.Shared.NoteWrite(write.Caller, write.Length, refusal, inTables,
                    _presenter.IsCommandWriteRegistered(write.Address, size), PendingOverlap(write.Address, size));
            }

            return refusal is null;
        }

        private readonly Dictionary<System.Reflection.MethodInfo, string> _cutNames = new();

        private void NoteCut(Action work)
        {
            if (work.Target is FrontCommandStreamHost.FrontDraw)
            {
                FrontSliceStats.Shared.NoteCut("FrontDraw");
                return;
            }

            if (!_cutNames.TryGetValue(work.Method, out var name))
            {
                name = $"{work.Method.DeclaringType?.Name}.{work.Method.Name}";
                _cutNames[work.Method] = name;
            }

            FrontSliceStats.Shared.NoteCut(name);
        }

        public void Run(Action work)
        {
            if (work.Target is FrontCommandStreamHost.FrontDraw draw)
            {
                draw.HoldSnapshot = true;
                RunCaptured(work, draw);
                return;
            }

            if (work.Target is FrontCommandStreamHost.FrontDispatch)
            {
                // A dispatch record keeps no reference to the banks: they go back to the front when Run ends.
                RunCaptured(work, null);
                return;
            }

            if (work.Target is FrontCommandStreamHost.FrontPost post)
            {
                post.RunNow();
                _cutter.Add(NewItem(post.Work, 0, null));
                return;
            }

            if (work.Target is FrontCommandStreamHost.FrontWrite write) // [local] SHARPEMU_FRONT_SLICE_WRITES: a render unit
            {
                var token = _writes.Add(write.Address, (ulong)write.Length);
                var writes = _writes;
                _cutter.Add(NewItem(new FrontPostWork(() =>
                {
                    write.Run();
                    writes.Applied(token);
                }), 0, null));
                return;
            }

            _cutter.Add(NewItem(AgcExports.GlobalBarrierWork.Instance, 0, null));
        }

        // [local] SHARPEMU_FRONT_SLICE_DEFER: after a taken item, outside its probe timing (the joins have their own buckets):
        // the full slices whose workers are done, in order (FrontSliceCutter.JoinReady).
        public void AfterTaken()
        {
            if (Defer && _cutter.PendingSlices != 0)
            {
                _ = _cutter.JoinReady(Ahead > 0 ? Ahead : 2 * Math.Max(_presenter.WorkerCount, 1));
            }
        }

        public void Flush(FrontSliceFlush reason)
        {
            if (ParallelResolveProbe.Enabled) // [local] gta/parres3: real and virtual runs
            {
                FrontSliceStats.Shared.ModelWorkers = Math.Max(_presenter.WorkerCount, 1);
                FrontSliceStats.Shared.NoteBoundary(reason == FrontSliceFlush.Item && _cutWrite, reason == FrontSliceFlush.Item && _cutPost);
            }

            _cutter.Flush(reason);
            if (!_cutter.HasOpen)
            {
                _openWriters = 0;
                _writes.Clear();
            }
        }

        // [local] A taken writer ended (joined or run here): with SHARPEMU_FRONT_SLICE_DEFER the run it was in may stay open.
        private void EndWriter(FrontSliceItem item)
        {
            if (item.Writer)
            {
                item.Writer = false;
                if (_openWriters > 0)
                {
                    _openWriters--;
                }
            }
        }

        private void RunCaptured(Action work, IFrontSliceOwner? owner)
        {
            _thread = Environment.CurrentManagedThreadId;
            _captured.Clear();
            var translation = _presenter._translation;
            var kind = owner is null ? 1 : 0;
            var probeStarted = ParallelResolveProbe.Enabled ? Stopwatch.GetTimestamp() : 0; // [local] SHARPEMU_PARRES_PROBE: slice_capture
            if (probeStarted != 0) ParallelResolveProbe.CaptureKind = kind;
            translation.BeginSliceCapture(_captured);
            try
            {
                work();
            }
            finally
            {
                translation.EndSliceCapture();
                ParallelResolveProbe.CaptureKind = -1;
            }

            var workDone = probeStarted != 0 ? Stopwatch.GetTimestamp() : 0;
            try
            {
                SortCaptured(owner);
            }
            finally
            {
                if (probeStarted != 0)
                {
                    ParallelResolveProbe.CaptureItems[kind]++;
                    ParallelResolveProbe.CaptureWorkTicks[kind] += workDone - probeStarted;
                    ParallelResolveProbe.CapturePostTicks[kind] += Stopwatch.GetTimestamp() - workDone;
                }
            }
        }

        private void SortCaptured(IFrontSliceOwner? owner)
        {
            if (_captured.Count == 0)
            {
                owner?.ReleaseSnapshot();
                return;
            }

            var inline = false;
            for (var index = 0; index < _captured.Count; index++)
            {
                var captured = _captured[index];
                var last = index == _captured.Count - 1 ? owner : null;
                if (!inline && AgcExports.CommandStreamTranslation.IsSliceableRecord(captured, Dispatches))
                {
                    var item = NewItem(captured, AgcExports.CommandStreamTranslation.SequenceOf(captured), last);
                    if (captured.IsDispatch)
                    {
                        _openWriters++;
                        item.Writer = true;
                    }

                    _cutter.Add(item);
                    continue;
                }

                if (!inline)
                {
                    inline = true;
                    Flush(FrontSliceFlush.Record);
                }

                RunWork(captured);
                last?.ReleaseSnapshot();
            }

            _captured.Clear();
        }

        // While a taken dispatch is open: a front preparation that read a page the open items may write, or whose pages are
        // not known, joins them first, so FrontPreparationStillValid sees the ranges they write first, as in serial.
        public void BeforePreparationCheck(FrontPrepReads.Pages? pages)
        {
            if (_cutter.Joining || Environment.CurrentManagedThreadId != _thread)
            {
                return;
            }

            if (FrontSliceGuards.PreparationNeedsJoin(pages, _presenter._translation.PendingWrites is null ? null : _pendingOverlap ??= PendingOverlap))
            {
                JoinBeforeRead(FrontSliceFlush.Prep);
            }
        }

        // A join from inside a read waits for the workers; inside a gate they may take, it would wait forever.
        private void JoinBeforeRead(FrontSliceFlush reason)
        {
            if (_presenter._translation.PipelineGatesHeld)
            {
                throw SubmissionScheduler.Fatal($"SHARPEMU_FRONT_SLICES: a guest read must join the taken items inside a pipeline gate (flush {reason}).");
            }

            Flush(reason);
        }

        private Func<ulong, ulong, bool>? _pendingOverlap;

        private bool PendingOverlap(ulong address, ulong size) =>
            _presenter._translation.PendingWrites is { } pending && pending.MayHavePending(address, size) &&
            pending.HasPendingBetween(address, size, pending.Released, long.MaxValue);

        // Joins everything taken when the read overlaps a pending write of a taken draw: in stream order the draw is
        // recorded before what reads after it.
        public void BeforeRead(ulong address, ulong size)
        {
            if (size == 0 || _cutter.Joining || Environment.CurrentManagedThreadId != _thread)
            {
                return;
            }

            if (FrontSliceGuards.ReadNeedsJoin(address, size, _pendingOverlap ??= PendingOverlap, _writes) is { } reason)
            {
                JoinBeforeRead(reason);
            }
        }

        private FrontSliceItem NewItem(AgcExports.DeferredWork work, long sequence, IFrontSliceOwner? owner)
        {
            var item = _spare.Count != 0 ? _spare.Pop() : new FrontSliceItem();
            item.Item.Work = work;
            item.Item.IsDraw = work is not (AgcExports.GlobalBarrierWork or FrontPostWork) && !work.IsDispatch;
            item.Item.Sequence = sequence;
            item.Owner = owner;
            item.Writer = false;
            return item;
        }

        // The item's Work stays set: a worker may still read it after the render thread ended the item. RecordSlice notes
        // the cost of the item it stopped at (Serial) after handing out the segment before it, and the render thread runs
        // that item as soon as it has the segment. NewItem only reuses items after every slice of the flush finished.
        private void Return(FrontSliceItem item)
        {
            EndWriter(item);
            item.Owner?.ReleaseSnapshot();
            item.Owner = null;
            _spare.Push(item);
        }

        private void RunWork(AgcExports.DeferredWork work)
        {
            var started = Stopwatch.GetTimestamp();
            if (work is FrontPostWork post) // [local] SHARPEMU_FRONT_SLICE_DEFER: a render unit, in stream order
                post.Run();
            else
                _presenter.RunFrontSliceWork(work);
            ParallelResolveProbe.SliceInlineTicks += Stopwatch.GetTimestamp() - started;
            ParallelResolveProbe.SliceInlineItems++;
        }

        int FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.Workers => _presenter.SlicesEnabled ? _presenter.WorkerCount : 0;

        RenderSlice FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.Dispatch(List<FrontSliceItem> items, int start, int count)
        {
            var started = Stopwatch.GetTimestamp();
            var slice = new RenderSlice();
            for (var index = start; index < start + count; index++)
            {
                slice.Items.Add(items[index].Item);
            }

            _presenter.DispatchSlice(slice);
            ParallelResolveProbe.SliceHandoffTicks += Stopwatch.GetTimestamp() - started;
            return slice;
        }

        bool FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.TryClaim(RenderSlice slice)
        {
            if (!slice.TryClaim())
            {
                return false;
            }

            Presenter.NoteStolen(slice);
            slice.Done.Set();
            return true;
        }

        bool FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.IsDone(RenderSlice slice) => slice.Done.IsSet;

        bool FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.NextSegment(RenderSlice slice, out FrontSliceSegment segment)
        {
            var started = Stopwatch.GetTimestamp();
            var next = _presenter.NextSegment(slice, _betweenItems);
            var waited = Stopwatch.GetTimestamp() - started;
            ParallelResolveProbe.SliceWaitTicks += waited;
            if (_cutter.JoiningRest) ParallelResolveProbe.SliceRestWaitTicks += waited;
            segment = next is null ? default : new FrontSliceSegment(next.First, next.Count, next.SerialReason is not null, next.SerialReason, next);
            return next is not null;
        }

        bool FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.JoinSegment(RenderSlice slice, in FrontSliceSegment segment)
        {
            var started = Stopwatch.GetTimestamp();
            var joined = _presenter.JoinSegment(slice, (SliceSegment)segment.Handle!);
            Presenter.NoteJoin(started);
            ParallelResolveProbe.SliceJoinTicks += Stopwatch.GetTimestamp() - started;
            return joined;
        }

        int FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.Resume(RenderSlice slice) => slice.Resume;

        void FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.Finish(RenderSlice slice) => _presenter.FinishSlice(slice);

        void FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.Complete(FrontSliceItem item)
        {
            _presenter._translation.CompleteDeferred(item.Item.Work!);
            Return(item);
        }

        void FrontSliceCutter<FrontSliceItem, RenderSlice>.IBackend.RunInline(FrontSliceItem item, FrontSliceInline reason)
        {
            RunWork(item.Item.Work!);
            Return(item);
        }
    }
}
