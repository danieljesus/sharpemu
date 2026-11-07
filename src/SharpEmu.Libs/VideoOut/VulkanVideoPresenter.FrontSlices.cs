// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Diagnostics;
using SharpEmu.Libs.Agc;

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

        internal void RunFrontSliceWork(AgcExports.DeferredWork work) => RunDeferredWork(work);

        internal Action FrontSliceRelay => RunRelayBetweenCommandItems;
    }

    // [local] SHARPEMU_FRONT_SLICES: a draw record or global barrier the render thread resolved and a worker records.
    private sealed class FrontSliceItem
    {
        public readonly CommandThread.Item Item = new() { Kind = CommandThread.Kind.Deferred };

        // The draw whose banks the record reads; set on the last record of the draw.
        public FrontCommandStreamHost.FrontDraw? Owner;
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

        private readonly Presenter _presenter;
        private readonly Action _barrier;
        private readonly FrontSliceCutter<FrontSliceItem, RenderSlice> _cutter;
        private readonly List<AgcExports.DeferredWork> _captured = new();
        private readonly Stack<FrontSliceItem> _spare = new();
        private readonly Action _betweenItems;
        private int _thread = -1;

        public FrontSlices(Presenter presenter, FrontCommandStreamHost front)
        {
            _presenter = presenter;
            _barrier = front.BarrierItem;
            _betweenItems = presenter.FrontSliceRelay;
            _cutter = new FrontSliceCutter<FrontSliceItem, RenderSlice>(this, FrontSliceStats.Shared, SliceItems, MinimumItems);
        }

        public bool HasOpen => _cutter.HasOpen;

        public bool Take(Action work)
        {
            if (work.Target is FrontCommandStreamHost.FrontDraw draw && _presenter.SlicesEnabled && _presenter._translation.DrawPassesSliceGate(draw.Banks))
            {
                return true;
            }

            if (ReferenceEquals(work, _barrier) && _cutter.HasOpen)
            {
                return true;
            }

            _cutter.Flush(FrontSliceFlush.Item);
            return false;
        }

        public void Run(Action work)
        {
            if (work.Target is FrontCommandStreamHost.FrontDraw draw)
            {
                RunDraw(draw);
                return;
            }

            _cutter.Add(NewItem(AgcExports.GlobalBarrierWork.Instance, 0, null));
        }

        public void Flush(FrontSliceFlush reason) => _cutter.Flush(reason);

        private void RunDraw(FrontCommandStreamHost.FrontDraw draw)
        {
            _thread = Environment.CurrentManagedThreadId;
            _captured.Clear();
            draw.HoldSnapshot = true;
            var translation = _presenter._translation;
            translation.BeginSliceCapture(_captured);
            try
            {
                draw.Run();
            }
            finally
            {
                translation.EndSliceCapture();
            }

            if (_captured.Count == 0)
            {
                draw.ReleaseSnapshot();
                return;
            }

            var inline = false;
            for (var index = 0; index < _captured.Count; index++)
            {
                var work = _captured[index];
                var owner = index == _captured.Count - 1 ? draw : null;
                if (!inline && AgcExports.CommandStreamTranslation.IsSliceableDraw(work))
                {
                    _cutter.Add(NewItem(work, AgcExports.CommandStreamTranslation.SequenceOf(work), owner));
                    continue;
                }

                if (!inline)
                {
                    inline = true;
                    _cutter.Flush(FrontSliceFlush.Record);
                }

                RunWork(work);
                owner?.ReleaseSnapshot();
            }

            _captured.Clear();
        }

        // Joins everything taken when the read overlaps a pending write of a taken draw: in stream order the draw is
        // recorded before what reads after it.
        public void BeforeRead(ulong address, ulong size)
        {
            if (size == 0 || _cutter.Joining || Environment.CurrentManagedThreadId != _thread)
            {
                return;
            }

            if (_presenter._translation.PendingWrites is { } pending && pending.MayHavePending(address, size) &&
                pending.HasPendingBetween(address, size, pending.Released, long.MaxValue))
            {
                _cutter.Flush(FrontSliceFlush.Read);
            }
        }

        private FrontSliceItem NewItem(AgcExports.DeferredWork work, long sequence, FrontCommandStreamHost.FrontDraw? owner)
        {
            var item = _spare.Count != 0 ? _spare.Pop() : new FrontSliceItem();
            item.Item.Work = work;
            item.Item.IsDraw = work is not AgcExports.GlobalBarrierWork;
            item.Item.Sequence = sequence;
            item.Owner = owner;
            return item;
        }

        // The item's Work stays set: a worker may still read it after the render thread ended the item. RecordSlice notes
        // the cost of the item it stopped at (Serial) after handing out the segment before it, and the render thread runs
        // that item as soon as it has the segment. NewItem only reuses items after every slice of the flush finished.
        private void Return(FrontSliceItem item)
        {
            item.Owner?.ReleaseSnapshot();
            item.Owner = null;
            _spare.Push(item);
        }

        private void RunWork(AgcExports.DeferredWork work)
        {
            var started = Stopwatch.GetTimestamp();
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
