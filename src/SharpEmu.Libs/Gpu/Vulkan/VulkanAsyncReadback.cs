// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Gpu.Vulkan;

public readonly record struct ReadbackPiece(GpuBuffer Source, ulong SourceOffset, ulong Size);

// Copies GPU-written buffer ranges back to the host on a second queue. The copy waits
// on the main queue's timeline for the tick that last wrote the sources only, so a
// guest read of one GPU-produced value no longer waits behind every later draw that
// is still queued on the main queue (a readback there has to go to the queue's tail).
internal sealed unsafe class VulkanAsyncReadback : IDisposable
{
    private const ulong Alignment = 16;
    // [local] SHARPEMU_READBACK_SLOTS (2..64, default 8): slot 0 is the synchronous one, the rest carry asynchronous
    // copies. 32 is sharpemu/sharpemu PR #1128's count (TarkusR, 6d963114). Kept at 8: in GTA V (2026-10-29, static and
    // panning, SHARPEMU_CHEAP_PROBE) no guest read ever found the 7 asynchronous slots busy.
    internal static readonly int SlotCount =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_READBACK_SLOTS"), out var slots) && slots is >= 2 and <= 64 ? slots : 8;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly Slot[] _slots;
    private readonly VkSemaphore _timeline;
    private readonly object _gate = new();

    // [local] SHARPEMU_READBACK_SUBMIT_GUARD=1 (off by default): before a copy that waits on the main timeline for
    // waitTick is submitted, wait until the submit thread has handed waitTick to the main queue, so the readback queue
    // never holds a wait-before-signal (legal for timeline semaphores). Tried for the GTA V intro freeze of 2026-10-12
    // and refuted: the freeze reproduced with it on; FPS neutral.
    internal static readonly bool SubmitGuard = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_SUBMIT_GUARD") == "1";
    private ulong _signaled;
    private int _waiters;
    private bool _disposed;

    internal sealed class Slot(CommandBuffer command)
    {
        public readonly CommandBuffer Command = command;
        public GpuBuffer? Staging;
        public bool Busy;
        public ulong WaitTick, Value; // [local] SHARPEMU_LABEL_POLL_SPLIT
    }

    internal sealed class Ticket(Slot slot, ulong value, ulong waitTick, ulong[] offsets, ulong[] sizes)
    {
        public Slot Slot { get; } = slot;
        public ulong Value { get; } = value;
        public ulong WaitTick { get; } = waitTick; // [local]
        public ulong[] Offsets { get; } = offsets;
        public ulong[] Sizes { get; } = sizes;
        // [local] SHARPEMU_LABEL_POLL_SPLIT: copies submitted before this one and not signaled yet, and the latest
        // main-queue tick they wait for; Observe makes Wait poll both timelines and stamp the sample.
        public ulong AheadCount { get; init; }
        public ulong AheadTick { get; init; }
        public Diagnostics.LabelPollSplit.Sample? Observe { get; set; }
    }

