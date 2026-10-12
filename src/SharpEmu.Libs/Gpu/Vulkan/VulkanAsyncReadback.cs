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
    private const int SlotCount = 8;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly Slot[] _slots;
    private readonly VkSemaphore _timeline;
    private readonly object _gate = new();

    // [local] SHARPEMU_READBACK_SUBMIT_GUARD (on by default; =0 restores 2484f3fa): before a copy that waits on the main
    // timeline for waitTick is submitted, wait until the submit thread has handed waitTick to the main queue, so the
    // readback queue never holds a wait-before-signal. The GTA V intro hang (2026-10-12, ~1 in 50-140 sessions) left
    // the submit thread, the record ring and the deferred uploads idle with empty queues and the render thread in the
    // readback wait: every sealed tick was submitted, so the only GPU dependency left outstanding was such a cross-queue
    // wait. It costs the render thread's own reads little: their copy cannot run before waitTick completes anyway.
    internal static readonly bool SubmitGuard = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_SUBMIT_GUARD") != "0";

    // [local] A readback wait longer than this reports where the ticks stand (timelines, sealed and submitted ticks).
    private const ulong StallReportNs = 5_000_000_000;
    private ulong _signaled;
    private int _waiters;
    private bool _disposed;

    internal sealed class Slot(CommandBuffer command)
    {
        public readonly CommandBuffer Command = command;
        public GpuBuffer? Staging;
        public bool Busy;
    }

    internal sealed class Ticket(Slot slot, ulong value, ulong waitTick, ulong[] offsets, ulong[] sizes)
    {
        public Slot Slot { get; } = slot;
        public ulong Value { get; } = value;
        public ulong WaitTick { get; } = waitTick; // [local]
        public ulong[] Offsets { get; } = offsets;
        public ulong[] Sizes { get; } = sizes;
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
            CommandBufferCount = SlotCount,
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
    // to consume, in order. The caller must have submitted waitTick already.
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

    public bool TryBegin(ReadOnlySpan<ReadbackPiece> pieces, ulong waitTick, out Ticket? ticket)
    {
        ticket = null;
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
        }

        return false;
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

        try
        {
            var vk = _device.Vk;
            if (GpuWaitSpin.TrySpin(() =>
                {
                    ulong value;
                    return vk.GetSemaphoreCounterValue(_device.Device, _timeline, &value) == Result.Success && value >= ticket.Value;
                }))
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
            using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.GpuCompletionWait))
            {
                var result = vk.WaitSemaphores(_device.Device, &waitInfo, StallReportNs);
                if (result == Result.Timeout)
                {
                    ReportStall(ticket);
                    result = vk.WaitSemaphores(_device.Device, &waitInfo, ulong.MaxValue);
                }

                Require(result, "vkWaitSemaphores(readback)");
            }
            VideoOut.RenderWaitProbe.Record("readback", probeStart);
        }
        finally
        {
            lock (_gate)
            {
                _waiters--;
            }
        }
    }

    // [local] Tells which side a stalled readback waits on: the readback queue (main_completed >= wait_tick), the main
    // queue (submitted >= wait_tick > main_completed) or the submit thread (submitted < wait_tick).
    private void ReportStall(Ticket ticket)
    {
        ulong value = 0;
        _ = _device.Vk.GetSemaphoreCounterValue(_device.Device, _timeline, &value);
        var timeline = _scheduler.Timeline;
        timeline.RefreshCompletedTick();
        Console.Error.WriteLine($"[GPU][STALL] readback wait over {StallReportNs / 1_000_000_000} s: ticket={ticket.Value} " +
            $"readback_value={value} signaled={Volatile.Read(ref _signaled)} wait_tick={ticket.WaitTick} " +
            $"main_completed={timeline.CompletedTick} submitted={_scheduler.SubmittedTick} current={timeline.CurrentTick} " +
            $"guard={SubmitGuard} thread={Environment.CurrentManagedThreadId}");
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
        return new Ticket(slot, signalValue, waitTick, offsets, sizes);
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
