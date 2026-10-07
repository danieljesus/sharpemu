// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataShareWriteByteTests
{
    public const uint InitialWord = 0xDEADBEEF;

    [Fact]
    public void DecodesTheEncodingSeenInGrandTheftAutoV()
    {
        // ds_write_b8 v0, v2 offset:512, taken from a compute shader in PPSA04264.
        var write = Decode(0xD8780200, 0x00000200);

        Assert.Equal("DsWriteB8", write.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(2) }, write.Sources);
        Assert.Empty(write.Destinations);
        var control = Assert.IsType<Gen5DataShareControl>(write.Control);
        Assert.Equal(0x200u, control.SingleOffsetBytes);
        Assert.False(control.Gds);
    }

    [Fact]
    public void DecodesTheHighHalfVariant()
    {
        var write = Decode(0xDA800200, 0x00000200);

        Assert.Equal("DsWriteB8D16Hi", write.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(2) }, write.Sources);
        Assert.Equal(0x200u, Assert.IsType<Gen5DataShareControl>(write.Control).SingleOffsetBytes);
    }

    [Fact]
    public void DecodesTheUnsignedByteRead()
    {
        // ds_read_u8 v5, v0 offset:512, from the same shader.
        var read = Decode(0xD8E80200, 0x05000000);

        Assert.Equal("DsReadU8", read.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(0) }, read.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(5) }, read.Destinations);
        Assert.Equal(0x200u, Assert.IsType<Gen5DataShareControl>(read.Control).SingleOffsetBytes);
    }

    [Theory]
    [InlineData(0u, false, false)]
    [InlineData(3u, false, false)]
    [InlineData(0u, true, false)]
    [InlineData(1u, false, true)]
    public void CompilesToSpirv(uint byteOffset, bool maskOddLanes, bool highHalf)
    {
        var (plan, resources, layout) = Prepare(CreateReadbackProgram(byteOffset, maskOddLanes, highHalf));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    // Every lane fills its own dword with InitialWord, then lane N stores the byte N at
    // byte address N + byteOffset, so four lanes share each written dword. The other bits
    // of the data register hold junk that must not reach memory. Each lane finally reads
    // its dword back and the byte at address N with DS_READ_U8, and stores both to the
    // result buffer (dwords at lane * 4 and bytes at 256 + lane * 4).
    public static Gen5ShaderProgram CreateReadbackProgram(uint byteOffset, bool maskOddLanes, bool highHalf) => Program(
        Vop2(0, "VLshlrevB32", 3, Operand(2), Gen5Operand.Vector(0)),
        MoveVector(4, 10, InitialWord),
        DataShare(8, "DsWriteB32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(10)], []),
        Vop2(16, "VLshlrevB32", 11, Operand(highHalf ? 16u : 0u), Gen5Operand.Vector(0)),
        Vop2(20, "VAddI32", 11, Operand(highHalf ? 0x010000AAu : 0x100u), Gen5Operand.Vector(11)),
        Vop2(24, "VAndB32", 6, Operand(maskOddLanes ? 1u : 0u), Gen5Operand.Vector(0)),
        Vopc(28, "VCmpxEqU32", Operand(0), 6),
        Decode((highHalf ? 0xDA800000u : 0xD8780000u) | byteOffset, 11u << 8) with { Pc = 32 },
        MoveScalar(40, 126, uint.MaxValue),
        MoveScalar(44, 127, uint.MaxValue),
        DataShare(48, "DsReadB32", false, [Gen5Operand.Vector(3)], [4]),
        BufferAccess(56, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 3),
        Decode(0xD8E80000, 7u << 24) with { Pc = 64 },
        Vop2(72, "VAddI32", 8, Operand(256), Gen5Operand.Vector(3)),
        BufferAccess(76, "BufferStoreDword", 4, vectorData: 7, offsetEnabled: true, vectorAddress: 8),
        EndProgram(84));

    public static uint ExpectedWord(int lane, int waveSize, uint byteOffset, bool maskOddLanes)
    {
        var bytes = BitConverter.GetBytes(InitialWord);
        for (var index = 0; index < 4; index++)
        {
            var writer = lane * 4 + index - (int)byteOffset;
            if (writer >= 0 && writer < waveSize && !(maskOddLanes && (writer & 1) != 0))
                bytes[index] = (byte)writer;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    public static uint ExpectedByte(int lane, int waveSize, uint byteOffset, bool maskOddLanes) =>
        (ExpectedWord(lane / 4, waveSize, byteOffset, maskOddLanes) >> (lane % 4 * 8)) & 0xFF;

    private static Gen5ShaderInstruction Decode(uint word0, uint word1)
    {
        uint[] words = [word0, word1, 0xBF810000];
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
