// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

internal static unsafe partial class VulkanVideoPresenter
{
    // SHARPEMU_RENDER_PIPELINE=1 interprets the command streams on a thread of their own, ahead
    // of the render thread: the interpreter and the shader provider (materialization, programs)
    // run there, and everything that touches the host (images, buffers, descriptors, recording,
    // guest-visible writes) is queued to the render thread in stream order.
    internal static readonly bool RenderPipelineEnabled = Environment.GetEnvironmentVariable("SHARPEMU_RENDER_PIPELINE") == "1";

    // The work the front queues for the render thread, in order. The front blocks when the
    // queue is full; the render thread runs the items between its other duties. An item that
    // fails fails the queue, and the front sees the failure on its next call.
    // [local] How often the front waits on the render thread, by reason.
    internal static class FrontSyncStats
    {
        private static readonly long[] Counts = new long[4];
        private static long _report = System.Diagnostics.Stopwatch.GetTimestamp();

        private static readonly Dictionary<string, int> Sites = new();
        private static int _siteSamples;

        public static void NoteSite(ulong address, int size)
        {
            if ((Interlocked.Increment(ref _siteSamples) & 15) != 0)
                return;
            var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
            var site = string.Join(" < ", frames.Take(4).Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?")) + $" size={size}";
            lock (Sites)
            {
                Sites[site] = Sites.GetValueOrDefault(site) + 16;
                if (_siteSamples % 4096 == 0)
                    foreach (var (key, value) in Sites.OrderByDescending(pair => pair.Value).Take(6))
                        Console.Error.WriteLine($"[FRONT_CMD_READ] count~{value} site={key}");
            }
        }

        public static void Note(int kind)
        {
            Interlocked.Increment(ref Counts[kind]);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _report)).TotalSeconds < 5)
                return;
            Volatile.Write(ref _report, System.Diagnostics.Stopwatch.GetTimestamp());
            Console.Error.WriteLine($"[FRONT_SYNC] word_reads={Interlocked.Exchange(ref Counts[0], 0)} command_reads={Interlocked.Exchange(ref Counts[1], 0)} write_now={Interlocked.Exchange(ref Counts[2], 0)} flush_wait={Interlocked.Exchange(ref Counts[3], 0)}");
        }
    }

    // [local] Bytes the front wrote with WriteGuestNow that the render thread has not applied
    // yet, per dword. The front patches its reads with them; the render thread retires each
    // entry once it applied the write (unless a newer write to the dword is pending).
    internal sealed class PendingWriteOverlay
    {
        private readonly Dictionary<ulong, (byte Value, long Serial)> _bytes = new();
        private long _serial;
        private int _count;

        public bool IsEmpty => Volatile.Read(ref _count) == 0;

        public long Add(ulong address, byte[] bytes)
        {
            lock (_bytes)
            {
                var serial = ++_serial;
                for (var index = 0; index < bytes.Length; index++)
                    _bytes[address + (ulong)index] = (bytes[index], serial);
                Volatile.Write(ref _count, _bytes.Count);
                return serial;
            }
        }

        public void Retire(ulong address, int length, long serial)
        {
            lock (_bytes)
            {
                for (var index = 0; index < length; index++)
                {
                    var key = address + (ulong)index;
                    if (_bytes.TryGetValue(key, out var entry) && entry.Serial == serial)
                        _bytes.Remove(key);
                }

                Volatile.Write(ref _count, _bytes.Count);
            }
        }

        public void Apply(ulong address, Span<byte> destination)
        {
            if (IsEmpty)
                return;
            lock (_bytes)
            {
                for (var index = 0; index < destination.Length; index++)
                    if (_bytes.TryGetValue(address + (ulong)index, out var entry))
                        destination[index] = entry.Value;
            }
        }
    }

    private sealed class BackQueue(Action wake, int capacity)
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _items = new();
        private int _backThreadId = -1;
        private int _count;
        private Exception? _failure;
        // For the profile: the ticks the front spent waiting for room and for a result, the deepest the queue got.
        internal long WaitTicks;
        internal long SyncWaitTicks;
        internal int MaxDepth;

        // Read without the gate: the render thread asks while it holds its own gate, and the
        // front wakes the render thread (taking that gate) only after releasing this one.
        public bool HasQueuedWork => Volatile.Read(ref _count) != 0;

        private bool OnBackThread => _backThreadId == Environment.CurrentManagedThreadId;

        public void BindBackThread() => _backThreadId = Environment.CurrentManagedThreadId;

        public void Enqueue(Action work, Action? beforeWait = null, Action? afterWait = null)
        {
            if (OnBackThread)
            {
                // Called from the render thread itself (shutdown drains): keep the order and run inline.
                RunPending(long.MaxValue);
                work();
                return;
            }

            bool wasEmpty;
            lock (_gate)
            {
                if (_items.Count >= capacity && _failure is null)
                {
                    beforeWait?.Invoke();
                    var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (_items.Count >= capacity && _failure is null)
                    {
                        Monitor.Wait(_gate);
                    }

                    WaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - waitStarted;
                    afterWait?.Invoke();
                }

                if (_failure is { } failure)
                {
                    throw new InvalidOperationException("The render thread failed; the command stream cannot continue.", failure);
                }

                wasEmpty = _items.Count == 0;
                _items.Enqueue(work);
                Volatile.Write(ref _count, _items.Count);
                if (_items.Count > MaxDepth) MaxDepth = _items.Count;
            }

            if (wasEmpty)
            {
                wake();
            }
        }

        private sealed class Completion<T>
        {
            public T? Result;
            public Exception? Error;
            public bool Done;
        }

        // Queues the work and waits for its result.
        public T Run<T>(Func<T> work, Action? beforeWait = null, Action? afterWait = null)
        {
            if (OnBackThread)
            {
                RunPending(long.MaxValue);
                return work();
            }

            var completion = new Completion<T>();
            Enqueue(() =>
            {
                try
                {
                    completion.Result = work();
                }
                catch (Exception exception)
                {
                    completion.Error = exception;
                    throw;
                }
                finally
                {
                    lock (_gate)
                    {
                        completion.Done = true;
                        Monitor.PulseAll(_gate);
                    }
                }
            }, beforeWait, afterWait);
            lock (_gate)
            {
                if (!completion.Done && _failure is null)
                {
                    beforeWait?.Invoke();
                    var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    while (!completion.Done && _failure is null)
                    {
                        Monitor.Wait(_gate);
                    }

                    SyncWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - waitStarted;
                    afterWait?.Invoke();
                }

                if (completion.Error is { } error)
                {
                    throw new InvalidOperationException("Queued render work failed.", error);
                }

                if (!completion.Done && _failure is { } failure)
                {
                    throw new InvalidOperationException("The render thread failed; the command stream cannot continue.", failure);
                }
            }

            return completion.Result!;
        }

        // Runs queued items on the render thread until the queue is empty or the deadline passes.
        public int RunPending(long deadline)
        {
            var ran = 0;
            while (true)
            {
                Action work;
                lock (_gate)
                {
                    if (_items.Count == 0)
                    {
                        return ran;
                    }

                    work = _items.Dequeue();
                    Volatile.Write(ref _count, _items.Count);
                    if (_items.Count == capacity - 1)
                    {
                        Monitor.PulseAll(_gate);
                    }
                }

                try
                {
                    work();
                }
                catch (Exception exception)
                {
                    Fail(exception);
                    throw;
                }

                ran++;
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                {
                    return ran;
                }
            }
        }

        public void Fail(Exception exception)
        {
            lock (_gate)
            {
                _failure ??= exception;
                Monitor.PulseAll(_gate);
            }
        }
    }

    // The interpreter's host on the front thread: reads guest memory directly, prepares the
    // programs of each draw and dispatch from a snapshot of the banks, and queues every
    // host-visible effect to the render thread in stream order.
    private sealed class FrontCommandStreamHost(Presenter presenter, BackQueue queue) : ICommandStreamHost
    {
        private readonly ConcurrentStack<RegisterBanks> _snapshots = new();
        private int _queueId;
        private readonly Action _flush = presenter.Flush;
        private readonly Action _barrier = presenter.EmitGlobalBarrier;
        private readonly Action _collect = presenter.RunGarbageCollector;
        private readonly Action _pauseAlias = presenter.PauseFrontAliasAccess;
        private readonly Action _resumeAlias = presenter.ResumeFrontAliasAccess;

        public bool HasQueuedWork => queue.HasQueuedWork;

        public ICpuMemory Memory => presenter.GuestMemory;

        public bool TryReadGuest(ulong address, Span<byte> destination)
        {
            // [local] Command memory the GPU may have written (indirect arguments, labels) is read
            // on the render thread, after the work queued before it, through the synchronized path.
            if (presenter.NeedsFrontCommandSync(address, (ulong)destination.Length))
            {
                var buffer = new byte[destination.Length];
                FrontSyncStats.Note(1);
                FrontSyncStats.NoteSite(address, destination.Length);
                var ok = queue.Run(() => presenter.TryReadGuest(address, buffer), _pauseAlias, _resumeAlias);
                buffer.CopyTo(destination);
                return ok;
            }

            using var readScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryRead);
            if (!presenter.GuestMemory.TryRead(address, destination))
            {
                return false;
            }

            presenter.FrontOverlay.Apply(address, destination);
            return true;
        }

        // Commands other threads post run on the render thread, between queued items.
        public bool RunPendingCommands() => false;

        public void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
        {
            _queueId = queueId;
            queue.Enqueue(() => presenter.BeginSubmission(queueId, submissionId, geometrySnapshots));
        }

        public void Flush() => queue.Enqueue(_flush, _pauseAlias, _resumeAlias);

        public void FlushAndWait() { FrontSyncStats.Note(3); queue.Run(() => { presenter.FlushAndWait(); return true; }, _pauseAlias, _resumeAlias); }

        public void SynchronizeGpu() => queue.Run(() => { presenter.SynchronizeGpu(); return true; }, _pauseAlias, _resumeAlias);

        public void RunGarbageCollector() => queue.Enqueue(_collect, _pauseAlias, _resumeAlias);

        public void EmitGlobalBarrier() => queue.Enqueue(_barrier, _pauseAlias, _resumeAlias);

        public void FillBuffer(ulong address, ulong size, uint value, bool isGds) =>
            queue.Enqueue(() => presenter.FillBuffer(address, size, value, isGds), _pauseAlias, _resumeAlias);

        public void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds) =>
            queue.Enqueue(() => presenter.CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds), _pauseAlias, _resumeAlias);

        public void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount)
        {
            var words = queue.Run(() =>
            {
                var read = new uint[wordCount];
                presenter.ReadGds(read, wordOffset, wordCount);
                return read;
            }, _pauseAlias, _resumeAlias);
            words.AsSpan(0, Math.Min(words.Length, destination.Length)).CopyTo(destination);
        }

        public void RecordEndOfPipe(in EndOfPipeWrite write)
        {
            var copy = write;
            queue.Enqueue(() => presenter.RecordEndOfPipe(in copy), _pauseAlias, _resumeAlias);
        }

        public void TriggerInterrupt(int eventId, uint contextId) =>
            queue.Enqueue(() => presenter.TriggerInterrupt(eventId, contextId), _pauseAlias, _resumeAlias);

        public bool HasFlipSlot() => presenter.HasFlipSlot();

        // The request is reserved here (the interpreter checked HasFlipSlot first), so the front
        // does not wait for the render thread to reach the flip; the preparation and the capture
        // follow in order.
        public ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
        {
            var result = VideoOutExports.TryReserveFlipRequest(handle, index, flipMode, flipArgument, gpuQueued: true, out var requestId);
            if (result != 0)
            {
                throw SubmissionScheduler.Fatal($"Could not submit the GPU flip: result={result} handle={handle} index={index} mode={flipMode} arg={flipArgument}");
            }

            if (requestId == 0)
            {
                throw SubmissionScheduler.Fatal("The GPU flip submission returned an invalid request ID of zero.");
            }

            queue.Enqueue(() => presenter.CompleteQueuedFlip(handle, index, flipMode, flipArgument, requestId), _pauseAlias, _resumeAlias);
            return requestId;
        }

        public bool IsFlipDone(int handle, int index) => presenter.IsFlipDone(handle, index);

        public void PrepareCpuFlip(int handle, int index, ulong requestId) =>
            queue.Enqueue(() => presenter.PrepareCpuFlip(handle, index, requestId), _pauseAlias, _resumeAlias);

        private RegisterBanks Snapshot()
        {
            var live = presenter.CommandStream.GetInterpreter(_queueId).TypedRegisters;
            if (!_snapshots.TryPop(out var snapshot))
            {
                snapshot = new RegisterBanks(live.Fatal);
            }

            snapshot.CopyFrom(live);
            return snapshot;
        }

        public void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments)
        {
            var snapshot = Snapshot();
            var prepared = presenter.TryPrepareOnFront(() => presenter.Translation.PrepareGraphicsPrograms(snapshot, out var prepareInputs) is { } programs ? (programs, prepareInputs) : default, out var inputs);
            var copy = arguments;
            queue.Enqueue(() =>
            {
                try
                {
                    presenter.ExecuteQueuedDraw(submitId, snapshot, true, in copy, default, prepared, in inputs);
                }
                finally
                {
                    _snapshots.Push(snapshot);
                }
            }, _pauseAlias, _resumeAlias);
        }

        public void DrawAuto(ulong submitId, in DrawAutoArguments arguments)
        {
            var snapshot = Snapshot();
            var prepared = presenter.TryPrepareOnFront(() => presenter.Translation.PrepareGraphicsPrograms(snapshot, out var prepareInputs) is { } programs ? (programs, prepareInputs) : default, out var inputs);
            var copy = arguments;
            queue.Enqueue(() =>
            {
                try
                {
                    presenter.ExecuteQueuedDraw(submitId, snapshot, false, default, in copy, prepared, in inputs);
                }
                finally
                {
                    _snapshots.Push(snapshot);
                }
            }, _pauseAlias, _resumeAlias);
        }

        public void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator, ulong indirectArgumentsAddress = 0)
        {
            var snapshot = Snapshot();
            var prepared = indirectArgumentsAddress == 0
                ? presenter.TryPrepareComputeOnFront(() => presenter.Translation.PrepareComputeProgram(snapshot, groupsX, groupsY, groupsZ, dispatchInitiator))
                : null;
            queue.Enqueue(() =>
            {
                try
                {
                    presenter.ExecuteQueuedDispatch(submitId, snapshot, groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress, prepared);
                }
                finally
                {
                    _snapshots.Push(snapshot);
                }
            }, _pauseAlias, _resumeAlias);
        }

        public bool ResolvesIndirectDispatchOnGpu => presenter.ResolvesIndirectDispatchOnGpu;

        public bool ResolvesIndirectThreadDispatchOnGpu => presenter.ResolvesIndirectThreadDispatchOnGpu;

        // [local] The counts are read on the render thread when the dispatch runs, so the front
        // does not wait for the queue to drain to read memory an earlier dispatch writes.
        private static readonly bool DeferIndirect = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_DEFER_INDIRECT") != "0";

        public bool DefersIndirectThreadDispatch => DeferIndirect;

        public void DispatchIndirectDeferred(ulong submitId, ulong argumentsAddress, uint dispatchInitiator)
        {
            var snapshot = Snapshot();
            queue.Enqueue(() =>
            {
                try
                {
                    Span<byte> counts = stackalloc byte[12];
                    if (!presenter.TryReadGuest(argumentsAddress, counts))
                    {
                        throw SubmissionScheduler.Fatal($"The indirect dispatch arguments cannot be read: address=0x{argumentsAddress:X16}.");
                    }

                    var groupsX = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(counts);
                    var groupsY = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(counts[4..]);
                    var groupsZ = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(counts[8..]);
                    presenter.ExecuteQueuedDispatch(submitId, snapshot, groupsX, groupsY, groupsZ, dispatchInitiator, argumentsAddress, null);
                }
                finally
                {
                    _snapshots.Push(snapshot);
                }
            }, _pauseAlias, _resumeAlias);
        }

        public bool ResolvesIndirectDrawOnGpu => presenter.ResolvesIndirectDrawOnGpu;

        public void OnQueueReset(int queueId) => queue.Enqueue(() => presenter.OnQueueReset(queueId), _pauseAlias, _resumeAlias);

        // A label or event store lands after the work queued before it, so the guest cannot
        // reuse memory a queued draw still has to capture.
        public void WriteGuest(ulong address, ReadOnlySpan<byte> source)
        {
            var bytes = source.ToArray();
            queue.Enqueue(() => presenter.WriteGuest(address, bytes), _pauseAlias, _resumeAlias);
        }

        // Data the stream reads back at once: the queued work captures what it replaces first.
        public void WriteGuestNow(ulong address, ReadOnlySpan<byte> source)
        {
            // [local] The write records device copies, so it runs on the render thread, in order.
            // The front does not wait for it: until the render thread applies it, the front's own
            // reads see the new bytes through the pending-write overlay.
            var bytes = source.ToArray();
            if (!WriteOverlay)
            {
                FrontSyncStats.Note(2);
                queue.Run(() => { presenter.WriteGuest(address, bytes); return true; }, _pauseAlias, _resumeAlias);
                return;
            }

            var serial = presenter.FrontOverlay.Add(address, bytes);
            queue.Enqueue(() =>
            {
                presenter.WriteGuest(address, bytes);
                presenter.FrontOverlay.Retire(address, bytes.Length, serial);
            }, _pauseAlias, _resumeAlias);
        }

        private static readonly bool WriteOverlay = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_WRITE_OVERLAY") != "0";

        public void RunAfterFlush(Action work) => queue.Enqueue(work, _pauseAlias, _resumeAlias);

        public void RunControlBarrier(Action work) => queue.Enqueue(work, _pauseAlias, _resumeAlias);

        // Waits until the render thread ran everything queued so far.
        public void Drain() => queue.Run(static () => true, _pauseAlias, _resumeAlias);

        public Exception Fatal(string message) => SubmissionScheduler.Fatal(message);
    }

    private sealed partial class Presenter
    {
        private FrontCommandStreamHost? _front;
        internal readonly PendingWriteOverlay FrontOverlay = new(); // [local]
        private BackQueue? _back;
        private long _frontBusyTicks;
        private long _frontSlices;

        // [PERF][FRONT]: what the front thread spent interpreting and preparing, what it spent
        // waiting on the render thread, and how deep the queue got in the window.
        internal string TakeFrontReport()
        {
            if (_back is null) return string.Empty;
            var busy = Interlocked.Exchange(ref _frontBusyTicks, 0);
            var slices = Interlocked.Exchange(ref _frontSlices, 0);
            var wait = Interlocked.Exchange(ref _back.WaitTicks, 0);
            var syncWait = Interlocked.Exchange(ref _back.SyncWaitTicks, 0);
            var depth = Interlocked.Exchange(ref _back.MaxDepth, 0);
            return FormattableString.Invariant(
                $"[PERF][FRONT] busy_ms={busy * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} wait_full_ms={wait * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} wait_sync_ms={syncWait * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} slices={slices} max_queue_depth={depth}");
        }

        internal ICpuMemory GuestMemory => _guestMemory;

        internal AgcExports.CommandStreamTranslation Translation => _translation;
        private Thread? _frontThread;
        private volatile bool _frontStop;
        private bool _frontAliasAccess;

        private const int BackQueueCapacity = 16384;

        private void CreateFrontPipeline()
        {
            if (!RenderPipelineEnabled)
            {
                return;
            }

            _back = new BackQueue(WakeRenderThread, BackQueueCapacity);
            _front = new FrontCommandStreamHost(this, _back);
            RenderPhaseProfile.FrontReport = TakeFrontReport;
            ShaderPipelineCache.BeforeGuestWrite = _front.Drain;
        }

        private void StartFrontThread()
        {
            if (_front is null || _frontThread is not null)
            {
                return;
            }

            _back!.BindBackThread();
            _frontThread = new Thread(FrontLoop) { IsBackground = true, Name = "GPU front" };
            _frontThread.Start();
            Console.Error.WriteLine("[LOADER][INFO] Vulkan VideoOut interprets command streams on a front thread (SHARPEMU_RENDER_PIPELINE=1).");
        }

        private void StopFrontThread()
        {
            if (_frontThread is null)
            {
                return;
            }

            _frontStop = true;
            _commandStream.Wake();
            _frontThread.Join();
            _frontThread = null;
        }

        // The render thread runs what the front queued, until the queue is empty or the deadline passes.
        private void RunBackQueue(long renderWorkDeadline)
        {
            using var profileScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CommandStream);
            _back!.RunPending(renderWorkDeadline);
        }

        // The front: the slices the render thread ran before, on their own thread.
        private void FrontLoop()
        {
            try
            {
                while (!_frontStop)
                {
                    if (!_vulkanReady || Volatile.Read(ref _presenterCloseRequested))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    SliceResult result;
                    ResumeFrontAliasAccess();
                    var sliceStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        result = _commandStream.ProcessOne();
                    }
                    finally
                    {
                        PauseFrontAliasAccess();
                        Interlocked.Add(ref _frontBusyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - sliceStarted);
                        Interlocked.Increment(ref _frontSlices);
                    }

                    switch (result)
                    {
                        case SliceResult.NoWork:
                            _commandStream.WaitForWork(8);
                            break;
                        case SliceResult.AllBlocked:
                            RetryBlockedCommandStreamIfDue();
                            _commandStream.WaitForRetryInterval(1);
                            break;
                        case SliceResult.BlockedWithoutProgress:
                            if (!_commandStream.HasUnblockedPending)
                            {
                                RetryBlockedCommandStreamIfDue();
                                _commandStream.WaitForRetryInterval(1);
                            }

                            break;
                        default:
                            _lastCommandStreamProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[GPU][ERROR] The front thread failed. {exception}");
                _back!.Fail(exception);
                WakeRenderThread();
            }
        }

        // The front reads guest memory through its backing aliases while it interprets; the
        // scope is released around every wait on the render thread, which may remap memory.
        internal void ResumeFrontAliasAccess()
        {
            if (_frontAliasAccess)
            {
                return;
            }

            _frontAliasAccess = _guestBacking?.TryEnterBackingAliasAccess() == true;
            _backingAliasAccess = _frontAliasAccess;
        }

        internal void PauseFrontAliasAccess()
        {
            if (!_frontAliasAccess)
            {
                return;
            }

            _frontAliasAccess = false;
            _backingAliasAccess = false;
            _guestBacking!.ExitBackingAliasAccess();
        }

        internal void ExecuteQueuedDraw(ulong submitId, RegisterBanks banks, bool indexed, in DrawIndexedArguments indexedArguments, in DrawAutoArguments autoArguments,
            GraphicsPrograms? prepared, in RenderExecutor.DrawProgramInputs preparedInputs)
        {
            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDrawTranslation);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _translation.ExecuteDraw(submitId, banks, indexed, in indexedArguments, in autoArguments, prepared, in preparedInputs);
            }
            finally
            {
                Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        internal void ExecuteQueuedDispatch(ulong submitId, RegisterBanks banks, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator,
            ulong indirectArgumentsAddress, ComputeProgram? prepared)
        {
            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDispatchTranslation);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _translation.ExecuteDispatch(submitId, banks, groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress, prepared);
            }
            finally
            {
                Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        // Applied on the render thread in stream order, through the device-ordered command write.
        internal void WriteGuest(ulong address, ReadOnlySpan<byte> source) => WriteCommandData(address, source);
    }
}
