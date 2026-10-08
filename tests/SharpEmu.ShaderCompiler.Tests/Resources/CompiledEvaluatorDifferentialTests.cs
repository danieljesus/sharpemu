// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// The compiled evaluator is an optimisation of the interpreter: over the same plan, inputs and
// memory, the materializer and the cache must produce the same snapshots, specializations,
// failures and guest reads through either. Each program here is materialized twice, once on a
// plan kept on the interpreter and once on a plan compiled up front.
public sealed class CompiledEvaluatorDifferentialTests
{
    private sealed record Scenario(string Name, Gen5ShaderProgram Program, uint UserDataCount, uint[] UserData, GuestWordReader Source);

    // Reads through a scenario's memory, records every address and lets a test overwrite words.
    private sealed class Memory(GuestWordReader source)
    {
        public readonly Dictionary<ulong, uint> Overrides = new();
        public readonly List<ulong> Reads = new();
        public readonly List<ulong> CleanReads = new();

        public bool Read(ulong address, out uint word)
        {
            Reads.Add(address);
            return ReadCore(address, out word);
        }

        public bool ReadClean(ulong address, out uint word)
        {
            CleanReads.Add(address);
            return ReadCore(address, out word);
        }

        public bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            for (var offset = 0; offset < destination.Length; offset += sizeof(uint))
            {
                if (!ReadCore(address + (ulong)offset, out var word)) return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            return true;
        }

        private bool ReadCore(ulong address, out uint word) => Overrides.TryGetValue(address, out word) || source(address, out word);

        public void Clear()
        {
            Reads.Clear();
            CleanReads.Clear();
        }
    }

    private sealed record Outcome(bool Succeeded, ResourceMaterializationFailure Failure, ResourceSnapshot? Snapshot,
        ResourceSpecialization? Specialization, ulong[] Reads, ulong[] CleanReads);

    private static readonly uint[] DescriptorUserData = [0x1000, 0];

    private static bool CandidateHeap(ulong address, out uint word)
    {
        word = 0;
        if (address < 0x3000 || address >= 0x3000 + 4 * 16) return false;
        var record = (address - 0x3000) / 16;
        word = (address % 16 / 4) switch
        {
            0 => 0x5000u + (uint)record * 0x100,
            1 => 16u << 16,
            2 => 64,
            _ => 0,
        };
        return true;
    }

    private static Scenario IndirectImage()
    {
        var memory = ResourceTrackerTests.LinearMemory();
        var descriptor = ResourceTrackerTests.ImageDescriptor();
        ResourceTrackerTests.WriteImage(memory, 0x2000, descriptor);
        ResourceTrackerTests.WriteImage(memory, 0x2020, descriptor);
        memory.At(0x1000 + 36) = 1;
        return new("indirect image", ResourceTrackerTests.IndirectImageProgram(false), 64,
            [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7], memory.Read);
    }

    private static Scenario RepeatedReads()
    {
        var memory = new TestWordMemory { Words = [11, 22, 33, 44, 55, 66, 77, 88] };
        var userData = new uint[9];
        userData[0] = 0x1000;
        userData[8] = 8;
        return new("repeated reads", FlattenedReadReuseTests.RepeatedReadProgram(4, dynamicOffset: true), 9, userData, memory.Read);
    }

    private static IEnumerable<Scenario> Scenarios()
    {
        var heap = new ResourceMaterializationCacheTests.Heap();
        yield return new("direct image table", DirectImageTableTests.CreateProgram(), 2, DescriptorUserData, DirectImageTableTests.ReadDescriptor);
        yield return new("guarded image table", DirectImageTableTests.CreateGuardedProgram(), 2, DescriptorUserData, DirectImageTableTests.ReadDescriptor);
        yield return new("wave-indexed descriptor", DirectImageTableTests.CreateWaveIndexedDescriptorProgram(), 2, DescriptorUserData, heap.Read);
        yield return new("wave-indexed read lane", DirectImageTableTests.CreateWaveIndexedReadLaneProgram(false), 2, DescriptorUserData, heap.Read);
        yield return new("conditional buffers, first", ResourceBranchTests.ConditionalBuffers(), 64, ResourceBranchTests.UserData(0), heap.Read);
        yield return new("conditional buffers, second", ResourceBranchTests.ConditionalBuffers("SCbranchScc0"), 64, ResourceBranchTests.UserData(1), heap.Read);
        yield return new("candidate table", BufferCandidateTablePlannerTests.CandidateProgram(), 64, new uint[64], CandidateHeap);
        yield return RepeatedReads();
        yield return IndirectImage();
    }

    public static TheoryData<string> ScenarioNames() => [.. Scenarios().Select(scenario => scenario.Name)];

    private static Scenario Find(string name) => Scenarios().Single(scenario => scenario.Name == name);

    private static (ShaderResourcePlan Interpreted, ShaderResourcePlan Compiled) Plans(Scenario scenario)
    {
        var interpreted = Extract(scenario.Program, userDataCount: scenario.UserDataCount);
        interpreted.UseInterpreter();
        Assert.Null(interpreted.CompiledEvaluator);
        var compiled = Extract(scenario.Program, userDataCount: scenario.UserDataCount);
        Assert.NotNull(compiled.CompileEvaluatorNow());
        return (interpreted, compiled);
    }

    private static ResourceRuntimeInputs InputsOf(Scenario scenario, Memory memory) =>
        Inputs(scenario.UserData, memory.Read, memory.ReadClean);

