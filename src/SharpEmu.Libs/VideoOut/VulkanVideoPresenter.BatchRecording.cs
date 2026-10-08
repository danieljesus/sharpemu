// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// The draws of one rendering scope recorded into a secondary command buffer that the primary
// executes when the scope ends (SHARPEMU_RECORD_BATCHES=1). With the recording of a scope in
// its own buffer, a later step can hand it to another thread; this step measures what the
// secondary buffers themselves cost.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter : IRenderHost
    {

        private static readonly bool RecordBatches = Environment.GetEnvironmentVariable("SHARPEMU_RECORD_BATCHES") == "1";

        // Secondaries with the tick they last recorded into; one is reused once that tick completed.
        private readonly List<(CommandBuffer Buffer, ulong Tick)> _secondaries = new();
        private CommandBuffer _batchSecondary;
        private bool _batchRecording;
        private long _batchesRecorded;

        // Opens the secondary for the scope the primary just began with secondary contents; every
        // command recorded while it is open lands there.
        private void OpenRecordingBatch(in RenderingState state)
        {
            var secondary = default(CommandBuffer);
            var reused = false;
            for (var index = 0; index < _secondaries.Count; index++)
            {
                if (_scheduler.IsTickComplete(_secondaries[index].Tick))
                {
                    secondary = _secondaries[index].Buffer;
                    _secondaries[index] = (secondary, _scheduler.CurrentTick);
                    reused = true;
                    break;
                }
            }

            if (!reused)
            {
                var allocate = new CommandBufferAllocateInfo
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = _commandPool,
                    Level = CommandBufferLevel.Secondary,
                    CommandBufferCount = 1,
                };
                Check(_vk.AllocateCommandBuffers(_device, &allocate, out secondary), "vkAllocateCommandBuffers(secondary)");
                _secondaries.Add((secondary, _scheduler.CurrentTick));
            }

            var formats = stackalloc Format[RenderingState.ColorAttachmentCapacity];
            for (var index = 0; index < state.ColorAttachmentCount; index++)
            {
                formats[index] = state.ColorAttachments[(int)index].Format;
            }

            var renderingInheritance = new CommandBufferInheritanceRenderingInfo
            {
                SType = StructureType.CommandBufferInheritanceRenderingInfo,
                ColorAttachmentCount = state.ColorAttachmentCount,
                PColorAttachmentFormats = formats,
                DepthAttachmentFormat = state.DepthFormat,
                StencilAttachmentFormat = state.StencilFormat,
                RasterizationSamples = (SampleCountFlags)Math.Max(state.Samples, 1u),
            };
            var inheritance = new CommandBufferInheritanceInfo
            {
                SType = StructureType.CommandBufferInheritanceInfo,
                PNext = &renderingInheritance,
            };
            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit | CommandBufferUsageFlags.RenderPassContinueBit,
                PInheritanceInfo = &inheritance,
            };
            Check(_vk.BeginCommandBuffer(secondary, &begin), "vkBeginCommandBuffer(secondary)");
            _batchSecondary = secondary;
            _batchRecording = true;
            _batchesRecorded++;
        }

        // Ends the open secondary and executes it in the primary; the ring reuses it once the tick completes.
        private void CloseRecordingBatch(CommandBuffer primary)
        {
            if (!_batchRecording)
            {
                return;
            }

            var secondary = _batchSecondary;
            _batchRecording = false;
            _batchSecondary = default;
            Check(_vk.EndCommandBuffer(secondary), "vkEndCommandBuffer(secondary)");
            var executed = secondary;
            _vk.CmdExecuteCommands(primary, 1, &executed);
            _commandBuffer = primary;
        }

        internal string TakeBatchReport() =>
            FormattableString.Invariant($"[PERF][RECORD_BATCHES] enabled={RecordBatches} batches={Interlocked.Exchange(ref _batchesRecorded, 0)}");
    }
}
