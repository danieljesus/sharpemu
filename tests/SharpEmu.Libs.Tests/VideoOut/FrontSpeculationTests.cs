// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_SPECULATE: a front preparation made from speculated reads is kept only when every read,
// replayed in order through the render thread's readers, answers the same.
[Collection(SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingStateCollection.Name)]
public sealed class FrontSpeculationTests
{
    private sealed class Replay : FrontSpeculation.IReplay
    {
        public readonly Dictionary<ulong, byte[]> Memory = new();
        public readonly HashSet<ulong> Dirty = new();
        public readonly List<string> Calls = new();
        public (ulong Reader, ulong BaseAddress) Context;

        private bool Read(ulong address, Span<byte> destination, bool clean)
        {
            if ((clean && Dirty.Contains(address)) || !Memory.TryGetValue(address, out var bytes) || bytes.Length < destination.Length)
                return false;
            bytes.AsSpan(0, destination.Length).CopyTo(destination);
            return true;
        }

        public bool ReadWord(ulong address, out uint word)
        {
            Calls.Add($"word:{address:X}:{Context.Reader:X}:{Context.BaseAddress:X}");
            Span<byte> bytes = stackalloc byte[4];
            var ok = Read(address, bytes, clean: false);
            word = ok ? BitConverter.ToUInt32(bytes) : 0;
            return ok;
        }

        public bool ReadCleanWord(ulong address, out uint word)
        {
            Calls.Add($"clean:{address:X}");
            Span<byte> bytes = stackalloc byte[4];
            var ok = Read(address, bytes, clean: true);
            word = ok ? BitConverter.ToUInt32(bytes) : 0;
            return ok;
        }

        public bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            Calls.Add($"resident:{address:X}:{destination.Length}:{clean}");
            return Read(address, destination, clean);
        }

        public (ulong Reader, ulong BaseAddress) Enter(ulong reader, ulong baseAddress)
        {
            var previous = Context;
            Context = (reader, baseAddress);
            return previous;
        }

