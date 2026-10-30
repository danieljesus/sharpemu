// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_RT_RAYQUERY: the VK_KHR_acceleration_structure entry points and limits, set once the device exists.
internal static unsafe class RayQueryDevice
{
    public static bool Enabled { get; private set; }
    public static KhrAccelerationStructure? Api { get; private set; }
    public static ulong ScratchAlignment { get; private set; } = 256;

    // The usage guest buffers need to be read by acceleration structure builds (vertex data in place).
    public static BufferUsageFlags GuestBufferUsage => Enabled ? BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr : 0;

    public static void Initialize(Vk vk, Instance instance, PhysicalDevice physicalDevice, Device device)
    {
        if (!vk.TryGetDeviceExtension(instance, device, out KhrAccelerationStructure api))
        {
            Console.Error.WriteLine("[GPU][RQ] VK_KHR_acceleration_structure entry points are missing");
            return;
        }

        var properties = new PhysicalDeviceAccelerationStructurePropertiesKHR { SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr };
        var properties2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &properties };
        vk.GetPhysicalDeviceProperties2(physicalDevice, &properties2);
        ScratchAlignment = Math.Max(properties.MinAccelerationStructureScratchOffsetAlignment, 1u);
        Api = api;
        Enabled = true;
        Console.Error.WriteLine($"[GPU][RQ] acceleration structures: scratch_alignment={ScratchAlignment} max_primitives={properties.MaxPrimitiveCount} max_geometry={properties.MaxGeometryCount}");
    }
}
