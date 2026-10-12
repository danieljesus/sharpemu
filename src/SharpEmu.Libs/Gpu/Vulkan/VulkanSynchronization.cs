// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal static unsafe class VulkanSynchronization
{
    public static AccessFlags2 Access(AccessFlags access) => (AccessFlags2)(ulong)access;

    public static void PipelineBarrier(
        Vk vk,
        CommandBuffer command,
        PipelineStageFlags sourceStages,
        PipelineStageFlags destinationStages,
        DependencyFlags dependencyFlags,
        uint memoryBarrierCount,
        MemoryBarrier2* memoryBarriers,
        uint bufferMemoryBarrierCount,
        BufferMemoryBarrier2* bufferMemoryBarriers,
        uint imageMemoryBarrierCount,
        ImageMemoryBarrier2* imageMemoryBarriers,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "", // [local]
        [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) // [local]
    {
        var sourceStages2 = (PipelineStageFlags2)(ulong)sourceStages;
        var destinationStages2 = (PipelineStageFlags2)(ulong)destinationStages;
        for (var index = 0u; index < memoryBarrierCount; index++)
        {
            memoryBarriers[index].SrcStageMask = sourceStages2;
            memoryBarriers[index].DstStageMask = destinationStages2;
        }
        for (var index = 0u; index < bufferMemoryBarrierCount; index++)
        {
            bufferMemoryBarriers[index].SrcStageMask = sourceStages2;
            bufferMemoryBarriers[index].DstStageMask = destinationStages2;
        }
        for (var index = 0u; index < imageMemoryBarrierCount; index++)
        {
            imageMemoryBarriers[index].SrcStageMask = sourceStages2;
            imageMemoryBarriers[index].DstStageMask = destinationStages2;
        }

        var dependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            DependencyFlags = dependencyFlags,
            MemoryBarrierCount = memoryBarrierCount,
            PMemoryBarriers = memoryBarriers,
            BufferMemoryBarrierCount = bufferMemoryBarrierCount,
            PBufferMemoryBarriers = bufferMemoryBarriers,
            ImageMemoryBarrierCount = imageMemoryBarrierCount,
            PImageMemoryBarriers = imageMemoryBarriers,
        };
        VkCallProbe.CountBarrier(command.Handle, memoryBarrierCount, bufferMemoryBarrierCount, imageMemoryBarrierCount, callerFile, callerLine); // [local]
        var vkProbe54 = SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Start(); // [local]
        vk.CmdPipelineBarrier2(command, &dependencyInfo);
        SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Stop(SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Kind.Barrier, vkProbe54); // [local]
    }
}
