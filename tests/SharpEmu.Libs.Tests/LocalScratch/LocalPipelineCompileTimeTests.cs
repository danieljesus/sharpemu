// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.LocalScratch;

// [local] Times SPIR-V translation and vkCreateComputePipelines for a dumped guest shader.
public sealed class LocalPipelineCompileTimeTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void TimeDumpedComputeShader()
    {
        var path = Environment.GetEnvironmentVariable("LOCAL_SHADER_BIN");
        if (string.IsNullOrEmpty(path)) return;
        var waveSize = uint.Parse(Environment.GetEnvironmentVariable("LOCAL_WAVE") ?? "64");
        var bytes = File.ReadAllBytes(path);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new Mem(bytes), Generation.Gen5), 0x1000, out var program, out var error), error);
        if (int.TryParse(Environment.GetEnvironmentVariable("LOCAL_TRUNCATE"), out var keep) && keep < program.Instructions.Count)
        {
            var kept = program.Instructions.Take(keep).ToList();
            var end = kept[^1].Pc + 8;
            kept.Add(EndProgram(end));
            program = Program([.. kept]);
        }

        if (Environment.GetEnvironmentVariable("LOCAL_DROP_BACKEDGES") == "1")
        {
            program = Program([.. program.Instructions.Select(i =>
                i.Opcode.StartsWith("SCbranch", StringComparison.Ordinal) && (short)(i.Words[0] & 0xFFFF) < 0 ? Nop(i.Pc) : i)]);
        }

        var (plan, resources, layout) = Prepare(program, userDataCount: 16);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = waveSize, ThreadCountX = waveSize, WaveSize = waveSize };
        var watch = Stopwatch.StartNew();
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        var translate = watch.Elapsed;
        File.WriteAllBytes(path + ".spv", shader.Spirv);
        Console.Error.WriteLine($"[LOCAL] instructions={program.Instructions.Count} spirv_bytes={shader.Spirv.Length} translate_ms={translate.TotalMilliseconds:F0}");
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        using var harness = new ImageTestHarness(vulkan);
        watch.Restart();
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        Console.Error.WriteLine($"[LOCAL] pipeline_ms={watch.Elapsed.TotalMilliseconds:F0}");
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
