// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A chain of workgroups that publish a status word and its value together with
// BUFFER_ATOMIC_SWAP_X2, the way a decoupled look-back scan does: each group takes a ticket,
// publishes (1, 1), waits with a GLC 64-bit load until the previous ticket's status is 2,
// and publishes (2, previous value + 1). The pair must change as a whole: a reader that sees
// the new status with the old value publishes a wrong value.
public sealed class ComputeWideStatusDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint WaveSize = 64;

    [Theory]
    [InlineData(64u)]
    [InlineData(256u)]
    [InlineData(1024u)]
    public void EveryTicketSeesThePreviousStatusAndValueTogether(uint groups)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true) || !vulkan!.SupportsBufferInt64Atomics) return;
        var (plan, resources, layout) = Prepare(CreateProgram());
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = WaveSize, ThreadCountX = groups * WaveSize, WaveSize = WaveSize, SupportsBufferInt64Atomics = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        // The ticket counter in dword 0, one (status, value) pair per ticket from byte 64 on.
        var bytes = 64 + groups * 8;
        var buffer = runner.CreateBuffer(new byte[bytes]);
        var registers = new uint[256];
        registers[6] = bytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, groups));
        var whole = runner.ReadBack(buffer, 0, bytes);
        harness.AssertNoValidationMessages();
        Assert.Equal(groups, BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan(0, 4)));
        var wrong = new List<string>();
        for (var ticket = 0u; ticket < groups; ticket++)
        {
            var status = BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan((int)(64 + ticket * 8)));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan((int)(68 + ticket * 8)));
            if (status != 2 || value != ticket + 1) wrong.Add($"{ticket}:{status}/{value}");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {groups} tickets published a wrong pair: {string.Join(" ", wrong.Take(16))}");
    }

    // buffer_atomic_add v4, off, s[4:7], 0 glc: the counter at the start of the buffer, old value back in v4.
    private static Gen5ShaderInstruction ReturningAtomicAdd(uint pc, uint resourceRegister, uint vectorData) =>
        new(pc, Gen5ShaderEncoding.Mubuf, "BufferAtomicAdd", [0u, 0u],
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand), Gen5Operand.Vector(vectorData)],
            [Gen5Operand.Vector(vectorData)],
            new Gen5BufferMemoryControl(1, 0, vectorData, resourceRegister, 0, false, false, Glc: true, Slc: false));

    // buffer_atomic_swap_x2 v[data:data+1], v[address], s[4:7], 0 offen: no return.
    private static Gen5ShaderInstruction SwapPair(uint pc, uint vectorData, uint vectorAddress) =>
        BufferAccess(pc, "BufferAtomicSwapX2", 4, dwords: 2, vectorData: vectorData, offsetEnabled: true, vectorAddress: vectorAddress);

    private static Gen5ShaderInstruction GlcPairLoad(uint pc, uint vectorData, uint vectorAddress)
    {
        var load = BufferAccess(pc, "BufferLoadDwordx2", 4, dwords: 2, vectorData: vectorData, offsetEnabled: true, vectorAddress: vectorAddress);
        return load with { Control = ((Gen5BufferMemoryControl)load.Control!) with { Glc = true } };
    }

    private static short Offset(uint from, uint to) => (short)(((int)to - (int)from - 4) / 4);

    private static Gen5ShaderProgram CreateProgram() =>
        Program(
            Vopc(0, "VCmpxEqU32", Operand(0), 0),
            MoveVector(8, 4, 1),
            ReturningAtomicAdd(16, 4, 4),
            Vop2(24, "VLshlrevB32", 12, Operand(3), Gen5Operand.Vector(4)),
            Vop2(32, "VAddI32", 12, Operand(64), Gen5Operand.Vector(12)),
            MoveVector(40, 10, 1),
            MoveVector(48, 11, 1),
            SwapPair(56, 10, 12),
            MoveVector(64, 16, 2),
            MoveVector(72, 17, 1),
            Vopc(80, "VCmpEqU32", Operand(0), 4),
            Branch(88, "SCbranchVccnz", Offset(88, 136)),
            Vop2(96, "VSubrevI32", 13, Operand(8), Gen5Operand.Vector(12)),
            GlcPairLoad(104, 14, 13),
            Vopc(112, "VCmpNeU32", Operand(2), 14),
            Branch(120, "SCbranchVccnz", Offset(120, 104)),
            Vop2(128, "VAddI32", 17, Operand(1), Gen5Operand.Vector(15)),
            SwapPair(136, 16, 12),
            EndProgram(144));
}
