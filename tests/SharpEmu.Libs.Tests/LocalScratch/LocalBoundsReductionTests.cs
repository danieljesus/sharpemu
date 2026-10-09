// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.LocalScratch;

// [local] The tail of GTA V's bounds reduction (0xF1D19431555095C0): lane 0 seeds LDS with
// +inf/-inf, every lane ds_min/ds_max's its value, lane 0 reads back and buffer_atomic_fmin/fmax's.
public sealed class LocalBoundsReductionTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void ReductionTail()
    {
        var path = "C:/Users/danyy/AppData/Local/Temp/gta/f1d1_tail.bin";
        if (!File.Exists(path) || Environment.GetEnvironmentVariable("LOCAL_BOUNDS") != "1") return;
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var bytes = File.ReadAllBytes(path);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new Mem(bytes), Generation.Gen5), 0x1000, out var program, out var error), error);
        var (plan, resources, layout) = Prepare(program, userDataCount: 16);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64, LocalDataShareDwords = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        for (var run = 0; run < 5; run++)
        {
            var init = new byte[24];
            for (var i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(init.AsSpan(i * 4), float.PositiveInfinity);
            for (var i = 3; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(init.AsSpan(i * 4), float.NegativeInfinity);
            var buffer = runner.CreateBuffer(init);
            var registers = new uint[256];
            registers[6] = 24;
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
            var result = runner.ReadBack(buffer, 0, 24);
            var floats = Enumerable.Range(0, 6).Select(i => BinaryPrimitives.ReadSingleLittleEndian(result.AsSpan(i * 4))).ToArray();
            output.WriteLine($"run {run}: [{string.Join(", ", floats)}] (expected -20 x3, 43 x3)");
            buffer.Dispose();
        }

        harness.AssertNoValidationMessages();
    }

    private sealed class Mem(byte[] b) : ICpuMemory
    {
        public bool TryRead(ulong a, Span<byte> d)
        {
            if (a < 0x1000 || a - 0x1000 + (ulong)d.Length > (ulong)b.Length) return false;
            b.AsSpan((int)(a - 0x1000), d.Length).CopyTo(d);
            return true;
        }

        public bool TryWrite(ulong a, ReadOnlySpan<byte> s) => false;
    }
}
