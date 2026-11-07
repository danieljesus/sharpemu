// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceMaterializationCacheTests
{
    private const ulong HeapBase = 0x1000;

    // A mask word, a two-entry index table and six 32-byte image records.
    internal sealed class Heap
    {
        public readonly Dictionary<ulong, uint> Words = new();
        public int Reads;
        public bool GpuOwned;

        public Heap()
        {
            Words[HeapBase + 0x80] = (1u << 1) | (1u << 4);
            Words[HeapBase + 0x40 + 0x90] = 2;
            Words[HeapBase + 0x40 + 4 * 0x90] = 5;
            foreach (var record in new uint[] { 2, 5 })
            {
                var entry = HeapBase + 0x100 + record * 32;
                for (uint dword = 0; dword < 8; dword++)
                    Words[entry + dword * 4] = dword switch
                    {
                        0 => (record & 1) == 0 ? 0x2000u : 0x1000u,
                        1 => 20u << 20,
                        3 => 0xFACu | (9u << 28),
                        _ => 0,
                    };
            }

        }

        // The same words, moved by delta: a game's table allocated at another address.
        public Heap Moved(ulong delta)
        {
            var moved = new Heap();
            moved.Words.Clear();
            foreach (var (address, word) in Words)
                moved.Words[address + delta] = word;
            return moved;
        }

        public bool Read(ulong address, out uint word)
        {
            Reads++;
            return Words.TryGetValue(address, out word);
        }

        public bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            if (GpuOwned)
                return false;
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!Words.TryGetValue(address + (ulong)offset, out var word))
                    return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            return true;
        }

    }

    private static ShaderResourcePlan Plan() =>
        ShaderResourcePlan.Extract(DirectImageTableTests.CreateWaveIndexedDescriptorProgram(), ShaderStage.Compute, Hash, 0, 2);

    internal static bool Run(ResourceMaterializationCache cache, ShaderResourcePlan plan, Heap heap, uint[] userData,
        out ResourceSnapshot snapshot, out ResourceSpecialization specialization, ulong shaderBase = 0, bool readsDirty = false)
    {
        snapshot = new ResourceSnapshot();
        specialization = new ResourceSpecialization();
        return cache.Materialize(plan, Inputs(userData, readsDirty ? heap.Read : null, heap.Read, shaderBase: shaderBase), heap.ReadResident,
            ref snapshot, ref specialization, out _);
    }

    [Fact]
    public void UnchangedMemoryReusesTheMaterialization()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out var firstSpecialization));
        var readsAfterFirst = heap.Reads;
        Assert.True(readsAfterFirst > 0);

        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out var secondSpecialization));
        Assert.Equal(readsAfterFirst, heap.Reads);
        Assert.Same(first, second);
        Assert.Same(firstSpecialization, secondSpecialization);
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void AChangedDescriptorWordMaterializesAgain()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));

        heap.Words[HeapBase + 0x100 + 5 * 32] = 0x3000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _));
        Assert.NotSame(first, second);
        Assert.Equal((0, 2), (cache.Hits, cache.Misses));

        // An uncached full walk gives the same result as the re-materialization.
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: heap.Read),
            ref reference, ref referenceSpecialization));
        Assert.Equal(reference.Images.Select(image => image.ToArray()), second.Images.Select(image => image.ToArray()));
    }

    [Fact]
    public void AlternatingDescriptorsReuseEveryRecentVariant()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        var word = HeapBase + 0x100 + 5 * 32;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));
        heap.Words[word] = 0x3000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _));
        Assert.Equal((0, 2), (cache.Hits, cache.Misses));

        heap.Words[word] = 0x1000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var firstAgain, out _));
        heap.Words[word] = 0x3000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var secondAgain, out _));
        Assert.Same(first, firstAgain);
        Assert.Same(second, secondAgain);
        Assert.Equal((2, 2), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void AChangedMaskMaterializesAgain()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        heap.Words[HeapBase + 0x80] = 1u << 1;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void GpuOwnedMemoryIsNeverTrusted()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        heap.GpuOwned = true;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void ADifferentDrawKeyIsADifferentEntry()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _, shaderBase: 0x100));
        Assert.NotSame(first, second);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var third, out _));
        Assert.Same(first, third);
        Assert.Equal((1, 2), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void AFailedReadIsNotCached()
    {
        var plan = Plan();
        var heap = new Heap();
        heap.Words.Remove(HeapBase + 0x100 + 5 * 32 + 4);
        var cache = new ResourceMaterializationCache();
        var ok = Run(cache, plan, heap, [0x1000, 0], out _, out _);
        var reads = heap.Reads;
        Assert.Equal(ok, Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.True(heap.Reads > reads);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void AFailedRecordingDoesNotPoisonTheNextReader()
    {
        var plan = Plan();
        var incomplete = new Heap();
        incomplete.Words.Remove(HeapBase + 0x100 + 5 * 32 + 4);
        var cache = new ResourceMaterializationCache();
        Run(cache, plan, incomplete, [0x1000, 0], out _, out _);

        var complete = new Heap();
        Assert.True(Run(cache, plan, complete, [0x1000, 0], out var first, out _));
        Assert.True(Run(cache, plan, complete, [0x1000, 0], out var second, out _));
        Assert.Same(first, second);
        Assert.Equal(1, cache.Hits);

        // Retained entries must not refer to scratch reads reused for another key.
        complete.Words[HeapBase + 0x80] = 1u << 1;
        Assert.True(Run(cache, plan, complete, [0x1000, 0], out _, out _, shaderBase: 0x100));
        var original = new Heap();
        Assert.True(Run(cache, plan, original, [0x1000, 0], out var restored, out _));
        Assert.Same(first, restored);
    }

    // The table pointer in s[0:1]; the rest of the nine user-data registers the program declares.
    private static readonly uint[] TableUserData = [0x1000, 0, 0, 0, 0, 0, 0, 0, 0];

    // Reads the four constants of FlattenedReadReuseTests.RepeatedReadProgram from a word memory.
    private static bool RunTable(ResourceMaterializationCache cache, ShaderResourcePlan plan, TestWordMemory memory,
        out ResourceSnapshot snapshot)
    {
        snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        return cache.Materialize(plan, Inputs(TableUserData, memory.Read, memory.Read), (address, destination, _) =>
        {
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word))
                    return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            return true;
        }, ref snapshot, ref specialization, out _);
    }

    [Fact]
    public void AChangedTableOnlyWordIsTakenByTheEntry()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var cache = new ResourceMaterializationCache();
        Assert.True(RunTable(cache, plan, memory, out var first));
        Assert.Equal([11u, 22, 33, 44], first.FlattenedResourceTable);

        memory.Words[2] = 99;
        Assert.True(RunTable(cache, plan, memory, out var second));
        Assert.Equal((1, 1, 0), (cache.Hits, cache.Misses, cache.TableRefreshes));
        Assert.Equal([11u, 22, 99, 44], second.FlattenedResourceTable);
        Assert.Equal([11u, 22, 33, 44], first.FlattenedResourceTable);
        Assert.Same(first.Buffers, second.Buffers);

        // The refreshed table matches an uncached full walk, and the refreshed entry is then a hit.
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(TableUserData, memory.Read, memory.Read), ref reference, ref referenceSpecialization));
        Assert.Equal(reference.FlattenedResourceTable, second.FlattenedResourceTable);
        Assert.True(RunTable(cache, plan, memory, out var third));
        Assert.Same(second, third);
        Assert.Equal((2, 1, 0), (cache.Hits, cache.Misses, cache.TableRefreshes));
    }

    // Upstream's version of the test above: without word patches the changed word refreshes the table.
    [Fact]
    public void AChangedTableOnlyWordRefreshesOnlyTheTable()
    {
        // [local] Override: upstream's counts assume no word patches; ours (always on) make the changed table-only
        // word a hit instead of a refresh (AChangedTableOnlyWordIsTakenByTheEntry).
        using var upstreamPatches = LocalCacheSwitches.WithoutWordPatches();
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var cache = new ResourceMaterializationCache();
        Assert.True(RunTable(cache, plan, memory, out var first));
        Assert.Equal([11u, 22, 33, 44], first.FlattenedResourceTable);

        memory.Words[2] = 99;
        Assert.True(RunTable(cache, plan, memory, out var second));
        Assert.Equal((0, 1, 1), (cache.Hits, cache.Misses, cache.TableRefreshes));
        Assert.Equal([11u, 22, 99, 44], second.FlattenedResourceTable);
        Assert.Same(first.Buffers, second.Buffers);

        // The refreshed table matches an uncached full walk, and the refreshed entry is then a hit.
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(TableUserData, memory.Read, memory.Read), ref reference, ref referenceSpecialization));
        Assert.Equal(reference.FlattenedResourceTable, second.FlattenedResourceTable);
        Assert.True(RunTable(cache, plan, memory, out var third));
        Assert.Same(second, third);
        Assert.Equal((1, 1, 1), (cache.Hits, cache.Misses, cache.TableRefreshes));
    }

    [Fact]
    public void AnUnreadableTableWordFallsBackToAFullWalk()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var cache = new ResourceMaterializationCache();
        Assert.True(RunTable(cache, plan, memory, out _));
        memory.Words[1] = 7;
        memory.FailAddress = 0x1000 + 3 * 4;
        RunTable(cache, plan, memory, out _);
        Assert.Equal(0, cache.TableRefreshes);
    }

    [Fact]
    public void AFullGenerationKeepsRecentEntries()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache(generationCapacity: 1);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _, shaderBase: 0x100));
        // The first entry moved to the old generation and is still found.
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(1, cache.Hits);
    }

    // A plan whose only use of s[0:1] is as the base of its descriptor reads.
    internal static ShaderResourcePlan DirectPlan() =>
        ShaderResourcePlan.Extract(DirectImageTableTests.CreateProgram(), ShaderStage.Compute, Hash, 0, 2);

    internal static Heap DirectHeap()
    {
        var heap = new Heap();
        heap.Words.Clear();
        for (var address = HeapBase + 312; address < HeapBase + 344 + 32 * 32; address += 4)
        {
            if (DirectImageTableTests.ReadDescriptor(address, out var word))
                heap.Words[address] = word;
        }

        return heap;
    }

    [Fact]
    public void TheSameTableAtAnotherAddressIsAHit()
    {
        if (!ResourceMaterializationCache.ContentKeyed)
            return;
        var plan = DirectPlan();
        Assert.True(plan.UserDataUse.AnyAddressOnly);
        var heap = DirectHeap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out var firstSpecialization));
        Assert.Equal(0, cache.Hits);

        // The table pointer in s[0:1] moves; every word it points at is the same.
        var moved = heap.Moved(0x4000);
        Assert.True(Run(cache, plan, moved, [0x5000, 0], out var second, out var secondSpecialization));
        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, moved.Reads);
        Assert.Equal(first.Images, second.Images);
        Assert.Same(firstSpecialization, secondSpecialization);
        Assert.Equal(new uint[] { 0x5000, 0 }, second.UserData);
        Assert.Equal(new uint[] { 0x1000, 0 }, first.UserData);
    }

    [Fact]
    public void ADifferentTableAtAnotherAddressMaterializesAgain()
    {
        var plan = DirectPlan();
        var heap = DirectHeap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));

        var moved = heap.Moved(0x4000);
        foreach (var address in moved.Words.Keys.ToArray())
        {
            if ((address - HeapBase - 0x4000 - 344) % 32 == 4)
                moved.Words[address] = 21u << 20;
        }

        Assert.True(Run(cache, plan, moved, [0x5000, 0], out var second, out _));
        Assert.Equal(0, cache.Hits);
        Assert.NotEqual(0, moved.Reads);
        Assert.NotEqual(first.Images, second.Images);
    }

    [Fact]
    public void ANullTablePointerIsNeverRebased()
    {
        var plan = DirectPlan();
        var heap = DirectHeap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        var materializedBefore = cache.Misses;
        _ = Run(cache, plan, heap.Moved(0x4000), [0, 0], out _, out _);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(materializedBefore + 1, cache.Misses);
    }

    [Fact]
    public void APointerThatAlsoAddressesAGlobalLoadIsComparedAsAValue()
    {
        // The wave-indexed program loads through s[0:1] with GLOBAL_LOAD, whose device range
        // is part of the materialization, so the pointer is a value and a move is a miss.
        var plan = Plan();
        Assert.False(plan.UserDataUse.AnyAddressOnly);
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.True(Run(cache, plan, heap.Moved(0x4000), [0x5000, 0], out _, out _));
        Assert.Equal((0, 2), (cache.Hits, cache.Misses));
    }

    // A buffer descriptor assembled in the shader from the pointer in s[0:1]: the shader sets
    // the stride and record count, user data supplies the base address.
    internal static ShaderResourcePlan PointerBufferPlan() =>
        ShaderResourcePlan.Extract(Program(
            MoveScalar(0, 2, 64),
            MoveScalar(4, 3, 0x2C004000),
            BufferAccess(8, "BufferLoadDword", 0, vectorData: 4),
            EndProgram(16)), ShaderStage.Compute, Hash, 0, 2);

    [Fact]
    public void ABufferBuiltFromAMovedPointerIsRebasedOnAHit()
    {
        if (!ResourceMaterializationCache.ContentKeyed)
            return;
        var plan = PointerBufferPlan();
        Assert.True(plan.UserDataUse.AnyAddressOnly);
        Assert.NotEmpty(plan.UserDataUse.RebasableDwords);
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0x10], out var first, out var firstSpecialization));
        Assert.Equal(new uint[] { 0x1000, 0x10, 64, 0x2C004000 }, first.Buffers[0]);

        Assert.True(Run(cache, plan, heap, [0x5000, 0x11], out var second, out var secondSpecialization));
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
        Assert.Equal(new uint[] { 0x5000, 0x11, 64, 0x2C004000 }, second.Buffers[0]);
        Assert.Equal(new uint[] { 0x1000, 0x10, 64, 0x2C004000 }, first.Buffers[0]);
        Assert.Same(firstSpecialization, secondSpecialization);
        Assert.Equal(new uint[] { 0x5000, 0x11 }, second.UserData);
    }

    [Fact]
    public void ABufferWhoseStrideBitsMovedMaterializesAgain()
    {
        var plan = PointerBufferPlan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0x10], out _, out _));

        // The high word carries the stride above its base-address bits.
        Assert.True(Run(cache, plan, heap, [0x1000, 0x10 | (1u << 20)], out var second, out _));
        Assert.Equal((0, 2), (cache.Hits, cache.Misses));
        Assert.Equal(0x10 | (1u << 20), second.Buffers[0][1]);
    }

    // A buffer descriptor loaded from the table at s[0:1]: the words reach the flattened table
    // and the buffer, nothing else.
    private static ShaderResourcePlan TableBufferPlan() =>
        ShaderResourcePlan.Extract(Program(
            ScalarLoad(0, 0, 4, 4, immediateOffset: 0x20),
            BufferAccess(8, "BufferLoadDword", 4, vectorData: 8),
            EndProgram(16)), ShaderStage.Compute, Hash, 0, 2);

    private static Heap TableBufferHeap(uint baseLow)
    {
        var heap = new Heap();
        heap.Words.Clear();
        heap.Words[HeapBase + 0x20] = baseLow;
        heap.Words[HeapBase + 0x24] = 0x10;
        heap.Words[HeapBase + 0x28] = 256;
        heap.Words[HeapBase + 0x2C] = 0x2C004000;
        return heap;
    }

    [Fact]
    public void ABufferWhoseBaseIsReadFromTheTableTakesTheDrawsBaseOnAHit()
    {
        if (!ResourceMaterializationCache.ContentKeyed)
            return;
        var plan = TableBufferPlan();
        Assert.NotEmpty(plan.UserDataUse.PatchableReads);
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, TableBufferHeap(0x7000), [0x1000, 0], out var first, out var firstSpecialization, readsDirty: true));
        Assert.Equal(new uint[] { 0x7000, 0x10, 256, 0x2C004000 }, first.Buffers[0]);

        // The next draw's table holds a constant buffer allocated elsewhere.
        var moved = TableBufferHeap(0x9000);
        Assert.True(Run(cache, plan, moved, [0x1000, 0], out var second, out var secondSpecialization, readsDirty: true));
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
        Assert.Equal(new uint[] { 0x9000, 0x10, 256, 0x2C004000 }, second.Buffers[0]);
        Assert.Equal(new uint[] { 0x7000, 0x10, 256, 0x2C004000 }, first.Buffers[0]);
        Assert.Same(firstSpecialization, secondSpecialization);
        Assert.Equal(second.FlattenedResourceTable.Length, first.FlattenedResourceTable.Length);
        for (var slot = 0; slot < first.FlattenedResourceTable.Length; slot++)
        {
            if (first.FlattenedResourceTable[slot] == 0x7000)
                Assert.Equal(0x9000u, second.FlattenedResourceTable[slot]);
            else
                Assert.Equal(first.FlattenedResourceTable[slot], second.FlattenedResourceTable[slot]);
        }
    }

    [Fact]
    public void ABufferWhoseFormatIsReadFromTheTableMaterializesAgainWhenItChanges()
    {
        var plan = TableBufferPlan();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, TableBufferHeap(0x7000), [0x1000, 0], out _, out _, readsDirty: true));

        var changed = TableBufferHeap(0x7000);
        changed.Words[HeapBase + 0x2C] = 0x2C005000;
        Assert.True(Run(cache, plan, changed, [0x1000, 0], out var second, out _, readsDirty: true));
        Assert.Equal((0, 2), (cache.Hits, cache.Misses));
        Assert.Equal(0x2C005000u, second.Buffers[0][3]);
    }

    // [local] SHARPEMU_MATERIALIZE_POH: the cache that keeps its arrays on the pinned object heap returns the same
    // materializations, hits and misses as the one that does not, for misses, hits, patched hits and rebased hits.
    private static IEnumerable<(ShaderResourcePlan Plan, Heap Heap, uint[] UserData, bool ReadsDirty)> RetentionSteps()
    {
        var plan = Plan();
        var heap = new Heap();
        yield return (plan, heap, [0x1000, 0], false);
        yield return (plan, heap, [0x1000, 0], false);
        var changed = new Heap();
        changed.Words[HeapBase + 0x100 + 5 * 32] = 0x3000;
        yield return (plan, changed, [0x1000, 0], false);
        yield return (plan, heap, [0x1000, 0], false);
        yield return (plan, heap.Moved(0x4000), [0x5000, 0], false);

        var direct = DirectPlan();
        var directHeap = DirectHeap();
        yield return (direct, directHeap, [0x1000, 0], false);
        yield return (direct, directHeap.Moved(0x4000), [0x5000, 0], false);
        yield return (direct, directHeap.Moved(0x8000), [0x9000, 0], false);

        var table = TableBufferPlan();
        yield return (table, TableBufferHeap(0x7000), [0x1000, 0], true);
        yield return (table, TableBufferHeap(0x9000), [0x1000, 0], true);
        yield return (table, TableBufferHeap(0x9000), [0x1000, 0], true);

        var pointer = PointerBufferPlan();
        var pointerHeap = new Heap();
        yield return (pointer, pointerHeap, [0x1000, 0x10], false);
        yield return (pointer, pointerHeap, [0x5000, 0x11], false);
        yield return (pointer, pointerHeap, [0x5000, 0x11], false);
    }

    private static void AssertSameSnapshot(ResourceSnapshot expected, ResourceSnapshot actual)
    {
        Assert.Equal(expected.Buffers.Select(words => words.ToArray()), actual.Buffers.Select(words => words.ToArray()));
        Assert.Equal(expected.Images.Select(words => words.ToArray()), actual.Images.Select(words => words.ToArray()));
        Assert.Equal(expected.Samplers.Select(words => words.ToArray()), actual.Samplers.Select(words => words.ToArray()));
        Assert.Equal(expected.FlattenedResourceTable, actual.FlattenedResourceTable);
        Assert.Equal(expected.UserData, actual.UserData);
        Assert.Equal(expected.DeviceAddressRanges, actual.DeviceAddressRanges);
    }

    [Fact]
    public void PinnedRetentionMaterializesTheSame()
    {
        var plain = new ResourceMaterializationCache { RetainPinned = false };
        var pinned = new ResourceMaterializationCache { RetainPinned = true };
        ResourceSnapshot? previousPlain = null, previousPinned = null;
        foreach (var (plan, heap, userData, readsDirty) in RetentionSteps())
        {
            var plainOk = Run(plain, plan, heap, userData, out var expected, out var expectedSpecialization, readsDirty: readsDirty);
            var pinnedOk = Run(pinned, plan, heap, userData, out var actual, out var actualSpecialization, readsDirty: readsDirty);
            Assert.Equal(plainOk, pinnedOk);
            Assert.Equal((plain.Hits, plain.Misses, plain.TableRefreshes), (pinned.Hits, pinned.Misses, pinned.TableRefreshes));
            AssertSameSnapshot(expected, actual);
            Assert.Equal(expectedSpecialization, actualSpecialization);
            // A repeated draw gets the same snapshot object from both caches.
            Assert.Equal(ReferenceEquals(previousPlain, expected), ReferenceEquals(previousPinned, actual));
            previousPlain = expected;
            previousPinned = actual;
        }

        Assert.NotEqual(0, pinned.Hits);
    }

    [Fact]
    public void PinnedRetentionKeepsTheEntrysArraysOutOfTheYoungGenerations()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache { RetainPinned = true };
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _));
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
        Assert.Same(first, second);
        Assert.NotEmpty(second.Images);
        foreach (var words in second.Buffers.Concat(second.Images).Concat(second.Samplers))
            if (words.Length != 0)
                Assert.Equal(GC.MaxGeneration, GC.GetGeneration(words));
        foreach (var array in new Array[] { second.FlattenedResourceTable, second.UserData, second.DeviceAddressRanges })
            if (array.Length != 0)
                Assert.Equal(GC.MaxGeneration, GC.GetGeneration(array));

        // A collection of the young generations moves nothing the entry keeps.
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var third, out _));
        Assert.Same(second, third);
    }

    // Upstream's tests (#1082, #1130) beside ours.
    [Fact]
    public void ToggledMipStatisticsBitsReuseTheMaterializationWithTheCurrentSlot()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));
        Assert.Null(first.MipStatistics);
        var record = HeapBase + 0x100 + 5 * 32;
        var image = Array.FindIndex(first.Images, words => words[0] == 0x1000);
        Assert.True(image >= 0);

        heap.Words[record + 5 * 4] = 1u << 25;
        heap.Words[record + 6 * 4] = 0x2A;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _));
        Assert.Same(first.Images, second.Images);
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
        Assert.Equal(0x2Au | ResourceSnapshot.MipStatisticsEnabled, second.MipStatistics![image]);
        Assert.Equal(0u, second.MipStatistics[1 - image]);

        heap.Words[record + 6 * 4] = 0x2B;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var third, out _));
        Assert.Same(first.Images, third.Images);
        Assert.Equal(0x2Bu | ResourceSnapshot.MipStatisticsEnabled, third.MipStatistics![image]);

        heap.Words[record] = 0x3000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var fourth, out _));
        Assert.NotSame(first.Images, fourth.Images);
        Assert.Equal((2, 2), (cache.Hits, cache.Misses));
    }

    private static ShaderResourcePlan PlanWithSpareUserData() =>
        ShaderResourcePlan.Extract(DirectImageTableTests.CreateWaveIndexedDescriptorProgram(), ShaderStage.Compute, Hash, 0, 4);

    [Fact]
    public void AUserDataWordTheProgramNeverReadsDoesNotSplitTheEntry()
    {
        var plan = PlanWithSpareUserData();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0, 5, 6], out var first, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0, 7, 8], out var second, out _));
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
        // The hit carries the second draw's user data and leaves the first draw's snapshot alone.
        Assert.Equal(new uint[] { 0x1000, 0, 7, 8 }, second.UserData);
        Assert.Equal(new uint[] { 0x1000, 0, 5, 6 }, first.UserData);
        Assert.Same(first.Images, second.Images);
        Assert.True(Run(cache, plan, heap, [0x1000, 0, 5, 6], out var third, out _));
        Assert.Same(first, third);
    }

    // A V# taken verbatim from s[4:7], the way titles pass per-draw constants.
    private static ShaderResourcePlan DirectBufferPlan() =>
        Extract(Program(BufferLoad(0, 4), EndProgram(8)), userDataCount: 8);

    // The same V#, whose base pair s[4:5] is also the address of a scalar load.
    private static ShaderResourcePlan SteeringBufferPlan() =>
        Extract(Program(ScalarLoad(0, 4, 16), Vop1(8, "VMovB32", 0, Gen5Operand.Scalar(16)), BufferStore(12, 4), EndProgram(20)),
            userDataCount: 8);

    private static bool RunWords(ResourceMaterializationCache cache, ShaderResourcePlan plan, TestWordMemory memory, uint[] userData,
        out ResourceSnapshot snapshot)
    {
        snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        return cache.Materialize(plan, Inputs(userData, memory.Read, memory.Read), (address, destination, _) =>
        {
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word))
                    return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            return true;
        }, ref snapshot, ref specialization, out _);
    }

    private static uint[] Reference(ShaderResourcePlan plan, TestWordMemory memory, uint[] userData)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref snapshot, ref specialization));
        return snapshot.Buffers[0];
    }

    private static uint[] LinearConstants(int draw) => [0, 0, 0, 0, 0x10000u + (uint)draw * 0x100u, 0, 256, 0x2_0000];

    [Fact]
    public void ALinearlyAllocatedBufferBaseStopsMissingOnceConfirmed()
    {
        // [local] Override: upstream's counts assume upstream's key; SHARPEMU_MATERIALIZATION_CONTENT_KEY (on by default)
        // makes every draw after the first a hit (the V# base is a buffer-dword register).
        using var upstreamKey = LocalCacheSwitches.WithoutContentKey();
        var plan = DirectBufferPlan();
        var memory = new TestWordMemory();
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 16; draw++)
        {
            var userData = LinearConstants(draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            Assert.Equal(Reference(plan, memory, userData), snapshot.Buffers[0]);
            Assert.Equal(userData, snapshot.UserData);
        }

        // One miss to build, three to confirm the word; every later draw is a hit.
        Assert.Equal((12, 4), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void InterleavedObjectsConfirmTheirOwnBases()
    {
        // Constants V# in s[4:7], a second buffer in s[8:11] whose size tells the two objects apart.
        var plan = Extract(Program(BufferLoad(0, 4), BufferLoad(8, 8), EndProgram(16)), userDataCount: 12);
        var memory = new TestWordMemory();
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 32; draw++)
        {
            uint[] userData = [0, 0, 0, 0, 0x10000u + (uint)draw * 0x100u, 0, 256, 0x2_0000, 0x40000, 0, draw % 2 == 0 ? 256u : 512u, 0x2_0000];
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.Buffers.Select(buffer => buffer.ToArray()), snapshot.Buffers.Select(buffer => buffer.ToArray()));
        }

        Assert.True(cache.Hits >= 20, $"hits={cache.Hits} misses={cache.Misses}");
    }

    // Pointer s[0:1] moves on every draw; the program loads four constants through it.
    private static uint[] PointerConstants(int draw) => [0x10000u + (uint)draw * 0x100u, 0, 0, 0, 0x30000, 0, 256, 0x2_0000, 0];

    [Fact]
    public void AMovingScalarLoadPointerStopsMissingOnceConfirmed()
    {
        // [local] Override: upstream's counts assume upstream's key; SHARPEMU_MATERIALIZATION_CONTENT_KEY (on by default)
        // makes every draw after the first a hit (s[0:1] is only read through).
        using var upstreamKey = LocalCacheSwitches.WithoutContentKey();
        // [local] Override: and without word patches (always on) the changed constant behind the last pointer is a hit, not a fresh read.
        using var upstreamPatches = LocalCacheSwitches.WithoutWordPatches();
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        // The same four words repeat every 0x100 bytes, so every pointer finds the same constants.
        var memory = new TestWordMemory { Base = 0x10000, Words = Enumerable.Range(0, 0x1000).Select(index => (uint)(index % 64) * 3 + 1).ToArray() };
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 16; draw++)
        {
            var userData = PointerConstants(draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
            Assert.Equal(expected.Buffers.Select(buffer => buffer.ToArray()), snapshot.Buffers.Select(buffer => buffer.ToArray()));
        }

        Assert.Equal((12, 4), (cache.Hits, cache.Misses));

        // Other constants behind the next pointer: the draw reads them instead of reusing the table.
        var moved = PointerConstants(30);
        memory.At(moved[0] + 8) = 0xBEEF;
        Assert.True(RunWords(cache, plan, memory, moved, out var reread));
        Assert.Equal(0xBEEFu, reread.FlattenedResourceTable[2]);
        Assert.Equal(12, cache.Hits);
    }

    [Fact]
    public void ARebasedHitLeavesTheCachedSnapshotAlone()
    {
        var plan = DirectBufferPlan();
        var memory = new TestWordMemory();
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 4; draw++)
            Assert.True(RunWords(cache, plan, memory, LinearConstants(draw), out _));

        Assert.True(RunWords(cache, plan, memory, LinearConstants(3), out var built));
        Assert.True(RunWords(cache, plan, memory, LinearConstants(9), out var patched));
        Assert.Equal(0x10900u, patched.Buffers[0][0]);
        Assert.Equal(0x10300u, built.Buffers[0][0]);
        Assert.NotSame(built.Buffers, patched.Buffers);
        Assert.True(RunWords(cache, plan, memory, LinearConstants(3), out var again));
        Assert.Equal(0x10300u, again.Buffers[0][0]);
    }

    [Fact]
    public void AReadThatMovesWithTheBaseAndKeepsItsBytesIsReused()
    {
        // [local] Override: upstream's counts assume upstream's key; SHARPEMU_MATERIALIZATION_CONTENT_KEY (on by default)
        // makes every draw after the first a hit (s[4:5] moves the read and the V# base).
        using var upstreamKey = LocalCacheSwitches.WithoutContentKey();
        // [local] Override: and without word patches (always on) the changed word at the moved base is a hit, not a refresh.
        using var upstreamPatches = LocalCacheSwitches.WithoutWordPatches();
        var plan = SteeringBufferPlan();
        var memory = new TestWordMemory { Base = 0x10000, Words = Enumerable.Repeat(0x1234u, 0x1000).ToArray() };
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 16; draw++)
        {
            var userData = LinearConstants(draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.Buffers[0], snapshot.Buffers[0]);
            Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        }

        Assert.Equal((12, 4), (cache.Hits, cache.Misses));

        // The moved read now finds other bytes at the new base: the draw must not reuse the table.
        var changed = LinearConstants(20);
        memory.At(changed[4]) = 0x5678;
        Assert.True(RunWords(cache, plan, memory, changed, out var refreshed));
        Assert.Equal(0x5678u, refreshed.FlattenedResourceTable[0]);
        Assert.Equal(12, cache.Hits);
    }

    [Fact]
    public void MovedConstantsThatChangeEveryDrawAreReadAgain()
    {
        // [local] Override: upstream's counts assume upstream's key; SHARPEMU_MATERIALIZATION_CONTENT_KEY (on by default)
        // makes the moved draws hits that take the new constants.
        using var upstreamKey = LocalCacheSwitches.WithoutContentKey();
        // [local] Override: and without word patches (always on) the moved constants are hits that take the new words, not table refreshes.
        using var upstreamPatches = LocalCacheSwitches.WithoutWordPatches();
        var plan = SteeringBufferPlan();
        var memory = new TestWordMemory { Base = 0x10000, Words = Enumerable.Range(0, 0x1000).Select(index => (uint)index * 7).ToArray() };
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 16; draw++)
        {
            var userData = LinearConstants(draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            Assert.Equal(Reference(plan, memory, userData), snapshot.Buffers[0]);
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        }

        // The constants differ at every base, so nothing is reused as is; once the base is
        // confirmed, the moved table is evaluated again instead of the whole walk.
        Assert.Equal(0, cache.Hits);
        Assert.True(cache.TableRefreshes >= 10, $"refreshes={cache.TableRefreshes} misses={cache.Misses}");
    }

    // The V# comes from a descriptor table at s[0:1]; its constants are read through it.
    private static ShaderResourcePlan MemoryDescriptorPlan() =>
        Extract(Program(ScalarLoad(0, 0, 8, 4), ScalarBufferLoad(4, 8, 12, 4), Vop1(8, "VMovB32", 0, Gen5Operand.Scalar(12)),
            BufferLoad(12, 8), EndProgram(16)), userDataCount: 2);

    // The title rewrites the table every draw: a freshly allocated base, and new constants there.
    private static void WriteDraw(TestWordMemory memory, int draw)
    {
        var bufferBase = 0x10000u + (uint)draw * 0x100u;
        memory.At(0x1000) = bufferBase;
        memory.At(0x1004) = 0;
        memory.At(0x1008) = 256;
        memory.At(0x100C) = 0x2_0000;
        for (var word = 0u; word < 4; word++)
            memory.At(bufferBase + word * 4) = (uint)draw * 10 + word;
    }

    [Fact]
    public void ABaseRewrittenInGuestMemoryStopsMissingOnceConfirmed()
    {
        // [local] Override: upstream's counts assume no word patches; ours (always on) make the resized draw a hit: its record count (dword 2) is a patchable word.
        using var upstreamPatches = LocalCacheSwitches.WithoutWordPatches();
        var plan = MemoryDescriptorPlan();
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x8000] };
        var cache = new ResourceMaterializationCache();
        uint[] userData = [0x1000, 0];
        for (var draw = 0; draw < 16; draw++)
        {
            WriteDraw(memory, draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.Buffers.Select(buffer => buffer.ToArray()), snapshot.Buffers.Select(buffer => buffer.ToArray()));
            Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        }

        // One miss to build, three to confirm the base; the later draws patch it.
        Assert.True(cache.Hits >= 10, $"hits={cache.Hits} misses={cache.Misses}");

        // Another descriptor word changed with the base: the draw is materialized again.
        WriteDraw(memory, 20);
        memory.At(0x1008) = 512;
        var hits = cache.Hits;
        Assert.True(RunWords(cache, plan, memory, userData, out var resized));
        Assert.Equal(hits, cache.Hits);
        Assert.Equal(512u, resized.Buffers[0][2]);
    }

    private static void AssertMatchesFullWalk(ShaderResourcePlan plan, TestWordMemory memory, uint[] userData, ResourceSnapshot snapshot)
    {
        var expected = new ResourceSnapshot();
        var expectedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
        Assert.Equal(expected.Buffers.Select(buffer => buffer.ToArray()), snapshot.Buffers.Select(buffer => buffer.ToArray()));
        Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
    }

    [Fact]
    public void ATableConstantBesideARewrittenBaseIsCopiedForTheDraw()
    {
        // The V# and a constant both come from the table at s[0:1]; the constant changes every draw.
        var plan = Extract(Program(ScalarLoad(0, 0, 8, 4), ScalarLoad(4, 0, 16, 1, immediateOffset: 16),
            ScalarBufferLoad(8, 8, 12, 4), Vop1(12, "VMovB32", 0, Gen5Operand.Scalar(12)),
            Vop1(16, "VMovB32", 1, Gen5Operand.Scalar(16)), BufferLoad(20, 8), EndProgram(24)), userDataCount: 2);
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x8000] };
        var cache = new ResourceMaterializationCache();
        uint[] userData = [0x1000, 0];
        for (var draw = 0; draw < 16; draw++)
        {
            WriteDraw(memory, draw);
            memory.At(0x1010) = (uint)draw * 3 + 1;
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            AssertMatchesFullWalk(plan, memory, userData, snapshot);
        }

        Assert.True(cache.Hits >= 10, $"hits={cache.Hits} misses={cache.Misses}");
    }

    [Fact]
    public void AlternatingObjectsWithRewrittenBasesRebaseTheirOwnVariant()
    {
        // Two objects share the draw key and differ in their buffer size; both move their base.
        var plan = MemoryDescriptorPlan();
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x8000] };
        var cache = new ResourceMaterializationCache();
        uint[] userData = [0x1000, 0];
        for (var draw = 0; draw < 32; draw++)
        {
            WriteDraw(memory, draw);
            memory.At(0x1008) = draw % 2 == 0 ? 256u : 512u;
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            AssertMatchesFullWalk(plan, memory, userData, snapshot);
        }

        Assert.True(cache.Hits >= 20, $"hits={cache.Hits} misses={cache.Misses}");
    }

    private static bool RunAfterChange(ResourceMaterializationCache cache, ShaderResourcePlan plan, Heap heap, ref uint value,
        out ResourceSnapshot snapshot)
    {
        value += 0x1000;
        heap.Words[HeapBase + 0x100 + 5 * 32] = value;
        return Run(cache, plan, heap, [0x1000, 0], out snapshot, out _);
    }

    [Fact]
    public void WithChurnLimitedAKeyIsStoredOnItsSecondSighting()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache(limitChurn: true);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));
        Assert.Equal((0, 0, 1), (cache.Hits, cache.Misses, cache.Unrecorded));

        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _));
        Assert.Equal((0, 1, 1), (cache.Hits, cache.Misses, cache.Unrecorded));
        Assert.Equal(first.Images.Select(image => image.ToArray()), second.Images.Select(image => image.ToArray()));

        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var third, out _));
        Assert.Same(second, third);
        Assert.Equal((1, 1, 1), (cache.Hits, cache.Misses, cache.Unrecorded));
    }

    [Fact]
    public void WithChurnLimitedAKeyThatIsStaleOnEveryLookupStopsBeingStored()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache(limitChurn: true);
        uint value = 0x3000;
        for (var lookup = 0; lookup < 4; lookup++)
        {
            Assert.True(RunAfterChange(cache, plan, heap, ref value, out _));
        }

        Assert.Equal((0, 3, 1), (cache.Hits, cache.Misses, cache.Unrecorded));

        for (var lookup = 0; lookup < 33; lookup++)
        {
            Assert.True(RunAfterChange(cache, plan, heap, ref value, out var snapshot));
            var reference = new ResourceSnapshot();
            var referenceSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: heap.Read),
                ref reference, ref referenceSpecialization));
            Assert.Equal(reference.Images.Select(image => image.ToArray()), snapshot.Images.Select(image => image.ToArray()));
        }

        Assert.Equal((0, 3, 34), (cache.Hits, cache.Misses, cache.Unrecorded));

        Assert.True(RunAfterChange(cache, plan, heap, ref value, out _));
        Assert.Equal((0, 4, 34), (cache.Hits, cache.Misses, cache.Unrecorded));
        Assert.True(RunAfterChange(cache, plan, heap, ref value, out _));
        Assert.Equal((0, 4, 35), (cache.Hits, cache.Misses, cache.Unrecorded));
    }

    [Fact]
    public void WithChurnLimitedAVolatileKeyIsStoredAgainOnceItSettles()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache(limitChurn: true);
        uint value = 0x3000;
        for (var lookup = 0; lookup < 37; lookup++)
        {
            Assert.True(RunAfterChange(cache, plan, heap, ref value, out _));
        }

        Assert.Equal((0, 3, 34), (cache.Hits, cache.Misses, cache.Unrecorded));

        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var stored, out _));
        Assert.Equal((0, 4, 34), (cache.Hits, cache.Misses, cache.Unrecorded));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var reused, out _));
        Assert.Same(stored, reused);
        Assert.Equal((1, 4, 34), (cache.Hits, cache.Misses, cache.Unrecorded));
    }

    [Fact]
    public void AStoredSnapshotRevalidatesUntilItsBytesChange()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var snapshot, out _));
        Assert.True(cache.Revalidate(snapshot, heap.ReadResident));
        Assert.True(cache.Revalidate(snapshot, heap.ReadResident));
        heap.Words[HeapBase + 0x100 + 5 * 32] = 0x3000;
        Assert.False(cache.Revalidate(snapshot, heap.ReadResident));
    }

    [Fact]
    public void AnUnrecordedSnapshotDoesNotRevalidate()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache(limitChurn: true);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var snapshot, out _));
        Assert.False(cache.Revalidate(snapshot, heap.ReadResident));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var stored, out _));
        Assert.True(cache.Revalidate(stored, heap.ReadResident));
    }
}
