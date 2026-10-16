// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] Every vkCmd* goes through here. A command buffer that the record ring is recording gets the command
// appended to the ring (copied, so stack arguments may go away); any other command buffer, or every call while the
// ring is off, goes straight to the driver. Arguments the ring cannot carry (a pNext chain, an oversized payload)
// drain the ring and go to the driver too.
internal static unsafe class VkRecord
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static CommandRing? Ring(CommandBuffer command)
    {
        var ring = CommandRing.Active;
        if (ring == null)
        {
            return null;
        }

        if (ring.ProducerBuffer == command.Handle && command.Handle != 0)
        {
            if (CommandRing.Check)
            {
                ring.CheckProducerThread();
            }

            return ring;
        }

        if (CommandRing.Check)
        {
            ring.CheckDirect(command.Handle);
        }

        return null;
    }

    private static void Fallback(CommandRing ring, RingOp op)
    {
        RingStats.Fallback(op);
        ring.Drain(RingDrainReason.Fallback);
    }

    private static bool Fits(int payload) => payload <= CommandRing.MaxPayload;

    public static void CmdPipelineBarrier2(Vk vk, CommandBuffer command, DependencyInfo* info)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteOp(command, VulkanCommandProfile.OpBit.Barrier); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            var memoryBytes = (int)info->MemoryBarrierCount * sizeof(MemoryBarrier2);
            var bufferBytes = (int)info->BufferMemoryBarrierCount * sizeof(BufferMemoryBarrier2);
            var imageBytes = (int)info->ImageMemoryBarrierCount * sizeof(ImageMemoryBarrier2);
            var payloadSize = sizeof(RingArgs.BarrierHead) + memoryBytes + bufferBytes + imageBytes;
            if (info->PNext == null && Fits(payloadSize) && BarriersHaveNoChain(info))
            {
                var payload = ring.Reserve(RingOp.Barrier, payloadSize);
                var head = (RingArgs.BarrierHead*)payload;
                head->Flags = info->DependencyFlags;
                head->Memory = info->MemoryBarrierCount;
                head->Buffer = info->BufferMemoryBarrierCount;
                head->Image = info->ImageMemoryBarrierCount;
                var cursor = payload + sizeof(RingArgs.BarrierHead);
                Unsafe.CopyBlockUnaligned(cursor, info->PMemoryBarriers, (uint)memoryBytes);
                cursor += memoryBytes;
                Unsafe.CopyBlockUnaligned(cursor, info->PBufferMemoryBarriers, (uint)bufferBytes);
                cursor += bufferBytes;
                Unsafe.CopyBlockUnaligned(cursor, info->PImageMemoryBarriers, (uint)imageBytes);
                ring.Commit();
                return;
            }

            Fallback(ring, RingOp.Barrier);
        }

        vk.CmdPipelineBarrier2(command, info);
    }

    private static bool BarriersHaveNoChain(DependencyInfo* info)
    {
        for (var index = 0u; index < info->MemoryBarrierCount; index++)
        {
            if (info->PMemoryBarriers[index].PNext != null)
            {
                return false;
            }
        }

        for (var index = 0u; index < info->BufferMemoryBarrierCount; index++)
        {
            if (info->PBufferMemoryBarriers[index].PNext != null)
            {
                return false;
            }
        }

        for (var index = 0u; index < info->ImageMemoryBarrierCount; index++)
        {
            if (info->PImageMemoryBarriers[index].PNext != null)
            {
                return false;
            }
        }

        return true;
    }

    public static void CmdBindPipeline(Vk vk, CommandBuffer command, PipelineBindPoint bindPoint, Pipeline pipeline)
    {
        NativeBindingEpoch.Advance(); // [local]
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.BindPipeline*)ring.Reserve(RingOp.BindPipeline, sizeof(RingArgs.BindPipeline));
            args->BindPoint = bindPoint;
            args->Pipeline = pipeline;
            ring.Commit();
            return;
        }

        vk.CmdBindPipeline(command, bindPoint, pipeline);
    }

    public static void CmdBindVertexBuffers(Vk vk, CommandBuffer command, uint first, uint count, VkBuffer* buffers, ulong* offsets)
    {
        NativeBindingEpoch.Advance(); // [local]
        if (Ring(command) is { } ring)
        {
            var payloadSize = sizeof(RingArgs.Range) + (int)count * (sizeof(VkBuffer) + sizeof(ulong));
            if (Fits(payloadSize))
            {
                var payload = ring.Reserve(RingOp.BindVertexBuffers, payloadSize);
                var args = (RingArgs.Range*)payload;
                args->First = first;
                args->Count = count;
                var target = (VkBuffer*)(payload + sizeof(RingArgs.Range));
                Unsafe.CopyBlockUnaligned(target, buffers, count * (uint)sizeof(VkBuffer));
                Unsafe.CopyBlockUnaligned(target + count, offsets, count * sizeof(ulong));
                ring.Commit();
                return;
            }

            Fallback(ring, RingOp.BindVertexBuffers);
        }

        vk.CmdBindVertexBuffers(command, first, count, buffers, offsets);
    }

    public static void CmdBindIndexBuffer(Vk vk, CommandBuffer command, VkBuffer buffer, ulong offset, IndexType type)
    {
        NativeBindingEpoch.Advance(); // [local]
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.IndexBuffer*)ring.Reserve(RingOp.BindIndexBuffer, sizeof(RingArgs.IndexBuffer));
            args->Buffer = buffer;
            args->Offset = offset;
            args->Type = type;
            ring.Commit();
            return;
        }

        vk.CmdBindIndexBuffer(command, buffer, offset, type);
    }

    public static void CmdDraw(Vk vk, CommandBuffer command, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteOp(command, VulkanCommandProfile.OpBit.Draw); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Four*)ring.Reserve(RingOp.Draw, sizeof(RingArgs.Four));
            args->A = vertexCount;
            args->B = instanceCount;
            args->C = firstVertex;
            args->D = firstInstance;
            ring.Commit();
            return;
        }

        vk.CmdDraw(command, vertexCount, instanceCount, firstVertex, firstInstance);
    }

    public static void CmdDrawIndexed(Vk vk, CommandBuffer command, uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteOp(command, VulkanCommandProfile.OpBit.Draw); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Four*)ring.Reserve(RingOp.DrawIndexed, sizeof(RingArgs.Four));
            args->A = indexCount;
            args->B = instanceCount;
            args->C = firstIndex;
            args->D = (uint)vertexOffset;
            args->E = firstInstance;
            ring.Commit();
            return;
        }

        vk.CmdDrawIndexed(command, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    public static void CmdDrawIndexedIndirect(Vk vk, CommandBuffer command, VkBuffer buffer, ulong offset, uint drawCount, uint stride)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteOp(command, VulkanCommandProfile.OpBit.Draw); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Indirect*)ring.Reserve(RingOp.DrawIndexedIndirect, sizeof(RingArgs.Indirect));
            args->Buffer = buffer;
            args->Offset = offset;
            args->DrawCount = drawCount;
            args->Stride = stride;
            ring.Commit();
            return;
        }

        vk.CmdDrawIndexedIndirect(command, buffer, offset, drawCount, stride);
    }

    public static void CmdDispatch(Vk vk, CommandBuffer command, uint x, uint y, uint z)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteOp(command, VulkanCommandProfile.OpBit.Dispatch); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Four*)ring.Reserve(RingOp.Dispatch, sizeof(RingArgs.Four));
            args->A = x;
            args->B = y;
            args->C = z;
            ring.Commit();
            return;
        }

        vk.CmdDispatch(command, x, y, z);
    }

    public static void CmdDispatchIndirect(Vk vk, CommandBuffer command, VkBuffer buffer, ulong offset)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteOp(command, VulkanCommandProfile.OpBit.Dispatch); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Indirect*)ring.Reserve(RingOp.DispatchIndirect, sizeof(RingArgs.Indirect));
            args->Buffer = buffer;
            args->Offset = offset;
            ring.Commit();
            return;
        }

        vk.CmdDispatchIndirect(command, buffer, offset);
    }

    public static void CmdSetViewport(Vk vk, CommandBuffer command, uint first, uint count, Viewport* viewports)
    {
        if (Ring(command) is { } ring && Fits(sizeof(RingArgs.Range) + (int)count * sizeof(Viewport)))
        {
            var payload = ring.Reserve(RingOp.SetViewport, sizeof(RingArgs.Range) + (int)count * sizeof(Viewport));
            ((RingArgs.Range*)payload)->First = first;
            ((RingArgs.Range*)payload)->Count = count;
            Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.Range), viewports, count * (uint)sizeof(Viewport));
            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.SetViewport);
        vk.CmdSetViewport(command, first, count, viewports);
    }

    public static void CmdSetScissor(Vk vk, CommandBuffer command, uint first, uint count, Rect2D* scissors)
    {
        if (Ring(command) is { } ring && Fits(sizeof(RingArgs.Range) + (int)count * sizeof(Rect2D)))
        {
            var payload = ring.Reserve(RingOp.SetScissor, sizeof(RingArgs.Range) + (int)count * sizeof(Rect2D));
            ((RingArgs.Range*)payload)->First = first;
            ((RingArgs.Range*)payload)->Count = count;
            Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.Range), scissors, count * (uint)sizeof(Rect2D));
            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.SetScissor);
        vk.CmdSetScissor(command, first, count, scissors);
    }

    private static void DrainIfRecorded(CommandBuffer command, RingOp op)
    {
        if (Ring(command) is { } ring)
        {
            Fallback(ring, op);
        }
    }

    public static void CmdSetLineWidth(Vk vk, CommandBuffer command, float width)
    {
        if (Ring(command) is { } ring)
        {
            ((RingArgs.Floats*)ring.Reserve(RingOp.SetLineWidth, sizeof(RingArgs.Floats)))->A = width;
            ring.Commit();
            return;
        }

        vk.CmdSetLineWidth(command, width);
    }

    public static void CmdSetBlendConstants(Vk vk, CommandBuffer command, float* constants)
    {
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Floats*)ring.Reserve(RingOp.SetBlendConstants, sizeof(RingArgs.Floats));
            args->A = constants[0];
            args->B = constants[1];
            args->C = constants[2];
            args->D = constants[3];
            ring.Commit();
            return;
        }

        vk.CmdSetBlendConstants(command, constants);
    }

    private static void PutUInt(CommandRing ring, RingOp op, uint value)
    {
        *(uint*)ring.Reserve(op, sizeof(uint)) = value;
        ring.Commit();
    }

    public static void CmdSetDepthTestEnable(Vk vk, CommandBuffer command, Bool32 enable)
    {
        if (Ring(command) is { } ring)
        {
            PutUInt(ring, RingOp.SetDepthTestEnable, enable.Value);
            return;
        }

        vk.CmdSetDepthTestEnable(command, enable);
    }

    public static void CmdSetDepthWriteEnable(Vk vk, CommandBuffer command, Bool32 enable)
    {
        if (Ring(command) is { } ring)
        {
            PutUInt(ring, RingOp.SetDepthWriteEnable, enable.Value);
            return;
        }

        vk.CmdSetDepthWriteEnable(command, enable);
    }

    public static void CmdSetDepthCompareOp(Vk vk, CommandBuffer command, CompareOp compare)
    {
        if (Ring(command) is { } ring)
        {
            PutUInt(ring, RingOp.SetDepthCompareOp, (uint)compare);
            return;
        }

        vk.CmdSetDepthCompareOp(command, compare);
    }

    public static void CmdSetDepthBiasEnable(Vk vk, CommandBuffer command, Bool32 enable)
    {
        if (Ring(command) is { } ring)
        {
            PutUInt(ring, RingOp.SetDepthBiasEnable, enable.Value);
            return;
        }

        vk.CmdSetDepthBiasEnable(command, enable);
    }

    public static void CmdSetDepthBias(Vk vk, CommandBuffer command, float constant, float clamp, float slope)
    {
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Floats*)ring.Reserve(RingOp.SetDepthBias, sizeof(RingArgs.Floats));
            args->A = constant;
            args->B = clamp;
            args->C = slope;
            ring.Commit();
            return;
        }

        vk.CmdSetDepthBias(command, constant, clamp, slope);
    }

    public static void CmdSetDepthBounds(Vk vk, CommandBuffer command, float min, float max)
    {
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Floats*)ring.Reserve(RingOp.SetDepthBounds, sizeof(RingArgs.Floats));
            args->A = min;
            args->B = max;
            ring.Commit();
            return;
        }

        vk.CmdSetDepthBounds(command, min, max);
    }

    private static void PutStencil(CommandRing ring, RingOp op, StencilFaceFlags faces, uint value)
    {
        var args = (RingArgs.Four*)ring.Reserve(op, sizeof(RingArgs.Four));
        args->A = (uint)faces;
        args->B = value;
        ring.Commit();
    }

    public static void CmdSetStencilCompareMask(Vk vk, CommandBuffer command, StencilFaceFlags faces, uint mask)
    {
        if (Ring(command) is { } ring)
        {
            PutStencil(ring, RingOp.SetStencilCompareMask, faces, mask);
            return;
        }

        vk.CmdSetStencilCompareMask(command, faces, mask);
    }

    public static void CmdSetStencilWriteMask(Vk vk, CommandBuffer command, StencilFaceFlags faces, uint mask)
    {
        if (Ring(command) is { } ring)
        {
            PutStencil(ring, RingOp.SetStencilWriteMask, faces, mask);
            return;
        }

        vk.CmdSetStencilWriteMask(command, faces, mask);
    }

    public static void CmdSetStencilReference(Vk vk, CommandBuffer command, StencilFaceFlags faces, uint reference)
    {
        if (Ring(command) is { } ring)
        {
            PutStencil(ring, RingOp.SetStencilReference, faces, reference);
            return;
        }

        vk.CmdSetStencilReference(command, faces, reference);
    }

    public static void CmdSetColorWriteEnable(ExtColorWriteEnable api, CommandBuffer command, uint count, Bool32* enables)
    {
        if (Ring(command) is { } ring && Fits(8 + (int)count * sizeof(Bool32)))
        {
            ring.ColorWriteApi ??= api;
            var payload = ring.Reserve(RingOp.SetColorWriteEnable, 8 + (int)count * sizeof(Bool32));
            *(uint*)payload = count;
            Unsafe.CopyBlockUnaligned(payload + 8, enables, count * (uint)sizeof(Bool32));
            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.SetColorWriteEnable);
        api.CmdSetColorWriteEnable(command, count, enables);
    }

    public static void CmdBeginRendering(Vk vk, CommandBuffer command, RenderingInfo* info)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteRendering(command, info); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            var colorCount = (int)info->ColorAttachmentCount;
            var payloadSize = sizeof(RingArgs.Rendering) + (colorCount + 2) * sizeof(RenderingAttachmentInfo);
            if (info->PNext == null && Fits(payloadSize) && AttachmentsHaveNoChain(info))
            {
                var payload = ring.Reserve(RingOp.BeginRendering, payloadSize);
                var args = (RingArgs.Rendering*)payload;
                args->Info = *info;
                args->HasDepth = info->PDepthAttachment != null ? 1u : 0u;
                args->HasStencil = info->PStencilAttachment != null ? 1u : 0u;
                var colors = (RenderingAttachmentInfo*)(payload + sizeof(RingArgs.Rendering));
                if (colorCount != 0)
                {
                    Unsafe.CopyBlockUnaligned(colors, info->PColorAttachments, (uint)(colorCount * sizeof(RenderingAttachmentInfo)));
                }

                if (info->PDepthAttachment != null)
                {
                    colors[colorCount] = *info->PDepthAttachment;
                }

                if (info->PStencilAttachment != null)
                {
                    colors[colorCount + 1] = *info->PStencilAttachment;
                }

                ring.Commit();
                return;
            }

            Fallback(ring, RingOp.BeginRendering);
        }

        vk.CmdBeginRendering(command, info);
    }

    private static bool AttachmentsHaveNoChain(RenderingInfo* info)
    {
        for (var index = 0u; index < info->ColorAttachmentCount; index++)
        {
            if (info->PColorAttachments[index].PNext != null)
            {
                return false;
            }
        }

        return (info->PDepthAttachment == null || info->PDepthAttachment->PNext == null) &&
            (info->PStencilAttachment == null || info->PStencilAttachment->PNext == null);
    }

    public static void CmdEndRendering(Vk vk, CommandBuffer command)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteEndRendering(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            ring.Reserve(RingOp.EndRendering, 0);
            ring.Commit();
            return;
        }

        vk.CmdEndRendering(command);
    }

    public static void CmdPushConstants(Vk vk, CommandBuffer command, PipelineLayout layout, ShaderStageFlags stages, uint offset, uint size, void* values)
    {
        NativeBindingEpoch.Advance(); // [local]
        if (Ring(command) is { } ring && Fits(sizeof(RingArgs.PushConstants) + (int)size))
        {
            var payload = ring.Reserve(RingOp.PushConstants, sizeof(RingArgs.PushConstants) + (int)size);
            var args = (RingArgs.PushConstants*)payload;
            args->Layout = layout;
            args->Stages = stages;
            args->Offset = offset;
            args->Size = size;
            Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.PushConstants), values, size);
            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.PushConstants);
        vk.CmdPushConstants(command, layout, stages, offset, size, values);
    }

    private enum DescriptorArray
    {
        Image,
        Buffer,
        TexelView,
        Unsupported,
    }

    private static DescriptorArray ArrayOf(DescriptorType type) => type switch
    {
        DescriptorType.Sampler or DescriptorType.CombinedImageSampler or DescriptorType.SampledImage or
            DescriptorType.StorageImage or DescriptorType.InputAttachment => DescriptorArray.Image,
        DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or DescriptorType.UniformBufferDynamic or
            DescriptorType.StorageBufferDynamic => DescriptorArray.Buffer,
        DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer => DescriptorArray.TexelView,
        _ => DescriptorArray.Unsupported,
    };

    public static void CmdPushDescriptorSet(KhrPushDescriptor api, CommandBuffer command, PipelineBindPoint bindPoint, PipelineLayout layout,
        uint set, uint count, WriteDescriptorSet* writes)
    {
        NativeBindingEpoch.Advance(); // [local]
        if (Ring(command) is { } ring)
        {
            var payloadSize = sizeof(RingArgs.DescriptorSets) + (int)count * sizeof(WriteDescriptorSet);
            var supported = true;
            for (var index = 0u; index < count && supported; index++)
            {
                var write = writes + index;
                var kind = ArrayOf(write->DescriptorType);
                payloadSize += kind switch
                {
                    DescriptorArray.Image => (int)write->DescriptorCount * sizeof(DescriptorImageInfo),
                    DescriptorArray.Buffer => (int)write->DescriptorCount * sizeof(DescriptorBufferInfo),
                    DescriptorArray.TexelView => (int)write->DescriptorCount * sizeof(BufferView),
                    _ => 0,
                };
                supported = kind != DescriptorArray.Unsupported && write->PNext == null && kind switch
                {
                    DescriptorArray.Image => write->PImageInfo != null,
                    DescriptorArray.Buffer => write->PBufferInfo != null,
                    _ => write->PTexelBufferView != null,
                };
            }

            if (supported && Fits(payloadSize))
            {
                ring.PushApi ??= api;
                var payload = ring.Reserve(RingOp.PushDescriptorSet, payloadSize);
                var args = (RingArgs.DescriptorSets*)payload;
                args->Layout = layout;
                args->BindPoint = bindPoint;
                args->Set = set;
                args->Count = count;
                var copies = (WriteDescriptorSet*)(payload + sizeof(RingArgs.DescriptorSets));
                var cursor = (byte*)(copies + count);
                for (var index = 0u; index < count; index++)
                {
                    var write = writes[index];
                    var offset = (nint)(cursor - payload);
                    uint bytes;
                    switch (ArrayOf(write.DescriptorType))
                    {
                        case DescriptorArray.Image:
                            bytes = write.DescriptorCount * (uint)sizeof(DescriptorImageInfo);
                            Unsafe.CopyBlockUnaligned(cursor, write.PImageInfo, bytes);
                            write.PImageInfo = (DescriptorImageInfo*)offset;
                            write.PBufferInfo = null;
                            write.PTexelBufferView = null;
                            break;
                        case DescriptorArray.Buffer:
                            bytes = write.DescriptorCount * (uint)sizeof(DescriptorBufferInfo);
                            Unsafe.CopyBlockUnaligned(cursor, write.PBufferInfo, bytes);
                            write.PBufferInfo = (DescriptorBufferInfo*)offset;
                            write.PImageInfo = null;
                            write.PTexelBufferView = null;
                            break;
                        default:
                            bytes = write.DescriptorCount * (uint)sizeof(BufferView);
                            Unsafe.CopyBlockUnaligned(cursor, write.PTexelBufferView, bytes);
                            write.PTexelBufferView = (BufferView*)offset;
                            write.PImageInfo = null;
                            write.PBufferInfo = null;
                            break;
                    }

                    copies[index] = write;
                    cursor += bytes;
                }

                ring.Commit();
                return;
            }

            Fallback(ring, RingOp.PushDescriptorSet);
        }

        api.CmdPushDescriptorSet(command, bindPoint, layout, set, count, writes);
    }

    public static void CmdBindDescriptorSets(Vk vk, CommandBuffer command, PipelineBindPoint bindPoint, PipelineLayout layout, uint first,
        uint count, DescriptorSet* sets, uint dynamicCount, uint* dynamicOffsets)
    {
        NativeBindingEpoch.Advance(); // [local]
        var payloadSize = sizeof(RingArgs.DescriptorSets) + (int)count * sizeof(DescriptorSet) + (int)dynamicCount * sizeof(uint);
        if (Ring(command) is { } ring && Fits(payloadSize))
        {
            var payload = ring.Reserve(RingOp.BindDescriptorSets, payloadSize);
            var args = (RingArgs.DescriptorSets*)payload;
            args->Layout = layout;
            args->BindPoint = bindPoint;
            args->Set = first;
            args->Count = count;
            args->DynamicCount = dynamicCount;
            var target = (DescriptorSet*)(payload + sizeof(RingArgs.DescriptorSets));
            Unsafe.CopyBlockUnaligned(target, sets, count * (uint)sizeof(DescriptorSet));
            if (dynamicCount != 0)
            {
                Unsafe.CopyBlockUnaligned(target + count, dynamicOffsets, dynamicCount * sizeof(uint));
            }

            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.BindDescriptorSets);
        vk.CmdBindDescriptorSets(command, bindPoint, layout, first, count, sets, dynamicCount, dynamicOffsets);
    }

    public static void CmdCopyBuffer(Vk vk, CommandBuffer command, VkBuffer source, VkBuffer destination, uint count, BufferCopy* regions)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdCopyBufferCore(vk, command, source, destination, count, regions);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.CopyBuffer);
    }

    private static void CmdCopyBufferCore(Vk vk, CommandBuffer command, VkBuffer source, VkBuffer destination, uint count, BufferCopy* regions)
    {
        var payloadSize = sizeof(RingArgs.CopyBuffer) + (int)count * sizeof(BufferCopy);
        if (Ring(command) is { } ring && Fits(payloadSize))
        {
            var payload = ring.Reserve(RingOp.CopyBuffer, payloadSize);
            var args = (RingArgs.CopyBuffer*)payload;
            args->Source = source;
            args->Destination = destination;
            args->Count = count;
            Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.CopyBuffer), regions, count * (uint)sizeof(BufferCopy));
            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.CopyBuffer);
        vk.CmdCopyBuffer(command, source, destination, count, regions);
    }

    public static void CmdFillBuffer(Vk vk, CommandBuffer command, VkBuffer destination, ulong offset, ulong size, uint data)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdFillBufferCore(vk, command, destination, offset, size, data);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.FillBuffer);
    }

    private static void CmdFillBufferCore(Vk vk, CommandBuffer command, VkBuffer destination, ulong offset, ulong size, uint data)
    {
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.FillBuffer*)ring.Reserve(RingOp.FillBuffer, sizeof(RingArgs.FillBuffer));
            args->Destination = destination;
            args->Offset = offset;
            args->Size = size;
            args->Data = data;
            ring.Commit();
            return;
        }

        vk.CmdFillBuffer(command, destination, offset, size, data);
    }

    public static void CmdUpdateBuffer(Vk vk, CommandBuffer command, VkBuffer destination, ulong offset, ulong size, void* data)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdUpdateBufferCore(vk, command, destination, offset, size, data);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.FillBuffer);
    }

    private static void CmdUpdateBufferCore(Vk vk, CommandBuffer command, VkBuffer destination, ulong offset, ulong size, void* data)
    {
        if (Ring(command) is { } ring && size <= (ulong)CommandRing.MaxPayload - (ulong)sizeof(RingArgs.FillBuffer))
        {
            var payload = ring.Reserve(RingOp.UpdateBuffer, sizeof(RingArgs.FillBuffer) + (int)size);
            var args = (RingArgs.FillBuffer*)payload;
            args->Destination = destination;
            args->Offset = offset;
            args->Size = size;
            Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.FillBuffer), data, (uint)size);
            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.UpdateBuffer);
        vk.CmdUpdateBuffer(command, destination, offset, size, data);
    }

    private static bool PutImageToImage<T>(CommandBuffer command, RingOp op, Image source, ImageLayout sourceLayout, Image destination,
        ImageLayout destinationLayout, uint count, T* regions, Filter filter) where T : unmanaged
    {
        var payloadSize = sizeof(RingArgs.ImageToImage) + (int)count * sizeof(T);
        if (Ring(command) is not { } ring)
        {
            return false;
        }

        if (!Fits(payloadSize))
        {
            Fallback(ring, op);
            return false;
        }

        var payload = ring.Reserve(op, payloadSize);
        var args = (RingArgs.ImageToImage*)payload;
        args->Source = source;
        args->SourceLayout = sourceLayout;
        args->Destination = destination;
        args->DestinationLayout = destinationLayout;
        args->Count = count;
        args->Filter = filter;
        Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.ImageToImage), regions, count * (uint)sizeof(T));
        ring.Commit();
        return true;
    }

    public static void CmdCopyImage(Vk vk, CommandBuffer command, Image source, ImageLayout sourceLayout, Image destination,
        ImageLayout destinationLayout, uint count, ImageCopy* regions)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdCopyImageCore(vk, command, source, sourceLayout, destination, destinationLayout, count, regions);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.CopyImage);
    }

    private static void CmdCopyImageCore(Vk vk, CommandBuffer command, Image source, ImageLayout sourceLayout, Image destination,
        ImageLayout destinationLayout, uint count, ImageCopy* regions)
    {
        if (!PutImageToImage(command, RingOp.CopyImage, source, sourceLayout, destination, destinationLayout, count, regions, default))
        {
            vk.CmdCopyImage(command, source, sourceLayout, destination, destinationLayout, count, regions);
        }
    }

    public static void CmdBlitImage(Vk vk, CommandBuffer command, Image source, ImageLayout sourceLayout, Image destination,
        ImageLayout destinationLayout, uint count, ImageBlit* regions, Filter filter)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdBlitImageCore(vk, command, source, sourceLayout, destination, destinationLayout, count, regions, filter);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.Blit);
    }

    private static void CmdBlitImageCore(Vk vk, CommandBuffer command, Image source, ImageLayout sourceLayout, Image destination,
        ImageLayout destinationLayout, uint count, ImageBlit* regions, Filter filter)
    {
        if (!PutImageToImage(command, RingOp.BlitImage, source, sourceLayout, destination, destinationLayout, count, regions, filter))
        {
            vk.CmdBlitImage(command, source, sourceLayout, destination, destinationLayout, count, regions, filter);
        }
    }

    public static void CmdResolveImage(Vk vk, CommandBuffer command, Image source, ImageLayout sourceLayout, Image destination,
        ImageLayout destinationLayout, uint count, ImageResolve* regions)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdResolveImageCore(vk, command, source, sourceLayout, destination, destinationLayout, count, regions);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.Resolve);
    }

    private static void CmdResolveImageCore(Vk vk, CommandBuffer command, Image source, ImageLayout sourceLayout, Image destination,
        ImageLayout destinationLayout, uint count, ImageResolve* regions)
    {
        if (!PutImageToImage(command, RingOp.ResolveImage, source, sourceLayout, destination, destinationLayout, count, regions, default))
        {
            vk.CmdResolveImage(command, source, sourceLayout, destination, destinationLayout, count, regions);
        }
    }

    private static bool PutBufferImage(CommandBuffer command, RingOp op, VkBuffer buffer, Image image, ImageLayout layout, uint count,
        BufferImageCopy* regions)
    {
        var payloadSize = sizeof(RingArgs.BufferImage) + (int)count * sizeof(BufferImageCopy);
        if (Ring(command) is not { } ring)
        {
            return false;
        }

        if (!Fits(payloadSize))
        {
            Fallback(ring, op);
            return false;
        }

        var payload = ring.Reserve(op, payloadSize);
        var args = (RingArgs.BufferImage*)payload;
        args->Buffer = buffer;
        args->Image = image;
        args->Layout = layout;
        args->Count = count;
        Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.BufferImage), regions, count * (uint)sizeof(BufferImageCopy));
        ring.Commit();
        return true;
    }

    public static void CmdCopyBufferToImage(Vk vk, CommandBuffer command, VkBuffer buffer, Image image, ImageLayout layout, uint count,
        BufferImageCopy* regions)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdCopyBufferToImageCore(vk, command, buffer, image, layout, count, regions);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.Upload);
    }

    private static void CmdCopyBufferToImageCore(Vk vk, CommandBuffer command, VkBuffer buffer, Image image, ImageLayout layout, uint count,
        BufferImageCopy* regions)
    {
        if (!PutBufferImage(command, RingOp.CopyBufferToImage, buffer, image, layout, count, regions))
        {
            vk.CmdCopyBufferToImage(command, buffer, image, layout, count, regions);
        }
    }

    public static void CmdCopyImageToBuffer(Vk vk, CommandBuffer command, Image image, ImageLayout layout, VkBuffer buffer, uint count,
        BufferImageCopy* regions)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdCopyImageToBufferCore(vk, command, image, layout, buffer, count, regions);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.Readback);
    }

    private static void CmdCopyImageToBufferCore(Vk vk, CommandBuffer command, Image image, ImageLayout layout, VkBuffer buffer, uint count,
        BufferImageCopy* regions)
    {
        if (!PutBufferImage(command, RingOp.CopyImageToBuffer, buffer, image, layout, count, regions))
        {
            vk.CmdCopyImageToBuffer(command, image, layout, buffer, count, regions);
        }
    }

    public static void CmdClearColorImage(Vk vk, CommandBuffer command, Image image, ImageLayout layout, ClearColorValue* color, uint count,
        ImageSubresourceRange* ranges)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdClearColorImageCore(vk, command, image, layout, color, count, ranges);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.Clear);
    }

    private static void CmdClearColorImageCore(Vk vk, CommandBuffer command, Image image, ImageLayout layout, ClearColorValue* color, uint count,
        ImageSubresourceRange* ranges)
    {
        var payloadSize = sizeof(RingArgs.ClearColor) + (int)count * sizeof(ImageSubresourceRange);
        if (Ring(command) is { } ring && Fits(payloadSize))
        {
            var payload = ring.Reserve(RingOp.ClearColorImage, payloadSize);
            var args = (RingArgs.ClearColor*)payload;
            args->Image = image;
            args->Layout = layout;
            args->Count = count;
            args->Value = *color;
            Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.ClearColor), ranges, count * (uint)sizeof(ImageSubresourceRange));
            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.ClearColorImage);
        vk.CmdClearColorImage(command, image, layout, color, count, ranges);
    }

    public static void CmdClearDepthStencilImage(Vk vk, CommandBuffer command, Image image, ImageLayout layout, ClearDepthStencilValue* value,
        uint count, ImageSubresourceRange* ranges)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.BeforeTransfer(command); // [local] SHARPEMU_PROFILE_GPU_PASSES
        CmdClearDepthStencilImageCore(vk, command, image, layout, value, count, ranges);
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.AfterTransfer(command, VulkanCommandProfile.IntervalKind.Clear);
    }

    private static void CmdClearDepthStencilImageCore(Vk vk, CommandBuffer command, Image image, ImageLayout layout, ClearDepthStencilValue* value,
        uint count, ImageSubresourceRange* ranges)
    {
        var payloadSize = sizeof(RingArgs.ClearDepth) + (int)count * sizeof(ImageSubresourceRange);
        if (Ring(command) is { } ring && Fits(payloadSize))
        {
            var payload = ring.Reserve(RingOp.ClearDepthStencilImage, payloadSize);
            var args = (RingArgs.ClearDepth*)payload;
            args->Image = image;
            args->Layout = layout;
            args->Count = count;
            args->Value = *value;
            Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.ClearDepth), ranges, count * (uint)sizeof(ImageSubresourceRange));
            ring.Commit();
            return;
        }

        DrainIfRecorded(command, RingOp.ClearDepthStencilImage);
        vk.CmdClearDepthStencilImage(command, image, layout, value, count, ranges);
    }

    public static void CmdBeginRenderPass(Vk vk, CommandBuffer command, RenderPassBeginInfo* info, SubpassContents contents)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteOp(command, VulkanCommandProfile.OpBit.RenderPass); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            var payloadSize = sizeof(RingArgs.RenderPass) + (int)info->ClearValueCount * sizeof(ClearValue);
            if (info->PNext == null && Fits(payloadSize))
            {
                var payload = ring.Reserve(RingOp.BeginRenderPass, payloadSize);
                var args = (RingArgs.RenderPass*)payload;
                args->Info = *info;
                args->Contents = contents;
                if (info->ClearValueCount != 0)
                {
                    Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.RenderPass), info->PClearValues, info->ClearValueCount * (uint)sizeof(ClearValue));
                }

                ring.Commit();
                return;
            }

            Fallback(ring, RingOp.BeginRenderPass);
        }

        vk.CmdBeginRenderPass(command, info, contents);
    }

    public static void CmdEndRenderPass(Vk vk, CommandBuffer command)
    {
        if (VulkanCommandProfile.PassesEnabled) VulkanCommandProfile.NoteOp(command, VulkanCommandProfile.OpBit.RenderPass); // [local] SHARPEMU_PROFILE_GPU_PASSES
        if (Ring(command) is { } ring)
        {
            ring.Reserve(RingOp.EndRenderPass, 0);
            ring.Commit();
            return;
        }

        vk.CmdEndRenderPass(command);
    }

    public static void CmdResetQueryPool(Vk vk, CommandBuffer command, QueryPool pool, uint first, uint count)
    {
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Query*)ring.Reserve(RingOp.ResetQueryPool, sizeof(RingArgs.Query));
            args->Pool = pool;
            args->First = first;
            args->Count = count;
            ring.Commit();
            return;
        }

        vk.CmdResetQueryPool(command, pool, first, count);
    }

    // VK_EXT_debug_utils labels, called through the device's function pointers.
    public static void CmdBeginDebugUtilsLabel(delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void> function, CommandBuffer command,
        DebugUtilsLabelEXT* label)
    {
        if (Ring(command) is { } ring)
        {
            var length = 0;
            while (label->PLabelName[length] != 0)
            {
                length++;
            }

            var payloadSize = sizeof(RingArgs.DebugLabel) + length + 1;
            if (label->PNext == null && Fits(payloadSize))
            {
                var payload = ring.Reserve(RingOp.BeginDebugLabel, payloadSize);
                var args = (RingArgs.DebugLabel*)payload;
                args->Function = (nint)function;
                args->Label = *label;
                Unsafe.CopyBlockUnaligned(payload + sizeof(RingArgs.DebugLabel), label->PLabelName, (uint)length + 1);
                ring.Commit();
                return;
            }

            Fallback(ring, RingOp.BeginDebugLabel);
        }

        function(command, label);
    }

    public static void CmdEndDebugUtilsLabel(delegate* unmanaged<CommandBuffer, void> function, CommandBuffer command)
    {
        if (Ring(command) is { } ring)
        {
            *(nint*)ring.Reserve(RingOp.EndDebugLabel, sizeof(nint)) = (nint)function;
            ring.Commit();
            return;
        }

        function(command);
    }

    public static void CmdWriteTimestamp2(Vk vk, CommandBuffer command, PipelineStageFlags2 stage, QueryPool pool, uint query)
    {
        if (Ring(command) is { } ring)
        {
            var args = (RingArgs.Query*)ring.Reserve(RingOp.WriteTimestamp2, sizeof(RingArgs.Query));
            args->Pool = pool;
            args->Stage = stage;
            args->First = query;
            ring.Commit();
            return;
        }

        vk.CmdWriteTimestamp2(command, stage, pool, query);
    }
}
