// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// [local] SHARPEMU_DIRTY_RUN_READS: a run of adjacent words goes to the host's run reader, which answers it as the resident
// reader (clean) or the word reader word by word (GPU-owned) would; every result equals the old path's, a refused run
// reads exactly what the old path read, and =2 tells a wrong answer from a raced one.
public sealed class DirtyRunReadsTests
{
    private const ulong Table = 0x1000;

    private sealed class Mode : IDisposable
    {
        private readonly int _previous = DirtyRunReads.Mode;

        public Mode(int mode) => DirtyRunReads.Mode = mode;

        public void Dispose() => DirtyRunReads.Mode = _previous;
    }

    // Guest memory as the render thread's readers see it: a word the GPU owns answers its synchronized value to the word
    // reader and makes the resident reader refuse any range holding one of its bytes. The run reader answers as the host's
    // does: the bytes when the resident checks pass, else each word through the word reader's body.
    private sealed class Memory
    {
        public readonly Dictionary<ulong, uint> Words = [];
        public readonly Dictionary<ulong, uint> GpuOwned = [];
        public ulong MappedEnd = 0x3000;
        public int WordReads;
        public int ResidentReads;
        public int RunReads;
        public int HostWordReads;
        public ResidentRunRead? Forced;
        public Action? BeforeRun;
        public uint? Corruption;

        private bool Mapped(ulong address, int length) => address >= 0x1000 && address + (ulong)length <= MappedEnd;

        private bool Owned(ulong address, int length)
        {
            foreach (var word in GpuOwned.Keys)
                if (word < address + (ulong)length && address < word + sizeof(uint))
                    return true;
            return false;
        }

        private bool Word(ulong address, out uint word)
        {
            word = 0;
            if (!Mapped(address, sizeof(uint)))
                return false;
            if (GpuOwned.TryGetValue(address, out word))
                return true;
            Words.TryGetValue(address, out word);
            return true;
        }

        public bool ReadWord(ulong address, out uint word)
        {
            WordReads++;
            return Word(address, out word);
        }

