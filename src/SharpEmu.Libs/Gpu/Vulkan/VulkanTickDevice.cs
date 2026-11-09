// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal sealed unsafe class VulkanCommandPool : IGpuCommandBufferSource, IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly CommandPool _pool;
    private readonly VulkanCommandProfile? _profile;

    public VulkanCommandPool(Vk vk, Device device, uint queueFamilyIndex, VulkanCommandProfile? profile)
    {
        _vk = vk;
        _device = device;
        _profile = profile;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        VulkanTickDevice.RequireSuccess(_vk.CreateCommandPool(_device, &poolInfo, null, out _pool), "vkCreateCommandPool(scheduler)");
    }

    public nint[] AllocateBuffers(int count)
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)count,
        };
        var buffers = new CommandBuffer[count];
        fixed (CommandBuffer* pointer = buffers)
        {
            VulkanTickDevice.RequireSuccess(_vk.AllocateCommandBuffers(_device, &allocateInfo, pointer), "vkAllocateCommandBuffers(scheduler)");
        }

        var handles = new nint[count];
        for (var i = 0; i < count; i++)
        {
            handles[i] = buffers[i].Handle;
        }

        return handles;
    }

    public void BeginBuffer(nint buffer)
    {
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanTickDevice.RequireSuccess(_vk.BeginCommandBuffer(new CommandBuffer(buffer), &beginInfo), "vkBeginCommandBuffer(scheduler)");
        _profile?.BeginBuffer(new CommandBuffer(buffer));
    }

    public void EndBuffer(nint buffer, bool hostRead)
    {
        var command = new CommandBuffer(buffer);
        RecordTail(_vk, _profile, command, hostRead);
        VulkanTickDevice.RequireSuccess(_vk.EndCommandBuffer(command), "vkEndCommandBuffer(scheduler)");
    }

    // The profile's tail marker and the host-read barrier before a buffer ends. [local] Both go through VkRecord, so
    // for the device's own buffer with the record ring they are appended to the ring ahead of its end.
    internal static void RecordTail(Vk vk, VulkanCommandProfile? profile, CommandBuffer command, bool hostRead)
    {
        profile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Tail);
        if (hostRead)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.HostReadBit,
            };
            VulkanSynchronization.PipelineBarrier(vk, command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.HostBit, 0,
                1, &barrier, 0, null, 0, null, BarrierSource.HostRead);
        }
    }

    public void Dispose() => _vk.DestroyCommandPool(_device, _pool, null);
}

