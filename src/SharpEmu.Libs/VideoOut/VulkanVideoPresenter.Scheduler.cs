// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter : IRenderingState
    {
        // This partial owns the submission scheduler and the GPU worker relay.

        private readonly object _queueGate = new();
        private readonly GpuWorkerRelay _relay;

        internal GpuWorkerRelay Relay => _relay;
        private readonly SubmissionContext _submissionContext = new();
        private SubmissionScheduler _scheduler = null!;
        internal SubmissionScheduler? Scheduler => _scheduler; // [local] SHARPEMU_REWIND_PATCH_WAKE=2
        private VulkanCommandProfile? _gpuCommandProfile;
        private GpuDeviceInfo _deviceInfo = null!;
        private GuestBufferCache _bufferCache = null!;
        private GuestImageCache _imageCache = null!;
        private SamplerStore _samplerStore = null!;

        bool IRenderingState.IsRendering => _renderingActive;

        void IRenderingState.EndRendering()
        {
            EndRendering();
        }

        bool IRenderingState.TryDeferUntilRenderingEnds(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, List<ImageMemoryBarrier2> barriers)
        {
            if (!_renderingActive)
            {
                return false;
            }

            _barriersAfterRendering.Add((sourceStages, destinationStages, barriers.ToArray()));
            return true;
        }

        internal static void WakeRenderThread()
        {
            SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.WakeRequested);
            lock (_gate)
            {
                SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.WakeSignaled);
                System.Threading.Monitor.PulseAll(_gate);
            }
        }

        private void CreateScheduler()
        {
            var tickDevice = new VulkanTickDevice(_vk, _device, _queue, _queueFamilyIndex, _queueGate,
                RenderPhaseProfile.Enabled || Gpu.Vulkan.SlowModeProbe.Enabled ? _physicalDevice : default, Gpu.Vulkan.CommandRing.Enabled,
                dormantProfile: !RenderPhaseProfile.Enabled); // [local]
            _gpuCommandProfile = tickDevice.CommandProfile;
            if (_gpuCommandProfile is { Dormant: true }) // [local] SlowModeProbe: interval lines name the shaders
                VulkanCommandProfile.DescribePipeline = id => _pipelineEntries.TryGetValue(id, out var entry)
                    ? (entry.ProfileComputeHash != 0 ? $"cs=0x{entry.ProfileComputeHash:X16}" : $"vs=0x{entry.ProfileVertexHash:X16},ps=0x{entry.ProfilePixelHash:X16}")
                    : "unknown";
            _scheduler = new SubmissionScheduler(
                tickDevice,
                this,
                PrepareGuestSubmission,
                CompleteGuestSubmission,
                _ => WakeRenderThread(),
                SubmissionScheduler.SubmitThreadDefault); // [local]
        }

        // Both stores share the manager's page guard and read guest memory through its address space.
        private static (GuestGpuMemory Memory, ICpuMemory Guest, IGuestBackedSpace Backing) RequireGuestMemory(string store)
        {
            if (GuestGpuMemoryHook.Current is not { AddressSpace: ICpuMemory guest and IGuestBackedSpace backing } memory)
            {
                throw SubmissionScheduler.Fatal($"The {store} requires the guest GPU memory manager with backed virtual memory.");
            }

            return (memory, guest, backing);
        }

        private void CreateBufferCache()
        {
            var (memory, guest, backing) = RequireGuestMemory("buffer store");
            _guestBacking = backing;
            _bufferCache = new GuestBufferCache(_deviceInfo, _scheduler, _relay, memory.Pages, guest, backing);
            Gpu.Rendering.RenderExecutor.HostMissReport = static () => // [local] SHARPEMU_ATTACH_REPEAT
            {
                var misses = Presenter.AttachRepeatMisses;
                var text = string.Join(",", misses.Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Array.Clear(misses);
                var refusals = GuestImageCache.RepeatRefusals;
                text += " image=" + string.Join(",", refusals.Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                Array.Clear(refusals);
                return text;
            };
            _bufferCache.StreamOffsetAlignment = Math.Max(_bufferCache.StreamOffsetAlignment, GuestStorageBufferOffsetAlignment);
            if (_readbackQueueFamilyIndex is { } readbackFamily)
            {
                _bufferCache.AsyncReadback = new Gpu.Vulkan.VulkanAsyncReadback(_deviceInfo, _scheduler, _readbackQueue, readbackFamily);
                if (_syncReadbackQueue.Handle != 0) // [local] SHARPEMU_SYNC_READBACK_QUEUE
                {
                    var second = new Gpu.Vulkan.VulkanAsyncReadback(_deviceInfo, _scheduler, _syncReadbackQueue, readbackFamily);
                    if (TableReadbackQueue) // [local] SHARPEMU_STORE_TABLE_SYNC
                        _bufferCache.TableReadback = second;
                    else
                        _bufferCache.SyncReadback = second;
                }
            }

            if (FrontStaleCheck) // [local]
            {
                Gpu.Buffers.GuestBufferCache.FirstGpuWrite = CheckFrontStaleReads;
            }

            // [local] What a [GPU][STALL] line reports after the wait it names.
            var scheduler = _scheduler;
            var bufferCache = _bufferCache;
            Gpu.Scheduling.GpuStallReport.Describe = () =>
                $"{scheduler.DescribeStall()} async_readback=[{bufferCache.AsyncReadback?.DescribeStall() ?? "none"}] " +
                $"sync_readback=[{bufferCache.SyncReadback?.DescribeStall() ?? "none"}] back_queue={_back?.Count ?? -1} " +
                $"pending_labels={FrontLabels.Count} front_stale_reads={FrontStaleReads}";
            Gpu.Scheduling.GpuStallReport.History = () => scheduler.FormatRecentSubmissions().ToArray();
        }

        // The image store follows the buffer store; readback of linear images stays off.
        private void CreateImageCache()
        {
            var (memory, _, backing) = RequireGuestMemory("image store");
            _imageCache = new GuestImageCache(_deviceInfo, _scheduler, memory.Pages, _bufferCache, backing, readbackLinearImages: false);
            _bufferCache.ImageCache = _imageCache;
            _samplerStore = new SamplerStore(_deviceInfo);
        }

        private void AttachGuestGpuMemory()
        {
            GuestGpuMemoryHook.Current?.AttachStores(_bufferCache, _imageCache);
            GuestGpuMemoryHook.Current?.AttachGpuQueue(_relay, _scheduler);
        }

        // Close admission, run accepted commands, drain the stores (images first), finish GPU work, shut down, detach.
        private void ShutdownScheduler()
        {
            try
            {
                // The front stops first; the drain below then runs its remaining slices here.
                StopFrontThread();
                // Drain accepted submissions before closing the relay.
                // Cancel blocked submissions if a full retry cycle makes no progress.
                _commandStream.StopAccepting();
                var outcome = _commandStream.DrainForShutdown(cancelBlockedOnNoProgress: true);
                FlushBatchedGuestCommands();
                Console.Error.WriteLine(
                    $"[LOADER][PERF] command_stream submissions={_commandStream.SubmissionsStarted} " +
                    $"slices={_commandStream.SlicesRun} blocked_retries={_commandStream.BlockedRetries} " +
                    $"outcome={outcome} fatal=0");
                Console.Error.WriteLine($"[LOADER][PERF] {ShaderCacheCounters.Summary()}");
                _relay.StopAcceptingWork();
                _relay.RunPendingCommands();
                try
                {
                    _imageCache.Shutdown();
                    _bufferCache.Shutdown();
                }
                finally
                {
                    GuestGpuMemoryHook.Current?.AttachStores(null, null);
                }

                if (_scheduler.Active)
                {
                    _scheduler.Finish();
                    _scheduler.WaitForAllPriorityOperations();
                }

                _scheduler.Shutdown();
            }
            finally
            {
                // An unmap waiting for the relay to go away must not outlive a failed shutdown.
                GuestGpuMemoryHook.Current?.AttachGpuQueue(null, null);
                VideoOutExports.CancelOutstandingFlips();
            }
        }

        private CommandBuffer CurrentRecordingBuffer()
        {
            if (!_scheduler.Active)
            {
                _scheduler.Begin(_submissionContext);
            }

            return new CommandBuffer(_scheduler.Current.Handle);
        }

        private void BindSubmissionContext(VulkanGuestQueueIdentity queue)
        {
            _submissionContext.QueueName = queue.Name;
            _submissionContext.SubmissionId = queue.SubmissionId;
        }

        private ulong SubmitPresentation(
            VkSemaphore imageAvailable,
            PipelineStageFlags waitStage,
            VkSemaphore renderFinished)
        {
            var bundle = new SubmitBundle();
            bundle.AddWait(imageAvailable.Handle, 1, (uint)waitStage);
            bundle.AddSignal(renderFinished.Handle, 1);
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit))
            {
                return _scheduler.Flush(bundle);
            }
        }
    }
}
