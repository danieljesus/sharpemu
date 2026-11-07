// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// [local] SHARPEMU_FAST_MATERIALIZE_MEMO: a fast-path run whose read user data words and guest reads answer the same is
// served from the memo (the same arrays, no program run), with the draw's own user data; any different answer runs the
// program. Every result equals a fresh full walk.
public sealed class LocalFastMemoTests
{
    private sealed class Mode : IDisposable
    {
        private readonly int _previous = ResourceMaterializer.FastMemoMode;

        public Mode(int mode) => ResourceMaterializer.FastMemoMode = mode;

        public void Dispose() => ResourceMaterializer.FastMemoMode = _previous;
    }

    // A buffer descriptor loaded from the table at s[0:1]; s2 and s3 are user data the resources never read.
    private static ShaderResourcePlan Plan() =>
        ShaderResourcePlan.Extract(Program(
            ScalarLoad(0, 0, 4, 4, immediateOffset: 0x20),
            BufferAccess(8, "BufferLoadDword", 4, vectorData: 8),
            EndProgram(16)), ShaderStage.Compute, Hash, 0, 4);

    private sealed class Memory
    {
        public readonly Dictionary<ulong, uint> Words = new()
        {
            [0x1020] = 0x7000,
            [0x1024] = 0x10,
            [0x1028] = 256,
            [0x102C] = 0x2C004000,
        };

        public int Reads;

        public bool Read(ulong address, out uint word)
        {
            Reads++;
            return Words.TryGetValue(address, out word);
        }
    }

    private static (ResourceSnapshot Snapshot, ResourceSpecialization Specialization) Run(ShaderResourcePlan plan, Memory memory, uint[] userData)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.MaterializeFast(plan, Inputs(userData, memory.Read, memory.Read), ref snapshot, ref specialization, out _));
        var expected = new ResourceSnapshot();
        var expectedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
        Assert.Equal(expected.Buffers.Select(words => words.ToArray()), snapshot.Buffers.Select(words => words.ToArray()));
        Assert.Equal(expected.Images.Select(words => words.ToArray()), snapshot.Images.Select(words => words.ToArray()));
        Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        Assert.Equal(expected.DeviceAddressRanges, snapshot.DeviceAddressRanges);
        Assert.Equal(userData, snapshot.UserData);
        Assert.True(expectedSpecialization.Equals(specialization));
        return (snapshot, specialization);
    }

    [Fact]
    public void UnreadUserDataChangesAreServedFromTheMemo()
    {
        using var mode = new Mode(1);
        var plan = Plan();
        Assert.True(ResourceMaterializer.CanMaterializeFast(plan));
        var memory = new Memory();
        var (first, firstSpecialization) = Run(plan, memory, [0x1000, 0, 7, 8]);
        Assert.Equal(new uint[] { 0x7000, 0x10, 256, 0x2C004000 }, first.Buffers[0]);

        var (second, secondSpecialization) = Run(plan, memory, [0x1000, 0, 9, 10]);
        Assert.Same(first.Buffers, second.Buffers);
        Assert.Same(first.FlattenedResourceTable, second.FlattenedResourceTable);
        Assert.Same(firstSpecialization, secondSpecialization);
        Assert.Equal(new uint[] { 0x1000, 0, 7, 8 }, first.UserData);
        Assert.Equal(MaterializationTally.Outcome.FastMemoHit, ResourceMaterializer.LastFastOutcome);
    }

    [Fact]
    public void AChangedGuestWordRunsTheProgram()
    {
        using var mode = new Mode(1);
        var plan = Plan();
        var memory = new Memory();
        var (first, _) = Run(plan, memory, [0x1000, 0, 7, 8]);
        memory.Words[0x1020] = 0x9000;
        var (second, _) = Run(plan, memory, [0x1000, 0, 7, 8]);
        Assert.NotSame(first.Buffers, second.Buffers);
        Assert.Equal(0x9000u, second.Buffers[0][0]);
        Assert.Equal(0x7000u, first.Buffers[0][0]);
    }

    [Fact]
    public void AChangedReadUserDataWordRunsTheProgram()
    {
        using var mode = new Mode(1);
        var plan = Plan();
        var memory = new Memory();
        foreach (var (address, word) in memory.Words.ToArray())
            memory.Words[address + 0x4000] = word + 1;
        var (first, _) = Run(plan, memory, [0x1000, 0, 7, 8]);
        var (second, _) = Run(plan, memory, [0x5000, 0, 7, 8]);
        Assert.NotSame(first.Buffers, second.Buffers);
        Assert.Equal(0x7001u, second.Buffers[0][0]);
    }

    [Fact]
    public void WithoutTheSwitchEveryRunRunsTheProgram()
    {
        using var mode = new Mode(0);
        var plan = Plan();
        var memory = new Memory();
        var (first, _) = Run(plan, memory, [0x1000, 0, 7, 8]);
        var (second, _) = Run(plan, memory, [0x1000, 0, 9, 10]);
        Assert.NotSame(first.Buffers, second.Buffers);
    }

    [Fact]
    public void TheVerifyModeRunsTheProgramAndFindsNoDifference()
    {
        using var mode = new Mode(2);
        var plan = Plan();
        var memory = new Memory();
        var mismatches = ResourceMaterializer.FastMemoMismatches;
        var (first, _) = Run(plan, memory, [0x1000, 0, 7, 8]);
        var (second, _) = Run(plan, memory, [0x1000, 0, 9, 10]);
        Assert.NotSame(first.Buffers, second.Buffers);
        Assert.Equal(mismatches, ResourceMaterializer.FastMemoMismatches);
    }
}
