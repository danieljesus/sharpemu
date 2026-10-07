// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class MoveRelativeBudgetDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    // M0 comes from V_READFIRSTLANE, so no relative move can be bounded and each one selects
    // over the whole register range.
    private const int MovesOverBudget = 120;
    private const int MovesUnderBudget = 4;

    [Theory]
    [InlineData(MovesUnderBudget, false)]
    [InlineData(MovesOverBudget, true)]
    public void ProgramsPastTheSelectBudgetIndexOneRegisterArray(int moves, bool expectArray)
    {
        var shader = Compile(CreateProgram(moves), waveSize: 64);
        Assert.Equal(expectArray, ContainsName(shader.Spirv, "vgpr"));
        if (expectArray)
        {
            // The select form of this program is several megabytes.
            Assert.True(shader.Spirv.Length < 256 * 1024, $"{shader.Spirv.Length} bytes");
        }
    }

    [Theory]
    [InlineData(MovesUnderBudget, 32u)]
    [InlineData(MovesOverBudget, 32u)]
    [InlineData(MovesOverBudget, 64u)]
    public void RelativeMovesReadAndWriteTheRegisterAtTheM0Offset(int moves, uint waveSize)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var shader = Compile(CreateProgram(moves), waveSize, out var request);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 256);
        for (var lane = 0; lane < waveSize; lane++)
        {
            // v_movrels v1, v10 reads v13; v_movreld v30, v1 writes v33.
            Assert.Equal(113u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }

    // v10..v20 = 110..120, M0 = readfirstlane(v0) + 3 (3 for lane 0), then
    // v1 = v[10 + M0], v[30 + M0] = v1, and each lane stores v33 at lane * 4.
    private static Gen5ShaderProgram CreateProgram(int moves)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        Gen5ShaderInstruction Next(Func<uint, Gen5ShaderInstruction> make)
        {
            var instruction = make(pc);
            pc += 8;
            return instruction;
        }

        for (var register = 10u; register <= 20u; register++)
        {
            var value = 100 + register;
            var target = register;
            instructions.Add(Next(at => MoveVector(at, target, value)));
        }

        instructions.Add(Next(at => MoveVector(at, 33, 0)));
        instructions.Add(Next(at => ReadFirstLane(at, 124, 0)));
        instructions.Add(Next(at => Sop2(at, "SAddI32", 124, Gen5Operand.Scalar(124), Operand(3))));
        for (var move = 0; move < moves; move++)
            instructions.Add(Next(at => Vop1(at, "VMovrelsB32", 1, Gen5Operand.Vector(10))));
        instructions.Add(Next(at => Vop1(at, "VMovreldB32", 30, Gen5Operand.Vector(1))));
        instructions.Add(Next(at => Vop2(at, "VLshlrevB32", 3, Operand(2), Gen5Operand.Vector(0))));
        instructions.Add(Next(at => BufferAccess(at, "BufferStoreDword", 4, vectorData: 33, offsetEnabled: true, vectorAddress: 3)));
        instructions.Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    private static Gen5SpirvShader Compile(Gen5ShaderProgram program, uint waveSize) =>
        Compile(program, waveSize, out _);

    private static Gen5SpirvShader Compile(Gen5ShaderProgram program, uint waveSize, out ShaderCompileRequest request)
    {
        var (plan, resources, layout) = Prepare(program);
        request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = waveSize, ThreadCountX = waveSize, WaveSize = waveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
    }

    private static bool ContainsName(byte[] spirv, string name) =>
        spirv.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name + "\0")) >= 0;
}
