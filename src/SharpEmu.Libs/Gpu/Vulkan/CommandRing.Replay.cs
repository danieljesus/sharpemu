// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] Record kinds of the linear recording ring. Payloads are blittable copies of the call's arguments; arrays
// follow the fixed part and the replay points the native structures at them inside the block.
internal enum RingOp : ushort
{
    None,
    NextBlock,
    Begin,
    End,
    Barrier,
    BindPipeline,
    BindVertexBuffers,
    BindIndexBuffer,
    Draw,
    DrawIndexed,
    DrawIndexedIndirect,
    Dispatch,
    DispatchIndirect,
    SetViewport,
    SetScissor,
    SetLineWidth,
    SetBlendConstants,
    SetDepthTestEnable,
    SetDepthWriteEnable,
    SetDepthCompareOp,
    SetDepthBiasEnable,
    SetDepthBias,
    SetStencilCompareMask,
    SetStencilWriteMask,
    SetStencilReference,
    SetColorWriteEnable,
    BeginRendering,
    EndRendering,
    PushConstants,
    PushDescriptorSet,
    BindDescriptorSets,
    CopyBuffer,
    FillBuffer,
    UpdateBuffer,
    CopyImage,
    BlitImage,
    ResolveImage,
    CopyBufferToImage,
    CopyImageToBuffer,
    ClearColorImage,
    ClearDepthStencilImage,
    BeginRenderPass,
    EndRenderPass,
    ResetQueryPool,
    WriteTimestamp2,
    BeginDebugLabel,
    EndDebugLabel,
    SetDepthBounds,
    Count,
}

