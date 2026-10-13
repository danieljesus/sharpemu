// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_NATIVE_STATE_REUSE: skip a pipeline, push-constant, descriptor, vertex-buffer or index-buffer bind
// that repeats what the same command buffer already has bound. 1 = skip; 2 = only count what would be skipped
// ([PERF][NATIVE_STATE]); unset or 0 = off. The shadow is dropped when the command buffer, the scheduler tick or the
// NativeBindingEpoch changes (any bind recorded by someone else, e.g. a helper pass). Adapted from 1fbecd32 by
// ParantezTech (#1082): our binds go through VkRecord and the epoch is advanced there, and a skipped descriptor
// update also skips the descriptor-heap commit and vkUpdateDescriptorSets.
internal static unsafe partial class VulkanVideoPresenter
{
    internal static readonly int NativeStateReuseMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_STATE_REUSE"), out var reuse) ? reuse : 0;

    private enum NativeStateKind { Pipeline, PushConstants, Descriptors, VertexBuffers, IndexBuffer, Count }

    private static readonly long[] _nativeStateCalls = new long[(int)NativeStateKind.Count];
    private static readonly long[] _nativeStateRepeats = new long[(int)NativeStateKind.Count];

    internal static string? TakeNativeStateReport()
    {
        if (NativeStateReuseMode == 0)
        {
            return null;
        }

        var text = new System.Text.StringBuilder("[PERF][NATIVE_STATE] mode=").Append(NativeStateReuseMode);
        for (var kind = 0; kind < (int)NativeStateKind.Count; kind++)
        {
            var calls = Interlocked.Exchange(ref _nativeStateCalls[kind], 0);
            var repeats = Interlocked.Exchange(ref _nativeStateRepeats[kind], 0);
            text.Append(' ').Append(((NativeStateKind)kind).ToString()).Append('=').Append(repeats).Append('/').Append(calls);
        }

        return text.ToString();
    }

    private sealed partial class Presenter
    {
        private const int ShadowVertexBufferCapacity = 32;

        private nint _shadowCommand;
        private ulong _shadowTick;
        private long _shadowEpoch = -1;
        private Pipeline _shadowGraphicsPipeline;
        private Pipeline _shadowComputePipeline;
        private PipelineLayout _shadowPushLayout;
        private ShaderStageFlags _shadowPushStages;
        private uint[]? _shadowPushData;
        private DescriptorShadow? _graphicsDescriptorShadow;
        private DescriptorShadow? _computeDescriptorShadow;
        private ulong[]? _shadowVertexBindings;
        private int _shadowVertexCount = -1;
        private VkBuffer _shadowIndexBuffer;
        private ulong _shadowIndexOffset;
        private IndexType _shadowIndexType;
        private bool _shadowIndexValid;
        private ulong[]? _descriptorKeyScratch;

        private sealed class DescriptorShadow
        {
            public bool Valid;
            public PipelineLayout Layout;
            public ulong[] Key = new ulong[256];
            public int Length;
        }

        // Whether the shadow describes this command buffer; resets it when it does not.
        private bool SyncNativeShadow(CommandBuffer command)
        {
            if (NativeStateReuseMode == 0)
            {
                return false;
            }

            var tick = _scheduler.CurrentTick;
            var epoch = NativeBindingEpoch.Value;
            if (command.Handle == _shadowCommand && tick == _shadowTick && epoch == _shadowEpoch)
            {
                return true;
            }

            _shadowCommand = command.Handle;
            _shadowTick = tick;
            _shadowEpoch = epoch;
            _shadowGraphicsPipeline = default;
            _shadowComputePipeline = default;
            _shadowPushLayout = default;
            if (_graphicsDescriptorShadow is not null)
            {
                _graphicsDescriptorShadow.Valid = false;
            }

            if (_computeDescriptorShadow is not null)
            {
                _computeDescriptorShadow.Valid = false;
            }

            _shadowVertexCount = -1;
            _shadowIndexValid = false;
            return true;
        }

        // Our own bind advanced the epoch; the shadow still describes the command buffer.
        private void AcceptOwnBind()
        {
            if (NativeStateReuseMode != 0)
            {
                _shadowEpoch = NativeBindingEpoch.Value;
            }
        }

        private static bool Reuse(NativeStateKind kind, bool repeated)
        {
            Interlocked.Increment(ref _nativeStateCalls[(int)kind]);
            if (repeated)
            {
                Interlocked.Increment(ref _nativeStateRepeats[(int)kind]);
            }

            return repeated && NativeStateReuseMode == 1;
        }

        private void BindPipelineShadowed(CommandBuffer command, PipelineBindPoint bindPoint, Pipeline pipeline)
        {
            if (SyncNativeShadow(command))
            {
                ref var bound = ref bindPoint == PipelineBindPoint.Compute ? ref _shadowComputePipeline : ref _shadowGraphicsPipeline;
                if (Reuse(NativeStateKind.Pipeline, bound.Handle == pipeline.Handle))
                {
                    return;
                }

                bound = pipeline;
            }

            VkRecord.CmdBindPipeline(_vk, command, bindPoint, pipeline);
            AcceptOwnBind();
        }

