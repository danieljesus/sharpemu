// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Tests;

// [local] Translates GTA V's RT reflections pass (0x8395E43F382309DF) from its dumped binary with
// SHARPEMU_NATIVE_BLAS_LOOP off, on and in verify mode. Skipped when the binary is not on this machine.
// LOCAL_SPV_OUT=<dir> also writes each module for spirv-val.
public sealed class LocalNativeBlasLoopTests(ITestOutputHelper output)
{
    private const ulong Hash = 0x8395E43F382309DF;
    private static readonly string Binary = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "Local", "Temp", "gta", "shaderbins", "shader_8395E43F382309DF.bin");

    [Fact]
    public void TranslatesInEveryMode()
    {
        if (!File.Exists(Binary))
        {
            return;
        }

        var previous = Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_BLAS_LOOP");
        try
        {
            foreach (var trace in new[] { false, true })
            {
                foreach (var mode in new[] { "0", "1", "2" })
                {
                    Environment.SetEnvironmentVariable("SHARPEMU_NATIVE_BLAS_LOOP", mode);
                    var spirv = Compile(trace);
                    output.WriteLine($"trace={trace} mode={mode} bytes={spirv.Length}");
                    if (Environment.GetEnvironmentVariable("LOCAL_SPV_OUT") is { Length: > 0 } directory)
                    {
                        Directory.CreateDirectory(directory);
                        File.WriteAllBytes(Path.Combine(directory, $"8395_trace{(trace ? 1 : 0)}_mode{mode}.spv"), spirv);
                    }
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_NATIVE_BLAS_LOOP", previous);
        }
    }

    private static byte[] Compile(bool trace)
    {
        var bytes = File.ReadAllBytes(Binary);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new BinaryMemory(bytes), Generation.Gen5), 0x1000, out var program, out var error), error);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 32, waveSize: 64);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 32),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            LocalSizeX = 8,
            LocalSizeY = 8,
            LocalDataShareDwords = 1024,
            ScratchDwords = 56,
            TraceDeviceAddressFaults = trace,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        return shader.Spirv;
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
