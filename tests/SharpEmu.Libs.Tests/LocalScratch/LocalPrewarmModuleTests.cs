// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.LocalScratch;

// [local] Rebuilds the game's own compile requests from a shader prewarm list (shader-prewarm.bin: code capture,
// specialization, compute info, push-data cursor) and writes each translated module, so a pass can be studied offline
// with the specialization the game uses (the default specialization gives another module: 0x8395 compiles to 128
// registers offline and to 168 in the game). The request is built as ShaderProgramCache.TryCompilePrewarm builds it.
// LOCAL_PREWARM_FILE=<shader-prewarm.bin> (copied before opening), LOCAL_PREWARM_HASH=<hex> (default 0x8395...),
// LOCAL_SPV_OUT=<dir>; host capabilities as on the RTX 5080: LOCAL_PREWARM_FLAGS=<sharedI64:bufferI64:f16:execGuard:szInfNan>
// (default 1:1:1:1:1). Process-wide translator switches (SHARPEMU_WAVE64_MULTIWAVE=1, SHARPEMU_BVH_TRIANGLE_RANGE=0)
// must be set in the environment of the test process, as the batch runner sets them for the game.
public sealed class LocalPrewarmModuleTests(ITestOutputHelper output)
{
    [Fact]
    public void WritesTheGameModules()
    {
        var source = Environment.GetEnvironmentVariable("LOCAL_PREWARM_FILE");
        var outputDirectory = Environment.GetEnvironmentVariable("LOCAL_SPV_OUT");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(outputDirectory) || !File.Exists(source))
        {
            return;
        }

        var hash = ulong.Parse((Environment.GetEnvironmentVariable("LOCAL_PREWARM_HASH") ?? "8395E43F382309DF").Replace("0x", ""),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var flags = (Environment.GetEnvironmentVariable("LOCAL_PREWARM_FLAGS") ?? "1,1,1,1,1").Split(',', ':').Select(flag => flag == "1").ToArray();
        var copy = Path.Combine(Path.GetTempPath(), $"local-prewarm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        File.Copy(source, Path.Combine(copy, ShaderPrewarmList.FileName));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            using var list = ShaderPrewarmList.Open(copy);
            Assert.NotNull(list);
            var index = 0;
            foreach (var (record, code) in list!.LoadedComputes().Where(entry => entry.Record.Hash == hash))
            {
                Assert.True(Gen5ShaderTranslator.TryDecodeProgram(code.CreateContext(), code.Address, out var program, out var error), error);
                var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, record.Hash, record.UserDataBase, record.UserDataCount,
                    waveSize: record.Info.WaveSize);
                var resources = ResourceMaterializer.ApplyTo(plan, record.Specialization);
                var layout = BindingLayout.Allocate(
                    resources.Info,
                    BindingLayout.CollectUserDataRegisters(program, record.UserDataBase, record.UserDataCount),
                    BindingLayout.UsesGlobalDataShare(program),
                    ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
                    BindingLayout.ReadsShaderBase(program),
                    record.PushDataCursor,
                    usesDispatchThreadLimits: record.Info.DispatchThreadDimensions,
                    limitsInShaderData: false);
                var info = record.Info;
                var request = new ShaderCompileRequest(plan, resources, layout)
                {
                    WaveSize = info.WaveSize,
                    EnableExecGuardElision = info.WaveSize != 64 || flags[3],
                    ScratchDwords = info.ScratchDwords,
                    SupportsSharedInt64Atomics = flags[0],
                    SupportsBufferInt64Atomics = flags[1],
                    SupportsFloat16Conversions = flags[2],
                    ShaderSignedZeroInfNanPreserveFloat32Supported = flags[4],
                    ComputeSystemRegisters = record.SystemRegisters,
                    LocalDataShareDwords = info.LocalDataShareDwords,
                    LocalSizeX = Math.Max(info.ThreadsX, 1),
                    LocalSizeY = Math.Max(info.ThreadsY, 1),
                    LocalSizeZ = Math.Max(info.ThreadsZ, 1),
                };
                Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
                var path = Path.Combine(outputDirectory, $"game_{hash:X16}_{index}.spv");
                File.WriteAllBytes(path, shader.Spirv);
                output.WriteLine($"{path}: bytes={shader.Spirv.Length} local={request.LocalSizeX}x{request.LocalSizeY}x{request.LocalSizeZ} " +
                    $"wave={request.WaveSize} lds={request.LocalDataShareDwords} scratch={request.ScratchDwords} images={resources.Info.Images.Count} " +
                    $"buffers={resources.Info.Buffers.Count} push={layout.PushDataStartDword}");
                index++;
            }

            Assert.True(index > 0, $"no prewarm record for 0x{hash:X16}");
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }
}
