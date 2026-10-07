// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Float64UnaryTests
{
    [Theory]
    // v_cvt_f64_f32 v[2:3], v5
    [InlineData(0x7E042105u, null)]
    // v_cvt_f64_f32 v[2:3], |v5| (VOP3 form)
    [InlineData(0xD5900102u, 0x00000105u)]
    public void DecodesAndCompilesToSpirv(uint word0, uint? word1)
    {
        var convert = Decode(word0, word1);

        Assert.Equal("VCvtF64F32", convert.Opcode);
        Assert.Equal(Gen5Operand.Vector(5), convert.Sources[0]);
        Assert.Equal(new[] { Gen5Operand.Vector(2) }, convert.Destinations);
        if (word1 is not null) Assert.Equal(1u, Assert.IsType<Gen5Vop3Control>(convert.Control).AbsoluteMask);

        var program = Program(
            MoveVector(0, 5, BitConverter.SingleToUInt32Bits(-1.5f)),
            convert with { Pc = 4 },
            BufferAccess(12, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 2),
            EndProgram(20));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    [Theory]
    // v_fract_f64 v[2:3], v[4:5]
    [InlineData(0x7E047D04u, null)]
    // v_fract_f64 v[2:3], -v[4:5] (VOP3 form)
    [InlineData(0xD5BE0002u, 0x20000104u)]
    public void FractDecodesAndCompilesToSpirv(uint word0, uint? word1)
    {
        var fract = Decode(word0, word1);

        Assert.Equal("VFractF64", fract.Opcode);
        Assert.Equal(Gen5Operand.Vector(4), fract.Sources[0]);
        Assert.Equal(new[] { Gen5Operand.Vector(2) }, fract.Destinations);
        if (word1 is not null) Assert.Equal(1u, Assert.IsType<Gen5Vop3Control>(fract.Control).NegateMask);

        var program = Program(
            MoveVector(0, 4, 0),
            MoveVector(4, 5, 0x400C0000),
            fract with { Pc = 8 },
            BufferAccess(16, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 2),
            EndProgram(24));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    private static Gen5ShaderInstruction Decode(uint word0, uint? word1)
    {
        uint[] words = word1 is { } second ? [word0, second, 0xBF810000] : [word0, 0xBF810000];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new InstructionMemory(bytes), Generation.Gen5),
            0x1000, out var program, out var error), error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