        // True when the same push constants are already recorded; otherwise the caller records them and calls AcceptOwnBind.
        private bool PushConstantsAlreadyRecorded(CommandBuffer command, PipelineLayout layout, ShaderStageFlags stages, uint* data)
        {
            if (!SyncNativeShadow(command))
            {
                return false;
            }

            var values = new ReadOnlySpan<uint>(data, (int)PushData.DwordCount);
            var shadowData = _shadowPushData ??= new uint[PushData.DwordCount];
            var repeated = _shadowPushLayout.Handle == layout.Handle && _shadowPushStages == stages && values.SequenceEqual(shadowData);
            if (Reuse(NativeStateKind.PushConstants, repeated))
            {
                return true;
            }

            _shadowPushLayout = layout;
            _shadowPushStages = stages;
            values.CopyTo(shadowData);
            return false;
        }

        private bool DescriptorsAlreadyBound(CommandBuffer command, PipelineBindPoint bindPoint, PipelineLayout layout, ReadOnlySpan<WriteDescriptorSet> writes)
        {
            if (!SyncNativeShadow(command))
            {
                return false;
            }

            var length = DescriptorKey(writes);
            var shadow = bindPoint == PipelineBindPoint.Compute
                ? _computeDescriptorShadow ??= new DescriptorShadow()
                : _graphicsDescriptorShadow ??= new DescriptorShadow();
            var key = _descriptorKeyScratch.AsSpan(0, length);
            var repeated = shadow.Valid && shadow.Layout.Handle == layout.Handle && key.SequenceEqual(shadow.Key.AsSpan(0, shadow.Length));
            if (Reuse(NativeStateKind.Descriptors, repeated))
            {
                return true;
            }

            if (shadow.Key.Length < length)
            {
                shadow.Key = new ulong[Math.Max(length, shadow.Key.Length * 2)];
            }

            key.CopyTo(shadow.Key);
            shadow.Length = length;
            shadow.Layout = layout;
            shadow.Valid = true;
            return false;
        }

        private int DescriptorKey(ReadOnlySpan<WriteDescriptorSet> writes)
        {
            var length = 0;
            _descriptorKeyScratch ??= new ulong[256];
            foreach (ref readonly var write in writes)
            {
                var needed = length + 2 + (int)write.DescriptorCount * 3;
                if (_descriptorKeyScratch.Length < needed)
                {
                    Array.Resize(ref _descriptorKeyScratch, Math.Max(needed, _descriptorKeyScratch.Length * 2));
                }

                var key = _descriptorKeyScratch;
                key[length++] = write.DstBinding | ((ulong)write.DescriptorType << 32) | ((ulong)write.DstArrayElement << 48);
                key[length++] = write.DescriptorCount | (write.PBufferInfo != null ? 1UL << 32 : 0) | (write.PImageInfo != null ? 1UL << 33 : 0) |
                    (write.PTexelBufferView != null ? 1UL << 34 : 0);
                for (var index = 0; index < write.DescriptorCount; index++)
                {
                    if (write.PBufferInfo != null)
                    {
                        var info = write.PBufferInfo[index];
                        key[length++] = info.Buffer.Handle;
                        key[length++] = info.Offset;
                        key[length++] = info.Range;
                    }
                    else if (write.PImageInfo != null)
                    {
                        var info = write.PImageInfo[index];
                        key[length++] = info.Sampler.Handle;
                        key[length++] = info.ImageView.Handle;
                        key[length++] = (ulong)info.ImageLayout;
                    }
                    else if (write.PTexelBufferView != null)
                    {
                        key[length++] = write.PTexelBufferView[index].Handle;
                        key[length++] = 0;
                        key[length++] = 0;
                    }
                    else
                    {
                        // A descriptor kind the key cannot describe never repeats.
                        key[length++] = ulong.MaxValue;
                        key[length++] = (ulong)NativeBindingEpoch.Value;
                        key[length++] = (ulong)length;
                    }
                }
            }

            return length;
        }

        private bool VertexBuffersAlreadyBound(CommandBuffer command, ReadOnlySpan<BufferBinding> bindings)
        {
            if (!SyncNativeShadow(command) || bindings.Length > ShadowVertexBufferCapacity)
            {
                _shadowVertexCount = -1;
                return false;
            }

            var shadow = _shadowVertexBindings ??= new ulong[ShadowVertexBufferCapacity * 2];
            var repeated = _shadowVertexCount == bindings.Length;
            for (var index = 0; repeated && index < bindings.Length; index++)
            {
                repeated = shadow[index * 2] == bindings[index].Handle && shadow[index * 2 + 1] == bindings[index].Offset;
            }

            if (Reuse(NativeStateKind.VertexBuffers, repeated))
            {
                return true;
            }

            for (var index = 0; index < bindings.Length; index++)
            {
                shadow[index * 2] = bindings[index].Handle;
                shadow[index * 2 + 1] = bindings[index].Offset;
            }

            _shadowVertexCount = bindings.Length;
            return false;
        }

        private bool IndexBufferAlreadyBound(CommandBuffer command, VkBuffer buffer, ulong offset, IndexType type)
        {
            if (!SyncNativeShadow(command))
            {
                return false;
            }

            var repeated = _shadowIndexValid && _shadowIndexBuffer.Handle == buffer.Handle && _shadowIndexOffset == offset && _shadowIndexType == type;
            if (Reuse(NativeStateKind.IndexBuffer, repeated))
            {
                return true;
            }

            _shadowIndexBuffer = buffer;
            _shadowIndexOffset = offset;
            _shadowIndexType = type;
            _shadowIndexValid = true;
            return false;
        }
    }
}
