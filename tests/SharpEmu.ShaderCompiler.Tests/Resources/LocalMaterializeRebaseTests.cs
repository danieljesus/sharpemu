// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// [local] SHARPEMU_MATERIALIZE_REBASE: a per-frame ring whose table at s[0:1] holds a constant buffer A inside the
// ring (so its base moves with the table) and a buffer B outside it. A's words hold the descriptor C the shader
// binds. A rebased hit must equal the full walk of the same draw.
public sealed class LocalMaterializeRebaseTests
{
    private const ulong Delta = 0x1000;
    private const uint OutsideBase = 0x90000;
    private const uint CBase = 0x80000;

    private static ShaderResourcePlan RingPlan() =>
        ShaderResourcePlan.Extract(Program(
            ScalarLoad(0, 0, 4, 4, immediateOffset: 0x20),
            ScalarBufferLoad(4, 4, 8, 4),
            ScalarLoad(8, 0, 12, 4, immediateOffset: 0x30),
            BufferAccess(12, "BufferLoadDword", 8, vectorData: 4),
            BufferAccess(16, "BufferLoadDword", 12, vectorData: 5),
            EndProgram(20)), ShaderStage.Compute, Hash, 0, 2);

    private static void Frame(ResourceMaterializationCacheTests.Heap heap, uint table, uint outside)
    {
        heap.Words[table + 0x20] = table + 0x100;
        heap.Words[table + 0x24] = 0;
        heap.Words[table + 0x28] = 64;
        heap.Words[table + 0x2C] = 0x2C004000;
        heap.Words[table + 0x30] = outside;
        heap.Words[table + 0x34] = 0;
        heap.Words[table + 0x38] = 256;
        heap.Words[table + 0x3C] = 0x2C004000;
        heap.Words[table + 0x100] = CBase;
        heap.Words[table + 0x104] = 0;
        heap.Words[table + 0x108] = 256;
        heap.Words[table + 0x10C] = 0x2C004000;
    }

    private static ResourceSnapshot Walk(ShaderResourcePlan plan, ResourceMaterializationCacheTests.Heap heap, uint table)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([table, 0], heap.Read, heap.Read), ref snapshot, ref specialization));
        return snapshot;
    }

    private static ResourceSnapshot Draw(ResourceMaterializationCache cache, ShaderResourcePlan plan, ResourceMaterializationCacheTests.Heap heap, uint table)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, Inputs([table, 0], heap.Read, heap.Read), heap.ReadResident,
            ref snapshot, ref specialization, out _));
        return snapshot;
    }

    private static void AssertSame(ResourceSnapshot walked, ResourceSnapshot drawn) =>
        Assert.Null(ResourceMaterializationCache.SnapshotDifference(drawn, walked));

    // Four frames confirm the plan's rebase; the fifth frame's B points where A pointed in the fourth, and B's word
    // stays there in the sixth frame while A moves on. B's base must stay, though it equals A's old base.
    private static (ResourceSnapshot Walked, ResourceSnapshot Drawn, long RebaseHits) Coincidence(int mode)
    {
        var previous = ResourceMaterializationCache.RebaseModeOverride;
        ResourceMaterializationCache.RebaseModeOverride = mode;
        try
        {
            var plan = RingPlan();
            var heap = new ResourceMaterializationCacheTests.Heap();
            heap.Words.Clear();
            var cache = new ResourceMaterializationCache();
            uint table = 0x10000;
            for (var frame = 0; frame < 5; frame++, table += (uint)Delta)
            {
                Frame(heap, table, OutsideBase);
                AssertSame(Walk(plan, heap, table), Draw(cache, plan, heap, table));
            }

            var stuck = table + 0x100;
            Frame(heap, table, stuck);
            AssertSame(Walk(plan, heap, table), Draw(cache, plan, heap, table));
            table += (uint)Delta;
            Frame(heap, table, stuck);
            var hitsBefore = cache.RebaseHits;
            var drawn = Draw(cache, plan, heap, table);
            return (Walk(plan, heap, table), drawn, cache.RebaseHits - hitsBefore);
        }
        finally
        {
            ResourceMaterializationCache.RebaseModeOverride = previous;
        }
    }

    [Fact]
    public void ARingFrameIsRebasedAfterTheConfirmations()
    {
        if (!ResourceMaterializationCache.ContentKeyed)
            return;
        var previous = ResourceMaterializationCache.RebaseModeOverride;
        ResourceMaterializationCache.RebaseModeOverride = 1;
        try
        {
            var plan = RingPlan();
            var heap = new ResourceMaterializationCacheTests.Heap();
            heap.Words.Clear();
            var cache = new ResourceMaterializationCache();
            uint table = 0x10000;
            for (var frame = 0; frame < 7; frame++, table += (uint)Delta)
            {
                Frame(heap, table, OutsideBase);
                AssertSame(Walk(plan, heap, table), Draw(cache, plan, heap, table));
            }

            Assert.Equal(2, cache.RebaseHits);
        }
        finally
        {
            ResourceMaterializationCache.RebaseModeOverride = previous;
        }
    }

    [Fact]
    public void AWordOutsideTheRingThatEqualsAMovedWordIsNotRebased()
    {
        if (!ResourceMaterializationCache.ContentKeyed)
            return;
        var (walked, drawn, hits) = Coincidence(1);
        Assert.Equal(0, hits);
        AssertSame(walked, drawn);
    }

    [Fact]
    public void TheValueRebaseOfMode4TakesTheWordOutsideTheRingAlong()
    {
        if (!ResourceMaterializationCache.ContentKeyed)
            return;
        var (walked, drawn, hits) = Coincidence(4);
        Assert.Equal(1, hits);
        Assert.NotNull(ResourceMaterializationCache.SnapshotDifference(drawn, walked));
    }
}
