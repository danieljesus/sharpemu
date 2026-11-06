// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    internal const ulong IndexedIndirectArgumentsSize = 20;
    internal const ulong AutoIndirectArgumentsSize = 16;

    // [local] SHARPEMU_PREPARE_STAGE_IMAGES=0 keeps the image work of a draw inside CommitBindings. The draw is recorded
    // by RenderRecorder since the upstream merge; it reads this switch where it prepares the stages' images.
    internal static readonly bool PrepareStageImagesEnabled = Environment.GetEnvironmentVariable("SHARPEMU_PREPARE_STAGE_IMAGES") != "0";

    // [local] SHARPEMU_BDA_READ_ONLY_SCOPES=1 (default off): a stage the resource plan proves only reads through device
    // addresses no longer makes its draw memory-writing, so it neither breaks a pruned render scope nor notes a write
    // for the scope barrier tracker. Otherwise device-address accesses count as stores. xnetcat 2aa04103.
    internal static bool BdaReadOnlyScopes = Environment.GetEnvironmentVariable("SHARPEMU_BDA_READ_ONLY_SCOPES") == "1";

    private static bool WritesThroughDeviceAddresses(ShaderProgramInfo program) =>
        program.UsesDeviceAddresses && !(BdaReadOnlyScopes && program.DeviceAddressesAreReadOnly);

    private bool DrawWritesMemory(ShaderStageResources stage) =>
        stage.Program is { } program &&
        (WritesThroughDeviceAddresses(program) || WritesStorageImage(program) || HasBufferWrites(stage)); // [local]

    // [local] With the stage's HasBufferWrites already evaluated by the caller.
    private static bool DrawWritesMemory(ShaderStageResources stage, bool bufferWrites) =>
        stage.Program is { } program &&
        (WritesThroughDeviceAddresses(program) || WritesStorageImage(program) || bufferWrites); // [local]

    private bool HasBufferWrites(ShaderStageResources stage)
    {
        var program = stage.Program ?? throw _host.Fatal("A shader stage has no program.");
        var descriptors = stage.Resources.Buffers;
        if (descriptors.Length != program.Buffers.Length)
        {
            throw _host.Fatal($"The buffer descriptor count does not match the program: descriptors={descriptors.Length} program={program.Buffers.Length}.");
        }

        for (var i = 0; i < program.Buffers.Length; i++)
        {
            if (!program.Buffers[i].Written)
            {
                continue;
            }

            if (descriptors[i].Length < 4)
            {
                throw _host.Fatal($"A written buffer descriptor is too short: index={i} words={descriptors[i].Length}.");
            }

            var descriptor = BufferDescriptorWords.From(descriptors[i]);
            var footprint = descriptor.Footprint() ?? throw _host.Fatal(
                $"The written buffer footprint overflows: index={i} stride={descriptor.Stride} records={descriptor.RecordCount}.");
            if (descriptor.Address != 0 && footprint != 0)
            {
                return true;
            }
        }

        return false;
    }
}