    private static Outcome Materialize(ShaderResourcePlan plan, Scenario scenario, Memory memory)
    {
        memory.Clear();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var succeeded = ResourceMaterializer.Materialize(plan, InputsOf(scenario, memory), ref snapshot, ref specialization, out var failure);
        return new(succeeded, failure, succeeded ? snapshot : null, succeeded ? specialization : null,
            [.. memory.Reads.Order()], [.. memory.CleanReads.Order()]);
    }

    private static Outcome Materialize(ResourceMaterializationCache cache, ShaderResourcePlan plan, Scenario scenario, Memory memory)
    {
        memory.Clear();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var succeeded = cache.Materialize(plan, InputsOf(scenario, memory), memory.ReadResident, ref snapshot, ref specialization, out var failure);
        return new(succeeded, failure, succeeded ? snapshot : null, succeeded ? specialization : null,
            [.. memory.Reads.Order()], [.. memory.CleanReads.Order()]);
    }

    private static void AssertSame(Outcome expected, Outcome actual, string context)
    {
        Assert.True(expected.Succeeded == actual.Succeeded, $"{context}: interpreted {expected.Succeeded}/{expected.Failure}, compiled {actual.Succeeded}/{actual.Failure}");
        Assert.True(expected.Failure == actual.Failure, $"{context}: failure {expected.Failure} vs {actual.Failure}");
        // The guest sees the same reads, in whichever order the evaluation reached them.
        Assert.True(expected.Reads.SequenceEqual(actual.Reads), $"{context}: reads differ ({expected.Reads.Length} vs {actual.Reads.Length})");
        Assert.True(expected.CleanReads.SequenceEqual(actual.CleanReads), $"{context}: clean reads differ ({expected.CleanReads.Length} vs {actual.CleanReads.Length})");
        if (expected.Snapshot is null) return;
        AssertSame(expected.Snapshot, actual.Snapshot!, context);
        Assert.True(expected.Specialization!.Equals(actual.Specialization), $"{context}: specializations differ");
    }

    private static void AssertSame(ResourceSnapshot expected, ResourceSnapshot actual, string context)
    {
        AssertSame(expected.Buffers, actual.Buffers, $"{context} buffers");
        AssertSame(expected.Images, actual.Images, $"{context} images");
        AssertSame(expected.Samplers, actual.Samplers, $"{context} samplers");
        Assert.True(expected.FlattenedResourceTable.SequenceEqual(actual.FlattenedResourceTable), $"{context}: flattened tables differ");
        Assert.True(expected.UserData.SequenceEqual(actual.UserData), $"{context}: user data differs");
        Assert.True(expected.DeviceAddressRanges.SequenceEqual(actual.DeviceAddressRanges), $"{context}: device address ranges differ");
    }

    private static void AssertSame(uint[][] expected, uint[][] actual, string context)
    {
        Assert.True(expected.Length == actual.Length, $"{context}: {expected.Length} vs {actual.Length} descriptors");
        for (var index = 0; index < expected.Length; index++)
            Assert.True(expected[index].SequenceEqual(actual[index]), $"{context}: descriptor {index} differs");
    }

    [Fact]
    public void SynchronousCompilationServesTheCompiledEvaluatorOnFirstUse()
    {
        var plan = Extract(DirectImageTableTests.CreateProgram(), userDataCount: 2);
        if (Environment.GetEnvironmentVariable("SHARPEMU_SRT_COMPILE_SYNC") == "1")
            Assert.NotNull(plan.CompiledEvaluator);
        else
            Assert.Null(plan.CompiledEvaluator);
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void TheMaterializerAgreesWithTheInterpreter(string name)
    {
        var scenario = Find(name);
        var (interpreted, compiled) = Plans(scenario);
        var memory = new Memory(scenario.Source);
        var expected = Materialize(interpreted, scenario, memory);
        var actual = Materialize(compiled, scenario, memory);
        AssertSame(expected, actual, name);

        // A descriptor word that changes, and a word that becomes unreadable, change both the same way.
        foreach (var address in expected.Reads.Concat(expected.CleanReads).Distinct().TakeLast(3).ToArray())
        {
            memory.Overrides[address] = 0x3010;
            AssertSame(Materialize(interpreted, scenario, memory), Materialize(compiled, scenario, memory), $"{name} with 0x{address:X} changed");
            memory.Overrides.Remove(address);
        }
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void TheCacheAgreesWithTheInterpreter(string name)
    {
        var scenario = Find(name);
        var (interpreted, compiled) = Plans(scenario);
        var memory = new Memory(scenario.Source);
        var interpretedCache = new ResourceMaterializationCache();
        var compiledCache = new ResourceMaterializationCache();
        void Compare(string context)
        {
            AssertSame(Materialize(interpretedCache, interpreted, scenario, memory), Materialize(compiledCache, compiled, scenario, memory), context);
            Assert.True((interpretedCache.Hits, interpretedCache.Misses, interpretedCache.TableRefreshes) ==
                (compiledCache.Hits, compiledCache.Misses, compiledCache.TableRefreshes),
                $"{context}: cache counters interpreted {(interpretedCache.Hits, interpretedCache.Misses, interpretedCache.TableRefreshes)} " +
                $"compiled {(compiledCache.Hits, compiledCache.Misses, compiledCache.TableRefreshes)}");
        }

        Compare($"{name}, first");
        Compare($"{name}, again");
        var addresses = memory.Reads.Concat(memory.CleanReads).Distinct().ToArray();
        if (addresses.Length == 0) return;
        memory.Overrides[addresses[^1]] = 0x3010;
        Compare($"{name}, last word changed");
        memory.Overrides.Remove(addresses[^1]);
        Compare($"{name}, restored");
    }
}
