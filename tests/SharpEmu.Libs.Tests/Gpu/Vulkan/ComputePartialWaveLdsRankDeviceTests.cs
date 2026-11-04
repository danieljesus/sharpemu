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

// [local] A thread-count dispatch whose limit arrives at dispatch time (the game's BVH builders): the returning LDS add
// ranks only the dispatched lanes of a partial last wave, in lane order, and counts none of the lanes past the limit,
// also when the indirect path dispatches whole groups past the limit.
public sealed class ComputePartialWaveLdsRankDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint WaveSize = 64;
    private const uint GroupRegister = 16;

    [Theory]
    [InlineData(41u, 1u)]
    [InlineData(41u, 41u)]
    [InlineData(100u, 2u)]
    [InlineData(100u, 100u)]
    [InlineData(2063u, 33u)]
    [InlineData(2063u, 2063u)]
    public void OrderedLdsAddRanksOnlyTheDispatchedLanes(uint threads, uint groups)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = CreateProgram();
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0x4321, 0, 17, waveSize: WaveSize);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 17),
            BindingLayout.UsesGlobalDataShare(program), ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program), 0, usesDispatchThreadLimits: true, limitsInShaderData: false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = WaveSize, WaveSize = WaveSize, HostComputeSubgroup32 = true, LocalDataShareDwords = 16,
            ComputeSystemRegisters = new Gen5ComputeSystemRegisters(GroupRegister, null, null, null),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var slots = groups * WaveSize;
        var initial = Enumerable.Repeat((byte)0xFF, (int)(slots * sizeof(uint))).ToArray();
        var output = runner.CreateBuffer(initial);
        var registers = new uint[256];
        registers[6] = slots * sizeof(uint);
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, groups,
            dispatchThreadLimits: [threads, 1, 1]));
        var bytes = runner.ReadBack(output, 0, slots * sizeof(uint));
        for (var thread = 0u; thread < slots; thread++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)(thread * sizeof(uint))));
            if (thread >= threads)
            {
                Assert.True(value == 0xFFFFFFFFu, $"thread {thread} past the limit wrote {value:X8}");
                continue;
            }

            var wave = thread / WaveSize;
            var inWave = Math.Min(WaveSize, threads - wave * WaveSize);
            var expected = (thread & (WaveSize - 1)) | (inWave << 16);
            Assert.True(value == expected, $"thread {thread}: rank {value & 0xFFFF} total {value >> 16}, expected rank {thread & 63} total {inWave}");
        }
        harness.AssertNoValidationMessages();
    }

    // Every lane: rank = ds_add_rtn_u32 of 1 on LDS word 0; after a barrier the word holds the wave's lane count.
    // v4 = rank | count << 16 at (group * 64 + lane) * 4.
    private static Gen5ShaderProgram CreateProgram() =>
        Program(
            MoveVector(0, 3, 0),
            MoveVector(8, 6, 1),
            MoveVector(16, 8, 0),
            DataShare(24, "DsWriteB32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(8)], []),
            new(32, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null),
            DataShare(40, "DsAddRtnU32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(6)], [9]),
            new(48, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null),
            DataShare(56, "DsReadB32", false, [Gen5Operand.Vector(3)], [10]),
            Vop2(64, "VLshlrevB32", 10, Operand(16), Gen5Operand.Vector(10)),
            Vop2(72, "VOrB32", 4, Gen5Operand.Vector(9), Gen5Operand.Vector(10)),
            MoveVectorFromScalar(80, 7, GroupRegister),
            Vop2(88, "VLshlrevB32", 7, Operand(6), Gen5Operand.Vector(7)),
            Vop2(96, "VAddI32", 7, Gen5Operand.Vector(7), Gen5Operand.Vector(0)),
            Vop2(104, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(7)),
            BufferAccess(112, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(120));
}
