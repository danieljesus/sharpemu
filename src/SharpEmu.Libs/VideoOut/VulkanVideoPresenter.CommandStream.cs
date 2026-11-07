// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // The presenter whose render thread runs the command stream; null while no window runs.
    private static Presenter? _activePresenter;
    private static Exception? _presenterStartupFailure;
    internal static Func<ICpuMemory, AgcExports.HeadlessCommandStream>? TestCommandStreamFactory { get; set; }

    public static void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.PresenterEntered,
            queue, submissionId, address, dwordCount);
        lock (_gate)
        {
            if (_closed || HostSessionControl.IsShutdownRequested || Volatile.Read(ref _presenterCloseRequested))
            {
                if (_presenterStartupFailure is { } failure)
                    throw new InvalidOperationException("The graphics presenter could not start.", failure);
                throw new OperationCanceledException("The graphics session is shutting down.");
            }
        }
        if (TestCommandStreamFactory?.Invoke(memory) is { } testStream)
        {
            testStream.Submit(queue, address, dwordCount, submissionId, geometrySnapshots);
            return;
        }

        EnsureStarted(1280, 720);
        WaitForShaderPrewarm("first GPU submission");
        // Queue publication precedes device initialization; only the render thread consumes it.
        lock (_gate)
        {
            while (_activePresenter is null && !_closed && !HostSessionControl.IsShutdownRequested &&
                   !Volatile.Read(ref _presenterCloseRequested))
            {
                System.Threading.Monitor.Wait(_gate);
            }

            if (_presenterStartupFailure is { } failure)
            {
                throw new InvalidOperationException("The graphics presenter could not start.", failure);
            }
            if (_closed || HostSessionControl.IsShutdownRequested || Volatile.Read(ref _presenterCloseRequested))
            {
                throw new OperationCanceledException("The graphics session is shutting down.");
            }
            _activePresenter!.EnqueueCommandStream(queue, address, dwordCount, submissionId, geometrySnapshots);
            Presenter.WakeRenderThread();
        }
    }

    public static IdleOutcome SubmitDone(ICpuMemory memory) =>
        Volatile.Read(ref _presenterStartupFailure) is not null ? IdleOutcome.Failed :
        HostSessionControl.IsShutdownRequested || Volatile.Read(ref _closed) || Volatile.Read(ref _presenterCloseRequested) ? IdleOutcome.Cancelled :
        TryGetActivePresenter(out var presenter)
            ? presenter.CommandStream.Done()
            : TestCommandStreamFactory?.Invoke(memory) is { } testStream
                ? testStream.Done()
                : IdleOutcome.Completed;

    public static void RunAfterPendingCommandStreams(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (TryGetActivePresenter(out var presenter))
        {
            if (presenter.Relay.TryRunAfterAcceptedCommandStreams(work))
            {
                return;
            }

            if (!HostSessionControl.IsShutdownRequested && !Volatile.Read(ref _closed) &&
                !Volatile.Read(ref _presenterCloseRequested))
            {
                throw SubmissionScheduler.Fatal("The GPU worker rejected an ordered video-out state change.");
            }
        }

        work();
    }

    // The blocked heads of the stream this memory submits to; null when no stream exists for it.
    internal static BlockedSnapshot? SnapshotBlockedCommandStream(ICpuMemory? memory)
    {
        if (TryGetActivePresenter(out var presenter))
        {
            return presenter.CommandStream.SnapshotBlocked();
        }

        return memory is not null && TestCommandStreamFactory?.Invoke(memory) is { } testStream
            ? testStream.Queue.SnapshotBlocked()
            : null;
    }

    // [local] SHARPEMU_REWIND_PATCH_WAKE: the guest changed a REWIND packet's valid bit through
    // sceAgcRewindPatchSetRewindState; the heads suspended on a REWIND re-evaluate it now. With onlyWhenGpuIdle, only
    // while no submitted tick is still running on the GPU: a completing tick retries the heads anyway, and without one
    // the next retry is the 100 ms all-blocked one.
    internal static int RetryRewindHeadsAfterGuestPatch(bool onlyWhenGpuIdle)
    {
        if (Volatile.Read(ref _activePresenter) is not { } presenter || Volatile.Read(ref _closed))
        {
            return 0;
        }

        if (onlyWhenGpuIdle &&
            (presenter.Scheduler is not { } scheduler || scheduler.SubmittedTick > scheduler.Timeline.CompletedTick))
        {
            return 0;
        }

        return presenter.CommandStream.RetryRewindHeads();
    }

    // [local] SHARPEMU_REWIND_PATCH_WAKE=3: the oldest valid REWIND patch not yet followed by a REWIND retry (0: none).
    private static readonly long RewindPatchRetryTicks =
        (Environment.GetEnvironmentVariable("SHARPEMU_REWIND_PATCH_WAKE") ?? "3") == "3"
            ? Math.Max(1, (long)((double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_REWIND_PATCH_WAKE_MS"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var patchMs) ? patchMs : 2.0) *
                System.Diagnostics.Stopwatch.Frequency / 1000))
            : 0;

    private static long _pendingRewindPatchTicks;

    internal static void NoteRewindPatch()
    {
        if (Volatile.Read(ref _pendingRewindPatchTicks) == 0)
            Interlocked.CompareExchange(ref _pendingRewindPatchTicks, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
    }

    private static bool TryGetActivePresenter([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Presenter? presenter)
    {
        lock (_gate)
        {
            presenter = _closed ? null : _activePresenter;
            return presenter is not null;
        }
    }

    // A draw or dispatch reaches the presenter only from the render thread; none is queued.
    private static bool TryGetRenderThreadPresenter([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Presenter? presenter)
    {
        presenter = Volatile.Read(ref _activePresenter);
        if (presenter is null || !presenter.IsVulkanReady)
        {
            presenter = null;
            return false;
        }

        if (!presenter.Relay.IsGpuQueueThread)
        {
            throw SubmissionScheduler.Fatal("A guest draw was submitted from a thread other than the render thread.");
        }

        return true;
    }

    // A video-out export flip: queued on the graphics queue, so it captures after the draws before it.
    public static bool TrySubmitGuestImage(int videoOutHandle, int displayBufferIndex, ulong address, uint width, uint height, uint pitchInPixel, ulong flipRequestId)
    {
        _ = width;
        _ = height;
        _ = pitchInPixel;
        WaitForShaderPrewarm("first flip");
        if (!IsKnownDisplayBuffer(address) || !TryGetActivePresenter(out var presenter))
        {
            return false;
        }

        if (!presenter.CommandStream.TryEnqueueFlipPreparation(videoOutHandle, displayBufferIndex, flipRequestId))
        {
            return false;
        }

        Presenter.WakeRenderThread();
        return true;
    }

    private static readonly string[] _commandQueueNames = CreateCommandQueueNames();

    private static string[] CreateCommandQueueNames()
    {
        var names = new string[CommandStreamQueue.QueueCount];
        names[0] = "dcb.graphics";
        for (var queueId = 1; queueId < names.Length; queueId++)
        {
            names[queueId] = $"acb.compute[{GpuCommandInterpreter.ComputeQueueBase + queueId - 1}]";
        }

        return names;
    }

    private sealed partial class Presenter : ICommandStreamHost, IFlipSubmitter
    {
        // This partial hosts the command interpreter on the render thread.

        private readonly CommandStreamQueue _commandStream;
        private readonly EndOfPipeEvents _endOfPipeEvents = new();
        private EndOfPipe _endOfPipe = null!;
        internal AgcExports.CommandStreamTranslation _translation = null!;
        internal ICpuMemory _guestMemory = null!;

        public CommandStreamQueue CommandStream => _commandStream;

        private void CreateCommandStream()
        {
            var (_, guest, _) = RequireGuestMemory("command stream");
            _guestMemory = guest;
            _endOfPipe = new EndOfPipe(_endOfPipeEvents, this);
            _recording = new RecordingHost(this);
            _translation = AgcExports.CreateCommandStreamTranslation(((ICommandStreamHost)this).Memory, this);
            CreateRenderWorkers();
        }

        public void EnqueueCommandStream(uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
        {
            RenderPhaseProfile.RecordSubmissionArrival();
            if (queue == 0)
            {
                _commandStream.EnqueueGraphics(address, dwordCount, submissionId, geometrySnapshots);
            }
            else
            {
                _commandStream.EnqueueCompute(queue, address, dwordCount, submissionId, geometrySnapshots);
            }
        }

        // The command stream thread interprets the submissions; this thread runs what it hands over
        // until the budget ends or nothing is queued. Null until the first render tick.
        internal CommandThread? _commandThread;
        private Action? _betweenCommandItems;

        private bool OnCommandThread => _commandThread is { IsProducerThread: true };

        private static readonly int ResolveWorkerCount =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RESOLVE_WORKERS"), out var resolveWorkers) && resolveWorkers >= 0
                ? resolveWorkers
                : Math.Clamp(Environment.ProcessorCount / 4, 1, 6);

        private sealed class ResolveWorkerState
        {
            public Presenter? Owner;
            public long Sequence;
            public ResolveReadLog Reads = null!;
            public CleanReadPages? CleanReads;
        }

        [ThreadStatic]
        private static ResolveWorkerState? _resolveWorker;

        private bool OnResolveWorker => _resolveWorker is { Sequence: not 0 } worker && ReferenceEquals(worker.Owner, this);

        int ICommandStreamHost.ResolveWorkerCount => ResolveWorkerCount;

        PendingCommandSignal ICommandStreamHost.CommandSignal => _relay.CommandSignal;

        void ICommandStreamHost.BeginWorkerResolve(long sequence, ResolveReadLog reads)
        {
            var worker = _resolveWorker ??= new ResolveWorkerState();
            if (!ReferenceEquals(worker.Owner, this))
            {
                worker.Owner = this;
                worker.CleanReads = null;
            }

            worker.Sequence = sequence;
            worker.Reads = reads;
            reads.Version = _bufferCache.GpuModifiedVersion;
            reads.Publications = _guestPublications.Ordinal;
        }

        void ICommandStreamHost.EndWorkerResolve()
        {
            var worker = _resolveWorker!;
            worker.Sequence = 0;
            worker.Reads = null!;
        }

        bool ICommandStreamHost.ValidateWorkerReads(ResolveReadLog reads)
        {
            var published = reads.Publications != _guestPublications.Ordinal;
            if (!published && reads.Version == _bufferCache.GpuModifiedVersion)
            {
                return true;
            }

            Span<byte> current = stackalloc byte[256];
            for (var index = 0; index < reads.Count; index++)
            {
                var bytes = reads.Bytes(index, out var address);
                var size = (ulong)bytes.Length;
                if (published && HasPendingMemoryPublication(address, size))
                {
                    return false;
                }

                if (!published && !_bufferCache.EverGpuWritten(address, size))
                {
                    continue;
                }

                if (_bufferCache.HasGpuDirtyBytes(address, size))
                {
                    return false;
                }

                var now = bytes.Length <= current.Length ? current[..bytes.Length] : new byte[bytes.Length];
                if (!_guestMemory.TryRead(address, now) || !now.SequenceEqual(bytes))
                {
                    return false;
                }
            }

            return true;
        }

        // A read for a draw resolving ahead of the stream: a range earlier GPU work may still write
        // sends the draw to the stream thread; everything else reads with the stores before it applied.
        private bool ReadOnResolveWorker(ulong address, Span<byte> destination)
        {
            var size = (ulong)destination.Length;
            if (HasPendingMemoryPublication(address, size))
            {
                throw new SerialResolveRequiredException("pending publication read");
            }

            var worker = _resolveWorker!;
            if (IsCleanReadPage(address, size) && TryReadAliasBytes(address, destination))
            {
                worker.Reads.Add(address, destination);
                return true;
            }

            if (!_guestMemory.CanRead(address, size))
            {
                return false;
            }

            if (TryReadShadowForWorker(address, destination))
            {
                return true;
            }

            if (IsGpuOwnedForWorkerRead(address, size))
            {
                throw new SerialResolveRequiredException("gpu owned read");
            }

            if (IsPendingForWorker(address, size))
            {
                throw new SerialResolveRequiredException("pending write read");
            }

            if (!ReadOverlayForWorker(address, destination, worker.Sequence))
            {
                return false;
            }

            worker.Reads.Add(address, destination);
            NoteCleanReadPage(address, size);
            return true;
        }

        // [local] SHARPEMU_FRONT_RESOLVE_POOL: with the front, no command thread holds stores or known GPU writes ahead of the
        // render thread for a worker (it never resolves past a queued write), so guest memory is what the stream position sees.
        private bool ReadOverlayForWorker(ulong address, Span<byte> destination, long sequence) =>
            _commandThread is { } thread ? thread.ReadOverlayBefore(address, destination, sequence) : _guestMemory.TryRead(address, destination);

        private bool TryReadShadowBeforeForWorker(ulong address, Span<byte> destination, long sequence, out long shadowSequence, out CommandThread.ShadowSource source) // [local]
        {
            if (_commandThread is { } thread)
                return thread.TryReadShadowBefore(address, destination, sequence, out shadowSequence, out source);
            shadowSequence = 0;
            source = default;
            return false;
        }

        // A known GPU write before the draw serves the read as it does on the stream thread; a write
        // marked between the two makes the known bytes stale, so the draw resolves in order instead.
        private bool TryReadShadowForWorker(ulong address, Span<byte> destination)
        {
            var sequence = _resolveWorker!.Sequence;
            if (!TryReadShadowBeforeForWorker(address, destination, sequence, out var shadowSequence, out var shadowSource)) // [local]
            {
                return false;
            }

            if (_translation.PendingWrites is { } pending && pending.HasPendingBetween(address, (ulong)destination.Length, shadowSequence, sequence))
            {
                throw new SerialResolveRequiredException("shadowed pending write read");
            }

            if (_commandThread!.HasPendingRangeBetween(address, (ulong)destination.Length, shadowSequence, sequence)) // [local] only after a shadow read, which needs the command thread
            {
                throw new SerialResolveRequiredException("shadowed pending range read");
            }

            AgcExports.NoteWorkerShadowRead((int)shadowSource);
            return true;
        }

        private bool IsGpuOwnedForWorkerRead(ulong address, ulong size) => _bufferCache.HasGpuDirtyBytes(address, size);

        private bool IsGpuOwnedForWorkerCleanRead(ulong address, ulong size) =>
            (_bufferCache.MayHaveGpuDirtyPages(address, size) && _bufferCache.HasGpuDirtyPages(address, size)) || _bufferCache.HasGpuDirtyBytes(address, size);

        private bool IsPendingForWorker(ulong address, ulong size)
        {
            var sequence = _resolveWorker!.Sequence;
            return (_translation.PendingWrites is { } pending && pending.HasPendingBetween(address, size, pending.Released, sequence)) ||
                (_commandThread?.HasShadowBefore(address, size, sequence) ?? false); // [local] no command thread with the front
        }

        // Items run in batches; completions are collected between them so blocked waits retry.
        private const int CommandItemBatch = 256;

        private void RunCommandStreamSlices(long renderWorkDeadline)
        {
            // [local] SHARPEMU_RENDER_PIPELINE=1: the front thread interprets the streams; a second interpreter thread
            // (the command stream thread) must never start beside it.
            if (_front is not null)
            {
                RunBackQueue(renderWorkDeadline);
                return;
            }

            if (_commandThread is null)
            {
                _commandThread = new CommandThread(this, _commandStream, _guestMemory, RunDeferredWork, RunCommandSlice);
                _bufferCache.ObservationGated = true;
                _betweenCommandItems = RunRelayBetweenCommandItems;
                _translation.Sink = _commandThread;
                _commandThread.Start();
            }

            var thread = _commandThread;
            for (;;)
            {
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Collect))
                {
                    CollectCompletedGuestSubmissions(waitForOldest: false);
                }

                int ran;
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CommandStream))
                {
                    ran = thread.RunBatch(renderWorkDeadline, CommandItemBatch, _betweenCommandItems!);
                }

                if (ran == 0 || System.Diagnostics.Stopwatch.GetTimestamp() >= renderWorkDeadline)
                {
                    _scheduler.SubmitDeferred();
                    return;
                }
            }
        }

        private SliceResult RunCommandSlice() => _commandStream.ProcessOne();

        private void RunDeferredWork(AgcExports.DeferredWork work)
        {
            if (work is AgcExports.GlobalBarrierWork)
            {
                EmitGlobalBarrier();
                return;
            }

            using var translationScope = RenderPhaseProfile.MeasureDetail(work.IsDispatch
                ? RenderPhaseProfile.Phase.CommandDispatchTranslation
                : RenderPhaseProfile.Phase.CommandDrawTranslation);
            var started = PerfDrawTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local] SHARPEMU_DRAW_TIMING_GATE
            var resolveToken = ResolveProfile.Enabled ? ResolveProfile.BeginDraw() : default; // [local] SHARPEMU_PROFILE_RESOLVE
            try
            {
                _translation.RunDeferred(work);
            }
            finally
            {
                if (PerfDrawTiming) Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                if (ResolveProfile.Enabled) EndResolveDraw(in resolveToken, dispatch: work.IsDispatch); // [local]
            }
        }

        // [local] The front thread (SHARPEMU_RENDER_PIPELINE=1) retries its blocked heads itself, as the render thread
        // did before the command stream thread; the command stream thread keeps its own timer (CommandThread.WaitBlocked).
        private long _lastCommandStreamProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        private static readonly long _blockedRetryTicks =
            CommandStreamQueue.AllBlockedRetryMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000L;

        private void RetryBlockedCommandStreamIfDue()
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (RewindPatchRetryTicks != 0) RetryRewindHeadsAfterPatchIfDue(now); // [local] SHARPEMU_REWIND_PATCH_WAKE=3
            if (now - _lastCommandStreamProgressTicks < _blockedRetryTicks)
            {
                return;
            }

            NoteBlockedRetry(now, _lastCommandStreamProgressTicks);
            _commandStream.RetryBlocked();
            _lastCommandStreamProgressTicks = now;
        }

        // [local] SHARPEMU_LABEL_TIMELINE: an all-blocked retry, after this long without progress, and the heads it retries.
        // The command stream thread calls it from CommandThread.WaitBlocked as well.
        internal void NoteBlockedRetry(long now, long lastProgressTicks)
        {
            if (Diagnostics.LabelTimeline.Enabled) Diagnostics.LabelTimeline.Log('Y', (ulong)((now - lastProgressTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency)); // [local]
            if (Diagnostics.LabelTimeline.Enabled) Diagnostics.LabelTimeline.LogText('Z', _commandStream.DescribeHeads()); // [local]
        }

        // [local] SHARPEMU_REWIND_PATCH_WAKE=3: every head is blocked and a valid REWIND patch is old enough: the heads
        // suspended on a REWIND re-evaluate it (only those), as the 100 ms retry would. Called by whichever thread
        // interprets the stream while it is blocked: the front thread, or the command stream thread (CommandThread.WaitBlocked).
        internal static bool RewindPatchRetryEnabled => RewindPatchRetryTicks != 0;

        // [local] Ticks until a pending REWIND patch is due (long.MaxValue when none is pending).
        internal long RewindPatchDueIn(long now)
        {
            var patched = Volatile.Read(ref _pendingRewindPatchTicks);
            return patched == 0 ? long.MaxValue : Math.Max(0, RewindPatchRetryTicks - (now - patched));
        }

        internal void RetryRewindHeadsAfterPatchIfDue(long now)
        {
            var patched = Volatile.Read(ref _pendingRewindPatchTicks);
            if (patched == 0 || now - patched < RewindPatchRetryTicks ||
                Interlocked.CompareExchange(ref _pendingRewindPatchTicks, 0, patched) != patched)
            {
                return;
            }

            var woken = _commandStream.RetryRewindHeads();
            if (Diagnostics.LabelTimeline.Enabled) Diagnostics.LabelTimeline.Log('U', 0, 3, (ulong)woken); // [local]
        }

        private void RunRelayBetweenCommandItems()
        {
            if (_relay.HasPendingCommands)
            {
                using var relayScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueRelay);
                _relay.RunPendingCommands();
            }
        }

        private DrainingWriteMemory? _commandMemory;

        ICpuMemory ICommandStreamHost.Memory => _commandMemory ??= new DrainingWriteMemory(_guestMemory, this);

        // On the command stream thread a store is queued behind the work before it and reads see
        // the queued stores.
        private sealed class DrainingWriteMemory(ICpuMemory inner, Presenter presenter) : ICpuMemory, ICpuMemoryWrapper
        {
            public ICpuMemory Inner => inner;

            public bool TryRead(ulong virtualAddress, Span<byte> destination)
            {
                if (presenter.OnResolveWorker)
                    return presenter.ReadOnResolveWorker(virtualAddress, destination);
                presenter.BeforeFrontSliceRead(virtualAddress, (ulong)destination.Length); // [local] SHARPEMU_FRONT_SLICES (render thread only)
                if (presenter.HasPendingMemoryPublication(virtualAddress, (ulong)destination.Length))
                    presenter.CompleteOverlappingPublications(virtualAddress, (ulong)destination.Length);
                return presenter.OnCommandThread
                    ? presenter._commandThread!.ReadOverlay(virtualAddress, destination)
                    : inner.TryRead(virtualAddress, destination);
            }

            public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
            {
                presenter.CompleteOverlappingPublications(virtualAddress, (ulong)source.Length);
                if (presenter.OnCommandThread)
                {
                    presenter._commandThread!.PostWrite(virtualAddress, source);
                    return true;
                }
                return inner.TryWrite(virtualAddress, source);
            }

            public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected, out bool equal)
            {
                if (presenter.OnCommandThread)
                {
                    var bytes = expected.ToArray();
                    var compared = false;
                    var result = presenter._commandThread!.Run(() => TryCompare(virtualAddress, bytes, out compared));
                    equal = compared;
                    return result;
                }

                presenter.CompleteOverlappingPublications(virtualAddress, (ulong)expected.Length);
                return inner.TryCompare(virtualAddress, expected, out equal);
            }

            public bool TryCopy(ulong destinationAddress, ulong sourceAddress, ulong length)
            {
                if (presenter.OnCommandThread)
                {
                    return presenter._commandThread!.Run(() => TryCopy(destinationAddress, sourceAddress, length));
                }

                presenter.CompleteOverlappingPublications(destinationAddress, length);
                presenter.CompleteOverlappingPublications(sourceAddress, length);
                return inner.TryCopy(destinationAddress, sourceAddress, length);
            }

            public bool TryScanCString(ulong address, byte needle, bool findLast, ulong maxLength, out ulong match) =>
                inner.TryScanCString(address, needle, findLast, maxLength, out match);

            public bool CanRead(ulong address, ulong size) => inner.CanRead(address, size);

            public string DescribeReadRange(ulong address, ulong size) => inner.DescribeReadRange(address, size);
        }

        private static string ReadSite()
        {
            foreach (var frame in new System.Diagnostics.StackTrace(2, false).GetFrames())
            {
                if (frame.GetMethod() is { DeclaringType: { } type } method &&
                    type.Name.StartsWith(nameof(SharpEmu.Libs.Gpu.GpuCommands.GpuCommandInterpreter), StringComparison.Ordinal) &&
                    method.Name is not ("ReadDword" or "ReadQword" or "ReadBytes" or "ReadWaitOperand"))
                {
                    return method.Name;
                }
            }

            return "unknown";
        }

        // A read the render thread runs after everything queued before it, for the command thread.
        private bool ReadOnRenderThread(ulong address, Span<byte> destination, bool operand, RenderReadSource source, bool publication)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var buffer = new byte[destination.Length];
            var read = _commandThread!.Run(() => operand ? TryReadGuestOperand(address, buffer) : TryReadGuest(address, buffer));
            buffer.CopyTo(destination);
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Diagnostics.CommandThreadTally.NoteRoundTrip(elapsed);
            RoundTripTotals.NoteRead((int)source * 2 + (publication ? 0 : 1), elapsed, address, ReadSite());
            return read;
        }

        // Command memory and polled labels: GPU-written pages go through the render thread, the
        // rest is read here with the stream's queued stores applied.
        private bool ReadOnCommandThread(ulong address, Span<byte> destination, RenderReadSource source = RenderReadSource.Memory)
        {
            var size = (ulong)destination.Length;
            if (HasPendingMemoryPublication(address, size))
            {
                return ReadOnRenderThread(address, destination, operand: false, source, publication: true);
            }

            if (IsCleanReadPage(address, size) && TryReadAliasBytes(address, destination))
            {
                RenderPhaseProfile.RecordCleanRead(hit: true);
                return true;
            }

            RenderPhaseProfile.RecordCleanRead(hit: false);
            if (!_guestMemory.CanRead(address, size)) return false; // [local] an unmapped read fails instead of reaching the page tracker's fatal range check (mb13)

            var thread = _commandThread!;
            if (thread.TryReadShadow(address, destination, out var shadowSequence, out var shadowSource) &&
                !NewerPendingWrite(address, size, shadowSequence) && !thread.HasPendingRangeAfter(address, size, shadowSequence))
            {
                Diagnostics.CommandThreadTally.NoteShadowRead((int)shadowSource);
                return true;
            }

            thread.WaitForPendingRange(address, size);
            AwaitPendingGpuWrite(address, size);
            if (_bufferCache.MayHaveGpuDirtyPages(address, size) && _bufferCache.HasGpuDirtyBytes(address, size))
            {
                return ReadOnRenderThread(address, destination, operand: false, source, publication: false);
            }

            if (!thread.ReadOverlay(address, destination))
            {
                return false;
            }

            NoteCleanReadPage(address, size);
            return true;
        }

        private void AwaitPendingGpuWrite(ulong address, ulong size)
        {
            if (_translation.PendingWrites is { } pending && pending.TryGetPending(address, size, out var sequence))
            {
                _commandThread!.WaitPending(sequence);
            }
        }

        private bool NewerPendingWrite(ulong address, ulong size, long afterSequence) =>
            _translation.PendingWrites is { } pending && pending.TryGetPending(address, size, out var sequence) && sequence > afterSequence;

        private bool IsPendingOnCommandThread(ulong address, ulong size) =>
            (_translation.PendingWrites is { } pending && pending.TryGetPending(address, size, out _)) || _commandThread!.HasShadow(address, size);

        public bool HasGpuWrittenCommandData(ulong address, ulong size) =>
            HasPendingMemoryPublication(address, size) ||
            (_translation.PendingWrites is { } pending && pending.TryGetPending(address, size, out _)) ||
            (OnCommandThread && (_commandThread!.HasShadow(address, size) || _commandThread.HasPendingRangeAfter(address, size, 0))) ||
            (_bufferCache.MayHaveGpuDirtyPages(address, size) && _bufferCache.HasGpuDirtyBytes(address, size));

        public bool TryReadGuestWaitOperand(ulong address, Span<byte> destination) =>
            OnCommandThread ? ReadOnCommandThread(address, destination, RenderReadSource.Wait) : TryReadGuestOperand(address, destination);

        internal static readonly bool ReadbacksOff =
            string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_GPU_READBACKS"), "off", StringComparison.OrdinalIgnoreCase);

        public bool TryReadGuestWaitOperand(ulong address, Span<byte> destination, bool allowStale, out bool stale)
        {
            stale = false;
            if (allowStale && ReadbacksOff && OnCommandThread && TryReadStaleOnCommandThread(address, destination))
            {
                stale = true;
                return true;
            }

            return TryReadGuestWaitOperand(address, destination);
        }

        private bool TryReadStaleOnCommandThread(ulong address, Span<byte> destination)
        {
            var size = (ulong)destination.Length;
            var needsRenderThread = HasPendingMemoryPublication(address, size) ||
                _bufferCache.MayHaveGpuDirtyPages(address, size) && _bufferCache.HasGpuDirtyBytes(address, size);
            return needsRenderThread && _commandThread!.ReadOverlay(address, destination);
        }

        public bool TryReadGuestOperand(ulong address, Span<byte> destination)
        {
            if (OnResolveWorker)
            {
                return ReadOnResolveWorker(address, destination);
            }

            if (OnCommandThread)
            {
                return ReadOnCommandThread(address, destination, RenderReadSource.Operand);
            }
            return TryReadGuest(address, destination);
        }

        public bool TryReadGuest(ulong address, Span<byte> destination)
        {
            if (OnResolveWorker)
            {
                return ReadOnResolveWorker(address, destination);
            }

            if (OnCommandThread)
            {
                return ReadOnCommandThread(address, destination);
            }

            BeforeFrontSliceRead(address, (ulong)destination.Length); // [local] SHARPEMU_FRONT_SLICES
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemorySync))
            {
                if (HasPendingMemoryPublication(address, (ulong)destination.Length))
                    CompleteOverlappingPublications(address, (ulong)destination.Length);
                if (_bufferCache.HasGpuDirtyBytes(address, (ulong)destination.Length) && _bufferCache.TryReadMirrored(address, destination)) // [local]
                {
                    return true;
                }

                if (!_bufferCache.TrySynchronizeCpuRead(address, (ulong)destination.Length,
                    SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.CommandMemoryRead))
                {
                    return false;
                }
            }

            using var readScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryRead);
            return _guestMemory.TryRead(address, destination);
        }

        // [local] SHARPEMU_RELAY_GUARD (on by default; =0 disables): every draw and dispatch polls the relay; skip the
        // measured scope and the call while nothing is queued. The relay loop checks the same counter.
        private static readonly bool RelayGuard = Environment.GetEnvironmentVariable("SHARPEMU_RELAY_GUARD") != "0";

        public bool RunPendingCommands()
        {
            // With the stream on its own thread the render thread runs them between its items.
            if (OnCommandThread)
            {
                return false;
            }

            if (RelayGuard && !_relay.HasPendingCommands) // [local] SHARPEMU_RELAY_GUARD=0 always takes the measured call
            {
                return false;
            }

            using var relayScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueRelay);
            return _relay.RunPendingCommands();
        }

        public void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
        {
            if (OnCommandThread)
            {
                // The translation state belongs to this thread; the queue context to the render thread.
                _translation.BeginSubmission(queueId, submissionId, geometrySnapshots, _commandStream.GetInterpreter(queueId));
                _commandThread!.Post(() => BeginSubmissionContext(queueId, submissionId));
                return;
            }
            BeginSubmissionContext(queueId, submissionId);
            _translation.BeginSubmission(queueId, submissionId, geometrySnapshots, _commandStream.GetInterpreter(queueId));
        }

        // [local] SHARPEMU_FRONT_SLICE_DEFER: BeginSubmission in two parts, as the command thread splits it above: the
        // translation state when the front's item is taken, the queue context in stream order (FrontPost).
        internal void BeginSubmissionTranslation(int queueId, ulong submissionId, object? geometrySnapshots) =>
            _translation.BeginSubmission(queueId, submissionId, geometrySnapshots, _commandStream.GetInterpreter(queueId));

        internal void BeginSubmissionInOrder(int queueId, ulong submissionId) => BeginSubmissionContext(queueId, submissionId);

        private void BeginSubmissionContext(int queueId, ulong submissionId)
        {
            if (_occlusionQueueId != queueId)
                _recording?.EndRendering();
            _occlusionQueueId = queueId;
            _imageCache.SetMetadataTraceSubmission(queueId, submissionId);
            using var contextScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueContext);
            _activeGuestQueue = new VulkanGuestQueueIdentity(_commandQueueNames[queueId], submissionId);
            BindSubmissionContext(_activeGuestQueue);
            _ = CurrentRecordingBuffer();
        }

        public void Flush()
        {
            if (OnCommandThread)
            {
                _commandThread!.Post(Flush);
                return;
            }
            using var flushScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.Flush);
            FlushBatchedGuestCommands();
        }

        // The guest reuses the frame's memory (descriptor tables, rings) once the boundary returns,
        // so the draws queued before it are recorded first.
        public void FlushFrame()
        {
            BufferUploadProfile.NoteGuestFrameLead(++_framesEnded - _bufferCache.FrameTicks.CompletedFrame(_scheduler.CompletedTick));
            if (OnCommandThread)
            {
                _commandThread!.Post(EndFrame);
                return;
            }

            EndFrame();
        }

        private long _framesEnded;

        private void EndFrame()
        {
            Flush();
            _bufferCache.FrameTicks.NoteFrameSubmitted(_scheduler.CurrentTick - 1);
        }

        public void FlushAndWait()
        {
            if (OnCommandThread)
            {
                _commandThread!.Run(FlushAndWait);
                return;
            }
            using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandGpuWait);
            _ = CurrentRecordingBuffer();
            _scheduler.FlushAndWait();
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }

        public void SynchronizeGpu()
        {
            if (OnCommandThread)
            {
                _commandThread!.Run(SynchronizeGpu);
                return;
            }
            using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandGpuWait);
            _ = CurrentRecordingBuffer();
            _scheduler.Finish();
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }

        public void RunGarbageCollector()
        {
            if (OnCommandThread)
            {
                _commandThread!.Post(RunGarbageCollector);
                return;
            }
            RunGuestCacheCollection(endsFrame: false);
        }

        public void EmitGlobalBarrier()
        {
            if (OnCommandThread)
            {
                _commandThread!.EnqueueDeferred(AgcExports.GlobalBarrierWork.Instance, isDraw: false);
                return;
            }

            if (Gpu.Rendering.BatchSimulation.Enabled && Recording._renderingWritesMemory) Gpu.Rendering.BatchSimulation.HardCut(); // [local] C8
            // Inside a rendering scope whose draws only wrote attachments, the barrier matters only
            // to what runs after the scope: everything before the scope is ordered by the barrier
            // BeginRendering records, and a draw that stores to memory ends a deferred scope first.
            if (RecordingHost.DeferGlobalBarriers && Recording._renderingActive && !Recording._renderingWritesMemory)
            {
                Recording._globalBarrierAfterRendering = true;
                return;
            }

            Recording.EndRendering(ScopeBreak.GuestFlush);
            var probeSuppress = Gpu.Buffers.CpWriteDeferProbe.Enabled && Gpu.Buffers.CpWriteDeferProbe.AfterBreak && RecordingHost.DeferGlobalBarriers && !Recording._probeBrokenWritesMemory; // [local] deferred in the open scope
            if (RecordingHost.BatchBarriers)
            {
                // [local] The batched barrier is recorded later, without RecordGlobalBarrier: the probes note it here, in order.
                if (Gpu.Vulkan.ScopeBarrierTracker.Enabled) Gpu.Vulkan.ScopeBarrierTracker.NoteFullBarrier();
                if (Gpu.Buffers.CpWriteDeferProbe.Enabled) Gpu.Buffers.CpWriteDeferProbe.OnFullBarrier();
                if (probeSuppress) Gpu.Buffers.CpWriteDeferProbe.Suppress = true;
                RequestGuestMemoryBarrier(RecordingHost.GlobalMemoryBarrier);
                if (probeSuppress) Gpu.Buffers.CpWriteDeferProbe.Suppress = false;
                return;
            }

            if (probeSuppress) Gpu.Buffers.CpWriteDeferProbe.Suppress = true;
            RecordGlobalBarrier(BeginBatchedGuestCommands());
            if (probeSuppress) Gpu.Buffers.CpWriteDeferProbe.Suppress = false;
        }

        // [local] mainStream false: a slice worker's barrier, recorded into a buffer submitted later; the scope-barrier
        // tracker and the probe follow the render thread's stream only.
        internal void RecordGlobalBarrier(CommandBuffer commandBuffer, bool mainStream = true)
        {
            if (Gpu.Vulkan.ScopeBarrierTracker.Enabled && mainStream) Gpu.Vulkan.ScopeBarrierTracker.NoteFullBarrier(); // [local]
            if (Gpu.Buffers.CpWriteDeferProbe.Enabled && mainStream) Gpu.Buffers.CpWriteDeferProbe.OnFullBarrier(); // [local]
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                1,
                &barrier,
                0,
                null,
                0,
                null, BarrierSource.Global);
        }

        // [local] SHARPEMU_BACKING_ALIAS=0: command-stream reads do not go through the backing aliases. The command stream
        // thread no longer holds an alias scope around its slices; its alias reads enter one per read (TryReadAliasBytes).
        internal static readonly bool BackingAliasEnabled = Environment.GetEnvironmentVariable("SHARPEMU_BACKING_ALIAS") != "0";

        // [local] SHARPEMU_CMDMEM_LOG=1: per 5 s, count, bytes and ms of command-processor writes into
        // registered buffers (0), fills (1) and copies (2) applied on the render thread.
        private static class CommandMemoryStats
        {
            private static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_CMDMEM_LOG") == "1";
            private static readonly long[] Counts = new long[3], Bytes = new long[3], Ticks = new long[3];
            private static long _report = System.Diagnostics.Stopwatch.GetTimestamp();

            public readonly struct Scope(int kind, ulong bytes, long started) : IDisposable
            {
                public void Dispose()
                {
                    if (started == 0) return;
                    Counts[kind]++;
                    Bytes[kind] += (long)bytes;
                    Ticks[kind] += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - _report < 5 * System.Diagnostics.Stopwatch.Frequency) return;
                    _report = now;
                    Console.Error.WriteLine(FormattableString.Invariant(
                        $"[CMDMEM] write={Counts[0]}/{Bytes[0]}B/{Ticks[0] * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0}ms fill={Counts[1]}/{Bytes[1]}B/{Ticks[1] * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0}ms copy={Counts[2]}/{Bytes[2]}B/{Ticks[2] * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F0}ms"));
                    Array.Clear(Counts); Array.Clear(Bytes); Array.Clear(Ticks);
                }
            }

            public static Scope Measure(int kind, ulong bytes) =>
                new(kind, bytes, Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0);
        }

        public void WriteCommandData(ulong address, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            if (OnCommandThread) // [local] the device-ordered write runs on the render thread, in stream order
            {
                var bytes = data.ToArray();
                var sequence = _commandThread!.PostRange(() => WriteCommandData(address, bytes), address, (ulong)bytes.Length, CommandThread.RangeKind.Copy);
                if (bytes.Length <= (int)CommandThread.SmallTransferBytes && ((address | (ulong)bytes.Length) & 3) == 0)
                {
                    var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
                    _commandThread.NoteKnownWrite(new SharpEmu.Libs.Gpu.Rendering.KnownGpuWrite(address, words, words.Length), sequence, CommandThread.ShadowSource.Copy);
                }

                return;
            }

            if (Diagnostics.LabelLatencyProbe.Enabled && Diagnostics.LabelLatencyProbe.Overlaps(address, (ulong)data.Length)) // [local]
            {
                Diagnostics.LabelLatencyProbe.RenderApply(_scheduler.CurrentTick);
                if (data.Length >= 8) Diagnostics.LabelLatencyProbe.RememberWrite(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data));
            }
            if (Gpu.Buffers.TableWordMemo.Enabled) Gpu.Buffers.TableWordMemo.NoteCommandWrite(address, data); // [local]
            SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidateTables(address, (ulong)data.Length, SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidationSource.CommandRender); // [local] applied on the render thread
            // A range the buffer cache tracks gets the bytes in both copies, the device one through a
            // copy recorded now; anything else is plain guest memory.
            if (_bufferCache.IsRegionRegistered(address, (ulong)data.Length))
            {
                using var transferScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryTransfer);
                using var stat = CommandMemoryStats.Measure(0, (ulong)data.Length); // [local]
                if (Gpu.Buffers.CpWriteDeferProbe.Enabled) // [local]
                {
                    var recording = Recording;
                    if (recording._renderingActive) { recording._probeBrokenState = recording._renderingState; recording._probeBrokenWritesMemory = recording._renderingWritesMemory; }
                    Gpu.Buffers.CpWriteDeferProbe.BeginWrite(address, (ulong)data.Length, recording._renderingActive);
                }
                _ = BeginBatchedGuestCommands();
                if (Gpu.Buffers.GuestBufferCache.BvhWatch(address, (ulong)data.Length)) Console.Error.WriteLine($"[BVH_CPWRITE] seq={Recording._bvhSeq++} device-order address=0x{address:X} size={data.Length} tick={_scheduler.CurrentTick} payload+8C=0x{(data.Length >= 0x94 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x8C)) : 0):X} payload+90=0x{(data.Length >= 0x94 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x90)) : 0):X}");
                _bufferCache.WriteHostMemory(address, data);
                if (Gpu.Buffers.CpWriteDeferProbe.Enabled) Gpu.Buffers.CpWriteDeferProbe.EndWrite(); // [local]
                if (Gpu.Buffers.GuestBufferCache.BvhWatch(address, (ulong)data.Length)) Recording.DumpBvhBuffer($"wr{Recording._bvhSeq - 1:D4}_afterwrite_hdr", default(Gpu.Rendering.BufferDescriptorWords).WithAddress(address + 0x80), 32);
                return;
            }

            if (!_guestMemory.TryWrite(address, data))
            {
                throw SubmissionScheduler.Fatal($"The command stream cannot write guest memory: address=0x{address:X16} size={data.Length}.");
            }
        }

        public void FillBuffer(ulong address, ulong size, uint value, bool isGds)
        {
            if (OnCommandThread)
            {
                var sequence = _commandThread!.PostRange(() => FillBuffer(address, size, value, isGds), address, isGds ? 0 : size, CommandThread.RangeKind.Fill);
                if (!isGds && size != 0 && size <= CommandThread.SmallTransferBytes && ((address | size) & 3) == 0)
                {
                    var words = new uint[size / sizeof(uint)];
                    Array.Fill(words, value);
                    _commandThread.NoteKnownWrite(new SharpEmu.Libs.Gpu.Rendering.KnownGpuWrite(address, words, words.Length), sequence, CommandThread.ShadowSource.Fill);
                    Diagnostics.CommandThreadTally.NoteSmallTransfer(copy: false);
                }

                return;
            }
            using var transferScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryTransfer);
            using var stat = CommandMemoryStats.Measure(1, size); // [local]
            // Transfers the buffer cache completes in guest memory leave the rendering scope open;
            // its GPU paths end the scope before they record.
            EnsureBatchedGuestCommands();
            if (Gpu.Buffers.GuestBufferCache.BvhWatch(address, size)) Console.Error.WriteLine($"[BVH_CPWRITE] fill address=0x{address:X} size=0x{size:X} value=0x{value:X} tick={_scheduler.CurrentTick}");
            SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidateTables(address, size, SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidationSource.FillRender); // [local] applied on the render thread
            _bufferCache.FillBuffer(address, size, value, isGds);
        }

        public void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
        {
            if (OnCommandThread)
            {
                uint[]? words = null;
                if (!destinationIsGds && !sourceIsGds && size != 0 && size <= CommandThread.SmallTransferBytes && ((destination | size) & 3) == 0)
                {
                    var settled = new uint[size / sizeof(uint)];
                    if (TryReadSettledSource(source, System.Runtime.InteropServices.MemoryMarshal.AsBytes(settled.AsSpan())))
                    {
                        words = settled;
                    }
                }

                var sequence = _commandThread!.PostRange(() => CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds), destination, destinationIsGds ? 0 : size, CommandThread.RangeKind.Copy);
                if (words is not null)
                {
                    _commandThread.NoteKnownWrite(new SharpEmu.Libs.Gpu.Rendering.KnownGpuWrite(destination, words, words.Length), sequence, CommandThread.ShadowSource.Copy);
                    Diagnostics.CommandThreadTally.NoteSmallTransfer(copy: true);
                }

                return;
            }
            using var transferScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryTransfer);
            using var stat = CommandMemoryStats.Measure(2, size); // [local]
            // Transfers the buffer cache completes in guest memory leave the rendering scope open;
            // its GPU paths end the scope before they record.
            EnsureBatchedGuestCommands();
            if (Gpu.Buffers.GuestBufferCache.BvhWatch(destination, size)) Console.Error.WriteLine($"[BVH_CPWRITE] copy destination=0x{destination:X} source=0x{source:X} size=0x{size:X} tick={_scheduler.CurrentTick}");
            SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidateTables(destination, size, SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidationSource.CopyRender); // [local] applied on the render thread
            _bufferCache.CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds);
        }

        public void TraceGuestWrite(string operation, ulong address, ulong size) =>
            _imageCache.TraceGuestWrite(operation, address, size);

        public void TraceWritersOf(string subject, ulong address, ulong size) =>
            _imageCache.TraceWritersOf(subject, address, size);

        public void TraceDrawTarget(string disposition, ulong address, ulong pixelShaderAddress, ulong vertexShaderAddress) =>
            _imageCache.TraceGuestWrite($"draw-target-{disposition}", address, 1, pixelShaderAddress, vertexShaderAddress);

        private bool TryReadSettledSource(ulong source, Span<byte> destination)
        {
            var size = (ulong)destination.Length;
            if (_commandThread!.HasPendingRange(source, size) || HasPendingMemoryPublication(source, size) || !IsCleanReadPage(source, size))
            {
                return false;
            }

            return TryReadAliasBytes(source, destination);
        }

        public void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount)
        {
            if (OnCommandThread)
            {
                var words = new uint[destination.Length];
                _commandThread!.Run(() => ReadGds(words, wordOffset, wordCount));
                words.CopyTo(destination);
                return;
            }
            EndOfPipe.ReadGdsWords(_bufferCache.GdsBuffer.Mapped, destination, wordOffset, wordCount);
        }

        public void RecordEndOfPipe(in EndOfPipeWrite write)
        {
            if (OnCommandThread)
            {
                var queued = write;
                _commandThread!.Post(() => RecordEndOfPipe(in queued));
                return;
            }
            using var completionScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandEndOfPipe);
            EnsureBatchedGuestCommands();
            var buffer = _scheduler.Current;
            if (LabelTrace.Enabled && (write.EventId != 0 || write.Kind is EndOfPipeWriteKind.InterruptOnly || write.Kind >= EndOfPipeWriteKind.Flip || LabelTrace.IsWatched(write.Destination)))
                LabelTrace.Note(LabelTrace.Kind.EopBack, -1, write.Destination, write.Value, (ulong)write.Kind | ((ulong)(uint)write.EventId << 8) | (_scheduler.CurrentTick << 32));
            if (Gpu.Buffers.GuestBufferCache.BvhWatch(write.Destination, 8)) Console.Error.WriteLine($"[BVH_CPWRITE] eop kind={write.Kind} destination=0x{write.Destination:X} value=0x{write.Value:X} tick={_scheduler.CurrentTick}");
            switch (write.Kind)
            {
                case EndOfPipeWriteKind.Write32:
                    _endOfPipe.RecordWrite32(write.SubmitId, buffer, write.Destination, (uint)write.Value);
                    break;
                case EndOfPipeWriteKind.Write64:
                    _endOfPipe.RecordWrite64(write.SubmitId, buffer, write.Destination, write.Value);
                    break;
                case EndOfPipeWriteKind.WriteBack32:
                    _endOfPipe.RecordWrite32WithWriteBack(write.SubmitId, buffer, write.Destination, (uint)write.Value);
                    break;
                case EndOfPipeWriteKind.WriteBack64:
                    _endOfPipe.RecordWrite64WithWriteBack(write.SubmitId, buffer, write.Destination, write.Value);
                    break;
                case EndOfPipeWriteKind.InterruptOnly:
                    _endOfPipe.QueueInterruptOnCompletion(buffer, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.Interrupt32:
                    _endOfPipe.RecordWrite32WithInterrupt(write.SubmitId, buffer, write.Destination, (uint)write.Value, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.Interrupt64:
                    _endOfPipe.RecordWrite64WithInterrupt(write.SubmitId, buffer, write.Destination, write.Value, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.InterruptWriteBack32:
                    _endOfPipe.RecordWrite32WithInterruptAndWriteBack(write.SubmitId, buffer, write.Destination, (uint)write.Value, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.InterruptWriteBack64:
                    _endOfPipe.RecordWrite64WithInterruptAndWriteBack(write.SubmitId, buffer, write.Destination, write.Value, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.GdsWrite32:
                    _endOfPipe.RecordGdsWrite32(write.SubmitId, buffer, write.Destination, write.GdsWordOffset, write.GdsWordCount);
                    break;
                case EndOfPipeWriteKind.ClockWrite:
                    _endOfPipe.RecordClockWrite(write.SubmitId, buffer, write.Destination);
                    break;
                case EndOfPipeWriteKind.ClockWriteBack:
                    _endOfPipe.RecordClockWriteWithWriteBack(write.SubmitId, buffer, write.Destination);
                    break;
                case EndOfPipeWriteKind.Flip:
                    _endOfPipe.RecordFlipCompletion(write.SubmitId, buffer, write.FlipHandle, write.FlipIndex, write.FlipMode, write.FlipArgument, write.FlipRequestId);
                    break;
                case EndOfPipeWriteKind.FlipWithWrite32:
                    _endOfPipe.RecordWrite32WithFlip(write.SubmitId, buffer, write.Destination, (uint)write.Value, write.FlipHandle, write.FlipIndex, write.FlipMode, write.FlipArgument, write.FlipRequestId);
                    break;
                case EndOfPipeWriteKind.FlipWithInterruptWriteBack32:
                    _endOfPipe.RecordWrite32WithInterruptWriteBackAndFlip(write.SubmitId, buffer, write.Destination, (uint)write.Value, write.FlipHandle, write.FlipIndex, write.FlipMode, write.FlipArgument, write.FlipRequestId, write.EventId);
                    break;
                default:
                    throw SubmissionScheduler.Fatal($"The end-of-pipe write kind is unknown: kind={write.Kind}.");
            }
        }

        public void TriggerInterrupt(int eventId, uint contextId)
        {
            if (OnCommandThread)
            {
                _commandThread!.Post(() => TriggerInterrupt(eventId, contextId));
                return;
            }
            _endOfPipeEvents.TriggerInterrupt(eventId, contextId);
        }

        public bool HasFlipSlot()
        {
            lock (_gate)
            {
                return _pendingGuestImagePresentations.Count < MaxPendingGuestFlipVersions;
            }
        }

        public ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument) =>
            PrepareFlip(0, handle, index, flipMode, flipArgument);

        public ulong PrepareFlip(int queueId, int handle, int index, int flipMode, long flipArgument)
        {
            if (OnCommandThread)
            {
                return _commandThread!.Run(() => PrepareFlip(queueId, handle, index, flipMode, flipArgument));
            }
            ResetMappedSizes(); // [local]
            EnsureBatchedGuestCommands();
            var publication = _guestPublications.LastRecorded(queueId) is null
                ? null : _guestPublications.AddAfterPending(queueId, 0, 0);
            if (publication is not null) _guestPublications.Register(publication, _scheduler.CurrentTick);
            _capturePublication = publication;
            try
            {
                var request = _endOfPipe.PrepareVideoOutFlip(_scheduler.Current, handle, index, flipMode, flipArgument);
                if (publication is not null && !(_flipPublications ??= new()).TryAdd(request, publication))
                    throw SubmissionScheduler.Fatal("The flip publication is already registered.");
                return request;
            }
            catch (Exception exception)
            {
                FailGuestPublications(exception);
                throw;
            }
            finally { _capturePublication = null; }
        }

        // A flip whose request the front already reserved: the translation's preparation and the
        // capture run here, in stream order, as they did inside PrepareFlip.
        internal void CompleteQueuedFlip(int handle, int index, int flipMode, long flipArgument, ulong requestId)
        {
            if (Diagnostics.FlipLatencyProbe.Enabled) Diagnostics.FlipLatencyProbe.RenderStarted(requestId); // [local] SHARPEMU_FLIP_PROBE
            ResetMappedSizes();
            _ = BeginBatchedGuestCommands();
            _translation.PrepareFlip(handle, index);
            try
            {
                CaptureFlip(handle, index, requestId, flipMode, flipArgument);
                if (Diagnostics.FlipLatencyProbe.Enabled) Diagnostics.FlipLatencyProbe.Captured(requestId, _scheduler.CurrentTick); // [local]
            }
            catch
            {
                VideoOutExports.CancelFlip(requestId);
                throw;
            }
        }

        public bool IsFlipDone(int handle, int index) => VideoOutExports.IsFlipDone(handle, index);

        public void PrepareCpuFlip(int handle, int index, ulong requestId)
        {
            if (OnCommandThread)
            {
                _commandThread!.Run(() => PrepareCpuFlip(handle, index, requestId));
                return;
            }
            CaptureFlip(handle, index, requestId, flipMode: 0, flipArg: 0);
        }

        public void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments)
        {
            if (OnCommandThread)
            {
                _translation.DrawIndexed(submitId, in arguments);
                return;
            }

            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDrawTranslation);
            var started = PerfDrawTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var resolveToken = ResolveProfile.Enabled ? ResolveProfile.BeginDraw() : default; // [local] SHARPEMU_PROFILE_RESOLVE
            try
            {
                _translation.DrawIndexed(submitId, in arguments);
            }
            finally
            {
                if (PerfDrawTiming) Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                if (ResolveProfile.Enabled) EndResolveDraw(in resolveToken, dispatch: false); // [local]
            }
        }

        public void DrawAuto(ulong submitId, in DrawAutoArguments arguments)
        {
            if (OnCommandThread)
            {
                _translation.DrawAuto(submitId, in arguments);
                return;
            }

            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDrawTranslation);
            var started = PerfDrawTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var resolveToken = ResolveProfile.Enabled ? ResolveProfile.BeginDraw() : default; // [local] SHARPEMU_PROFILE_RESOLVE
            try
            {
                _translation.DrawAuto(submitId, in arguments);
            }
            finally
            {
                if (PerfDrawTiming) Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                if (ResolveProfile.Enabled) EndResolveDraw(in resolveToken, dispatch: false); // [local]
            }
        }

        // RenderExecutor records indirect workgroup dispatches with vkCmdDispatchIndirect.
        // SHARPEMU_CPU_INDIRECT_DISPATCH=1 reads the counts back on the CPU as before.
        public bool ResolvesIndirectDispatchOnGpu => !_cpuIndirectDispatch;

        // [local]
        private static readonly bool GpuThreadIndirect = Environment.GetEnvironmentVariable("SHARPEMU_GPU_THREAD_INDIRECT") != "0";

        public bool ResolvesIndirectThreadDispatchOnGpu => GpuThreadIndirect && !_cpuIndirectDispatch;

        // RenderExecutor records indexed indirect draws with vkCmdDrawIndexedIndirect.
        // SHARPEMU_CPU_INDIRECT_DRAW=1 reads the arguments back on the CPU as before.
        public bool ResolvesIndirectDrawOnGpu => !_cpuIndirectDraw;

        private static readonly bool _cpuIndirectDraw = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_CPU_INDIRECT_DRAW"), "1", StringComparison.Ordinal);

        private static readonly bool _cpuIndirectDispatch = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_CPU_INDIRECT_DISPATCH"), "1", StringComparison.Ordinal);

        public void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator, ulong indirectArgumentsAddress = 0)
        {
            if (OnCommandThread)
            {
                _translation.Dispatch(submitId, groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress);
                return;
            }
            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDispatchTranslation);
            var started = PerfDrawTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var resolveToken = ResolveProfile.Enabled ? ResolveProfile.BeginDraw() : default; // [local] SHARPEMU_PROFILE_RESOLVE
            try
            {
                _translation.Dispatch(submitId, groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress);
            }
            finally
            {
                if (PerfDrawTiming) Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                if (ResolveProfile.Enabled) EndResolveDraw(in resolveToken, dispatch: true); // [local]
            }
        }

        public void OnQueueReset(int queueId)
        {
            if (OnCommandThread)
            {
                _translation.QueueReset(queueId);
                return;
            }
            _translation.QueueReset(queueId);
        }

        public Exception Fatal(string message) => SubmissionScheduler.Fatal(message);

        // The flip packet: translation first, then the request, then the capture in stream order.
        public int SubmitFlipFromGpu(RecordingBuffer buffer, int handle, int index, int flipMode, long flipArg, out ulong requestId)
        {
            _ = buffer;
            _translation.PrepareFlip(handle, index);
            var result = VideoOutExports.TryReserveFlipRequest(handle, index, flipMode, flipArg, gpuQueued: true, out requestId);
            if (result != 0)
            {
                return result;
            }

            try
            {
                CaptureFlip(handle, index, requestId, flipMode, flipArg);
            }
            catch
            {
                VideoOutExports.CancelFlip(requestId);
                throw;
            }
            return 0;
        }

        // The interpreter must suspend before capture so this worker can present queued frames.
        public void WaitForSubmitSlot() => throw SubmissionScheduler.Fatal("The GPU worker cannot wait for a flip slot. Suspend the command stream before capture.");

        public void CompleteFlip(ulong requestId)
        {
            VideoOutExports.CompleteFlip(requestId);
        }

        // Copies the display surface through the store; presentation shows the copy once its tick retires.
        internal void CaptureFlip(int handle, int index, ulong requestId, int flipMode, long flipArg)
        {
            var publication = _capturePublication ?? _guestPublications.LastRecorded(0);
            // A no-buffer flip has no image to capture; its completion still retires in order.
            if (index == -1)
            {
                if (_capturePublication is not null) return;
                if (publication is null) VideoOutExports.ReleaseNoBufferFlip(index, requestId);
                else _scheduler.QueueMemoryPublication(() => VideoOutExports.ReleaseNoBufferFlip(index, requestId), publication.Tick);
                return;
            }
            if (!VideoOutExports.TryGetDisplayBufferInfo(handle, index, out var displayBuffer))
            {
                throw SubmissionScheduler.Fatal($"The flip names a display buffer that is not registered: handle={handle} index={index} request={requestId}.");
            }

            if (_deviceLost)
            {
                VideoOutExports.DiscardFlip(requestId);
                return;
            }

            FlushBatchedGuestCommands();
            RunGuestCacheCollection(endsFrame: true);
            EnsureGuestSubmissionCapacity(collectNow: true);
            long version;
            lock (_gate)
            {
                version = ++_guestFlipVersionSequence;
            }

            var submitted = false;
            GuestImageResource? snapshot = null;
            try
            {
                var surface = new DisplaySurfaceWords(
                    displayBuffer.Address, 0, displayBuffer.PixelFormat, displayBuffer.Width, displayBuffer.Height, displayBuffer.TilingMode, displayBuffer.Option, 0, 0, false);
                var request = ImageRequestBuilders.DisplaySurface(surface);
                EnsureBatchedGuestCommands();
                var imageIdentifier = _imageCache.FindImage(ref request);
                var source = _imageCache.GetImage(imageIdentifier);
                source.Uses.VideoOut = true;
                _imageCache.RefreshImage(imageIdentifier);
                // The refresh can end the tick; the copy records into the buffer that is current now.
                var commandBuffer = BeginBatchedGuestCommands();
                var extent = source.Backing.Extent;
                snapshot = CreateGuestFlipSnapshot(GetPresentationSnapshotFormat(source.Backing.Format),
                    extent.Width, extent.Height, displayBuffer.Address, version);
                source.Transition(AccessFlags.TransferReadBit, null, commandBuffer);
                var toTransferDst = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags2.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransferDst);
                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(extent.Width, extent.Height, 1),
                };
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdCopyImage(_vk, // [local]
                        commandBuffer, source.Backing.Handle, ImageLayout.General, snapshot.Image, ImageLayout.TransferDstOptimal, 1, &copy);
                }
                var toShaderRead = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &toShaderRead);

                // The tick of this flush, not the shared field: the presenter thread also
                // writes _submitTimeline, so reading it here can name an older tick that is
                // already complete and let the blit run before the copy above.
                var captureTick = FlushBatchedGuestCommands();
                submitted = true;
                _guestImageVersions.Add(version, snapshot);

                lock (_gate)
                {
                    var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
                    var presentation = new Presentation(
                        null,
                        displayBuffer.Width,
                        displayBuffer.Height,
                        sequence,
                        GuestDrawKind.None,
                        IsSplash: false,
                        GuestImageAddress: displayBuffer.Address,
                        GuestImageVersion: version,
                        IsHdr: VideoOutExports.IsHdrPixelFormat(displayBuffer.PixelFormat),
                        RequiredTick: captureTick,
                        FlipRequestId: requestId,
                        Publication: publication);
                    _latestPresentation = presentation;
                    _pendingGuestImagePresentations.Enqueue(presentation);
                    while (_pendingGuestImagePresentations.Count > MaxPendingGuestFlipVersions)
                    {
                        RetirePresentation(_pendingGuestImagePresentations.Dequeue());
                    }
                }

                CollectAbandonedGuestImageVersions();
                TraceVulkanShader(
                    $"vk.flip_capture version={version} " +
                    $"queue={_activeGuestQueue.Name} submission={_activeGuestQueue.SubmissionId} " +
                    $"request={requestId} addr=0x{displayBuffer.Address:X16} " +
                    $"size={extent.Width}x{extent.Height}");
                if (RenderDocCapture.IsAvailable) // [local] the capture boundary must see the frame's work issued and submitted
                {
                    Gpu.Vulkan.CommandRing.DrainActive(Gpu.Vulkan.RingDrainReason.Capture);
                    _scheduler.WaitAllSubmitted();
                }

                IndexedDrawTrace.OnFlip(version);
                VisibilityResultTrace.OnFlip(version);
                RenderDocCapture.OnGuestFlipBoundary(version);
                VideoOutExports.TraceGpuFlip(_guestMemory, handle, index, flipMode, flipArg, displayBuffer.Address, VideoOutExports.GetFlipEventCount(requestId));
            }
            finally
            {
                if (!submitted)
                {
                    VideoOutExports.DiscardFlip(requestId);
                    if (snapshot is not null)
                    {
                        DestroyGuestImage(snapshot);
                    }
                }
            }
        }

        // The presenter drops a frame it will never show: its flip no longer blocks the stream.
        private static void RetirePresentation(in Presentation presentation)
        {
            if (presentation.FlipRequestId != 0)
            {
                VideoOutExports.DiscardFlip(presentation.FlipRequestId);
            }
        }

        // The presenter is done with the frame; only a successful present counts as presented.
        private void CompletePresentation(in Presentation presentation, bool presented)
        {
            _presentedSequence = presentation.Sequence;
            if (presentation.FlipRequestId == 0)
            {
                return;
            }

            if (presented)
            {
                VideoOutExports.MarkFlipPresented(presentation.FlipRequestId);
            }
            else
            {
                VideoOutExports.DiscardFlip(presentation.FlipRequestId);
            }

            _commandStream.RetryBlocked();
        }

        // Keep GPU completion separate from refresh eligibility.
        private bool IsPresentationReadyLocked(in Presentation presentation) =>
            (presentation.Publication is null || presentation.Publication.Completed) &&
            (presentation.RequiredTick == 0 || (_scheduler is not null && _scheduler.Timeline.CompletedTick >= presentation.RequiredTick)) &&
            VideoOutExports.CanPresentFlip(presentation.FlipRequestId, System.Diagnostics.Stopwatch.GetTimestamp());

        private bool TryTakePresentation(out Presentation presentation)
        {
            lock (_gate)
            {
                // Remove cancelled frames without releasing their in-flight image resources.
                while (_pendingGuestImagePresentations.Count > 0 &&
                       (_pendingGuestImagePresentations.Peek().Sequence <= _presentedSequence ||
                        !VideoOutExports.IsFlipPresentationPending(_pendingGuestImagePresentations.Peek().FlipRequestId)))
                {
                    var retired = _pendingGuestImagePresentations.Dequeue();
                    RetirePresentation(retired);
                    if (_latestPresentation is { } last && last.Sequence == retired.Sequence)
                    {
                        _latestPresentation = null;
                    }
                    _commandStream.RetryBlocked();
                }

                if (_pendingGuestImagePresentations.Count > 0)
                {
                    var pending = _pendingGuestImagePresentations.Peek();
                    if (IsPresentationReadyLocked(in pending))
                    {
                        presentation = _pendingGuestImagePresentations.Dequeue();
                        TryReplaceWithHostMovieFrame(ref presentation);
                        TryReplaceWithHeldSplash(ref presentation);
                        return true;
                    }

                    presentation = default;
                    return false;
                }

                while (_pendingVideoPresentations.Count > 0 &&
                       _pendingVideoPresentations.Peek().Sequence <= _presentedSequence)
                {
                    _pendingVideoPresentations.Dequeue();
                }

                if (_pendingVideoPresentations.Count > 0)
                {
                    presentation = _pendingVideoPresentations.Dequeue();
                    return true;
                }

                if (_latestPresentation is not { } latest ||
                    latest.Sequence == _presentedSequence && !(latest.IsSplash && ShaderCacheSplashNeedsRefresh) ||
                    !IsPresentationReadyLocked(in latest))
                {
                    if (_latestPresentation is { } rejected &&
                        rejected.GuestImageAddress != 0 &&
                        rejected.Sequence != _presentedSequence &&
                        _tracedGuestImagePresentRejections.Add(rejected.Sequence))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.guest_present_rejected addr=0x{rejected.GuestImageAddress:X16} " +
                            $"seq={rejected.Sequence} presentedSeq={_presentedSequence} " +
                            $"required_tick={rejected.RequiredTick} completed_tick={_scheduler.Timeline.CompletedTick}");
                    }

                    presentation = default;
                    return false;
                }

                presentation = latest;
                TryReplaceWithHostMovieFrame(ref presentation);
                TryReplaceWithHeldSplash(ref presentation);
                return true;
            }
        }

        // True when a guest frame can be shown now; the wait loop wakes for it.
        private bool HasReadyPresentationLocked()
        {
            if (_pendingGuestImagePresentations.Count > 0)
            {
                var pending = _pendingGuestImagePresentations.Peek();
                return pending.Sequence <= _presentedSequence ||
                    !VideoOutExports.IsFlipPresentationPending(pending.FlipRequestId) ||
                    IsPresentationReadyLocked(in pending);
            }

            return _pendingVideoPresentations.Count > 0 ||
                (_latestPresentation is { } latest &&
                 (latest.Sequence != _presentedSequence || latest.IsSplash && ShaderCacheSplashNeedsRefresh) &&
                 IsPresentationReadyLocked(in latest));
        }
    }
}
