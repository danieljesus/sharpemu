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

// A dispatch that asks for fewer threads than the wave holds starts the wave with EXEC
// clear past the last thread. Wave-level reductions on EXEC (an append counter built
// from s_bcnt1 of a saved EXEC, for example) must see only the dispatched lanes.
public sealed class ComputePartialWaveExecDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint WaveSize = 64;

    [Theory]
    [InlineData(2u)]
    [InlineData(24u)]
    [InlineData(40u)]
    [InlineData(64u)]
    public void ExecAndCompareMasksCountOnlyDispatchedLanes(uint threads)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(CreateProgram());
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = WaveSize, ThreadCountX = threads, WaveSize = WaveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(512);
        var registers = new uint[256];
        registers[6] = 512;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 512);
        for (var lane = 0; lane < WaveSize; lane++)
        {
            var expected = lane < threads ? threads : 0u;
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * sizeof(uint))));
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(256 + lane * sizeof(uint))));
        }
        harness.AssertNoValidationMessages();
    }

    // Every dispatched lane stores popcount(EXEC) at lane * 4 and popcount(VCC) of an
    // always-true compare (a wave mask built through the subgroup ballot) at 256 + lane * 4.
    private static Gen5ShaderProgram CreateProgram() =>
        Program(
            Sop1(0, "SBcnt1I32B64", 8, Gen5Operand.Scalar(126)),
            Vopc(4, "VCmpLeU32", Operand(0), 0),
            Sop1(8, "SBcnt1I32B64", 9, Gen5Operand.Scalar(106)),
            MoveVectorFromScalar(12, 4, 8),
            MoveVectorFromScalar(16, 7, 9),
            Vop2(20, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(24, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            BufferAccess(32, "BufferStoreDword", 4, offset: 256, vectorData: 7, offsetEnabled: true, vectorAddress: 5),
            EndProgram(40));
}
