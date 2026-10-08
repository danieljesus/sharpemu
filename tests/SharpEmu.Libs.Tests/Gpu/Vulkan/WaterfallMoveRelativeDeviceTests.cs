// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class WaterfallMoveRelativeDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void WaterfallLoopsAroundRelativeMovesAreCollapsed()
    {
        var collapsed = Gen5WaterfallMoveRelative.Collapse(CreateProgram(readM0AfterFirstLoop: false));
        Assert.Equal(3, collapsed.Instructions.Count(instruction => instruction.Opcode == Gen5WaterfallMoveRelative.IndexToM0));
        Assert.DoesNotContain(collapsed.Instructions, instruction => instruction.Opcode == "SCbranchScc1");
    }

    // GTA V's BVH refit waits for the loads between the M0 copy and the relative move.
    [Fact]
    public void LoopsWithAWaitInsideAreCollapsed()
    {
        var collapsed = Gen5WaterfallMoveRelative.Collapse(CreateProgram(readM0AfterFirstLoop: false, waitInsideLoops: true));
        Assert.Equal(3, collapsed.Instructions.Count(instruction => instruction.Opcode == Gen5WaterfallMoveRelative.IndexToM0));
        Assert.DoesNotContain(collapsed.Instructions, instruction => instruction.Opcode == "SCbranchScc1");
    }

    [Fact]
    public void LoopWhoseM0IsReadAfterwardIsKept()
    {
        var collapsed = Gen5WaterfallMoveRelative.Collapse(CreateProgram(readM0AfterFirstLoop: true));
        Assert.Equal(2, collapsed.Instructions.Count(instruction => instruction.Opcode == Gen5WaterfallMoveRelative.IndexToM0));
        Assert.Single(collapsed.Instructions, instruction => instruction.Opcode == "SCbranchScc1");
    }

    [Theory]
    [InlineData(32u, false)]
    [InlineData(64u, false)]
    [InlineData(64u, true)]
    public void PerLaneArrayReadsAndWritesTheLaneIndex(uint waveSize, bool readM0AfterFirstLoop)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(CreateProgram(readM0AfterFirstLoop));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = waveSize, ThreadCountX = waveSize, WaveSize = waveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
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
            Assert.Equal(110u + (uint)(lane & 7), BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }

    // v10..v17 = 110..117 and v30..v37 = 0 in every lane; index (v5) = lane & 7. Three waterfall
    // loops (shaped like a compiler's per-lane array access) run v20 = v[10 + index],
    // v[30 + index] = v20 and v21 = v[30 + index], then each lane stores v21.
    private static Gen5ShaderProgram CreateProgram(bool readM0AfterFirstLoop, bool waitInsideLoops = false)
    {
        var code = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        uint Add(Func<uint, Gen5ShaderInstruction> make)
        {
            var at = pc;
            code.Add(make(at));
            pc += 8;
            return at;
        }

        for (var register = 0u; register < 8; register++)
        {
            var low = 10 + register;
            var high = 30 + register;
            Add(at => MoveVector(at, low, 110 + low - 10));
            Add(at => MoveVector(at, high, 0));
        }

        Add(at => Vop2(at, "VAndB32", 5, Operand(7), Gen5Operand.Vector(0)));
        Add(at => Sop1(at, "SMovB64", 0, Gen5Operand.Scalar(126)));
        Add(at => Sop1(at, "SMovB64", 126, Gen5Operand.Scalar(0)));
        Add(at => Sop1(at, "SMovB64", 2, Gen5Operand.Scalar(0)));

        void Waterfall(string opcode, uint destination, uint source, bool readM0After)
        {
            var head = Add(at => ReadFirstLane(at, 10, 5));
            Add(at => Vopc(at, "VCmpxEqU32", Gen5Operand.Scalar(10), 5));
            Add(at => MoveScalarRegister(at, 124, 10));
            if (waitInsideLoops)
                Add(at => Wait(at));
            Add(at => Vop1(at, opcode, destination, Gen5Operand.Vector(source)));
            Add(at => Sop2(at, "SAndn2B64", 2, Gen5Operand.Scalar(2), Gen5Operand.Scalar(126)));
            Add(at => Sop1(at, "SMovB64", 126, Gen5Operand.Scalar(2)));
            Add(at => Branch(at, "SCbranchScc1", (short)(((long)head - (at + 4)) / 4)));
            Add(at => Sop1(at, "SMovB64", 126, Gen5Operand.Scalar(0)));
            Add(at => Sop1(at, "SMovB64", 2, Gen5Operand.Scalar(0)));
            if (readM0After)
                Add(at => MoveScalarRegister(at, 11, 124));
        }

        Waterfall("VMovrelsB32", 20, 10, readM0AfterFirstLoop);
        Waterfall("VMovreldB32", 30, 20, false);
        Waterfall("VMovrelsB32", 21, 30, false);
        // M0 is rewritten before the store, which conservatively counts as reading M0.
        Add(at => MoveScalar(at, 124, 0));
        Add(at => Vop2(at, "VLshlrevB32", 3, Operand(2), Gen5Operand.Vector(0)));
        Add(at => BufferAccess(at, "BufferStoreDword", 4, vectorData: 21, offsetEnabled: true, vectorAddress: 3));
        code.Add(EndProgram(pc));
        return Program([.. code]);
    }

    private static Gen5ShaderInstruction Wait(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C3F70], [], [], null);
}
