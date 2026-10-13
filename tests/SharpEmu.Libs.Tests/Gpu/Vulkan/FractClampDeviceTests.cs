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

// [local] V_FRACT_F16/F32/F64 return x - floor(x) clamped below 1.0, as the hardware does: a tiny negative input must
// give the largest value below one, not 1.0 (x - floor(x) rounds to 1.0 there). NaN stays NaN.
public sealed class FractClampDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const int LaneCount = 32;
    private const int LaneBytes = 32;

    // (f32 input, f16 input, f64 input) per lane; the rest of the lanes repeat lane 0.
    private static readonly (uint F32, ushort F16, ulong F64)[] Inputs =
    [
        (0xAEDBE6FFu, 0x8800, 0xBBE5_79A9_7A9A_2A35ul), // -1e-10, -2^-13, -3.6e-20: x - floor(x) rounds to 1.0
        (0x3FC00000u, 0x3E00, 0x3FF8_0000_0000_0000ul), // 1.5
        (0xBE800000u, 0xB400, 0xBFD0_0000_0000_0000ul), // -0.25
        (0x00000000u, 0x0000, 0x0000_0000_0000_0000ul), // 0
        (0x7FC00000u, 0x7E00, 0x7FF8_0000_0000_0000ul), // NaN
        (0xC0490FDBu, 0xC248, 0xC009_21FB_5444_2D18ul), // -pi
        (0x3F7FFFFFu, 0x3BFF, 0x3FEF_FFFF_FFFF_FFFFul), // the largest value below one
        (0xB3800000u, 0x9000, 0xBCA0_0000_0000_0000ul), // -2^-24, -2^-11, -2^-53: exactly the largest value below one
    ];

    [Fact]
    public void FractStaysBelowOne()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Vop2(0, "VLshlrevB32", 2, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(4, "BufferLoadDword", 4, vectorData: 10, offsetEnabled: true, vectorAddress: 2),
            BufferAccess(12, "BufferLoadDword", 4, offset: 4, vectorData: 11, offsetEnabled: true, vectorAddress: 2),
            BufferAccess(20, "BufferLoadDwordx2", 4, offset: 8, dwords: 2, vectorData: 12, offsetEnabled: true, vectorAddress: 2),
            Vop1(28, "VFractF32", 14, Gen5Operand.Vector(10)),
            Vop1(32, "VFractF16", 15, Gen5Operand.Vector(11)),
            Vop1(36, "VFractF64", 16, Gen5Operand.Vector(12)),
            BufferAccess(40, "BufferStoreDword", 4, offset: 16, vectorData: 14, offsetEnabled: true, vectorAddress: 2),
            BufferAccess(48, "BufferStoreDword", 4, offset: 20, vectorData: 15, offsetEnabled: true, vectorAddress: 2),
            BufferAccess(56, "BufferStoreDwordx2", 4, offset: 24, dwords: 2, vectorData: 16, offsetEnabled: true, vectorAddress: 2),
            EndProgram(64));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = LaneCount, ThreadCountX = LaneCount, WaveSize = LaneCount };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var data = new byte[LaneCount * LaneBytes];
        for (var lane = 0; lane < LaneCount; lane++)
        {
            var input = Inputs[lane < Inputs.Length ? lane : 0];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(lane * LaneBytes), input.F32);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(lane * LaneBytes + 4), input.F16);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(lane * LaneBytes + 8), input.F64);
        }

        var buffer = runner.CreateBuffer(data);
        var registers = new uint[256];
        registers[6] = (uint)data.Length;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
        var actual = runner.ReadBack(buffer, 0, (ulong)data.Length);
        harness.AssertNoValidationMessages();
        for (var lane = 0; lane < Inputs.Length; lane++)
        {
            var input = Inputs[lane];
            var at = lane * LaneBytes;
            var f32 = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(at + 16)));
            var f16 = (float)BitConverter.UInt16BitsToHalf((ushort)BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(at + 20)));
            var f64 = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(actual.AsSpan(at + 24)));
            AssertFract(lane, "f32", Expected(BitConverter.UInt32BitsToSingle(input.F32), MathF.BitDecrement(1f)), f32);
            AssertFract(lane, "f16", Expected((float)BitConverter.UInt16BitsToHalf(input.F16), (float)BitConverter.UInt16BitsToHalf(0x3BFF)), f16);
            AssertFract(lane, "f64", Expected(BitConverter.UInt64BitsToDouble(input.F64), Math.BitDecrement(1d)), f64);
        }
    }

    private static double Expected(double value, double belowOne)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        var fraction = value - Math.Floor(value);
        return fraction >= belowOne ? belowOne : fraction;
    }

    private static void AssertFract(int lane, string kind, double expected, double actual)
    {
        if (double.IsNaN(expected))
        {
            Assert.True(double.IsNaN(actual), $"lane={lane} {kind}: expected NaN, got {actual:R}");
            return;
        }

        Assert.True(actual < 1d, $"lane={lane} {kind}: {actual:R} is not below one");
        Assert.True(Math.Abs(expected - actual) <= 1e-3 * Math.Max(1e-30, Math.Abs(expected)) || expected == actual,
            $"lane={lane} {kind}: expected {expected:R}, got {actual:R}");
    }
}
