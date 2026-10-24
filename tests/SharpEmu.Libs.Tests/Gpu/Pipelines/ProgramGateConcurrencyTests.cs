// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// [local] SHARPEMU_PROGRAM_GATE: lookups from several threads without the gate find the same entries and permutations,
// and compile each one once, as the gated lookups do.
[Collection(SchedulingStateCollection.Name)]
public sealed class ProgramGateConcurrencyTests : IDisposable
{
    private const ulong Code = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong Header = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong DataBase = PipelineTestGuest.MemoryBase + 0x4_0000;
    private const uint Format32x4Uint = 75;
    private const uint Format32x4Float = 77;
    private const int Threads = 8;
    private const int Lookups = 400;

    private readonly FatalScope _fatal = new();

    public void Dispose() => _fatal.Dispose();

    private static uint[] Descriptor(int variant) => variant switch
    {
        0 => PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint),
        1 => PipelineTestGuest.BufferDescriptor(DataBase + 0x100, 16, 8, Format32x4Uint),
        _ => PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Float),
    };

    // The id each variant resolves to, and the cursor and buffer the stage carries, from one thread.
    private static (ulong Id, uint Cursor, uint Records)[] Expected(PipelineTestGuest guest, ShaderProgramCache programs)
    {
        var expected = new (ulong, uint, uint)[3];
        for (var variant = 0; variant < 3; variant++)
        {
            var cursor = 0u;
            var program = programs.GetOrCompile(guest.Source(Code, ShaderStage.Compute, Descriptor(variant)), PipelineTestGuest.ComputeOptions(),
                ref cursor, out var stage);
            expected[variant] = (program.Id, cursor, stage.Resources.Buffers[0][2]);
        }

        return expected;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentLookups_FindWhatOneThreadFinds(bool lockFree)
    {
        var reference = new PipelineTestGuest();
        reference.RegisterProgram(Code, Header, PipelineTestGuest.FormatLoadProgram);
        var expected = Expected(reference, reference.Programs);

        var guest = new PipelineTestGuest();
        guest.RegisterProgram(Code, Header, PipelineTestGuest.FormatLoadProgram);
        var gate = new object();
        var programs = new ShaderProgramCache(guest.Context, guest.Compiler, guest.Host, gate, lockFree);
        var sources = Enumerable.Range(0, 3).Select(variant => guest.Source(Code, ShaderStage.Compute, Descriptor(variant))).ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // Which variant compiles first is a race, so ids are compared with the first one each variant got.
        var ids = new System.Collections.Concurrent.ConcurrentDictionary<int, ulong>();
        using var start = new Barrier(Threads);
        var workers = Enumerable.Range(0, Threads).Select(thread => new Thread(() =>
        {
            var options = PipelineTestGuest.ComputeOptions();
            start.SignalAndWait();
            for (var lookup = 0; lookup < Lookups; lookup++)
            {
                var variant = (lookup + thread) % 3;
                var cursor = 0u;
                ShaderProgram program;
                ShaderStageResources stage;
                if (lockFree)
                {
                    program = programs.GetOrCompile(sources[variant], options.Reset().WithCompute(), ref cursor, out stage);
                }
                else
                {
                    lock (gate)
                    {
                        program = programs.GetOrCompile(sources[variant], options.Reset().WithCompute(), ref cursor, out stage);
                    }
                }

                if (program.Id != ids.GetOrAdd(variant, program.Id) || cursor != expected[variant].Cursor || stage.Resources.Buffers[0][2] != expected[variant].Records)
                {
                    failures.Enqueue($"thread {thread} lookup {lookup} variant {variant}: id={program.Id} cursor={cursor} records={stage.Resources.Buffers[0][2]}");
                }
            }
        })).ToArray();
        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) worker.Join();

        Assert.Empty(failures);
        Assert.Equal(expected[0].Id == expected[1].Id, ids[0] == ids[1]);
        Assert.Equal(expected[0].Id == expected[2].Id, ids[0] == ids[2]);
        var entry = Assert.Single(programs.Entries);
        Assert.Equal(2, entry.Permutations.Count);
        Assert.Equal(entry.Permutations, entry.Published);
        Assert.Equal(2, guest.Compiler.Compilations);
    }
}

internal static class ProgramGateTestOptions
{
    // The options ComputeOptions builds, refilled into a reused instance.
    public static StageCompileOptions WithCompute(this StageCompileOptions options)
    {
        options.ComputeInfo = PipelineTestGuest.ComputeOptions().ComputeInfo;
        return options;
    }
}
