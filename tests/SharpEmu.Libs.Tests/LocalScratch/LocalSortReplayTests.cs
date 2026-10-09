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

// [local] Replays GTA V's onesweep pass (0x220AB61E9C35767C) on dumped buffers.
// LOCAL_SORT_REPLAY=1, LOCAL_SORT_DIR=<dump dir>, LOCAL_SORT_TICK=<tick>, LOCAL_SORT_RUNS=<n>.
public sealed class LocalSortReplayTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const string Shader = "C:/Users/danyy/AppData/Local/Temp/gta/shaderbins/shader_220AB61E9C35767C.bin";
    private const ulong Table = 0x10000;
    private static readonly ulong[] Bases = [0x500000, 0x1000000, 0x3000000, 0x400000, 0x2000000];

    [Fact]
    public void ReplayPass()
    {
        if (Environment.GetEnvironmentVariable("LOCAL_SORT_REPLAY") != "1") return;
        var dir = Environment.GetEnvironmentVariable("LOCAL_SORT_DIR")!;
        var tick = Environment.GetEnvironmentVariable("LOCAL_SORT_TICK")!;
        var runs = int.Parse(Environment.GetEnvironmentVariable("LOCAL_SORT_RUNS") ?? "5");
        var pass = uint.Parse(Environment.GetEnvironmentVariable("LOCAL_SORT_PASS") ?? "0");
        byte[] Slot(int slot) => File.ReadAllBytes(Directory.GetFiles(dir, $"sort_220A_s{slot}_pre_*_{tick}.gpu.bin").Single());
        var slots = Enumerable.Range(0, 5).Select(Slot).ToArray();
        var records = (uint)(slots[1].Length / 16);
        var rows = (uint)(slots[2].Length / 1024);
        var groups = (records + 511) / 512;
        output.WriteLine($"records={records} rows={rows} groups={groups}");

        var bytes = File.ReadAllBytes(Shader);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new Mem(bytes), Generation.Gen5), 0x1000, out var program, out var error), error);
        var plan = Extract(program, userDataCount: 16);

        uint[] VSharp(ulong address, uint strideBytes, uint numRecords) =>
            [(uint)address, (uint)(address >> 32) | (strideBytes << 16), numRecords, 0x00016204];
        var table = new Dictionary<ulong, uint>();
        void Put(ulong at, uint[] words) { for (var i = 0; i < words.Length; i++) table[at + (ulong)i * 4] = words[i]; }
        Put(Table, VSharp(Bases[3], 4, (uint)slots[3].Length / 4));
        Put(Table + 16, VSharp(Bases[0], 4, (uint)slots[0].Length / 4));
        Put(Table + 32, [(uint)Bases[2], (uint)(Bases[2] >> 32)]);
        var userData = new uint[16];
        userData[0] = (uint)Table;
        userData[2] = (uint)Bases[1];
        userData[3] = (uint)(Bases[1] >> 32);
        userData[4] = (uint)Bases[4];
        userData[5] = (uint)(Bases[4] >> 32);
        userData[6] = rows;
        userData[7] = records;
        userData[8] = (pass << 28) | 512;
        var inputs = Inputs(userData, (ulong address, out uint word) => table.TryGetValue(address, out word));
        var snapshot = new ResourceSnapshot();
        var specialization = ResourceSpecialization.Default(plan.Info);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization), "materialize");
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            BindingLayout.UsesGlobalDataShare(program), ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        output.WriteLine($"strides=[{string.Join(",", specialization.Buffers.Select(b => b.PackedStride))}] table={snapshot.FlattenedResourceTable.Length}");
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 256, WaveSize = 64, LocalDataShareDwords = uint.Parse(Environment.GetEnvironmentVariable("LOCAL_LDS") ?? "768") };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);

        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var registers = new uint[256];
        Array.Copy(userData, registers, userData.Length);
        for (var run = 0; run < runs; run++)
        {
            var buffers = new GpuBuffer[5];
            for (var slot = 0; slot < 5; slot++)
            {
                var initial = slot is 0 or 2 ? new byte[slots[slot].Length] : slots[slot];
                var size = slot == 3 ? (ulong)slots[slot].Length + 64 * 32 * 4 : (ulong)slots[slot].Length;
                buffers[slot] = runner.CreateBuffer(initial, size);
            }

            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers }, groups,
                flattenedTable: snapshot.FlattenedResourceTable));
            var outBytes = runner.ReadBack(buffers[4], 0, (ulong)slots[4].Length);
            var status = runner.ReadBack(buffers[2], 0, (ulong)slots[2].Length);
            var ids = new Dictionary<(uint, uint), int>();
            for (var i = 0; i < records; i++)
            {
                var key = (BinaryPrimitives.ReadUInt32LittleEndian(outBytes.AsSpan(i * 16 + 8)), BinaryPrimitives.ReadUInt32LittleEndian(outBytes.AsSpan(i * 16 + 12)));
                ids[key] = ids.GetValueOrDefault(key) + 1;
            }

            var duplicates = ids.Values.Where(c => c > 1).Sum(c => c - 1);
            var missing = 0;
            for (var i = 0; i < records; i++)
            {
                var key = (BinaryPrimitives.ReadUInt32LittleEndian(slots[1].AsSpan(i * 16 + 8)), BinaryPrimitives.ReadUInt32LittleEndian(slots[1].AsSpan(i * 16 + 12)));
                if (!ids.ContainsKey(key)) missing++;
            }
            output.WriteLine($"run {run}: missing input records={missing}");
            if (run == 0) File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "replay_out.bin"), outBytes);
            var anomalies = new List<string>();
            for (var row = 0; row < groups; row++)
            {
                var word = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(row * 1024));
                var expected = Math.Min(512u * (uint)(row + 1), records);
                if ((word & 0x1FFFFFFF) != expected) anomalies.Add($"{row}:{word >> 29}/{word & 0x1FFFFFFF}");
            }

            output.WriteLine($"run {run}: duplicates={duplicates} bucket0 anomalies=[{string.Join(" ", anomalies.Take(12))}]");
            if (Environment.GetEnvironmentVariable("LOCAL_REPLAY_PROBE") == "1")
            {
                var probe = runner.ReadBack(buffers[3], 0, (ulong)slots[3].Length + 64 * 32 * 4);
                for (var ticket = Math.Max(0, (int)groups - 4); ticket < groups; ticket++)
                {
                    var w = Enumerable.Range(0, 8).Select(k => BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(8192 + 2048 + ticket * 32 + k * 4))).ToArray();
                    output.WriteLine($"  b255 t{ticket}: lookback={w[0]} rounds={w[1]} trips={w[2]} first=0x{w[3]:X} last=0x{w[4]:X} entered={w[5]} own={w[6]} ticket={w[7]}");
                }
                output.WriteLine($"  last LDS[255] write value={BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(8192 + 4096))}");
                for (var ticket = 0; ticket < Math.Min(groups, 20); ticket++)
                {
                    var w = Enumerable.Range(0, 8).Select(k => BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(8192 + ticket * 32 + k * 4))).ToArray();
                    output.WriteLine($"  t{ticket}: lookback={w[0]} rounds={w[1]} trips={w[2]} first=0x{w[3]:X} last=0x{w[4]:X} entered={w[5]} pred={(int)w[6]} ticket={w[7]}");
                }
            }
            foreach (var buffer in buffers) buffer.Dispose();
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
