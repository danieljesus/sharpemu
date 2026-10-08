// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Float16ConversionTests
{
    // Each lane converts its input word f16 -> f32 -> f16, squares it as f16 and stores
    // the three results; the buffer is read and written through s[4:7] at lane * 16.
    public static Gen5ShaderProgram CreateRoundTripProgram() =>
        Program(
            Vop2(0, "VLshlrevB32", 2, Operand(4), Gen5Operand.Vector(0)),
            BufferAccess(4, "BufferLoadDword", 4, vectorData: 1, offsetEnabled: true, vectorAddress: 2),
            Vop1(12, "VCvtF32F16", 3, Gen5Operand.Vector(1)),
            Vop1(16, "VCvtF16F32", 4, Gen5Operand.Vector(3)),
            Vop2(20, "VMulF16", 5, Gen5Operand.Vector(1), Gen5Operand.Vector(1)),
            BufferAccess(24, "BufferStoreDword", 4, offset: 4, vectorData: 3, offsetEnabled: true, vectorAddress: 2),
            BufferAccess(32, "BufferStoreDword", 4, offset: 8, vectorData: 4, offsetEnabled: true, vectorAddress: 2),
            BufferAccess(40, "BufferStoreDword", 4, offset: 12, vectorData: 5, offsetEnabled: true, vectorAddress: 2),
            EndProgram(48));

    // Normals, subnormals, zeros, infinities, NaNs, the largest finite value and words with
    // bits above the half, which the conversion must ignore.
    public static readonly uint[] RoundTripInputs =
    [
        0x3C00, 0xBC00, 0x3555, 0x7BFF, 0xFBFF, 0x0001, 0x8001, 0x03FF, 0x0400, 0x0000, 0x8000,
        0x7C00, 0xFC00, 0x7E00, 0xFC01, 0x7C01, 0x1234, 0x5678, 0x9ABC, 0xDEF0, 0x4248, 0xC248,
        0xABCD3C00, 0xFFFF0001, 0x12347C00, 0x00017E00, 0x3800, 0x3C01, 0x6400, 0x2E66, 0x0200, 0x8200,
    ];

    private static ShaderCompileRequest Request(bool native)
    {
        var (plan, resources, layout) = Prepare(CreateRoundTripProgram());
        return new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32, SupportsFloat16Conversions = native,
        };
    }

    [Fact]
    public void NativeConversionsDeclareTheFloat16ControlsAndShrinkTheModule()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(native: false), out var emulated, out var error), error);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(native: true), out var native, out error), error);

        var emulatedOps = Instructions(emulated.Spirv);
        var nativeOps = Instructions(native.Spirv);
        Assert.DoesNotContain(emulatedOps, op => op.Opcode == (ushort)SpirvOp.FConvert);
        Assert.DoesNotContain(Capabilities(emulated.Spirv), c => c == (uint)SpirvCapability.Float16);

        Assert.Contains(nativeOps, op => op.Opcode == (ushort)SpirvOp.FConvert);
        var capabilities = Capabilities(native.Spirv);
        Assert.Contains((uint)SpirvCapability.Float16, capabilities);
        Assert.Contains((uint)SpirvCapability.DenormPreserve, capabilities);
        Assert.Contains((uint)SpirvCapability.SignedZeroInfNanPreserve, capabilities);
        Assert.Contains((uint)SpirvCapability.RoundingModeRTE, capabilities);
        var modes = nativeOps.Where(op => op.Opcode == (ushort)SpirvOp.ExecutionMode).Select(op => (op.Operands[1], op.Operands[2])).ToArray();
        Assert.Contains(((uint)SpirvExecutionMode.DenormPreserve, 16u), modes);
        Assert.Contains(((uint)SpirvExecutionMode.SignedZeroInfNanPreserve, 16u), modes);
        Assert.Contains(((uint)SpirvExecutionMode.RoundingModeRTE, 16u), modes);
        // Four conversions of about forty instructions each become four of about four.
        Assert.True(nativeOps.Count + 100 <= emulatedOps.Count, $"native={nativeOps.Count} emulated={emulatedOps.Count}");
    }

    [Fact]
    public void WithoutTheDeviceSupportNoFloat16ControlsAreDeclared()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(native: false), out var shader, out var error), error);
        Assert.DoesNotContain(Instructions(shader.Spirv), op => op.Opcode == (ushort)SpirvOp.ExecutionMode && op.Operands[1] >= 4459);
    }

    internal static IReadOnlyList<uint> Capabilities(byte[] spirv) =>
        Instructions(spirv).Where(op => op.Opcode == (ushort)SpirvOp.Capability).Select(op => op.Operands[0]).ToArray();

    internal static IReadOnlyList<(ushort Opcode, uint[] Operands)> Instructions(byte[] spirv)
    {
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));
        var instructions = new List<(ushort, uint[])>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + (index + 1) * sizeof(uint)));
            instructions.Add(((ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }
}
