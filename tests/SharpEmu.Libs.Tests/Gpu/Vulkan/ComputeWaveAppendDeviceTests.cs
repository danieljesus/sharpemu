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

// The wave-level append used by BVH builders: one elected lane reserves popcount(EXEC)
// entries with a returning atomic add, every lane takes the reserved base plus its rank
// (v_mbcnt of the saved EXEC) as its slot. Across many workgroups every slot must be
// claimed exactly once.
public sealed class ComputeWaveAppendDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint WaveSize = 64;

    [Theory]
    [InlineData(1u, 64u)]
    [InlineData(1u, 37u)]
    [InlineData(16u, 1024u)]
    [InlineData(200u, 12800u)]
    [InlineData(200u, 12777u)]
    public void EveryLaneClaimsOneDistinctSlot(uint groups, uint threads)
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
        // The counter in dword 0, the list from byte 64 on (one buffer, one V#).
        var listBytes = groups * WaveSize * sizeof(uint);
        var initial = Enumerable.Repeat((byte)0xFF, (int)listBytes + 64).ToArray();
        Array.Clear(initial, 0, 64);
        var list = runner.CreateBuffer(initial);
        var registers = new uint[256];
        registers[6] = listBytes + 64;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [list] }, groups));
        var whole = runner.ReadBack(list, 0, listBytes + 64);
        var total = BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan(0, 4));
        Assert.Equal(threads, total);
        var actual = whole.AsSpan(64).ToArray();
        var seen = new HashSet<uint>();
        for (var slot = 0u; slot < groups * WaveSize; slot++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan((int)(slot * sizeof(uint))));
            if (slot >= threads)
            {
                Assert.Equal(0xFFFFFFFFu, value);
                continue;
            }

            // value = (reserved base << 8) | lane; the base is the wave's block and the lane its rank.
            var reservedBase = value >> 8;
            var lane = value & 0xFF;
            Assert.True(reservedBase <= slot && slot < reservedBase + WaveSize, $"slot {slot}: base {reservedBase} lane {lane}");
            Assert.Equal(slot - reservedBase, lane);
            Assert.True(seen.Add(value), $"slot {slot} claimed twice ({value:X})");
        }
        harness.AssertNoValidationMessages();
    }

    private static Gen5ShaderInstruction PlainReturningAtomicAdd(uint pc, uint resourceRegister, uint vectorData) =>
        new(pc, Gen5ShaderEncoding.Mubuf, "BufferAtomicAdd", [0u, 0u],
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand), Gen5Operand.Vector(vectorData)],
            [Gen5Operand.Vector(vectorData)],
            new Gen5BufferMemoryControl(1, 0, vectorData, resourceRegister, 0, false, false, Glc: true, Slc: false));

    private static Gen5ShaderProgram CreateProgram() =>
        Program(
            Sop1(0, "SMovB64", 12, Gen5Operand.Scalar(126)),
            Sop1(8, "SBcnt1I32B64", 14, Gen5Operand.Scalar(12)),
            Sop1(16, "SFF1I32B64", 16, Gen5Operand.Scalar(12)),
            Sop2(24, "SLshlB64", 18, Operand(1), Gen5Operand.Scalar(16)),
            Sop1(32, "SAndSaveexecB64", 20, Gen5Operand.Scalar(18)),
            MoveVectorFromScalar(40, 4, 14),
            PlainReturningAtomicAdd(48, 4, 4),
            Sop1(56, "SMovB64", 126, Gen5Operand.Scalar(20)),
            ReadFirstLane(64, 22, 4),
            Vop2(72, "VMbcntLoU32B32", 6, Gen5Operand.Scalar(12), Operand(0)),
            Vop2(80, "VMbcntHiU32B32", 6, Gen5Operand.Scalar(13), Gen5Operand.Vector(6)),
            Vop2(88, "VAddI32", 7, Gen5Operand.Scalar(22), Gen5Operand.Vector(6)),
            Vop2(96, "VLshlrevB32", 8, Operand(2), Gen5Operand.Vector(7)),
            MoveScalar(104, 24, 64),
            Vop2(112, "VAddI32", 8, Gen5Operand.Scalar(24), Gen5Operand.Vector(8)),
            MoveVectorFromScalar(120, 10, 22),
            Vop2(128, "VLshlrevB32", 9, Operand(8), Gen5Operand.Vector(10)),
            Vop2(136, "VOrB32", 9, Gen5Operand.Vector(9), Gen5Operand.Vector(0)),
            BufferAccess(144, "BufferStoreDword", 4, vectorData: 9, offsetEnabled: true, vectorAddress: 8),
            EndProgram(152));

    private static Gen5ShaderInstruction PlainReturningAtomicAdd(uint pc, uint resourceRegister, uint vectorData) =>
        new(pc, Gen5ShaderEncoding.Mubuf, "BufferAtomicAdd", [0u, 0u],
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand), Gen5Operand.Vector(vectorData)],
            [Gen5Operand.Vector(vectorData)],
            new Gen5BufferMemoryControl(1, 0, vectorData, resourceRegister, 0, false, false, Glc: true, Slc: false));

    private static Gen5ShaderInstruction ReturningAtomicAdd(uint pc, uint resourceRegister, uint vectorData, uint vectorOffset) =>
        new(pc, Gen5ShaderEncoding.Mubuf, "BufferAtomicAdd", [0u, 0u],
            [Gen5Operand.Vector(vectorOffset), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand), Gen5Operand.Vector(vectorData)],
            [Gen5Operand.Vector(vectorData)],
            new Gen5BufferMemoryControl(1, vectorOffset, vectorData, resourceRegister, 0, false, true, Glc: Environment.GetEnvironmentVariable("APPEND_TEST_GLC") != "0", Slc: false));

    private static Gen5ShaderProgram CreateProgram() =>
        Program(
            Sop1(0, "SMovB64", 12, Gen5Operand.Scalar(126)),
            Sop1(8, "SBcnt1I32B64", 14, Gen5Operand.Scalar(12)),
            Sop1(16, "SFF1I32B64", 16, Gen5Operand.Scalar(12)),
            Sop2(24, "SLshlB64", 18, Operand(1), Gen5Operand.Scalar(16)),
            Sop1(32, "SAndSaveexecB64", 20, Gen5Operand.Scalar(18)),
            MoveVectorFromScalar(40, 4, 14),
            PlainReturningAtomicAdd(48, 4, 4),
            Sop1(56, "SMovB64", 126, Gen5Operand.Scalar(20)),
            ReadFirstLane(64, 22, 4),
            Vop2(72, "VMbcntLoU32B32", 6, Gen5Operand.Scalar(12), Operand(0)),
            Vop2(80, "VMbcntHiU32B32", 6, Gen5Operand.Scalar(13), Gen5Operand.Vector(6)),
            Vop2(88, "VAddI32", 7, Gen5Operand.Scalar(22), Gen5Operand.Vector(6)),
            Vop2(96, "VLshlrevB32", 8, Operand(2), Gen5Operand.Vector(7)),
            MoveScalar(104, 24, 64),
            Vop2(112, "VAddI32", 8, Gen5Operand.Scalar(24), Gen5Operand.Vector(8)),
            MoveVectorFromScalar(120, 10, 22),
            Vop2(128, "VLshlrevB32", 9, Operand(8), Gen5Operand.Vector(10)),
            Vop2(136, "VOrB32", 9, Gen5Operand.Vector(9), Gen5Operand.Vector(0)),
            BufferAccess(144, "BufferStoreDword", 4, vectorData: 9, offsetEnabled: true, vectorAddress: 8),
            EndProgram(152));

    [Fact]
    public void DebugWaveAppendLaneValues()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(CreateDebugProgram());
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = WaveSize, ThreadCountX = WaveSize, WaveSize = WaveSize };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var bytes = 64 + WaveSize * 32;
        var initial = Enumerable.Repeat((byte)0xEE, (int)bytes).ToArray();
        Array.Clear(initial, 0, 64);
        var buffer = runner.CreateBuffer(initial);
        var registers = new uint[256];
        registers[6] = bytes;
        registers[12] = 0;
        harness.Run(() => runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
        var actual = runner.ReadBack(buffer, 0, bytes);
        var lines = new List<string>();
        foreach (var lane in new[] { 0, 1, 31, 32, 33, 63 })
        {
            var w = Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(64 + lane * 32 + i * 4)).ToString("X")).ToArray();
            lines.Add($"lane {lane}: exec={w[1]}{w[0]} bcnt={w[2]} ff1={w[3]} saved={w[4]} old={w[5]} v8={w[6]} rank={w[7]}");
        }
        lines.Add($"counter={BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(0)):X} dword1={BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(4)):X}");
        Assert.Fail(string.Join(" | ", lines));
    }

    // Per lane at 64 + lane * 32: exec lo, exec hi, bcnt, ff1, saved exec lo, old (readfirstlane), slot byte offset (v8), rank (v6).
    private static Gen5ShaderProgram CreateDebugProgram() =>
        Program(
            Sop1(0, "SMovB64", 12, Gen5Operand.Scalar(126)),
            Sop1(8, "SBcnt1I32B64", 14, Gen5Operand.Scalar(12)),
            Sop1(16, "SFF1I32B64", 16, Gen5Operand.Scalar(12)),
            Sop2(24, "SLshlB64", 18, Operand(1), Gen5Operand.Scalar(16)),
            Sop1(32, "SAndSaveexecB64", 20, Gen5Operand.Scalar(18)),
            MoveVectorFromScalar(40, 4, 14),
            PlainReturningAtomicAdd(48, 4, 4),
            Sop1(56, "SMovB64", 126, Gen5Operand.Scalar(20)),
            ReadFirstLane(64, 22, 4),
            Vop2(72, "VMbcntLoU32B32", 6, Gen5Operand.Scalar(12), Operand(0)),
            Vop2(80, "VMbcntHiU32B32", 6, Gen5Operand.Scalar(13), Gen5Operand.Vector(6)),
            Vop2(88, "VAddI32", 7, Gen5Operand.Scalar(22), Gen5Operand.Vector(6)),
            Vop2(96, "VLshlrevB32", 8, Operand(2), Gen5Operand.Vector(7)),
            MoveScalar(104, 24, 64),
            Vop2(112, "VAddI32", 8, Gen5Operand.Scalar(24), Gen5Operand.Vector(8)),
            Vop2(120, "VLshlrevB32", 3, Operand(5), Gen5Operand.Vector(0)),
            Vop2(128, "VAddI32", 3, Gen5Operand.Scalar(24), Gen5Operand.Vector(3)),
            MoveVectorFromScalar(136, 10, 12),
            MoveVectorFromScalar(144, 11, 13),
            MoveVectorFromScalar(152, 12, 14),
            MoveVectorFromScalar(160, 13, 16),
            MoveVectorFromScalar(168, 14, 20),
            MoveVectorFromScalar(176, 15, 22),
            Vop1(184, "VMovB32", 16, Gen5Operand.Vector(8)),
            Vop1(192, "VMovB32", 17, Gen5Operand.Vector(6)),
            BufferAccess(200, "BufferStoreDwordx4", 4, dwords: 4, vectorData: 10, offsetEnabled: true, vectorAddress: 3),
            BufferAccess(208, "BufferStoreDwordx4", 4, offset: 16, dwords: 4, vectorData: 14, offsetEnabled: true, vectorAddress: 3),
            EndProgram(216));

}
