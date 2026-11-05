// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.LocalScratch;

// [local] Writes the decoded listing of guest shader binaries (LOCAL_DUMP_SHADER_HASHES dumps) next to them as dis_<hash>.txt.
// LOCAL_DIS_BINS=<bin>;<bin>...
public sealed class LocalDisassembleTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void WritesTheListings()
    {
        var bins = (Environment.GetEnvironmentVariable("LOCAL_DIS_BINS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var binary in bins)
        {
            var code = File.ReadAllBytes(binary);
            var memory = new FakeCpuMemory(ShaderAddress, ((code.Length + 0xFFF) & ~0xFFF) + 0x1000);
            Assert.True(memory.TryWrite(ShaderAddress, code));
            var ctx = new CpuContext(memory, Generation.Gen5);
            Assert.True(Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error), error);
            var name = Path.GetFileNameWithoutExtension(binary).Replace("shader_", "dis_") + ".txt";
            using var writer = new StreamWriter(Path.Combine(Path.GetDirectoryName(binary)!, name));
            writer.WriteLine($"path={binary} bytes={code.Length} instructions={program.Instructions.Count}");
            foreach (var instruction in program.Instructions)
            {
                writer.WriteLine($"pc=0x{instruction.Pc:X} enc={instruction.Encoding} op={instruction.Opcode} words={string.Join(',', instruction.Words.Select(w => w.ToString("X8")))} " +
                    $"src={string.Join('/', instruction.Sources)} dst={string.Join('/', instruction.Destinations)} control={instruction.Control?.ToString() ?? "-"}");
            }
        }
    }
}