// One transient command pool and one timeline semaphore on the presenter's queue.
internal sealed unsafe class VulkanTickDevice : IGpuTickDevice
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly VulkanCommandPool _pool;
    private readonly List<VulkanCommandPool> _pools = new();
    private readonly VkSemaphore _timeline;

    // [local] SHARPEMU_RECORD_RING: only the device's own pool (the scheduler's main recording buffer) records through
    // the ring. Pools from CreateBufferSource (render workers) record directly on their own threads: VkRecord appends
    // only for the buffer the ring is recording, so their commands go straight to the driver.
    private readonly CommandRing? _ring; // [local]
    private long _recorded; // [local]

    public VulkanTickDevice(Vk vk, Device device, Queue queue, uint queueFamilyIndex, object queueGate, PhysicalDevice profilePhysicalDevice = default,
        bool recordRing = false, bool dormantProfile = false) // [local] dormantProfile: SlowModeProbe
    {
        _vk = vk;
        _device = device;
        _queue = queue;
        _queueFamilyIndex = queueFamilyIndex;
        QueueGate = queueGate;
        var typeInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var createInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &typeInfo,
        };
        RequireSuccess(_vk.CreateSemaphore(_device, &createInfo, null, out _timeline), "vkCreateSemaphore(scheduler timeline)");
        if (profilePhysicalDevice.Handle != 0)
        {
            CommandProfile = new VulkanCommandProfile(vk, profilePhysicalDevice, device, queueFamilyIndex)
            {
                Dormant = dormantProfile, // [local] SlowModeProbe
            };
            if (CommandProfile.Dormant)
                CommandProfile.PerCommandMarkers = true;
        }

        _pool = new VulkanCommandPool(vk, device, queueFamilyIndex, CommandProfile);
        if (recordRing) // [local]
        {
            _ring = new CommandRing(vk);
            CommandRing.Active = _ring;
        }
    }

    public long RecordedPosition => _recorded; // [local]

    public void WaitRecorded(long position) => _ring?.WaitReplayed(position); // [local]

    public bool IsRecorded(long position) => _ring == null || _ring.IsReplayed(position); // [local] SHARPEMU_SUBMIT_BATCH

    // [local] GpuStallReport: the raw main timeline (its result too, a lost device shows here) and the ring.
    public string DescribeStall()
    {
        ulong value = 0;
        var result = _vk.GetSemaphoreCounterValue(_device, _timeline, &value);
        return $"timeline_raw={value} timeline_result={result} ring_recorded={_recorded} {_ring?.DescribeStall() ?? "ring=off"}";
    }

    public VulkanCommandProfile? CommandProfile { get; }

    public object QueueGate { get; }

    public ulong TimelineHandle => _timeline.Handle;

    public ulong ReadTimeline()
    {
        ulong value;
        RequireSuccess(_vk.GetSemaphoreCounterValue(_device, _timeline, &value), "vkGetSemaphoreCounterValue");
        return value;
    }

    public bool TryWaitTimeline(ulong tick, out string failure)
    {
        if (GpuStarveProbe.Enabled) // [local] SHARPEMU_GPU_STARVE_PROBE
        {
            var entered = GpuStarveProbe.EnterGpuWait(tick);
            try
            {
                return TryWaitTimelineTraced(tick, out failure);
            }
            finally
            {
                GpuStarveProbe.ExitGpuWait(entered);
            }
        }

        return TryWaitTimelineTraced(tick, out failure);
    }

    private bool TryWaitTimelineTraced(ulong tick, out string failure)
    {
        if (!LabelTrace.Enabled)
        {
            return TryWaitTimelineCore(tick, out failure);
        }

        // [local] SHARPEMU_LABEL_TRACE: the wait's start and end on the critical-path timeline.
        LabelTrace.Note(LabelTrace.Kind.GpuWait, Environment.CurrentManagedThreadId, 2, tick);
        try
        {
            return TryWaitTimelineCore(tick, out failure);
        }
        finally
        {
            LabelTrace.Note(LabelTrace.Kind.GpuWaitEnd, Environment.CurrentManagedThreadId, 2, tick);
        }
    }

    private bool TryWaitTimelineCore(ulong tick, out string failure)
    {
        var target = tick;
        var spinStart = Render2Probe.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
        var spun = GpuWaitSpin.TrySpin(() => ReadTimeline() >= target);
        if (spinStart != 0) Render2Probe.NoteSpin(Render2Probe.SpinSite.Tick, spun, System.Diagnostics.Stopwatch.GetTimestamp() - spinStart); // [local]
        if (spun)
        {
            failure = Result.Success.ToString();
            return true;
        }

        var semaphore = _timeline;
        var waitInfo = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &tick,
        };
        Result result;
        var probeStart = System.Diagnostics.Stopwatch.GetTimestamp(); // [local]
        var probeCompleted = RenderWaitProbe.Enabled ? ReadTimeline() : 0; // [local] SHARPEMU_RENDER_WAIT_PROBE
        var waitStart = GpuWaitSiteProfile.Begin();
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.GpuCompletionWait))
        {
            // [local] A wait over GpuStallReport.TimeoutNs reports where the queues stand, then keeps waiting.
            var reports = 0;
            if (GpuWorkerRelay.CanPumpInWait) // [local] SHARPEMU_RELAY_PUMP_IN_WAITS: wait in slices, running nestable relay work
            {
                GpuWorkerRelay.PumpNestableInWait();
                var sliced = System.Diagnostics.Stopwatch.GetTimestamp();
                while ((result = _vk.WaitSemaphores(_device, &waitInfo, GpuWorkerRelay.PumpSliceNs)) == Result.Timeout)
                {
                    GpuWorkerRelay.PumpNestableInWait();
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(sliced).TotalMilliseconds >= GpuStallReport.TimeoutMs * (reports + 1.0) &&
                        reports++ < GpuStallReport.MaxReportsPerWait)
                    {
                        GpuStallReport.Report($"timeline wait over {GpuStallReport.TimeoutNs / 1_000_000_000 * (ulong)reports} s: wait_tick={tick}");
                    }
                }
            }
            else
            {
                while ((result = _vk.WaitSemaphores(_device, &waitInfo, GpuStallReport.TimeoutNs)) == Result.Timeout)
                {
                    if (reports++ < GpuStallReport.MaxReportsPerWait)
                    {
                        GpuStallReport.Report($"timeline wait over {GpuStallReport.TimeoutNs / 1_000_000_000 * (ulong)reports} s: wait_tick={tick}");
                    }
                }
            }
        }

        GpuWaitSiteProfile.End(waitStart);
        if (Render2Probe.Enabled) Render2Probe.NoteBlocked(Render2Probe.SpinSite.Tick, System.Diagnostics.Stopwatch.GetTimestamp() - probeStart); // [local]
        RenderWaitProbe.Record("timeline", probeStart, (long)tick - (long)probeCompleted);
        failure = result.ToString();
        return result == Result.Success;
    }

    public nint[] AllocateBuffers(int count)
    {
        _ring?.Drain(RingDrainReason.Allocate); // [local] the replay thread records from the same pool
        return _pool.AllocateBuffers(count);
    }

    public void BeginBuffer(nint buffer)
    {
        if (Diagnostics.DgcRunProbe.Enabled) Diagnostics.DgcRunProbe.NoteBufferBegin(buffer); // [local] SHARPEMU_DGC_RUN_PROBE
        if (_ring != null) // [local]
        {
            _ring.BeginBuffer(buffer);
            CommandProfile?.BeginBuffer(new CommandBuffer(buffer));
            return;
        }

        _pool.BeginBuffer(buffer);
    }

    public void EndBuffer(nint buffer, bool hostRead)
    {
        if (Diagnostics.DgcRunProbe.Enabled) Diagnostics.DgcRunProbe.NoteBufferEnd(buffer, hostRead); // [local] SHARPEMU_DGC_RUN_PROBE
        if (_ring != null) // [local] the tail marker and host-read barrier are appended before the ring's end
        {
            VulkanCommandPool.RecordTail(_vk, CommandProfile, new CommandBuffer(buffer), hostRead);
            _recorded = _ring.EndBuffer(buffer);
            return;
        }

        _pool.EndBuffer(buffer, hostRead);
    }

    public IGpuCommandBufferSource CreateBufferSource()
    {
        var pool = new VulkanCommandPool(_vk, _device, _queueFamilyIndex, null);
        lock (_pools)
        {
            _pools.Add(pool);
        }

        return pool;
    }

    // [local] Marked on the recording thread: the profile's buffer table is not shared with a submit thread. Replaces
    // upstream's MarkSubmitted after TrySubmit; only the device's own buffers are profiled.
    public void MarkSealed(nint buffer) => CommandProfile?.MarkSubmitted(buffer);

    public bool TrySubmit(ReadOnlySpan<nint> buffers, SubmitBundle bundle, out string failure)
    {
        fixed (ulong* waitSemaphores = bundle.WaitSemaphores)
        fixed (ulong* waitTicks = bundle.WaitTicks)
        fixed (uint* waitStages = bundle.WaitStages)
        fixed (ulong* signalSemaphores = bundle.SignalSemaphores)
        fixed (ulong* signalTicks = bundle.SignalTicks)
        {
            var waitInfos = stackalloc SemaphoreSubmitInfo[bundle.WaitCount];
            for (var index = 0; index < bundle.WaitCount; index++)
            {
                waitInfos[index] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = new VkSemaphore(waitSemaphores[index]),
                    Value = waitTicks[index],
                    StageMask = (PipelineStageFlags2)waitStages[index],
                };
            }

            var signalInfos = stackalloc SemaphoreSubmitInfo[bundle.SignalCount];
            for (var index = 0; index < bundle.SignalCount; index++)
            {
                signalInfos[index] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = new VkSemaphore(signalSemaphores[index]),
                    Value = signalTicks[index],
                    StageMask = PipelineStageFlags2.AllCommandsBit,
                };
            }

            var commandInfos = stackalloc CommandBufferSubmitInfo[buffers.Length];
            for (var index = 0; index < buffers.Length; index++)
            {
                commandInfos[index] = new CommandBufferSubmitInfo
                {
                    SType = StructureType.CommandBufferSubmitInfo,
                    CommandBuffer = new CommandBuffer(buffers[index]),
                    DeviceMask = 1,
                };
            }

            var submitInfo = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = (uint)bundle.WaitCount,
                PWaitSemaphoreInfos = waitInfos,
                CommandBufferInfoCount = (uint)buffers.Length,
                PCommandBufferInfos = commandInfos,
                SignalSemaphoreInfoCount = (uint)bundle.SignalCount,
                PSignalSemaphoreInfos = signalInfos,
            };
            if (Diagnostics.SubmitSiteTally.Enabled)
            {
                Diagnostics.SubmitSiteTally.Note();
            }

            var vkProbe = VkCallProbe.Start(); // [local]
            var result = _vk.QueueSubmit2(_queue, 1, &submitInfo, default);
            VkCallProbe.Stop(VkCallProbe.Kind.Submit, vkProbe); // [local]
            failure = result.ToString();
            return result == Result.Success;
        }
    }

    // [local] SHARPEMU_SUBMIT_BATCH: one vkQueueSubmit2 with one VkSubmitInfo2 per sealed submission (its buffers, waits
    // and signals), in tick order. The spec orders the submissions of one call as it orders separate calls, so this is
    // the same work as submitting them one by one.
    public bool TrySubmitBatch(ReadOnlySpan<nint[]> buffers, ReadOnlySpan<SubmitBundle> bundles, out string failure)
    {
        if (buffers.Length == 1)
        {
            return TrySubmit(buffers[0], bundles[0], out failure);
        }

        var count = buffers.Length;
        var waitTotal = 0;
        var signalTotal = 0;
        var commandTotal = 0;
        for (var index = 0; index < count; index++)
        {
            waitTotal += bundles[index].WaitCount;
            signalTotal += bundles[index].SignalCount;
            commandTotal += buffers[index].Length;
        }

        var waitInfos = stackalloc SemaphoreSubmitInfo[Math.Max(waitTotal, 1)];
        var signalInfos = stackalloc SemaphoreSubmitInfo[Math.Max(signalTotal, 1)];
        var commandInfos = stackalloc CommandBufferSubmitInfo[Math.Max(commandTotal, 1)];
        var submitInfos = stackalloc SubmitInfo2[count];
        var waitNext = 0;
        var signalNext = 0;
        var commandNext = 0;
        for (var index = 0; index < count; index++)
        {
            var bundle = bundles[index];
            var waitFirst = waitNext;
            for (var wait = 0; wait < bundle.WaitCount; wait++)
            {
                waitInfos[waitNext++] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = new VkSemaphore(bundle.WaitSemaphores[wait]),
                    Value = bundle.WaitTicks[wait],
                    StageMask = (PipelineStageFlags2)bundle.WaitStages[wait],
                };
            }

            var signalFirst = signalNext;
            for (var signal = 0; signal < bundle.SignalCount; signal++)
            {
                signalInfos[signalNext++] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = new VkSemaphore(bundle.SignalSemaphores[signal]),
                    Value = bundle.SignalTicks[signal],
                    StageMask = PipelineStageFlags2.AllCommandsBit,
                };
            }

            var commandFirst = commandNext;
            foreach (var buffer in buffers[index])
            {
                commandInfos[commandNext++] = new CommandBufferSubmitInfo
                {
                    SType = StructureType.CommandBufferSubmitInfo,
                    CommandBuffer = new CommandBuffer(buffer),
                    DeviceMask = 1,
                };
            }

            submitInfos[index] = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = (uint)bundle.WaitCount,
                PWaitSemaphoreInfos = waitInfos + waitFirst,
                CommandBufferInfoCount = (uint)buffers[index].Length,
                PCommandBufferInfos = commandInfos + commandFirst,
                SignalSemaphoreInfoCount = (uint)bundle.SignalCount,
                PSignalSemaphoreInfos = signalInfos + signalFirst,
            };
        }

        if (Diagnostics.SubmitSiteTally.Enabled)
        {
            Diagnostics.SubmitSiteTally.Note();
        }

        var vkProbe = VkCallProbe.Start();
        var result = _vk.QueueSubmit2(_queue, (uint)count, submitInfos, default);
        VkCallProbe.Stop(VkCallProbe.Kind.Submit, vkProbe);
        failure = result.ToString();
        return result == Result.Success;
    }

    public void Dispose()
    {
        _ring?.Dispose(); // [local]
        CommandProfile?.Dispose();
        _vk.DestroySemaphore(_device, _timeline, null);
        _pool.Dispose();
        lock (_pools)
        {
            foreach (var pool in _pools)
            {
                pool.Dispose();
            }

            _pools.Clear();
        }
    }

    internal static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed with {result}");
        }
    }
}
