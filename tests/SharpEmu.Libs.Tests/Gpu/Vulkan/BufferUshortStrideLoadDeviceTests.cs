// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// [local] buffer_load_ushort at byte offsets 0, 2 and 4 of 6-byte records (an index buffer of ushort triangles, as GTA V's
// BVH leaf writer 0xFADB reads it): each lane gets its record's three indices whatever the record's dword alignment.
public sealed class BufferUshortStrideLoadDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Records = 64;

    [Fact]
    public void ThreeUshortsOfSixByteRecords()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = CreateProgram();
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0x7777, 0, 16, waveSize: 64);
        var specialization = ResourceSpecialization.Default(plan.Info);
        for (var index = 0; index < specialization.Buffers.Count; index++)
        {
            specialization.Buffers[index] = specialization.Buffers[index] with { PackedStride = index == 0 ? 6u : 4u };
        }

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            BindingLayout.UsesGlobalDataShare(program), ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program), 0, usesDispatchThreadLimits: false, limitsInShaderData: false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, ThreadCountX = Records, WaveSize = 64, HostComputeSubgroup32 = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var indices = new byte[Records * 6];
        for (var k = 0; k < Records * 3; k++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(k * 2), (ushort)(0x1000 + k * 7));
        }

        var source = runner.CreateBuffer(indices);
        var output = runner.CreateBuffer(Records * 16);
        var registers = new uint[256];
        registers[4] = 0; registers[5] = 6u << 16; registers[6] = Records; registers[7] = 0x00016204; // s[4:7]: stride 6, 64 records
        registers[8] = 0; registers[9] = 16u << 16; registers[10] = Records; registers[11] = 0x00016204; // s[8:11]: stride 16
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [source, output] }, 1));
        var bytes = runner.ReadBack(output, 0, Records * 16);
        for (var k = 0; k < Records; k++)
        {
            for (var c = 0; c < 3; c++)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(k * 16 + c * 4));
                var expected = 0x1000u + (uint)(k * 3 + c) * 7;
                Assert.True(value == expected, $"record {k} ushort {c}: {value:X} expected {expected:X}");
            }
        }
        harness.AssertNoValidationMessages();
    }

    // v9 = lane (record index); v5/v6/v7 = ushorts at +0/+2/+4 (idxen, stride 6, s[4:7]); stored as x3 at lane*16 in s[8:11].
    private static Gen5ShaderProgram CreateProgram() =>
        Program(
            MoveVector(0, 9, 0),
            Vop2(8, "VAddI32", 9, Gen5Operand.Vector(9), Gen5Operand.Vector(0)),
            BufferAccess(16, "BufferLoadUshort", 4, vectorData: 5, indexEnabled: true, vectorAddress: 9, offset: 0),
            BufferAccess(24, "BufferLoadUshort", 4, vectorData: 6, indexEnabled: true, vectorAddress: 9, offset: 2),
            BufferAccess(32, "BufferLoadUshort", 4, vectorData: 7, indexEnabled: true, vectorAddress: 9, offset: 4),
            Vop2(40, "VLshlrevB32", 10, Operand(4), Gen5Operand.Vector(9)),
            BufferAccess(48, "BufferStoreDwordx3", 8, vectorData: 5, offsetEnabled: true, vectorAddress: 10, dwords: 3),
            EndProgram(56));
}