        public void Leave((ulong Reader, ulong BaseAddress) previous) => Context = previous;
    }

    private static FrontSpeculation.Reads Reads(params (FrontSpeculation.Kind Kind, ulong Address, byte[] Bytes, bool Ok)[] items)
    {
        var reads = new FrontSpeculation.Reads();
        foreach (var (kind, address, bytes, ok) in items)
            reads.Add(kind, address, bytes, ok, reader: 0xAB, baseAddress: 0x1000);
        return reads;
    }

    private static byte[] Word(uint value) => BitConverter.GetBytes(value);

    [Fact]
    public void KeepsAPreparationWhoseReadsAllAnswerTheSame()
    {
        var replay = new Replay();
        replay.Memory[0x100] = Word(7);
        replay.Memory[0x200] = Word(9);
        var large = Enumerable.Range(0, 200).Select(index => (byte)index).ToArray();
        replay.Memory[0x300] = large;
        var reads = Reads(
            (FrontSpeculation.Kind.Word, 0x100, Word(7), true),
            (FrontSpeculation.Kind.CleanWord, 0x200, Word(9), true),
            (FrontSpeculation.Kind.Resident, 0x300, large, true),
            (FrontSpeculation.Kind.Word, 0x400, Word(0), false));

        Assert.Equal(FrontSpeculation.Outcome.Same, FrontSpeculation.Validate(reads, replay));
        Assert.Equal(["word:100:AB:1000", "clean:200", "resident:300:200:False", "word:400:AB:1000"], replay.Calls);
        Assert.Equal(default, replay.Context);
    }

    [Fact]
    public void StopsAtTheFirstReadWhoseValueDiffers()
    {
        var replay = new Replay();
        replay.Memory[0x100] = Word(7);
        replay.Memory[0x200] = Word(10);
        replay.Memory[0x300] = Word(3);
        var reads = Reads(
            (FrontSpeculation.Kind.Word, 0x100, Word(7), true),
            (FrontSpeculation.Kind.Word, 0x200, Word(9), true),
            (FrontSpeculation.Kind.Word, 0x300, Word(3), true));

        Assert.Equal(FrontSpeculation.Outcome.Value, FrontSpeculation.Validate(reads, replay));
        Assert.Equal(2, replay.Calls.Count);
    }

    [Fact]
    public void ARefusedReadDiffersFromAnAnsweredOne()
    {
        var replay = new Replay();
        replay.Memory[0x100] = Word(7);
        replay.Dirty.Add(0x100);

        // The front read the word; on the render thread the clean reader refuses it while the GPU owns it.
        Assert.Equal(FrontSpeculation.Outcome.Success,
            FrontSpeculation.Validate(Reads((FrontSpeculation.Kind.CleanWord, 0x100, Word(7), true)), replay));
        Assert.Equal(FrontSpeculation.Outcome.Same,
            FrontSpeculation.Validate(Reads((FrontSpeculation.Kind.Word, 0x100, Word(7), true)), replay));
        Assert.Equal(FrontSpeculation.Outcome.Success,
            FrontSpeculation.Validate(Reads((FrontSpeculation.Kind.ResidentClean, 0x100, Word(7), true)), replay));
        Assert.Equal(FrontSpeculation.Outcome.Success,
            FrontSpeculation.Validate(Reads((FrontSpeculation.Kind.Word, 0x900, Word(0), true)), replay));
    }

    [Fact]
    public void APreparationWithoutSpeculatedReadsLeavesNothingToTake()
    {
        FrontSpeculation.Begin();
        Assert.True(FrontSpeculation.Active);
        Assert.False(FrontSpeculation.AbandonsFatal);
        FrontSpeculation.End();
        Assert.False(FrontSpeculation.Active);
        Assert.Null(FrontSpeculation.Take());
    }

    [Fact]
    public void SpeculatedReadsAreTakenOnceInOrder()
    {
        FrontSpeculation.Begin();
        FrontSpeculation.Note(FrontSpeculation.Kind.Word, 0x10, Word(1), true, 1, 2);
        FrontSpeculation.Note(FrontSpeculation.Kind.Resident, 0x20, new byte[] { 5, 6, 7 }, true, 3, 4);
        FrontSpeculation.End();
        var reads = FrontSpeculation.Take();
        Assert.NotNull(reads);
        Assert.Null(FrontSpeculation.Take());
        Assert.Equal([0x10UL, 0x20UL], reads.Items.Select(read => read.Address));
        Assert.Equal(new byte[] { 5, 6, 7 }, reads.BytesOf(reads.Items[1]).ToArray());
        Assert.Equal((3UL, 4UL), (reads.Items[1].Reader, reads.Items[1].BaseAddress));
        FrontSpeculation.Release(reads);
    }

    [Fact]
    public void AFatalCheckAbandonsOnlyAPreparationThatSpeculated()
    {
        using var fatal = new SharpEmu.Libs.Tests.Gpu.Scheduling.FatalScope();
        FrontSpeculation.Begin();
        try
        {
            Assert.False(FrontSpeculation.AbandonsFatal);
            FrontSpeculation.Note(FrontSpeculation.Kind.Word, 0x10, Word(1), true, 0, 0);
            Assert.True(FrontSpeculation.AbandonsFatal);
            Assert.Throws<FrontSpeculation.AbandonException>(() => SubmissionScheduler.Fatal("stale header"));
            Assert.Empty(fatal.Messages);
        }
        finally
        {
            FrontSpeculation.End();
        }

        Assert.False(FrontSpeculation.AbandonsFatal);
        FrontSpeculation.Release(FrontSpeculation.Take());
    }

    [Fact]
    public async Task TheModeOverrideStaysInItsFlow()
    {
        FrontSpeculation.Mode = 2;
        Assert.True(FrontSpeculation.Verify);
        var other = await Task.Run(() => FrontSpeculation.Mode = 0);
        Assert.Equal(0, other);
        Assert.Equal(2, FrontSpeculation.Mode);
        FrontSpeculation.Mode = 0;
    }
}
