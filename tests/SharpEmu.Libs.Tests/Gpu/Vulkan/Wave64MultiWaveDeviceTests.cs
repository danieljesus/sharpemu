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

// Wave64 compute workgroups that hold several guest waves: lane reads that cross the
// 32-lane half of a wave must see the other half, as the scan in a radix sort does.
public sealed class Wave64MultiWaveDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Exec = 126;
    private static readonly Gen5Operand AllLanes = Gen5Operand.Source(193);

    private readonly List<Gen5ShaderInstruction> _instructions = [];
    private uint _pc;

    private uint Add(Gen5ShaderInstruction instruction)
    {
        var pc = _pc;
        _instructions.Add(instruction with { Pc = pc });
        _pc += 8;
        return pc;
    }

    private void Point(uint branchPc, uint targetPc)
    {
        var index = _instructions.FindIndex(instruction => instruction.Pc == branchPc);
        var offset = (short)(((int)targetPc - (int)branchPc - 4) / 4);
        _instructions[index] = _instructions[index] with { Words = [unchecked((uint)(ushort)offset)] };
    }

    private bool Ready() => GatePrerequisites.Ready(fixture.Vulkan, shaderInt64: true);

    private uint[] Run(uint threads, uint? dispatchedThreads = null)
    {
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. _instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = threads, ThreadCountX = dispatchedThreads ?? threads, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotNull(fixture.Vulkan);
        using var harness = new ImageTestHarness(fixture.Vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(threads * sizeof(uint));
        var registers = new uint[256];
        registers[6] = threads * sizeof(uint);
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1,
            dispatchThreadLimits: dispatchedThreads is { } limit ? [limit, 1, 1] : null));
        var bytes = runner.ReadBack(output, 0, threads * sizeof(uint));
        harness.AssertNoValidationMessages();
        var values = new uint[threads];
        for (var thread = 0; thread < values.Length; thread++)
        {
            values[thread] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(thread * sizeof(uint)));
        }

        return values;
    }

    // The carry step of a wave64 scan: with EXEC on one half, v_readlane takes a lane of the other.
    [Theory]
    [InlineData(64u, 31u, true)]
    [InlineData(64u, 63u, false)]
    [InlineData(256u, 31u, true)]
    [InlineData(256u, 63u, false)]
    [InlineData(256u, 5u, true)]
    [InlineData(128u, 40u, false)]
    public void ReadLane_ReachesTheOtherHalfOfItsWave(uint threads, uint lane, bool upperHalfActive)
    {
        if (!Ready()) return;
        Add(MoveScalar(0, 20, 0xDEAD));
        Add(upperHalfActive
            ? Sop2(0, "SBfmB64", Exec, Operand(32), Operand(32))
            : Sop2(0, "SBfmB64", Exec, Operand(32), Operand(0)));
        Add(ReadLane(0, 20, 0, lane));
        Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Add(MoveVectorFromScalar(0, 4, 20));

        var values = Run(threads);
        for (uint thread = 0; thread < threads; thread++)
        {
            Assert.True((thread & ~63u) + lane == values[thread], $"thread {thread}: {values[thread]}");
        }
    }

    // A wave-uniform branch that only the first wave takes, with a cross-half read inside:
    // the other waves never reach the read, so pairing must stay within the wave.
    [Fact]
    public void ReadLane_InABranchOnlyOneWaveTakes_StillPairsItsHalves()
    {
        if (!Ready()) return;
        Add(MoveScalar(0, 20, 7));
        Add(Vopc(0, "VCmpxGtU32", Operand(64), 0));
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        Add(Sop2(0, "SBfmB64", Exec, Operand(32), Operand(32)));
        Add(ReadLane(0, 20, 0, 31));
        var join = Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Point(skip, join);
        Add(MoveVectorFromScalar(0, 4, 20));

        var values = Run(256);
        for (uint thread = 0; thread < 256; thread++)
        {
            Assert.True((thread < 64 ? 31u : 7u) == values[thread], $"thread {thread}: {values[thread]}");
        }
    }

    // The last wave of a partial dispatch has no running upper half: the lower half's
    // lane reads still meet, and the dispatch ends.
    [Fact]
    public void APartialDispatch_WithoutAnUpperHalf_StillReadsItsLanes()
    {
        if (!Ready()) return;
        Add(MoveScalar(0, 20, 0xDEAD));
        Add(ReadLane(0, 20, 0, 5));
        Add(MoveVectorFromScalar(0, 4, 20));

        var values = Run(256, 200);
        for (uint thread = 0; thread < 200; thread++)
        {
            Assert.True((thread & ~63u) + 5 == values[thread], $"thread {thread}: {values[thread]}");
        }
    }

    // A returning LDS add counts in lane order on the guest: the lower half of a wave64 before
    // the upper one, and inside a half from the lowest lane. A radix sort's ranks rely on it.
    [Theory]
    [InlineData(64u)]
    [InlineData(256u)]
    public void AReturningLdsAdd_RanksLanesInOrder(uint threads)
    {
        if (!Ready()) return;
        // v2 = lane in wave, v3 = 4 * (lane % 4) (the LDS word), v6 = 1 << (8 * wave), v7 = 8 * wave.
        Add(Vop2(0, "VAndB32", 2, Operand(63), Gen5Operand.Vector(0)));
        Add(Vop2(0, "VAndB32", 3, Operand(3), Gen5Operand.Vector(0)));
        Add(Vop2(0, "VLshlrevB32", 3, Operand(2), Gen5Operand.Vector(3)));
        Add(Vop2(0, "VLshrrevB32", 7, Operand(6), Gen5Operand.Vector(0)));
        Add(Vop2(0, "VLshlrevB32", 7, Operand(3), Gen5Operand.Vector(7)));
        Add(Vop2(0, "VLshlrevB32", 6, Gen5Operand.Vector(7), Operand(1)));
        Add(MoveVector(0, 8, 0));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(8)], []));
        Add(Sop0Barrier());
        Add(DataShare(0, "DsAddRtnU32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(6)], [9]));
        Add(Vop3(0, "VBfeU32", 4, Gen5Operand.Vector(9), Gen5Operand.Vector(7), Operand(8)));

        var values = Run(threads);
        for (uint thread = 0; thread < threads; thread++)
        {
            Assert.True(((thread & 63) >> 2) == values[thread], $"thread {thread}: rank {values[thread]}; wave 0: {string.Join(",", values.Take(64))}");
        }
    }

    private static Gen5ShaderInstruction Sop0Barrier() =>
        new(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null);
}
