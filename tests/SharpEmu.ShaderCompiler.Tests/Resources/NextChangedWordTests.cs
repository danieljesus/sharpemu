// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// [local] SHARPEMU_VALIDATE_IN_PLACE: the vectorized skip finds the same changed words as a word-by-word scan.
public sealed class NextChangedWordTests
{
    private static List<int> WordByWord(byte[] current, byte[] cached)
    {
        var changed = new List<int>();
        for (var offset = 0; offset < current.Length; offset += sizeof(uint))
        {
            if (BitConverter.ToUInt32(current, offset) != BitConverter.ToUInt32(cached, offset))
                changed.Add(offset);
        }

        return changed;
    }

    private static List<int> Skipping(byte[] current, byte[] cached)
    {
        var changed = new List<int>();
        var offset = 0;
        while (true)
        {
            offset = ResourceMaterializationCache.NextChangedWord(current, cached, offset);
            if (offset >= current.Length)
                return changed;
            changed.Add(offset);
            offset += sizeof(uint);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    [InlineData(64, 3)]
    [InlineData(4096, 7)]
    [InlineData(65536, 40)]
    public void TheSkipFindsEveryChangedWordInOrder(int bytes, int changes)
    {
        var random = new Random(bytes + changes);
        for (var round = 0; round < 20; round++)
        {
            var cached = new byte[bytes];
            random.NextBytes(cached);
            var current = (byte[])cached.Clone();
            for (var change = 0; change < changes && bytes != 0; change++)
                current[random.Next(bytes)] ^= (byte)(1 << random.Next(8));
            Assert.Equal(WordByWord(current, cached), Skipping(current, cached));
        }
    }

    [Fact]
    public void AChangeInTheLastByteOfAWordFindsThatWord()
    {
        var cached = new byte[32];
        var current = new byte[32];
        current[23] = 1;
        Assert.Equal(20, ResourceMaterializationCache.NextChangedWord(current, cached, 0));
        Assert.Equal(32, ResourceMaterializationCache.NextChangedWord(current, cached, 24));
    }
}
