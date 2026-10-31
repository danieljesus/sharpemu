// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Tests;

// [local] SHARPEMU_DEVICE_DESCRIPTOR_READS on GTA V's TLAS-chain shader 0xF5EB from its dumped binary: the listed loads
// leave the host table and the program still translates. Skipped when the binary is not on this machine.
public sealed class LocalDeviceDescriptorReadTests(ITestOutputHelper output)
{
    private const ulong Hash = 0xF5EBBA997605508B;
    private static readonly string Binary = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "Local", "Temp", "gta", "shaderbins", "shader_F5EBBA997605508B.bin");

    [Fact]
    public void ListedLoadsLeaveTheHostTable()
    {
        if (!File.Exists(Binary))
        {
            return;
        }

        var previous = ResourceTableReadPlanner.DeviceDescriptorReads;
        try
        {
            ResourceTableReadPlanner.DeviceDescriptorReads = [];
            var before = Offsets(Plan(out _));
            ResourceTableReadPlanner.DeviceDescriptorReads = [(Hash, 0x1C), (Hash, 0xC)];
            var plan = Plan(out var program);
            var after = Offsets(plan);
            output.WriteLine($"before=[{string.Join(",", before)}] after=[{string.Join(",", after)}] uses_device_addresses={plan.Info.UsesDeviceAddresses}");
            Assert.Contains((0x1Cu, 0u), before);
            Assert.Contains((0x1Cu, 1u), before);
            Assert.DoesNotContain((0x1Cu, 0u), after);
            Assert.DoesNotContain((0x1Cu, 1u), after);
            Assert.Contains((0xCu, 0u), after);
            Assert.Contains((0x3D4u, 0u), after);
            Compile(plan, program);
        }
        finally
        {
            ResourceTableReadPlanner.DeviceDescriptorReads = previous;
        }
    }

    private static List<(uint Pc, uint Component)> Offsets(ShaderResourcePlan plan) =>
        plan.FlattenedSlotByMemoryIndex.Keys.Select(index => (plan.Memory[index].Pc, plan.Memory[index].ComponentIndex)).Order().ToList();

    private static ShaderResourcePlan Plan(out Gen5ShaderProgram program)
    {
        var bytes = File.ReadAllBytes(Binary);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new BinaryMemory(bytes), Generation.Gen5), 0x1000, out program, out var error), error);
        return ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 16, waveSize: 64);
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
