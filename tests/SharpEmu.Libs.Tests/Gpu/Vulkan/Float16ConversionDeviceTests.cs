// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// The native f16 conversions must agree bit for bit with the explicit integer sequences
// they replace, except for the payload of a NaN, which only has to stay a NaN.
public sealed class Float16ConversionDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const int LaneCount = 32;
    private const int LaneBytes = 16;

    [Fact]
    public void NativeConversionsMatchTheIntegerSequences()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true, shaderFloat16Conversions: true)) return;
        var emulated = Run(vulkan, native: false);
        var native = Run(vulkan, native: true);
        for (var lane = 0; lane < LaneCount; lane++)
        {
            var input = Gen5Float16ConversionTests.RoundTripInputs[lane];
            AssertSameFloat(input, emulated[lane].Widened, native[lane].Widened);
            AssertSameHalf(input, emulated[lane].Narrowed, native[lane].Narrowed);
            AssertSameHalf(input, emulated[lane].Squared, native[lane].Squared);
        }
    }

    private static (uint Widened, uint Narrowed, uint Squared)[] Run(HeadlessVulkan vulkan, bool native)
    {
        var (plan, resources, layout) = Prepare(Gen5Float16ConversionTests.CreateRoundTripProgram());
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = LaneCount, ThreadCountX = LaneCount, WaveSize = LaneCount, SupportsFloat16Conversions = native,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var data = new byte[LaneCount * LaneBytes];
        for (var lane = 0; lane < LaneCount; lane++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(lane * LaneBytes), Gen5Float16ConversionTests.RoundTripInputs[lane]);
        var buffer = runner.CreateBuffer(data);
        var registers = new uint[256];
        registers[6] = (uint)data.Length;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
        var actual = runner.ReadBack(buffer, 0, (ulong)data.Length);
        harness.AssertNoValidationMessages();
        var results = new (uint, uint, uint)[LaneCount];
        for (var lane = 0; lane < LaneCount; lane++)
        {
            var at = lane * LaneBytes;
            results[lane] = (
                BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(at + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(at + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(at + 12)));
        }

        return results;
    }

    private static void AssertSameFloat(uint input, uint expected, uint actual)
    {
        if (float.IsNaN(BitConverter.UInt32BitsToSingle(expected)))
            Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(actual)), $"input=0x{input:X8} expected NaN, got 0x{actual:X8}");
        else
            Assert.True(expected == actual, $"input=0x{input:X8} expected 0x{expected:X8}, got 0x{actual:X8}");
    }

    private static void AssertSameHalf(uint input, uint expected, uint actual)
    {
        static bool IsNaN(uint word) => (word & 0x7C00) == 0x7C00 && (word & 0x03FF) != 0;
        if (IsNaN(expected))
            Assert.True(IsNaN(actual) && (expected >> 16) == (actual >> 16), $"input=0x{input:X8} expected a NaN like 0x{expected:X8}, got 0x{actual:X8}");
        else
            Assert.True(expected == actual, $"input=0x{input:X8} expected 0x{expected:X8}, got 0x{actual:X8}");
    }
}
