// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Tests;

// [local] SHARPEMU_DEVICE_DATA_READS=auto on GTA V's TLAS-chain compute shaders from their dumped binaries: the per-site
// rule, without a hash list, moves exactly the data-only loads of the hand-listed package to the device and keeps every
// load that feeds a descriptor, an address or a store on the host. Skipped when the binaries are not on this machine.
public sealed class LocalDeviceDataReadAutoTests(ITestOutputHelper output)
{
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Temp", "gta", "shaderbins");

    // (hash, pc, component) of the S_LOAD dwords that must leave the host table, and of those that must stay.
    public static TheoryData<ulong, uint[], uint[]> Sites => new()
    {
        // B198: the root box (6 floats, FSub/FMul only).
        { 0xB198A93EC372A571, [0x4_0, 0x4_1, 0x4_2, 0x4_3, 0xC_0, 0xC_1], [] },
        // 4481: its box at +0x0..+0x14 only feeds VSubF32 (s107 is overwritten at 0x5C before it offsets the s_buffer_loads);
        // the V#s at +0xB0 and +0xD8 (a table of V#s) and +0x90 stay.
        { 0x4481C2A89BEA7646, [0x4_0, 0x4_1, 0x4_2, 0x4_3, 0xC_0, 0xC_1], [0x9C_0, 0x9C_1, 0xAC_0, 0xAC_1, 0xAC_2, 0xAC_3, 0xCC_0] },
        // 5981: +0x60 sizes the node-index V#, +0x40/+0x50/+0x30/+0x58/+0x78 build V#s; +0x54 and +0x5C are dead (s47 and s11
        // are overwritten at 0x68 and 0x130), so they move.
        { 0x5981037B07E391D5, [0x4C_1, 0x11C_1], [0x1C_0, 0xC_0, 0xC_1, 0x4C_0, 0x70_0, 0x70_1, 0xB8_0, 0x11C_0] },
        // 02F1: +0x8C only feeds IAdd/Phi, and s2+0x9C only a lane offset (VAddI32 into the stores' vector address).
        { 0x02F144937456C9DE, [0xA8_0, 0x100_0], [] },
        // 994A: the self-relative V# of its stores (+0x28 offset, +0x74 records).
        { 0x994AF59D50BEDCA8, [], [0x78_0, 0x78_1, 0x80_0] },
        // F5EB: every load builds a V# (read s[16:19], written s[8:11] and s[4:7]).
        { 0xF5EBBA997605508B, [], [0xC_0, 0x1C_0, 0x1C_1, 0x3D4_0, 0x3D4_1, 0x3FC_0, 0x408_0, 0x408_1] },
    };

    [Theory]
    [MemberData(nameof(Sites))]
    public void AutoMovesOnlyDataLoads(ulong hash, uint[] device, uint[] host)
    {
        var binary = Path.Combine(Directory, $"shader_{hash:X16}.bin");
        if (!File.Exists(binary))
        {
            return;
        }

        var previous = ResourceTableReadPlanner.DeviceDataReadsAuto;
        try
        {
            ResourceTableReadPlanner.DeviceDataReadsAuto = 0;
            var basePlan = Plan(binary, hash, ShaderStage.Compute, out _);
            ResourceTableReadPlanner.DeviceDataReadsAuto = 1;
            var plan = Plan(binary, hash, ShaderStage.Compute, out var program);
            var before = Sorted(Flattened(basePlan));
            var after = Sorted(Flattened(plan));
            var moved = before.Except(after).ToList();
            output.WriteLine($"hash=0x{hash:X16} before=[{Format(before)}] after=[{Format(after)}] moved=[{Format(moved)}] device_scalar=[{Format(DeviceScalar(plan))}]");
            Assert.Equal(Sorted(device.Select(Key)), Sorted(moved));
            Assert.Equal(Sorted(device.Select(Key)), Sorted(DeviceScalar(plan)));
            foreach (var site in host.Select(Key))
            {
                Assert.Contains(site, after);
            }

            Assert.Empty(after.Except(before));
            if (device.Length != 0)
            {
                Assert.True(plan.Info.UsesDeviceAddresses);
            }

            Compile(plan, program);

            // A graphics stage keeps every load flattened unless auto-all is asked for.
            var pixel = Plan(binary, hash, ShaderStage.Pixel, out _);
            Assert.Equal(Sorted(Flattened(Plan(binary, hash, ShaderStage.Pixel, out _, auto: 0))), Sorted(Flattened(pixel)));
        }
        finally
        {
            ResourceTableReadPlanner.DeviceDataReadsAuto = previous;
        }
    }

    private static (uint Pc, uint Component) Key(uint site) => (site >> 4, site & 0xF);

    private static List<(uint Pc, uint Component)> Sorted(IEnumerable<(uint Pc, uint Component)> sites) => sites.Distinct().Order().ToList();

    private static string Format(IEnumerable<(uint Pc, uint Component)> sites) => string.Join(",", sites.Select(site => $"0x{site.Pc:X}.{site.Component}"));

    private static IEnumerable<(uint Pc, uint Component)> Flattened(ShaderResourcePlan plan) =>
        plan.FlattenedSlotByMemoryIndex.Keys.Where(index => plan.Memory[index].Kind == MemoryResourceKind.ScalarAddress)
            .Select(index => (plan.Memory[index].Pc, plan.Memory[index].ComponentIndex));

    // The S_LOAD dwords the shader performs through the page table (neither flattened nor planning-only).
    private static List<(uint Pc, uint Component)> DeviceScalar(ShaderResourcePlan plan) =>
        plan.Memory.Entries.Where(memory => memory.Kind == MemoryResourceKind.ScalarAddress && !memory.PlanningOnly)
            .Select(memory => (memory.Pc, memory.ComponentIndex)).ToList();

    private static ShaderResourcePlan Plan(string binary, ulong hash, ShaderStage stage, out Gen5ShaderProgram program, int? auto = null)
    {
        var previous = ResourceTableReadPlanner.DeviceDataReadsAuto;
        if (auto is { } mode)
        {
            ResourceTableReadPlanner.DeviceDataReadsAuto = mode;
        }

        try
        {
            var bytes = File.ReadAllBytes(binary);
            Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new BinaryMemory(bytes), Generation.Gen5), 0x1000, out program, out var error), error);
            return ShaderResourcePlan.Extract(program, stage, hash, 0, 16, waveSize: 64);
        }
        finally
        {
            ResourceTableReadPlanner.DeviceDataReadsAuto = previous;
        }
    }

    private static void Compile(ShaderResourcePlan plan, Gen5ShaderProgram program)
    {
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 16),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            LocalSizeX = 64,
            LocalSizeY = 1,
            LocalDataShareDwords = 0,
            ScratchDwords = 0,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    private sealed class BinaryMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || address - 0x1000 + (ulong)destination.Length > (ulong)bytes.Length)
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
