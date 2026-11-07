// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceMaterializationCacheTests;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// [local] SHARPEMU_MATERIALIZE_CHURN_LEARN: a churn-limited cache whose key changes in one rebasable word on every lookup
// records those first sightings so the rebase can learn the word, then serves the lookups from rebased hits that equal
// a fresh walk.
public sealed class LocalChurnLearnTests
{
    private sealed class Mode : IDisposable
    {
        private readonly int _previous = ResourceMaterializationCache.ChurnLearnMode;

        public Mode(int mode) => ResourceMaterializationCache.ChurnLearnMode = mode;

        public void Dispose() => ResourceMaterializationCache.ChurnLearnMode = _previous;
    }

    private static void AssertSameAsFreshWalk(ShaderResourcePlan plan, Heap heap, uint[] userData, ResourceSnapshot snapshot,
        ResourceSpecialization specialization)
    {
        var expected = new ResourceSnapshot();
        var expectedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: heap.Read), ref expected, ref expectedSpecialization));
        Assert.Equal(expected.Buffers.Select(words => words.ToArray()), snapshot.Buffers.Select(words => words.ToArray()));
        Assert.Equal(expected.Images.Select(words => words.ToArray()), snapshot.Images.Select(words => words.ToArray()));
        Assert.Equal(expected.Samplers.Select(words => words.ToArray()), snapshot.Samplers.Select(words => words.ToArray()));
        Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        Assert.Equal(expected.DeviceAddressRanges, snapshot.DeviceAddressRanges);
        Assert.Equal(userData, snapshot.UserData);
        Assert.True(expectedSpecialization.Equals(specialization));
    }

    // A buffer whose base is the pointer in s[0:1], allocated 0x100 further on every lookup (a per-draw constant ring).
    // With sameContents false the high word also moves the stride bits, which the move never explains.
    private static (long Hits, long Recorded, long Unrecorded) RunMovingBase(int mode, int lookups, bool sameContents = true)
    {
        using var keyScope = LocalCacheSwitches.WithoutContentKey();
        using var modeScope = new Mode(mode);
        var plan = PointerBufferPlan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache(limitChurn: true);
        for (var lookup = 0; lookup < lookups; lookup++)
        {
            uint[] userData = [0x1000 + 0x100 * (uint)lookup, sameContents ? 0x10u : 0x10u | ((uint)(lookup % 5) << 20)];
            Assert.True(Run(cache, plan, heap, userData, out var snapshot, out var specialization));
            AssertSameAsFreshWalk(plan, heap, userData, snapshot, specialization);
        }

        return (cache.Hits, cache.ChurnRecorded, cache.Unrecorded);
    }

    [Fact]
    public void WithoutTheSwitchAChurningKeyIsNeverRecorded()
    {
        var (hits, recorded, unrecorded) = RunMovingBase(mode: 0, lookups: 16);
        // The second lookup is recorded (before any walk is recorded the key holds no user data word); every later one
        // has a new key.
        Assert.Equal(0, hits);
        Assert.Equal(0, recorded);
        Assert.Equal(15, unrecorded);
    }

    [Fact]
    public void WithTheSwitchAChurningKeyIsLearnedAndThenHits()
    {
        var (hits, recorded, unrecorded) = RunMovingBase(mode: 1, lookups: 16);
        Assert.InRange(recorded, 1, ResourceMaterializationCache.ChurnLearnBudget);
        Assert.True(hits >= 8, $"hits={hits} recorded={recorded} unrecorded={unrecorded}");
    }

    [Fact]
    public void AWordTheMoveDoesNotExplainStopsBeingRecorded()
    {
        var (hits, recorded, unrecorded) = RunMovingBase(mode: 1, lookups: 24, sameContents: false);
        Assert.Equal(0, hits);
        Assert.InRange(recorded, 0, ResourceMaterializationCache.ChurnLearnBudget);
        Assert.True(unrecorded >= 24 - ResourceMaterializationCache.ChurnLearnBudget - 1, $"recorded={recorded} unrecorded={unrecorded}");
    }
}
