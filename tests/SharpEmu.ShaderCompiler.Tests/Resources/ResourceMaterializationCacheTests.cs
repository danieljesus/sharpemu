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
    private sealed class Heap
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

    private static bool Run(ResourceMaterializationCache cache, ShaderResourcePlan plan, Heap heap, uint[] userData,
        out ResourceSnapshot snapshot, out ResourceSpecialization specialization, ulong shaderBase = 0)
    {
        snapshot = new ResourceSnapshot();
        specialization = new ResourceSpecialization();
        return cache.Materialize(plan, Inputs(userData, readCleanMemory: heap.Read, shaderBase: shaderBase), heap.ReadResident,
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
    public void AChangedTableOnlyWordRefreshesOnlyTheTable()
    {
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
    private static ShaderResourcePlan DirectPlan() =>
        ShaderResourcePlan.Extract(DirectImageTableTests.CreateProgram(), ShaderStage.Compute, Hash, 0, 2);

    private static Heap DirectHeap()
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
    private static ShaderResourcePlan PointerBufferPlan() =>
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
}
