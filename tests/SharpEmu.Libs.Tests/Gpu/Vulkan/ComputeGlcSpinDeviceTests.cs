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

// A chain of workgroups that hand a value on through memory, the way a decoupled look-back
// scan does: each group takes a ticket with a returning atomic, waits with a GLC load until
// the previous ticket's slot is published, and publishes that value plus one. A GLC load
// must see another workgroup's store; a load served from a stale cached copy spins until
// the translator's step guard ends it and publishes a wrong value.
public sealed class ComputeGlcSpinDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint WaveSize = 64;

    [Theory]
    [InlineData(8u)]
    [InlineData(64u)]
    [InlineData(256u)]
    public void EveryTicketSeesThePreviousTicketsValue(uint groups)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(CreateProgram());
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = WaveSize, ThreadCountX = groups * WaveSize, WaveSize = WaveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        // The ticket counter in dword 0, the published values from byte 64 on.
        var bytes = 64 + groups * sizeof(uint);
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
            var value = BinaryPrimitives.ReadUInt32LittleEndian(whole.AsSpan((int)(64 + ticket * sizeof(uint))));
            if (value != ticket + 1) wrong.Add($"{ticket}:{value}");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {groups} tickets published a wrong value: {string.Join(" ", wrong.Take(16))}");
    }

    // buffer_atomic_add v4, off, s[4:7], 0 glc: the counter at the start of the buffer, old value back in v4.
    private static Gen5ShaderInstruction ReturningAtomicAdd(uint pc, uint resourceRegister, uint vectorData) =>
        new(pc, Gen5ShaderEncoding.Mubuf, "BufferAtomicAdd", [0u, 0u],
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand), Gen5Operand.Vector(vectorData)],
            [Gen5Operand.Vector(vectorData)],
            new Gen5BufferMemoryControl(1, 0, vectorData, resourceRegister, 0, false, false, Glc: true, Slc: false));

    private static Gen5ShaderInstruction GlcLoad(uint pc, uint vectorData, uint vectorAddress)
    {
        var load = BufferAccess(pc, "BufferLoadDword", 4, vectorData: vectorData, offsetEnabled: true, vectorAddress: vectorAddress);
        return load with { Control = ((Gen5BufferMemoryControl)load.Control!) with { Glc = true } };
    }

    private static short Offset(uint from, uint to) => (short)(((int)to - (int)from - 4) / 4);

    private static Gen5ShaderProgram CreateProgram() =>
        Program(
            Vopc(0, "VCmpxEqU32", Operand(0), 0),
            MoveVector(8, 4, 1),
            ReturningAtomicAdd(16, 4, 4),
            MoveVector(24, 5, 0),
            Vopc(32, "VCmpEqU32", Operand(0), 4),
            Branch(40, "SCbranchVccnz", Offset(40, 88)),
            Vop2(48, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(4)),
            Vop2(56, "VAddI32", 6, Operand(60), Gen5Operand.Vector(6)),
            GlcLoad(64, 5, 6),
            Vopc(72, "VCmpEqU32", Operand(0), 5),
            Branch(80, "SCbranchVccnz", Offset(80, 48)),
            Vop2(88, "VAddI32", 7, Operand(1), Gen5Operand.Vector(5)),
            Vop2(96, "VLshlrevB32", 8, Operand(2), Gen5Operand.Vector(4)),
            Vop2(104, "VAddI32", 8, Operand(64), Gen5Operand.Vector(8)),
            BufferAccess(112, "BufferStoreDword", 4, vectorData: 7, offsetEnabled: true, vectorAddress: 8),
            EndProgram(120));
}
