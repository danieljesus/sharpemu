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

public sealed class DataShareWriteByteDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(32u, 0u, false, false)]
    [InlineData(32u, 3u, false, false)]
    [InlineData(32u, 0u, true, false)]
    [InlineData(64u, 0u, false, false)]
    [InlineData(64u, 1u, true, false)]
    [InlineData(32u, 0u, false, true)]
    [InlineData(64u, 2u, true, true)]
    public void ByteStoresKeepNeighbouringBytesOfTheSameDword(uint waveSize, uint byteOffset, bool maskOddLanes, bool highHalf)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Gen5DataShareWriteByteTests.CreateReadbackProgram(byteOffset, maskOddLanes, highHalf);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = waveSize, ThreadCountX = waveSize, WaveSize = waveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(512);
        var registers = new uint[256];
        registers[6] = 512;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 512);
        for (var lane = 0; lane < waveSize; lane++)
        {
            var expected = Gen5DataShareWriteByteTests.ExpectedWord(lane, (int)waveSize, byteOffset, maskOddLanes);
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * sizeof(uint))));
            var expectedByte = Gen5DataShareWriteByteTests.ExpectedByte(lane, (int)waveSize, byteOffset, maskOddLanes);
            Assert.Equal(expectedByte, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(256 + lane * sizeof(uint))));
        }
        harness.AssertNoValidationMessages();
    }
}
