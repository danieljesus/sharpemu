// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class RuntimePipelineReadinessTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly ProgramKey Key = new(ShaderStage.Compute, 123, 0, 4, 0, []);
    private static readonly StageCompileOptions Options = new() { ComputeInfo = new ComputeInputInfo() };

    private static ProgramSourceEntry Entry(long generation) => new()
    {
        Program = null!, Plan = null!, HasShaderCalls = false, HasBitwiseExclusiveOr = false,
        RuntimeCallRecords = true, Generation = generation,
    };

    private static T Field<T>(ShaderProgramCache cache, string name) =>
        (T)typeof(ShaderProgramCache).GetField(name, Members)!.GetValue(cache)!;

    private static bool Ready(ShaderProgramCache cache, ProgramSourceEntry entry, ulong id, uint callees,
        ProgramSourceEntry? fallback = null)
    {
        var stage = new ShaderStageResources(new ShaderProgramInfo
        {
            Hash = 123, Bindings = new BindingLayout { CallTargetCount = callees },
        }, null!);
        lock (Field<object>(cache, "_callGate"))
            return (bool)typeof(ShaderProgramCache).GetMethod("RuntimePipelineReady", Members)!
                .Invoke(cache, [Key, entry, new ShaderProgram(id), stage, Options, fallback])!;
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void OlderReadyVersionServesUntilNewestIsReady(uint callees)
    {
        var cache = new PipelineTestGuest().Programs;
        var older = Entry(1);
        var newest = Entry(2);
        var ready = new HashSet<ulong>();
        var polls = new List<ulong>();
        cache.TryComputePipeline = (program, _, _) => { polls.Add(program.Id); return ready.Contains(program.Id); };
        Assert.False(Ready(cache, older, 1, callees));
        ready.Add(1);
        polls.Clear();
        Assert.False(Ready(cache, newest, 2, callees));
        Assert.Equal(new ulong[] { 1, 2 }, polls);
        var fallbacks = Field<Dictionary<ProgramKey, ProgramSourceEntry>>(cache, "_runtimeFallbacks");
        Assert.Same(older, fallbacks[Key]);
        Assert.True(Ready(cache, older, 1, callees, older));
        ready.Add(2);
        Assert.True(Ready(cache, newest, 2, callees));
        Assert.Same(newest, fallbacks[Key]);
        var pending = Field<Dictionary<ProgramKey, List<(ProgramSourceEntry, ShaderProgram, ShaderStageResources)>>>(cache, "_runtimeCompiles");
        Assert.Empty(pending[Key]);
        Assert.True(Ready(cache, older, 1, callees));
        Assert.Same(newest, fallbacks[Key]); // A late poll cannot demote the fallback.
    }

    [Fact]
    public void NoReadyFallbackRemainsPendingWithoutDuplicateTracking()
    {
        var cache = new PipelineTestGuest().Programs;
        var entry = Entry(1);
        cache.TryComputePipeline = (_, _, _) => false;
        Assert.False(Ready(cache, entry, 1, 0));
        Assert.False(Ready(cache, entry, 1, 0));
        Assert.Empty(Field<Dictionary<ProgramKey, ProgramSourceEntry>>(cache, "_runtimeFallbacks"));
        var pending = Field<Dictionary<ProgramKey, List<(ProgramSourceEntry, ShaderProgram, ShaderStageResources)>>>(cache, "_runtimeCompiles");
        Assert.Single(pending[Key]);
        Assert.False(Ready(cache, entry, 1, 0, entry));
        Assert.Single(pending[Key]);
    }

    [Fact]
    public void FailedPollPropagatesWithoutPromotingFallback()
    {
        var cache = new PipelineTestGuest().Programs;
        var failure = new InvalidOperationException("Compiler failure.");
        cache.TryComputePipeline = (_, _, _) => throw failure;
        Assert.Same(failure, Assert.Throws<TargetInvocationException>(() => Ready(cache, Entry(1), 1, 0)).InnerException);
        Assert.Empty(Field<Dictionary<ProgramKey, ProgramSourceEntry>>(cache, "_runtimeFallbacks"));
    }

    // Exercise the sole RuntimePipelineReady caller, including its actual fallback recursion. The
    // synthetic runtime-record versions have no callees, covering the zero-target boundary of the guard.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteProgramSelectsReadyFallbackOrReturnsThePendingHandleForRecorderToWait(bool olderReady)
    {
        var guest = new PipelineTestGuest();
        const ulong code = PipelineTestGuest.MemoryBase + 0x1000;
        guest.RegisterProgram(code, PipelineTestGuest.MemoryBase + 0x8000, PipelineTestGuest.EndProgram);
        var source = guest.Source(code, ShaderStage.Compute, []);
        var cursor = 0u;
        var oldHandle = guest.Programs.GetOrCompile(source, Options, ref cursor, out _);
        var older = guest.Programs.Entries.Single();
        typeof(ProgramSourceEntry).GetProperty(nameof(ProgramSourceEntry.RuntimeCallRecords))!.SetValue(older, true);
        typeof(ProgramSourceEntry).GetProperty(nameof(ProgramSourceEntry.Generation))!.SetValue(older, 1L);
        var newest = new ProgramSourceEntry
        {
            Plan = older.Plan, Program = older.Program, HasShaderCalls = false, HasBitwiseExclusiveOr = false,
            RuntimeCallRecords = true, Generation = 2,
        };
        foreach (var permutation in older.Permutations)
            newest.AddPermutation(new ProgramPermutation
            {
                Specialization = permutation.Specialization, Program = permutation.Program,
                Compiled = permutation.Compiled, Handle = new ShaderProgram(99),
            });
        var cache = guest.Programs;
        var key = (ProgramKey)typeof(ShaderProgramCache).GetMethod("KeyFor", Members)!.Invoke(cache, [source, Options])!;
        if (olderReady)
            Field<Dictionary<ProgramKey, ProgramSourceEntry>>(cache, "_runtimeFallbacks").Add(key, older);
        var polled = new List<ulong>();
        cache.TryComputePipeline = (program, _, _) => { polled.Add(program.Id); return olderReady && program.Id == oldHandle.Id; };
        object?[] arguments = [newest, true, key, source, Options, 0u, default(ShaderStageResources), false, null];
        var returned = (ShaderProgram)typeof(ShaderProgramCache).GetMethod("CompleteProgram", Members)!.Invoke(cache, arguments)!;
        Assert.Equal(olderReady ? oldHandle.Id : 99UL, returned.Id);
        Assert.Equal(olderReady ? new ulong[] { 99, oldHandle.Id } : new ulong[] { 99 }, polled);
        Assert.True(((ShaderStageResources)arguments[6]!).IsValid);
        var fallbacks = Field<Dictionary<ProgramKey, ProgramSourceEntry>>(cache, "_runtimeFallbacks");
        if (olderReady) Assert.Same(older, fallbacks[key]);
        else Assert.Empty(fallbacks);
    }
}