        public bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            ResidentReads++;
            if (!Mapped(address, destination.Length) || Owned(address, destination.Length))
                return false;
            for (var offset = 0; offset < destination.Length; offset += sizeof(uint))
            {
                Words.TryGetValue(address + (ulong)offset, out var word);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], word);
            }

            return true;
        }

        public ResidentRunRead ReadRun(ulong address, Span<byte> destination, ReadOnlySpan<ulong> wordAddresses, Span<uint> words, Span<bool> ok)
        {
            RunReads++;
            if (Forced is { } forced)
                return forced;
            BeforeRun?.Invoke();
            var outcome = ResidentRunRead.Refused;
            if (Mapped(address, destination.Length) && !Owned(address, destination.Length))
            {
                ResidentReads--;
                ReadResident(address, destination, clean: false);
                outcome = ResidentRunRead.Bytes;
            }
            else
            {
                for (var index = 0; index < wordAddresses.Length; index++)
                {
                    HostWordReads++;
                    ok[index] = Word(wordAddresses[index], out words[index]);
                }

                outcome = ResidentRunRead.Words;
            }

            if (Corruption is { } corruption)
            {
                if (outcome == ResidentRunRead.Bytes)
                    BinaryPrimitives.WriteUInt32LittleEndian(destination, corruption);
                else if (outcome == ResidentRunRead.Words)
                    words[0] = corruption;
            }

            return outcome;
        }
    }

    // The V# at table+0x20: one run of four adjacent words.
    private static ShaderResourcePlan Plan() =>
        ShaderResourcePlan.Extract(Program(
            ScalarLoad(0, 0, 4, 4, immediateOffset: 0x20),
            BufferAccess(8, "BufferLoadDword", 4, vectorData: 8),
            EndProgram(16)), ShaderStage.Compute, Hash, 0, 4);

    private static Memory NewMemory()
    {
        var memory = new Memory();
        memory.Words[Table + 0x20] = 0x7000;
        memory.Words[Table + 0x24] = 0x10;
        memory.Words[Table + 0x28] = 256;
        memory.Words[Table + 0x2C] = 0x2C004000;
        return memory;
    }

    private static ResourceRuntimeInputs Inputs(Memory memory, bool runs, bool clean = false) => new()
    {
        UserData = [(uint)Table, 0, 7, 8],
        ReadMemory = memory.ReadWord,
        ReadCleanMemory = memory.ReadWord,
        ReadResidentMemory = memory.ReadResident,
        ReadResidentRun = runs ? memory.ReadRun : null,
        ReadsClean = clean,
    };

    private static (bool Ok, ResourceSnapshot Snapshot, ResourceMaterializationFailure Failure) Fast(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var ok = ResourceMaterializer.MaterializeFast(plan, inputs, ref snapshot, ref specialization, out var failure);
        return (ok, snapshot, failure);
    }

    private static void AssertSame((bool Ok, ResourceSnapshot Snapshot, ResourceMaterializationFailure Failure) expected,
        (bool Ok, ResourceSnapshot Snapshot, ResourceMaterializationFailure Failure) actual)
    {
        Assert.Equal(expected.Ok, actual.Ok);
        Assert.Equal(expected.Failure, actual.Failure);
        if (!expected.Ok)
            return;
        Assert.Equal(expected.Snapshot.Buffers.Select(words => words.ToArray()), actual.Snapshot.Buffers.Select(words => words.ToArray()));
        Assert.Equal(expected.Snapshot.FlattenedResourceTable, actual.Snapshot.FlattenedResourceTable);
        Assert.Equal(expected.Snapshot.DeviceAddressRanges, actual.Snapshot.DeviceAddressRanges);
    }

    private static long Raced() =>
        long.Parse(Regex.Match(DirtyRunReads.TakeReport(), @" raced=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    private static void Reset(Memory memory) => memory.WordReads = memory.ResidentReads = memory.RunReads = memory.HostWordReads = 0;

    [Fact]
    public void ACleanRunIsReadAsBytes()
    {
        using var mode = new Mode(1);
        var plan = Plan();
        Assert.True(ResourceMaterializer.CanMaterializeFast(plan));
        var memory = NewMemory();
        var expected = Fast(plan, Inputs(memory, runs: false));
        Assert.True(expected.Ok);
        Reset(memory);
        AssertSame(expected, Fast(plan, Inputs(memory, runs: true)));
        Assert.Equal(1, memory.RunReads);
        Assert.Equal(0, memory.ResidentReads);
        Assert.Equal(0, memory.WordReads);
    }

    // The GPU owns the V#'s record count: the resident read refuses, and the word reader answers its synchronized value,
    // which the guest bytes do not hold yet. The run reader answers it the same way, without the per-word path.
    [Fact]
    public void AGpuOwnedRunIsAnsweredWordByWordWithTheSynchronizedValues()
    {
        using var mode = new Mode(1);
        var plan = Plan();
        var memory = NewMemory();
        memory.GpuOwned[Table + 0x28] = 512;
        Reset(memory);
        var expected = Fast(plan, Inputs(memory, runs: false));
        Assert.True(expected.Ok);
        Assert.Equal(512u, expected.Snapshot.Buffers[0][2]);
        Assert.Equal(1, memory.ResidentReads);
        Assert.Equal(4, memory.WordReads);
        Reset(memory);
        AssertSame(expected, Fast(plan, Inputs(memory, runs: true)));
        Assert.Equal(1, memory.RunReads);
        Assert.Equal(4, memory.HostWordReads);
        Assert.Equal(0, memory.ResidentReads);
        Assert.Equal(0, memory.WordReads);
    }

    // A failed word inside a GPU-owned run keeps the old path's validity rule (zero, valid only for a null base or an
    // absolute read): the outcome, success or failure, is the old path's.
    [Fact]
    public void AFailedWordKeepsTheOldValidityRule()
    {
        using var mode = new Mode(1);
        var plan = Plan();
        var memory = NewMemory();
        memory.GpuOwned[Table + 0x20] = 0x9000;
        memory.MappedEnd = Table + 0x2C;
        var expected = Fast(plan, Inputs(memory, runs: false));
        var actual = Fast(plan, Inputs(memory, runs: true));
        AssertSame(expected, actual);
    }

    [Fact]
    public void ARefusedRunReadsExactlyAsBefore()
    {
        using var mode = new Mode(1);
        var plan = Plan();
        var memory = NewMemory();
        memory.GpuOwned[Table + 0x24] = 0x11;
        Reset(memory);
        var expected = Fast(plan, Inputs(memory, runs: false));
        var (words, resident) = (memory.WordReads, memory.ResidentReads);
        Reset(memory);
        memory.Forced = ResidentRunRead.Refused;
        AssertSame(expected, Fast(plan, Inputs(memory, runs: true)));
        Assert.Equal(words, memory.WordReads);
        Assert.Equal(resident, memory.ResidentReads);
    }

    [Fact]
    public void WithoutTheSwitchOrForCleanReadsNoRunIsReadThroughTheHost()
    {
        var plan = Plan();
        var memory = NewMemory();
        using (new Mode(0))
            Fast(plan, Inputs(memory, runs: true));
        using (new Mode(1))
            Fast(plan, Inputs(memory, runs: true, clean: true));
        Assert.Equal(0, memory.RunReads);
    }

    [Fact]
    public void AThreadThatReadsNoRunsStopsAsking()
    {
        var plan = Plan();
        var memory = NewMemory();
        var expected = Fast(plan, Inputs(memory, runs: false));
        Reset(memory);
        memory.Forced = ResidentRunRead.OtherThread;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                DirtyRunReads.Mode = 1;
                AssertSame(expected, Fast(plan, Inputs(memory, runs: true)));
                AssertSame(expected, Fast(plan, Inputs(memory, runs: true)));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal(1, memory.RunReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifyKeepsTheRealAnswerAndCountsAWrongHostAnswer(bool gpuOwned)
    {
        using var mode = new Mode(2);
        var plan = Plan();
        var memory = NewMemory();
        if (gpuOwned)
            memory.GpuOwned[Table + 0x28] = 512;
        var expected = Fast(plan, Inputs(memory, runs: false));
        memory.Corruption = 0x1234;
        var wrongBefore = DirtyRunReads.WrongTotal;
        AssertSame(expected, Fast(plan, Inputs(memory, runs: true)));
        Assert.Equal(wrongBefore + 1, DirtyRunReads.WrongTotal);
    }

    // A guest write between the old path's reads and the host's: the word read again agrees with the host, so it is raced.
    [Fact]
    public void VerifyCountsAWordChangedBetweenTheReadsAsRaced()
    {
        using var mode = new Mode(2);
        var plan = Plan();
        var memory = NewMemory();
        var expected = Fast(plan, Inputs(memory, runs: false));
        memory.BeforeRun = () => memory.Words[Table + 0x2C] = 0x2C005000;
        var wrongBefore = DirtyRunReads.WrongTotal;
        Raced();
        AssertSame(expected, Fast(plan, Inputs(memory, runs: true)));
        Assert.Equal(wrongBefore, DirtyRunReads.WrongTotal);
        Assert.True(Raced() >= 1);
    }

    [Fact]
    public void VerifyOfACorrectHostAnswerCountsNothingWrong()
    {
        using var mode = new Mode(2);
        var plan = Plan();
        var memory = NewMemory();
        memory.GpuOwned[Table + 0x28] = 512;
        var expected = Fast(plan, Inputs(memory, runs: false));
        var wrongBefore = DirtyRunReads.WrongTotal;
        Reset(memory);
        AssertSame(expected, Fast(plan, Inputs(memory, runs: true)));
        Assert.Equal(wrongBefore, DirtyRunReads.WrongTotal);
        Assert.Equal(1, memory.RunReads);
        Assert.Equal(4, memory.WordReads);
    }
}