    public VulkanAsyncReadback(GpuDeviceInfo device, SubmissionScheduler scheduler, Queue queue, uint queueFamilyIndex)
    {
        _device = device;
        _scheduler = scheduler;
        _queue = queue;
        var vk = device.Vk;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Require(vk.CreateCommandPool(device.Device, &poolInfo, null, out _pool), "vkCreateCommandPool(readback)");
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)SlotCount,
        };
        var commands = stackalloc CommandBuffer[SlotCount];
        Require(vk.AllocateCommandBuffers(device.Device, &allocateInfo, commands), "vkAllocateCommandBuffers(readback)");
        _slots = new Slot[SlotCount];
        for (var index = 0; index < SlotCount; index++)
        {
            _slots[index] = new Slot(commands[index]);
        }

        var typeInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
        };
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &typeInfo };
        Require(vk.CreateSemaphore(device.Device, &semaphoreInfo, null, out _timeline), "vkCreateSemaphore(readback)");
    }

    // Copies the pieces once the main-queue tick has completed and hands each one's bytes
    // to consume, in order. The caller must have sealed waitTick (flushed it); the guard waits for its submit.
    public void Read(ReadOnlySpan<ReadbackPiece> pieces, ulong waitTick, ReadbackConsumer consume)
    {
        if (!PrepareMainQueueWait(_scheduler, waitTick, SubmitGuard))
        {
            // The caller is the recording thread: nothing else would ever seal waitTick.
            throw SubmissionScheduler.Fatal($"A synchronous readback waits for the unsealed tick {waitTick} (current {_scheduler.CurrentTick}).");
        }

        Ticket ticket;
        lock (_gate)
        {
            var slot = _slots[0];
            if (slot.Busy)
            {
                throw SubmissionScheduler.Fatal("The synchronous readback slot is already in use.");
            }

            ticket = BeginLocked(slot, pieces, waitTick);
        }

        Wait(ticket);
        Complete(ticket, consume);
    }

    public bool TryBegin(ReadOnlySpan<ReadbackPiece> pieces, ulong waitTick, out Ticket? ticket) =>
        TryBegin(pieces, waitTick, out ticket, out _);

    // [local] slotsBusy: false only because every asynchronous slot was in use.
    public bool TryBegin(ReadOnlySpan<ReadbackPiece> pieces, ulong waitTick, out Ticket? ticket, out bool slotsBusy)
    {
        ticket = null;
        slotsBusy = false;
        if (!PrepareMainQueueWait(_scheduler, waitTick, SubmitGuard))
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            for (var index = 1; index < _slots.Length; index++)
            {
                if (!_slots[index].Busy)
                {
                    ticket = BeginLocked(_slots[index], pieces, waitTick);
                    return true;
                }
            }

            slotsBusy = true;
        }

        return false;
    }

    // [local] Adapted from 6d963114 by TarkusR: blocks the caller (not the GPU queue thread) until an asynchronous slot
    // is free or the timeout passes; Complete pulses the gate when it frees a slot.
    public void WaitForFreeSlot(int timeoutMilliseconds)
    {
        lock (_gate)
        {
            for (var index = 1; index < _slots.Length; index++)
            {
                if (_disposed || !_slots[index].Busy)
                {
                    return;
                }
            }

            Monitor.Wait(_gate, timeoutMilliseconds);
        }
    }

    private static int _unsealedReports; // [local]

    // [local] Before a copy that waits on the main timeline for waitTick is recorded and submitted. False when waitTick
    // is still the tick being recorded: its signal is not sealed yet, and only the recording thread can seal it. With
    // the guard, returns once the submit thread has handed waitTick to the main queue (at once without a submit thread).
    internal static bool PrepareMainQueueWait(SubmissionScheduler scheduler, ulong waitTick, bool guard)
    {
        if (waitTick == 0)
        {
            return true;
        }

        if (waitTick >= scheduler.CurrentTick)
        {
            if (Interlocked.Increment(ref _unsealedReports) <= 4)
            {
                Console.Error.WriteLine($"[GPU][ERROR] readback wait for unsealed tick={waitTick} current={scheduler.CurrentTick} " +
                    $"submitted={scheduler.SubmittedTick}");
            }

            return false;
        }

        if (guard)
        {
            scheduler.WaitSubmitted(waitTick);
        }

        return true;
    }

    public bool IsComplete(Ticket ticket)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return true;
            }

            ulong value;
            return _device.Vk.GetSemaphoreCounterValue(_device.Device, _timeline, &value) == Result.Success && value >= ticket.Value;
        }
    }

    public void Wait(Ticket ticket)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _waiters++;
        }

        if (VideoOut.LabelTrace.Enabled) VideoOut.LabelTrace.Note(VideoOut.LabelTrace.Kind.GpuWait, Environment.CurrentManagedThreadId, 1, ticket.WaitTick, ticket.Value); // [local]
        var starveEntered = Scheduling.GpuStarveProbe.EnterGpuWait(ticket.WaitTick); // [local] SHARPEMU_GPU_STARVE_PROBE
        try
        {
            var vk = _device.Vk;
            if (ticket.Observe is { } sample) // [local] SHARPEMU_LABEL_POLL_SPLIT
            {
                Observe(ticket, sample);
                return;
            }

            var spinStart = VideoOut.Render2Probe.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
            var spun = GpuWaitSpin.TrySpin(() =>
                {
                    ulong value;
                    return vk.GetSemaphoreCounterValue(_device.Device, _timeline, &value) == Result.Success && value >= ticket.Value;
                });
            if (spinStart != 0) VideoOut.Render2Probe.NoteSpin(VideoOut.Render2Probe.SpinSite.Readback, spun, System.Diagnostics.Stopwatch.GetTimestamp() - spinStart); // [local]
            if (spun)
            {
                return;
            }

            var semaphore = _timeline;
            var signalValue = ticket.Value;
            var waitInfo = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &semaphore,
                PValues = &signalValue,
            };
            var probeStart = System.Diagnostics.Stopwatch.GetTimestamp(); // [local]
            var probeCompleted = _scheduler.Timeline.CompletedTick; // [local] SHARPEMU_RENDER_WAIT_PROBE
            using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.GpuCompletionWait))
            {
                var reports = 0;
                Result result;
                if (GpuWorkerRelay.CanPumpInWait) // [local] SHARPEMU_RELAY_PUMP_IN_WAITS: wait in slices, running nestable relay work
                {
                    GpuWorkerRelay.PumpNestableInWait();
                    var sliced = System.Diagnostics.Stopwatch.GetTimestamp();
                    while ((result = vk.WaitSemaphores(_device.Device, &waitInfo, GpuWorkerRelay.PumpSliceNs)) == Result.Timeout)
                    {
                        GpuWorkerRelay.PumpNestableInWait();
                        if (System.Diagnostics.Stopwatch.GetElapsedTime(sliced).TotalMilliseconds >= GpuStallReport.TimeoutMs * (reports + 1.0) &&
                            reports++ < GpuStallReport.MaxReportsPerWait)
                        {
                            ReportStall(ticket, reports);
                        }
                    }
                }
                else
                {
                    while ((result = vk.WaitSemaphores(_device.Device, &waitInfo, GpuStallReport.TimeoutNs)) == Result.Timeout)
                    {
                        if (reports++ < GpuStallReport.MaxReportsPerWait)
                        {
                            ReportStall(ticket, reports);
                        }
                    }
                }

                Require(result, "vkWaitSemaphores(readback)");
            }
            if (VideoOut.Render2Probe.Enabled) VideoOut.Render2Probe.NoteBlocked(VideoOut.Render2Probe.SpinSite.Readback, System.Diagnostics.Stopwatch.GetTimestamp() - probeStart); // [local]
            VideoOut.RenderWaitProbe.Record("readback", probeStart, (long)ticket.WaitTick - (long)probeCompleted);
            if (Diagnostics.LabelTimeline.Enabled) Diagnostics.LabelTimeline.NoteWait(probeStart, ticket.WaitTick, 1, Scheduling.GpuWorkerRelay.OnAnyGpuQueueThread); // [local]
        }
        finally
        {
            if (VideoOut.LabelTrace.Enabled) VideoOut.LabelTrace.Note(VideoOut.LabelTrace.Kind.GpuWaitEnd, Environment.CurrentManagedThreadId, 1, ticket.WaitTick, ticket.Value); // [local]
            Scheduling.GpuStarveProbe.ExitGpuWait(starveEntered);
            lock (_gate)
            {
                _waiters--;
            }
        }
    }

    // [local] SHARPEMU_LABEL_POLL_SPLIT: waits by polling the main timeline (writer done), this queue's timeline at the
    // value before the ticket's (the copies ahead done) and at the ticket's (done).
    private void Observe(Ticket ticket, Diagnostics.LabelPollSplit.Sample sample)
    {
        var vk = _device.Vk;
        var main = new VkSemaphore(_scheduler.Timeline.Handle);
        sample.WaitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            ulong mainValue = 0, ownValue = 0;
            _ = vk.GetSemaphoreCounterValue(_device.Device, main, &mainValue);
            _ = vk.GetSemaphoreCounterValue(_device.Device, _timeline, &ownValue);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (sample.WriterAt == 0 && mainValue >= ticket.WaitTick) sample.WriterAt = now;
            if (sample.AheadAt == 0 && ownValue + 1 >= ticket.Value) sample.AheadAt = now;
            if (ownValue >= ticket.Value)
            {
                if (sample.WriterAt == 0) sample.WriterAt = now;
                if (sample.AheadAt == 0) sample.AheadAt = now;
                sample.DoneAt = now;
                return;
            }

            Thread.SpinWait(20);
        }
    }

    // [local] Tells which side a stalled readback waits on: the readback queue (main_completed >= wait_tick), the main
    // queue (submitted >= wait_tick > main_completed) or the submit thread (submitted < wait_tick); the rest of the line
    // is GpuStallReport's description of every queue.
    private void ReportStall(Ticket ticket, int reports)
    {
        GpuStallReport.Report($"readback wait over {GpuStallReport.TimeoutNs / 1_000_000_000 * (ulong)reports} s: ticket={ticket.Value} " +
            $"wait_tick={ticket.WaitTick} readback=[{DescribeStall()}] guard={SubmitGuard}");
    }

    // [local] GpuStallReport: this readback timeline's value, the last value submitted and the busy slots.
    internal string DescribeStall()
    {
        ulong value = 0;
        var result = _device.Vk.GetSemaphoreCounterValue(_device.Device, _timeline, &value);
        var busy = 0;
        foreach (var slot in _slots)
        {
            busy += slot.Busy ? 1 : 0;
        }

        return $"value={value} result={result} signaled={Volatile.Read(ref _signaled)} busy_slots={busy} waiters={Volatile.Read(ref _waiters)}";
    }

    public void Complete(Ticket ticket, ReadbackConsumer? consume)
    {
        lock (_gate)
        {
            try
            {
                if (!_disposed && consume is not null && ticket.Slot.Staging is { } staging)
                {
                    for (var index = 0; index < ticket.Offsets.Length; index++)
                    {
                        var offset = ticket.Offsets[index];
                        var size = ticket.Sizes[index];
                        staging.Invalidate(offset, size);
                        consume(index, staging.Mapped.Slice((int)offset, (int)size));
                    }
                }
            }
            finally
            {
                ticket.Slot.Busy = false;
                Monitor.PulseAll(_gate); // [local] WaitForFreeSlot
            }
        }
    }

    private Ticket BeginLocked(Slot slot, ReadOnlySpan<ReadbackPiece> pieces, ulong waitTick)
    {
        var total = 0UL;
        foreach (var piece in pieces)
        {
            total = AlignUp(total, Alignment) + piece.Size;
        }

        var staging = EnsureStaging(slot, total);
        var vk = _device.Vk;
        var command = slot.Command;
        Require(vk.ResetCommandBuffer(command, 0), "vkResetCommandBuffer(readback)");
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Require(vk.BeginCommandBuffer(command, &beginInfo), "vkBeginCommandBuffer(readback)");
        var offsets = new ulong[pieces.Length];
        var sizes = new ulong[pieces.Length];
        var offset = 0UL;
        for (var index = 0; index < pieces.Length; index++)
        {
            var piece = pieces[index];
            offset = AlignUp(offset, Alignment);
            var region = new BufferCopy(piece.SourceOffset, offset, piece.Size);
            vk.CmdCopyBuffer(command, piece.Source.Handle, staging.Handle, 1, &region);
            offsets[index] = offset;
            sizes[index] = piece.Size;
            offset += piece.Size;
        }

        // Make the copied bytes available to the host once the queue signals.
        var toHost = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &toHost,
        };
        vk.CmdPipelineBarrier2(command, &dependency);
        Require(vk.EndCommandBuffer(command), "vkEndCommandBuffer(readback)");

        ulong aheadCount = 0, aheadTick = 0;
        if (Diagnostics.LabelPollSplit.Enabled) // [local]
        {
            ulong signaledNow = 0;
            _ = vk.GetSemaphoreCounterValue(_device.Device, _timeline, &signaledNow);
            foreach (var other in _slots)
            {
                if (other.Busy && other.Value > signaledNow)
                {
                    aheadCount++;
                    aheadTick = Math.Max(aheadTick, other.WaitTick);
                }
            }
        }

        var signalValue = ++_signaled;
        // The semaphore wait orders the copies after every write the main queue made
        // up to waitTick and makes those writes visible to them.
        var wait = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = new VkSemaphore(_scheduler.Timeline.Handle),
            Value = waitTick,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        var signal = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = _timeline,
            Value = signalValue,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        var commandInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = command,
            DeviceMask = 1,
        };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = waitTick == 0 ? 0u : 1u,
            PWaitSemaphoreInfos = &wait,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandInfo,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signal,
        };
        // [local] PrepareMainQueueWait ran before the gate: with the guard, waitTick is already on the main queue.
        Require(vk.QueueSubmit2(_queue, 1, &submit, default), "vkQueueSubmit2(readback)");
        slot.Busy = true;
        slot.WaitTick = waitTick;
        slot.Value = signalValue;
        return new Ticket(slot, signalValue, waitTick, offsets, sizes) { AheadCount = aheadCount, AheadTick = aheadTick };
    }

    public delegate void ReadbackConsumer(int index, ReadOnlySpan<byte> bytes);

    private GpuBuffer EnsureStaging(Slot slot, ulong size)
    {
        if (slot.Staging is { } existing && existing.Size >= size)
        {
            return existing;
        }

        slot.Staging?.Dispose();
        var capacity = Math.Max(size, 1UL << 20);
        slot.Staging = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.TransferDstBit, capacity);
        return slot.Staging;
    }

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    private static void Require(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed: {result}.");
        }
    }

    public void Dispose()
    {
        var vk = _device.Vk;
        ulong signaled;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            signaled = _signaled;
        }

        if (signaled != 0)
        {
            var semaphore = _timeline;
            var waitInfo = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &semaphore,
                PValues = &signaled,
            };
            _ = vk.WaitSemaphores(_device.Device, &waitInfo, ulong.MaxValue);
        }

        var spin = new SpinWait();
        while (Volatile.Read(ref _waiters) != 0)
        {
            spin.SpinOnce();
        }

        lock (_gate)
        {
            foreach (var slot in _slots)
            {
                slot.Staging?.Dispose();
                slot.Staging = null;
            }

            vk.DestroySemaphore(_device.Device, _timeline, null);
            vk.DestroyCommandPool(_device.Device, _pool, null);
        }
    }
}
