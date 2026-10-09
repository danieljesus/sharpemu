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

// [local] Replays GTA V's single-pass scan (0x35B85620669D35A4) on a dumped input.
// LOCAL_SCAN_REPLAY=1, LOCAL_SCAN_INPUT=<dump of the counts>, LOCAL_SCAN_RUNS=<n>.
public sealed class LocalScanReplayTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const string Shader = "C:/Users/danyy/AppData/Local/Temp/gta/shaderbins/shader_35B85620669D35A4.bin";
    private const ulong CounterBase = 0x1000000, StatusBase = 0x2000000, DataBase = 0x3000000, TotalBase = 0x4000000;

    [Fact]
    public void ReplayScan()
    {
        if (Environment.GetEnvironmentVariable("LOCAL_SCAN_REPLAY") != "1") return;
        var input = File.ReadAllBytes(Environment.GetEnvironmentVariable("LOCAL_SCAN_INPUT")!);
        var runs = int.Parse(Environment.GetEnvironmentVariable("LOCAL_SCAN_RUNS") ?? "5");
        var items = (uint)(input.Length / 4);
        const uint perGroup = 256;
        var groups = (items + perGroup - 1) / perGroup;
        var counts = Enumerable.Range(0, (int)items).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(i * 4))).ToArray();
        var expected = new uint[items];
        uint sum = 0;
        for (var i = 0; i < items; i++) { expected[i] = sum; sum += counts[i]; }
        output.WriteLine($"items={items} groups={groups} total={sum}");

        var bytes = File.ReadAllBytes(Shader);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new Mem(bytes), Generation.Gen5), 0x1000, out var program, out var error), error);
        var plan = Extract(program, userDataCount: 16);
        var userData = new uint[16];
        userData[0] = (uint)CounterBase; userData[1] = (uint)(CounterBase >> 32) | (4u << 16); userData[2] = 1; userData[3] = 0x5204;
        userData[4] = (uint)StatusBase; userData[5] = (uint)(StatusBase >> 32);
        userData[6] = (uint)DataBase; userData[7] = (uint)(DataBase >> 32);
        userData[8] = (uint)TotalBase; userData[9] = (uint)(TotalBase >> 32);
        userData[10] = perGroup; userData[11] = groups; userData[12] = items;
        var inputs = Inputs(userData, (ulong address, out uint word) => { word = 0; return false; });
        var snapshot = new ResourceSnapshot();
        var specialization = ResourceSpecialization.Default(plan.Info);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization), "materialize");
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            BindingLayout.UsesGlobalDataShare(program), ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        for (var k = 0; k < plan.Memory.Count; k++) output.WriteLine($"mem[{k}] pc={plan.Memory[k].Pc:X4} {plan.Memory[k].Opcode} {plan.Memory[k].Access}");
        output.WriteLine($"buffers={specialization.Buffers.Count} strides=[{string.Join(",", specialization.Buffers.Select(b => b.PackedStride))}]");
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, WaveSize = 64, LocalDataShareDwords = 0, SupportsBufferInt64Atomics = Environment.GetEnvironmentVariable("LOCAL_SCAN_WIDE") == "1" && fixture.Vulkan!.SupportsBufferInt64Atomics };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);

        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var registers = new uint[256];
        Array.Copy(userData, registers, userData.Length);
        var order = Environment.GetEnvironmentVariable("LOCAL_SCAN_ORDER") ?? "counter,status,data,total";
        for (var run = 0; run < runs; run++)
        {
            var named = new Dictionary<string, GpuBuffer>
            {
                ["counter"] = runner.CreateBuffer(new byte[4]),
                ["status"] = runner.CreateBuffer(new byte[groups * 8]),
                ["data"] = runner.CreateBuffer(input),
                ["total"] = runner.CreateBuffer(new byte[4]),
            };
            var buffers = order.Split(',').Select(n => named[n]).ToArray();
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers }, groups,
                flattenedTable: snapshot.FlattenedResourceTable));
            var result = runner.ReadBack(named["data"], 0, (ulong)input.Length);
            var bad = new List<int>();
            for (var i = 0; i < items; i++)
                if (BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(i * 4)) != expected[i]) bad.Add(i);
            var badBlocks = bad.Select(i => i / (int)perGroup).Distinct().ToList();
            var total = BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(named["total"], 0, 4));
            output.WriteLine($"run {run}: wrong items={bad.Count} blocks={badBlocks.Count} first=[{string.Join(",", badBlocks.Take(6))}] total={total}");
            foreach (var buffer in named.Values) buffer.Dispose();
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