internal static class RingArgs
{
    [StructLayout(LayoutKind.Sequential)]
    public struct BarrierHead
    {
        public DependencyFlags Flags;
        public uint Memory;
        public uint Buffer;
        public uint Image;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BindPipeline
    {
        public PipelineBindPoint BindPoint;
        public Pipeline Pipeline;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Range
    {
        public uint First;
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IndexBuffer
    {
        public VkBuffer Buffer;
        public ulong Offset;
        public IndexType Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Four
    {
        public uint A;
        public uint B;
        public uint C;
        public uint D;
        public uint E;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Indirect
    {
        public VkBuffer Buffer;
        public ulong Offset;
        public uint DrawCount;
        public uint Stride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Floats
    {
        public float A;
        public float B;
        public float C;
        public float D;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rendering
    {
        public RenderingInfo Info;
        public uint HasDepth;
        public uint HasStencil;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PushConstants
    {
        public PipelineLayout Layout;
        public ShaderStageFlags Stages;
        public uint Offset;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DescriptorSets
    {
        public PipelineLayout Layout;
        public PipelineBindPoint BindPoint;
        public uint Set;
        public uint Count;
        public uint DynamicCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CopyBuffer
    {
        public VkBuffer Source;
        public VkBuffer Destination;
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FillBuffer
    {
        public VkBuffer Destination;
        public ulong Offset;
        public ulong Size;
        public uint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ImageToImage
    {
        public Image Source;
        public Image Destination;
        public ImageLayout SourceLayout;
        public ImageLayout DestinationLayout;
        public uint Count;
        public Filter Filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferImage
    {
        public VkBuffer Buffer;
        public Image Image;
        public ImageLayout Layout;
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClearColor
    {
        public Image Image;
        public ImageLayout Layout;
        public uint Count;
        public ClearColorValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ClearDepth
    {
        public Image Image;
        public ImageLayout Layout;
        public uint Count;
        public ClearDepthStencilValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderPass
    {
        public RenderPassBeginInfo Info;
        public SubpassContents Contents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DebugLabel
    {
        public nint Function;
        public DebugUtilsLabelEXT Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Query
    {
        public QueryPool Pool;
        public PipelineStageFlags2 Stage;
        public uint First;
        public uint Count;
    }
}

internal sealed unsafe partial class CommandRing
{
    private CommandBuffer Current => new(_current);

    private void Execute(RingOp op, byte* payload)
    {
        var vk = _vk;
        var command = Current;
        switch (op)
        {
            case RingOp.Begin:
            {
                _current = *(nint*)payload;
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Require(vk.BeginCommandBuffer(new CommandBuffer(_current), &beginInfo), "vkBeginCommandBuffer(ring)");
                break;
            }
            case RingOp.End:
                Require(vk.EndCommandBuffer(new CommandBuffer(*(nint*)payload)), "vkEndCommandBuffer(ring)");
                _current = 0;
                break;
            case RingOp.Barrier:
                ExecuteBarrier(command, payload);
                break;
            case RingOp.BindPipeline:
            {
                var args = (RingArgs.BindPipeline*)payload;
                vk.CmdBindPipeline(command, args->BindPoint, args->Pipeline);
                break;
            }
            case RingOp.BindVertexBuffers:
            {
                var args = (RingArgs.Range*)payload;
                var buffers = (VkBuffer*)(payload + sizeof(RingArgs.Range));
                vk.CmdBindVertexBuffers(command, args->First, args->Count, buffers, (ulong*)(buffers + args->Count));
                break;
            }
            case RingOp.BindIndexBuffer:
            {
                var args = (RingArgs.IndexBuffer*)payload;
                vk.CmdBindIndexBuffer(command, args->Buffer, args->Offset, args->Type);
                break;
            }
            case RingOp.Draw:
            {
                var args = (RingArgs.Four*)payload;
                vk.CmdDraw(command, args->A, args->B, args->C, args->D);
                break;
            }
            case RingOp.DrawIndexed:
            {
                var args = (RingArgs.Four*)payload;
                vk.CmdDrawIndexed(command, args->A, args->B, args->C, (int)args->D, args->E);
                break;
            }
            case RingOp.DrawIndexedIndirect:
            {
                var args = (RingArgs.Indirect*)payload;
                vk.CmdDrawIndexedIndirect(command, args->Buffer, args->Offset, args->DrawCount, args->Stride);
                break;
            }
            case RingOp.Dispatch:
            {
                var args = (RingArgs.Four*)payload;
                vk.CmdDispatch(command, args->A, args->B, args->C);
                break;
            }
            case RingOp.DispatchIndirect:
            {
                var args = (RingArgs.Indirect*)payload;
                vk.CmdDispatchIndirect(command, args->Buffer, args->Offset);
                break;
            }
            case RingOp.SetViewport:
            {
                var args = (RingArgs.Range*)payload;
                vk.CmdSetViewport(command, args->First, args->Count, (Viewport*)(payload + sizeof(RingArgs.Range)));
                break;
            }
            case RingOp.SetScissor:
            {
                var args = (RingArgs.Range*)payload;
                vk.CmdSetScissor(command, args->First, args->Count, (Rect2D*)(payload + sizeof(RingArgs.Range)));
                break;
            }
            case RingOp.SetLineWidth:
                vk.CmdSetLineWidth(command, ((RingArgs.Floats*)payload)->A);
                break;
            case RingOp.SetBlendConstants:
                vk.CmdSetBlendConstants(command, (float*)payload);
                break;
            case RingOp.SetDepthTestEnable:
                vk.CmdSetDepthTestEnable(command, new Bool32(*(uint*)payload));
                break;
            case RingOp.SetDepthWriteEnable:
                vk.CmdSetDepthWriteEnable(command, new Bool32(*(uint*)payload));
                break;
            case RingOp.SetDepthCompareOp:
                vk.CmdSetDepthCompareOp(command, *(CompareOp*)payload);
                break;
            case RingOp.SetDepthBiasEnable:
                vk.CmdSetDepthBiasEnable(command, new Bool32(*(uint*)payload));
                break;
            case RingOp.SetDepthBias:
            {
                var args = (RingArgs.Floats*)payload;
                vk.CmdSetDepthBias(command, args->A, args->B, args->C);
                break;
            }
            case RingOp.SetDepthBounds:
            {
                var args = (RingArgs.Floats*)payload;
                vk.CmdSetDepthBounds(command, args->A, args->B);
                break;
            }
            case RingOp.SetStencilCompareMask:
            {
                var args = (RingArgs.Four*)payload;
                vk.CmdSetStencilCompareMask(command, (StencilFaceFlags)args->A, args->B);
                break;
            }
            case RingOp.SetStencilWriteMask:
            {
                var args = (RingArgs.Four*)payload;
                vk.CmdSetStencilWriteMask(command, (StencilFaceFlags)args->A, args->B);
                break;
            }
            case RingOp.SetStencilReference:
            {
                var args = (RingArgs.Four*)payload;
                vk.CmdSetStencilReference(command, (StencilFaceFlags)args->A, args->B);
                break;
            }
            case RingOp.SetColorWriteEnable:
            {
                var count = *(uint*)payload;
                ColorWriteApi!.CmdSetColorWriteEnable(command, count, (Bool32*)(payload + 8));
                break;
            }
            case RingOp.BeginRendering:
            {
                var args = (RingArgs.Rendering*)payload;
                var colors = (RenderingAttachmentInfo*)(payload + sizeof(RingArgs.Rendering));
                var tail = colors + args->Info.ColorAttachmentCount;
                args->Info.PColorAttachments = args->Info.ColorAttachmentCount == 0 ? null : colors;
                args->Info.PDepthAttachment = args->HasDepth != 0 ? tail : null;
                args->Info.PStencilAttachment = args->HasStencil != 0 ? tail + 1 : null;
                vk.CmdBeginRendering(command, &args->Info);
                break;
            }
            case RingOp.EndRendering:
                vk.CmdEndRendering(command);
                break;
            case RingOp.PushConstants:
            {
                var args = (RingArgs.PushConstants*)payload;
                vk.CmdPushConstants(command, args->Layout, args->Stages, args->Offset, args->Size, payload + sizeof(RingArgs.PushConstants));
                break;
            }
            case RingOp.PushDescriptorSet:
            {
                var args = (RingArgs.DescriptorSets*)payload;
                var writes = (WriteDescriptorSet*)(payload + sizeof(RingArgs.DescriptorSets));
                for (var index = 0u; index < args->Count; index++)
                {
                    ref var write = ref writes[index];
                    if (write.PImageInfo != null) write.PImageInfo = (DescriptorImageInfo*)(payload + (nint)write.PImageInfo);
                    if (write.PBufferInfo != null) write.PBufferInfo = (DescriptorBufferInfo*)(payload + (nint)write.PBufferInfo);
                    if (write.PTexelBufferView != null) write.PTexelBufferView = (BufferView*)(payload + (nint)write.PTexelBufferView);
                }

                PushApi!.CmdPushDescriptorSet(command, args->BindPoint, args->Layout, args->Set, args->Count, writes);
                break;
            }
            case RingOp.BindDescriptorSets:
            {
                var args = (RingArgs.DescriptorSets*)payload;
                var sets = (DescriptorSet*)(payload + sizeof(RingArgs.DescriptorSets));
                var offsets = (uint*)(sets + args->Count);
                vk.CmdBindDescriptorSets(command, args->BindPoint, args->Layout, args->Set, args->Count, sets, args->DynamicCount,
                    args->DynamicCount == 0 ? null : offsets);
                break;
            }
            case RingOp.CopyBuffer:
            {
                var args = (RingArgs.CopyBuffer*)payload;
                vk.CmdCopyBuffer(command, args->Source, args->Destination, args->Count, (BufferCopy*)(payload + sizeof(RingArgs.CopyBuffer)));
                break;
            }
            case RingOp.FillBuffer:
            {
                var args = (RingArgs.FillBuffer*)payload;
                vk.CmdFillBuffer(command, args->Destination, args->Offset, args->Size, args->Data);
                break;
            }
            case RingOp.UpdateBuffer:
            {
                var args = (RingArgs.FillBuffer*)payload;
                vk.CmdUpdateBuffer(command, args->Destination, args->Offset, args->Size, payload + sizeof(RingArgs.FillBuffer));
                break;
            }
            case RingOp.CopyImage:
            {
                var args = (RingArgs.ImageToImage*)payload;
                vk.CmdCopyImage(command, args->Source, args->SourceLayout, args->Destination, args->DestinationLayout, args->Count,
                    (ImageCopy*)(payload + sizeof(RingArgs.ImageToImage)));
                break;
            }
            case RingOp.BlitImage:
            {
                var args = (RingArgs.ImageToImage*)payload;
                vk.CmdBlitImage(command, args->Source, args->SourceLayout, args->Destination, args->DestinationLayout, args->Count,
                    (ImageBlit*)(payload + sizeof(RingArgs.ImageToImage)), args->Filter);
                break;
            }
            case RingOp.ResolveImage:
            {
                var args = (RingArgs.ImageToImage*)payload;
                vk.CmdResolveImage(command, args->Source, args->SourceLayout, args->Destination, args->DestinationLayout, args->Count,
                    (ImageResolve*)(payload + sizeof(RingArgs.ImageToImage)));
                break;
            }
            case RingOp.CopyBufferToImage:
            {
                var args = (RingArgs.BufferImage*)payload;
                vk.CmdCopyBufferToImage(command, args->Buffer, args->Image, args->Layout, args->Count,
                    (BufferImageCopy*)(payload + sizeof(RingArgs.BufferImage)));
                break;
            }
            case RingOp.CopyImageToBuffer:
            {
                var args = (RingArgs.BufferImage*)payload;
                vk.CmdCopyImageToBuffer(command, args->Image, args->Layout, args->Buffer, args->Count,
                    (BufferImageCopy*)(payload + sizeof(RingArgs.BufferImage)));
                break;
            }
            case RingOp.ClearColorImage:
            {
                var args = (RingArgs.ClearColor*)payload;
                vk.CmdClearColorImage(command, args->Image, args->Layout, &args->Value, args->Count,
                    (ImageSubresourceRange*)(payload + sizeof(RingArgs.ClearColor)));
                break;
            }
            case RingOp.ClearDepthStencilImage:
            {
                var args = (RingArgs.ClearDepth*)payload;
                vk.CmdClearDepthStencilImage(command, args->Image, args->Layout, &args->Value, args->Count,
                    (ImageSubresourceRange*)(payload + sizeof(RingArgs.ClearDepth)));
                break;
            }
            case RingOp.BeginRenderPass:
            {
                var args = (RingArgs.RenderPass*)payload;
                args->Info.PClearValues = args->Info.ClearValueCount == 0 ? null : (ClearValue*)(payload + sizeof(RingArgs.RenderPass));
                vk.CmdBeginRenderPass(command, &args->Info, args->Contents);
                break;
            }
            case RingOp.EndRenderPass:
                vk.CmdEndRenderPass(command);
                break;
            case RingOp.ResetQueryPool:
            {
                var args = (RingArgs.Query*)payload;
                vk.CmdResetQueryPool(command, args->Pool, args->First, args->Count);
                break;
            }
            case RingOp.WriteTimestamp2:
            {
                var args = (RingArgs.Query*)payload;
                vk.CmdWriteTimestamp2(command, args->Stage, args->Pool, args->First);
                break;
            }
            case RingOp.BeginDebugLabel:
            {
                var args = (RingArgs.DebugLabel*)payload;
                args->Label.PLabelName = payload + sizeof(RingArgs.DebugLabel);
                ((delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void>)args->Function)(command, &args->Label);
                break;
            }
            case RingOp.EndDebugLabel:
                ((delegate* unmanaged<CommandBuffer, void>)(*(nint*)payload))(command);
                break;
            default:
                throw new InvalidOperationException($"Unknown record ring operation {op}.");
        }
    }

    private void ExecuteBarrier(CommandBuffer command, byte* payload)
    {
        var head = (RingArgs.BarrierHead*)payload;
        var memory = (MemoryBarrier2*)(payload + sizeof(RingArgs.BarrierHead));
        var buffers = (BufferMemoryBarrier2*)(memory + head->Memory);
        var images = (ImageMemoryBarrier2*)(buffers + head->Buffer);
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            DependencyFlags = head->Flags,
            MemoryBarrierCount = head->Memory,
            PMemoryBarriers = head->Memory == 0 ? null : memory,
            BufferMemoryBarrierCount = head->Buffer,
            PBufferMemoryBarriers = head->Buffer == 0 ? null : buffers,
            ImageMemoryBarrierCount = head->Image,
            PImageMemoryBarriers = head->Image == 0 ? null : images,
        };
        _vk.CmdPipelineBarrier2(command, &dependency);
    }

    // Stages that order against every other stage for the merge test.
    private const PipelineStageFlags2 BroadStages = PipelineStageFlags2.AllCommandsBit | PipelineStageFlags2.AllGraphicsBit |
        PipelineStageFlags2.TopOfPipeBit | PipelineStageFlags2.BottomOfPipeBit;

    private static bool StagesMeet(PipelineStageFlags2 earlierDestination, PipelineStageFlags2 laterSource)
    {
        if (earlierDestination == 0 || laterSource == 0)
        {
            return false;
        }

        if ((earlierDestination & BroadStages) != 0 || (laterSource & BroadStages) != 0)
        {
            return true;
        }

        return (earlierDestination & laterSource) != 0;
    }

    private MemoryBarrier2[] _mergeMemory = new MemoryBarrier2[16];
    private BufferMemoryBarrier2[] _mergeBuffers = new BufferMemoryBarrier2[64];
    private ImageMemoryBarrier2[] _mergeImages = new ImageMemoryBarrier2[64];
    private readonly HashSet<ulong> _mergeResources = new();

    // Replays a run of adjacent barrier records in the current block as few calls as the merge rule allows; returns
    // the bytes consumed and the records in the run.
    private int ReplayBarrierRun(Block block, int published, out int records)
    {
        var position = _readPos;
        records = 0;
        var memoryCount = 0;
        var bufferCount = 0;
        var imageCount = 0;
        PipelineStageFlags2 destination = 0;
        DependencyFlags flags = 0;
        _mergeResources.Clear();
        while (position < published)
        {
            var header = block.Data + position;
            if ((RingOp)(*(ushort*)header) != RingOp.Barrier)
            {
                break;
            }

            var payload = header + 8;
            var head = (RingArgs.BarrierHead*)payload;
            var memory = (MemoryBarrier2*)(payload + sizeof(RingArgs.BarrierHead));
            var buffers = (BufferMemoryBarrier2*)(memory + head->Memory);
            var images = (ImageMemoryBarrier2*)(buffers + head->Buffer);
            PipelineStageFlags2 source = 0;
            PipelineStageFlags2 laterDestination = 0;
            for (var index = 0u; index < head->Memory; index++)
            {
                source |= memory[index].SrcStageMask;
                laterDestination |= memory[index].DstStageMask;
            }

            for (var index = 0u; index < head->Buffer; index++)
            {
                source |= buffers[index].SrcStageMask;
                laterDestination |= buffers[index].DstStageMask;
            }

            for (var index = 0u; index < head->Image; index++)
            {
                source |= images[index].SrcStageMask;
                laterDestination |= images[index].DstStageMask;
            }

            if (records != 0)
            {
                var conflict = head->Flags != flags || StagesMeet(destination, source);
                for (var index = 0u; !conflict && index < head->Buffer; index++)
                {
                    conflict = _mergeResources.Contains(buffers[index].Buffer.Handle);
                }

                for (var index = 0u; !conflict && index < head->Image; index++)
                {
                    conflict = _mergeResources.Contains(images[index].Image.Handle);
                }

                if (conflict)
                {
                    break;
                }
            }

            flags = head->Flags;
            destination |= laterDestination;
            EnsureMergeCapacity(ref _mergeMemory, memoryCount + (int)head->Memory);
            EnsureMergeCapacity(ref _mergeBuffers, bufferCount + (int)head->Buffer);
            EnsureMergeCapacity(ref _mergeImages, imageCount + (int)head->Image);
            for (var index = 0u; index < head->Memory; index++)
            {
                _mergeMemory[memoryCount++] = memory[index];
            }

            for (var index = 0u; index < head->Buffer; index++)
            {
                _mergeBuffers[bufferCount++] = buffers[index];
                _mergeResources.Add(buffers[index].Buffer.Handle);
            }

            for (var index = 0u; index < head->Image; index++)
            {
                _mergeImages[imageCount++] = images[index];
                _mergeResources.Add(images[index].Image.Handle);
            }

            position += *(int*)(header + 4);
            records++;
        }

        fixed (MemoryBarrier2* memory = _mergeMemory)
        fixed (BufferMemoryBarrier2* buffers = _mergeBuffers)
        fixed (ImageMemoryBarrier2* images = _mergeImages)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                DependencyFlags = flags,
                MemoryBarrierCount = (uint)memoryCount,
                PMemoryBarriers = memoryCount == 0 ? null : memory,
                BufferMemoryBarrierCount = (uint)bufferCount,
                PBufferMemoryBarriers = bufferCount == 0 ? null : buffers,
                ImageMemoryBarrierCount = (uint)imageCount,
                PImageMemoryBarriers = imageCount == 0 ? null : images,
            };
            _vk.CmdPipelineBarrier2(Current, &dependency);
        }

        if (records > 1)
        {
            RingStats.Merged(records);
        }

        return position - _readPos;
    }

    private static void EnsureMergeCapacity<T>(ref T[] array, int count)
    {
        if (array.Length < count)
        {
            Array.Resize(ref array, Math.Max(count, array.Length * 2));
        }
    }
}
