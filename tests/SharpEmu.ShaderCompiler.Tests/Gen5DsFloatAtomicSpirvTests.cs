// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// DS_MIN_F32/DS_MAX_F32 take ADDR and a single data operand: mem = min/max(mem, DATA0).
// The emulated CAS loop must compare DATA0 against the observed word and store DATA0.
public sealed class Gen5DsFloatAtomicSpirvTests
{
    [Theory]
    [InlineData("DsMinF32", false)]
    [InlineData("DsMaxF32", false)]
    [InlineData("DsMinF32", true)]
    [InlineData("DsMaxF32", true)]
    public void FloatMinMaxCompareAndStoreData0(string opcode, bool gds)
    {
        var program = Program(
            MoveVector(0, 3, 0),
            MoveVector(4, 10, BitConverter.SingleToUInt32Bits(1.5f)),
            DataShare(8, opcode, gds, [Gen5Operand.Vector(3), Gen5Operand.Vector(10)], []),
            EndProgram(16));
        var request = Request(program, userDataCount: 0);

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var compiled, out var error), error);

        var instructions = ReadInstructions(compiled.Spirv);
        var definitions = instructions
            .Where(instruction => HasResult(instruction.Opcode) && instruction.Operands.Length >= 2)
            .ToDictionary(instruction => instruction.Operands[1]);
        var expectedCompare = opcode == "DsMaxF32" ? SpirvOp.FOrdGreaterThan : SpirvOp.FOrdLessThan;
        var loops = 0;
        foreach (var exchange in instructions.Where(instruction => instruction.Opcode == SpirvOp.AtomicCompareExchange))
        {
            if (!definitions.TryGetValue(exchange.Operands[6], out var select) || select.Opcode != SpirvOp.Select ||
                !definitions.TryGetValue(select.Operands[2], out var compare) ||
                compare.Opcode is not (SpirvOp.FOrdLessThan or SpirvOp.FOrdGreaterThan))
                continue;

            loops++;
            Assert.Equal(expectedCompare, compare.Opcode);
            var stored = select.Operands[3];
            var compared = Assert.Contains(compare.Operands[2], definitions);
            Assert.Equal(SpirvOp.Bitcast, compared.Opcode);
            Assert.Equal(stored, compared.Operands[2]);
            Assert.Equal(exchange.Operands[7], select.Operands[4]);
        }

        Assert.Equal(1, loops);
    }

    private static bool HasResult(SpirvOp opcode) => opcode is SpirvOp.AtomicCompareExchange or SpirvOp.Select or
        SpirvOp.FOrdLessThan or SpirvOp.FOrdGreaterThan or SpirvOp.Bitcast or SpirvOp.Load or SpirvOp.Phi or
        SpirvOp.AtomicLoad;

    private static IReadOnlyList<ParsedInstruction> ReadInstructions(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + (index + 1) * sizeof(uint)));
            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private readonly record struct ParsedInstruction(SpirvOp Opcode, uint[] Operands);
}
