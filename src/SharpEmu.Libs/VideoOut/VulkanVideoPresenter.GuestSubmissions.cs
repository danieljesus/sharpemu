// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial owns guest GPU submission execution and lifetime.

        // Batched submissions each carry up to a frame's worth of work, so a cap of
        // 8 made draw preparation wait on the GPU once per frame; 32 keeps the CPU
        // ahead. SHARPEMU_MAX_INFLIGHT_SUBMISSIONS overrides it.
        private static readonly int MaxInFlightGuestSubmissions =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MAX_INFLIGHT_SUBMISSIONS"), out var inFlight) && inFlight > 0
                ? inFlight
                : 32;
        // Scheduler ticks: the last submitted tick and the highest tick known retired.
        private ulong _submitTimeline;
        private ulong _completedTimeline;
        private readonly Queue<(GuestImageResource Image, ulong RetireTimeline)>
            _deferredGuestImageVersionDestroys = new();
        private readonly List<(VkBuffer Buffer, DeviceMemory Memory)> _batchRetireBuffers = new();

        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        private VulkanGuestQueueIdentity _activeGuestQueue =
            VulkanGuestQueueIdentity.Default;

        private sealed record PendingGuestSubmission(
            ulong Tick,
            IReadOnlyList<SubmissionUploadResources> Resources,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers);

        // Translated draws are recorded into the scheduler's current buffer and
        // submitted once per drained work batch (one vkQueueSubmit per batch).
        private bool _batchOpen;
        private int _batchDrawCount;
        private readonly List<SubmissionUploadResources> _batchResources = new();
        // [local] The debug name of the batch's first committed draw or dispatch; a draw with nothing to retire
        // adds no resource entry, so the name is kept apart.
        private string? _batchFirstDebugName;
        private const string DrawDebugName = "SharpEmu draw";
        private const string DispatchDebugName = "SharpEmu dispatch";


        private CommandBuffer BeginBatchedGuestCommands()
        {
            var commandBuffer = CurrentRecordingBuffer();
            if (!_batchOpen)
            {
                _batchOpen = true;
                _batchDrawCount = 0;
            }

            return commandBuffer;
        }

        // Submits the current recording buffer with everything the batch lists own.
        // Returns the timeline tick that contains the commands this call submitted, so a
        // caller that must wait for its own recording does not read the shared
        // _submitTimeline field, which the presenter thread also writes.
        private ulong FlushBatchedGuestCommands()
        {
            if (!_batchOpen)
            {
                return _submitTimeline;
            }

            using var profile = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit);
            var tick = _scheduler.Flush();
            _bufferCache?.OnBatchSubmitted();
            return tick;
        }

        private void PrepareGuestSubmission(SubmitBundle bundle)
        {
            _ = bundle;
            EndRendering();
            if (!_batchOpen)
            {
                return;
            }

            _lastSubmitDebugName = _batchFirstDebugName switch
            {
                null => string.Empty,
                DrawDebugName => "batch=" + DrawDebugName,
                DispatchDebugName => "batch=" + DispatchDebugName,
                var name => $"batch={name}",
            };
        }

        private void CompleteGuestSubmission(ulong tick)
        {
            _submitTimeline = tick;
            if (!_batchOpen)
            {
                return;
            }

            _batchOpen = false;
            var resources = _batchResources.ToArray();
            var retireBuffers = _batchRetireBuffers.Count > 0 ? _batchRetireBuffers.ToArray() : [];
            _batchResources.Clear();
            _batchFirstDebugName = null;
            _batchRetireBuffers.Clear();

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    tick,
                    resources,
                    retireBuffers));
        }

        // [local] SHARPEMU_COMPLETION_POLL_US: completed submissions are collected at most this often before draws
        // (each poll reads the timeline and runs the deferred tick work); a full queue always collects. 0 polls every draw.
        private static readonly long CompletionPollTicks = (long)(System.Diagnostics.Stopwatch.Frequency / 1e6 *
            (double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_COMPLETION_POLL_US"), System.Globalization.CultureInfo.InvariantCulture, out var poll) && poll >= 0 ? poll : 100));
        private long _lastCompletionPoll;

        private void EnsureGuestSubmissionCapacity()
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.SubmissionCapacity);
            if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions || CompletionPollTicks == 0 ||
                System.Diagnostics.Stopwatch.GetTimestamp() - _lastCompletionPoll >= CompletionPollTicks)
            {
                CollectCompletedGuestSubmissions(waitForOldest: false);
                _lastCompletionPoll = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void WaitForAllGuestSubmissions()
        {
            while (_pendingGuestSubmissions.Count != 0)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void CollectCompletedGuestSubmissions(bool waitForOldest)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CompletedSubmissionCleanup);
            if (waitForOldest && _pendingGuestSubmissions.TryPeek(out var oldest))
            {
                _scheduler.Wait(oldest.Tick);
            }

            while (_pendingGuestSubmissions.TryPeek(out var submission) &&
                   _scheduler.IsTickComplete(submission.Tick))
            {
                _pendingGuestSubmissions.Dequeue();
                RetireGuestSubmission(submission);
            }

            _completedTimeline = Math.Max(_completedTimeline, _scheduler.Timeline.CompletedTick);
            // Deferred tick work (buffer erases, one-shot uploads, fault parses) runs here.
            if (_scheduler.Active)
            {
                _scheduler.RunCompletedOperations();
            }

            _commandStream.NotifyCompletedGpuTick(_scheduler.Timeline.CompletedTick);

            ProcessDeferredTextureDestroys();
        }

        private void RetireGuestSubmission(PendingGuestSubmission submission)
        {
            if (submission.Tick > _completedTimeline)
            {
                _completedTimeline = submission.Tick;
            }

            foreach (var resources in submission.Resources)
            {
                RecycleSubmissionUploads(resources);
            }

            foreach (var (buffer, memory) in submission.RetireBuffers)
            {
                _vk.DestroyBuffer(_device, buffer, null);
                _deviceInfo.FreeMemory(memory);
            }
        }

    }
}
