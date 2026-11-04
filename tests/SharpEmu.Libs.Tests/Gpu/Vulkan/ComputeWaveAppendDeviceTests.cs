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
// claimed exactly once, and a partial last wave claims only its dispatched lanes.
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

    // buffer_atomic_add v4, off, s[4:7], 0 glc: the counter at the start of the buffer, old value back in v4.
    private static Gen5ShaderInstruction ReturningAtomicAdd(uint pc, uint resourceRegister, uint vectorData) =>
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
            ReturningAtomicAdd(48, 4, 4),
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
}
