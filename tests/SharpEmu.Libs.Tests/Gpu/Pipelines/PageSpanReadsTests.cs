// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// [local] SHARPEMU_PAGE_SPAN_READS: the words of one level that lie in one 4 KB page and are not adjacent are read as
// one span; every result equals the reads without the switch, a refused span reads exactly what they read, and =2
// tells a wrong span from a raced one.
public sealed class PageSpanReadsTests
{
    private const ulong Table = 0x1000;

    private sealed class Mode : IDisposable
    {
        private readonly int _previous = PageSpanReads.Mode;

        public Mode(int mode) => PageSpanReads.Mode = mode;

        public void Dispose() => PageSpanReads.Mode = _previous;
    }

    // Guest memory as the render thread's readers see it: a word the GPU owns answers its synchronized value to the word
    // reader, and makes the resident and span readers refuse any range that holds one of its bytes.
    private sealed class Memory
    {
        public readonly Dictionary<ulong, uint> Words = [];
        public readonly Dictionary<ulong, uint> GpuOwned = [];
        public ulong MappedEnd = 0x3000;
        public int WordReads;
        public int ResidentReads;
        public int SpanReads;
        public Func<ulong, int, ResidentSpanRead?>? SpanOverride;
        public Action? BeforeSpan;
        public uint? SpanCorruption;

        private bool Mapped(ulong address, int length) => address >= 0x1000 && address + (ulong)length <= MappedEnd;

        private bool Owned(ulong address, int length)
        {
            foreach (var word in GpuOwned.Keys)
                if (word < address + (ulong)length && address < word + sizeof(uint))
                    return true;
            return false;
        }

        private void Copy(ulong address, Span<byte> destination)
        {
            for (var offset = 0; offset < destination.Length; offset += sizeof(uint))
            {
                Words.TryGetValue(address + (ulong)offset, out var word);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], word);
            }
        }

        public bool ReadWord(ulong address, out uint word)
        {
            WordReads++;
            word = 0;
            if (!Mapped(address, sizeof(uint)))
                return false;
            if (GpuOwned.TryGetValue(address, out word))
                return true;
            Words.TryGetValue(address, out word);
            return true;
        }

        public bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            ResidentReads++;
            if (!Mapped(address, destination.Length) || Owned(address, destination.Length))
                return false;
            Copy(address, destination);
            return true;
        }

        public ResidentSpanRead ReadSpan(ulong address, Span<byte> destination)
        {
            SpanReads++;
            Assert.True((address & ~0xFFFul) == ((address + (ulong)destination.Length - 1) & ~0xFFFul), "A span crossed a 4 KB page.");
            if (SpanOverride?.Invoke(address, destination.Length) is { } forced)
                return forced;
            BeforeSpan?.Invoke();
            if (!Mapped(address, destination.Length) || Owned(address, destination.Length))
                return ResidentSpanRead.Refused;
            Copy(address, destination);
            if (SpanCorruption is { } corruption)
                BinaryPrimitives.WriteUInt32LittleEndian(destination, corruption);
            return ResidentSpanRead.Read;
        }

        public void Buffer(ulong address, uint baseLow)
        {
            Words[address] = baseLow;
            Words[address + 4] = 0x10;
            Words[address + 8] = 256;
            Words[address + 12] = 0x2C004000;
        }
    }

    // Level 1: the V# at table+0x20, the offset word at table+0x44 and the V# at table+0x60 (three units of one page,
    // not adjacent). Level 2: the V# at table + that offset.
    private static ShaderResourcePlan GappyPlan() =>
        ShaderResourcePlan.Extract(Program(
            ScalarLoad(0, 0, 4, 4, immediateOffset: 0x20),
            ScalarLoad(8, 0, 12, 1, immediateOffset: 0x44),
            ScalarLoad(16, 0, 8, 4, immediateOffset: 0x60),
            ScalarLoad(24, 0, 16, 4, dynamicOffsetRegister: 12),
            BufferAccess(32, "BufferLoadDword", 4, vectorData: 20),
            BufferAccess(40, "BufferLoadDword", 8, vectorData: 21),
            BufferAccess(48, "BufferLoadDword", 16, vectorData: 22),
            EndProgram(56)), ShaderStage.Compute, Hash, 0, 4);

    private static Memory GappyMemory(ulong table = Table)
    {
        var memory = new Memory();
        memory.Buffer(table + 0x20, 0x7000);
        memory.Words[table + 0x44] = 0x80;
        memory.Buffer(table + 0x60, 0x8000);
        memory.Buffer(table + 0x80, 0x9000);
        return memory;
    }

    private static ResourceRuntimeInputs Inputs(Memory memory, ulong table, bool spans, bool clean = false) => new()
    {
        UserData = [(uint)table, (uint)(table >> 32), 7, 8],
        ReadMemory = memory.ReadWord,
        ReadCleanMemory = memory.ReadWord,
        ReadResidentMemory = memory.ReadResident,
        ReadResidentSpan = spans ? memory.ReadSpan : null,
        ReadsClean = clean,
    };

    private static ResourceSnapshot Fast(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.MaterializeFast(plan, inputs, ref snapshot, ref specialization, out _));
        return snapshot;
    }

    // The fast materialization without the switch (no span reader) on the same memory: the reference.
    private static (ResourceSnapshot Snapshot, int WordReads, int ResidentReads) Reference(ShaderResourcePlan plan, Memory memory, ulong table)
    {
        var (words, resident) = (memory.WordReads, memory.ResidentReads);
        var snapshot = Fast(plan, Inputs(memory, table, spans: false));
        return (snapshot, memory.WordReads - words, memory.ResidentReads - resident);
    }

    private static void AssertSame(ResourceSnapshot expected, ResourceSnapshot actual)
    {
        Assert.Equal(expected.Buffers.Select(words => words.ToArray()), actual.Buffers.Select(words => words.ToArray()));
        Assert.Equal(expected.Images.Select(words => words.ToArray()), actual.Images.Select(words => words.ToArray()));
        Assert.Equal(expected.FlattenedResourceTable, actual.FlattenedResourceTable);
        Assert.Equal(expected.DeviceAddressRanges, actual.DeviceAddressRanges);
    }

    private static long Raced() =>
        long.Parse(Regex.Match(PageSpanReads.TakeReport(), @" raced=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    [Fact]
    public void GappyWordsOfOnePageAreReadAsOneSpan()
    {
        using var mode = new Mode(1);
        var plan = GappyPlan();
        Assert.True(ResourceMaterializer.CanMaterializeFast(plan));
        var memory = GappyMemory();
        var (expected, referenceWords, referenceResident) = Reference(plan, memory, Table);
        Assert.Equal(new uint[] { 0x9000, 0x10, 256, 0x2C004000 }, expected.Buffers[2]);
        Assert.True(referenceWords >= 1);
        Assert.True(referenceResident >= 3);

        memory.WordReads = memory.ResidentReads = memory.SpanReads = 0;
        var snapshot = Fast(plan, Inputs(memory, Table, spans: true));
        AssertSame(expected, snapshot);
        Assert.Equal(1, memory.SpanReads);
        Assert.Equal(0, memory.WordReads);
        Assert.Equal(1, memory.ResidentReads); // level 2's V#: a single unit, no span
    }

    [Fact]
    public void WithoutTheSwitchNoSpanIsRead()
    {
        using var mode = new Mode(0);
        var plan = GappyPlan();
        var memory = GappyMemory();
        var (expected, _, _) = Reference(plan, memory, Table);
        AssertSame(expected, Fast(plan, Inputs(memory, Table, spans: true)));
        Assert.Equal(0, memory.SpanReads);
    }

    [Fact]
    public void CleanReadsReadNoSpan()
    {
        using var mode = new Mode(1);
        var plan = GappyPlan();
        var memory = GappyMemory();
        Fast(plan, Inputs(memory, Table, spans: true, clean: true));
        Assert.Equal(0, memory.SpanReads);
    }

    // The V# at +0x20 and the offset at +0x44 sit at the end of one page, the V# at +0x60 in the next: the first two form
    // a span, the third is read on its own, never as part of a span over the page boundary.
    [Fact]
    public void ASpanStopsAtThePageBoundary()
    {
        using var mode = new Mode(1);
        const ulong table = 0x1FA0;
        var plan = GappyPlan();
        var memory = GappyMemory(table);
        var (expected, _, _) = Reference(plan, memory, table);
        memory.SpanReads = 0;
        AssertSame(expected, Fast(plan, Inputs(memory, table, spans: true)));
        Assert.Equal(1, memory.SpanReads);
    }

    [Fact]
    public void AWordTheGpuOwnsInAGapRefusesTheSpanAndTheUnitsReadAsBefore()
    {
        using var mode = new Mode(1);
        var plan = GappyPlan();
        var memory = GappyMemory();
        memory.GpuOwned[Table + 0x50] = 0xDEAD; // not read by the plan, inside the span
        var (expected, referenceWords, referenceResident) = Reference(plan, memory, Table);
        memory.WordReads = memory.ResidentReads = memory.SpanReads = 0;
        AssertSame(expected, Fast(plan, Inputs(memory, Table, spans: true)));
        Assert.Equal(1, memory.SpanReads);
        Assert.Equal(referenceWords, memory.WordReads);
        Assert.Equal(referenceResident, memory.ResidentReads);
    }

    // The word reader answers a GPU-owned word with its synchronized value, which the guest bytes do not hold yet: the
    // span must not answer it from the bytes.
    [Fact]
    public void AReadWordTheGpuOwnsTakesItsSynchronizedValue()
    {
        using var mode = new Mode(1);
        var plan = GappyPlan();
        var memory = GappyMemory();
        memory.GpuOwned[Table + 0x44] = 0x60; // the offset word: the GPU moved the third V# to +0x60
        var (expected, _, _) = Reference(plan, memory, Table);
        Assert.Equal(new uint[] { 0x8000, 0x10, 256, 0x2C004000 }, expected.Buffers[2]);
        AssertSame(expected, Fast(plan, Inputs(memory, Table, spans: true)));
    }

    // A null table pointer: the words read at its offsets keep the null-base rule of the word reader, never a span.
    [Fact]
    public void NullBaseWordsAreNeverPartOfASpan()
    {
        using var mode = new Mode(1);
        var plan = GappyPlan();
        var memory = GappyMemory();
        AssertSameOutcome(plan, memory, 0);
        Assert.Equal(0, memory.SpanReads);
    }

    [Fact]
    public void AnUnmappedSpanIsRefusedAndTheWordsFailAsBefore()
    {
        using var mode = new Mode(1);
        var plan = GappyPlan();
        var memory = GappyMemory();
        memory.MappedEnd = Table + 0x50; // the V# at +0x60 is unmapped
        AssertSameOutcome(plan, memory, Table);
        Assert.True(memory.SpanReads >= 1); // the program's run, and again in the fallback walk's program run
    }

    // Success or failure, the same as without the switch.
    private static void AssertSameOutcome(ShaderResourcePlan plan, Memory memory, ulong table)
    {
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        var referenceOk = ResourceMaterializer.MaterializeFast(plan, Inputs(memory, table, spans: false), ref reference, ref referenceSpecialization, out var referenceFailure);
        memory.SpanReads = 0;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var ok = ResourceMaterializer.MaterializeFast(plan, Inputs(memory, table, spans: true), ref snapshot, ref specialization, out var failure);
        Assert.Equal(referenceOk, ok);
        Assert.Equal(referenceFailure, failure);
        if (ok)
            AssertSame(reference, snapshot);
    }

    [Fact]
    public void AThreadThatReadsNoSpansStopsAsking()
    {
        var plan = GappyPlan();
        var memory = GappyMemory();
        var (expected, _, _) = Reference(plan, memory, Table);
        memory.SpanReads = 0;
        memory.SpanOverride = (_, _) => ResidentSpanRead.OtherThread;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                PageSpanReads.Mode = 1;
                AssertSame(expected, Fast(plan, Inputs(memory, Table, spans: true)));
                AssertSame(expected, Fast(plan, Inputs(memory, Table, spans: true)));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal(1, memory.SpanReads);
    }

    [Fact]
    public void VerifyKeepsTheRealAnswerAndCountsAWrongSpan()
    {
        using var mode = new Mode(2);
        var plan = GappyPlan();
        var memory = GappyMemory();
        var (expected, _, _) = Reference(plan, memory, Table);
        memory.SpanCorruption = 0x1234;
        var wrongBefore = PageSpanReads.WrongTotal;
        AssertSame(expected, Fast(plan, Inputs(memory, Table, spans: true)));
        Assert.Equal(wrongBefore + 1, PageSpanReads.WrongTotal);
    }

    // A guest write between the real reads and the span: the word read again agrees with the span, so it is raced.
    [Fact]
    public void VerifyCountsAWordChangedBetweenTheReadsAsRaced()
    {
        using var mode = new Mode(2);
        var plan = GappyPlan();
        var memory = GappyMemory();
        var (expected, _, _) = Reference(plan, memory, Table);
        memory.BeforeSpan = () => memory.Words[Table + 0x28] = 512;
        var wrongBefore = PageSpanReads.WrongTotal;
        Raced();
        AssertSame(expected, Fast(plan, Inputs(memory, Table, spans: true)));
        Assert.Equal(wrongBefore, PageSpanReads.WrongTotal);
        Assert.True(Raced() >= 1);
    }

    [Fact]
    public void VerifyOfACorrectSpanCountsNothingWrong()
    {
        using var mode = new Mode(2);
        var plan = GappyPlan();
        var memory = GappyMemory();
        var (expected, referenceWords, referenceResident) = Reference(plan, memory, Table);
        var wrongBefore = PageSpanReads.WrongTotal;
        memory.WordReads = memory.ResidentReads = memory.SpanReads = 0;
        AssertSame(expected, Fast(plan, Inputs(memory, Table, spans: true)));
        Assert.Equal(wrongBefore, PageSpanReads.WrongTotal);
        Assert.Equal(1, memory.SpanReads);
        Assert.Equal(referenceWords, memory.WordReads);
        Assert.Equal(referenceResident, memory.ResidentReads);
    }

    // The probe maps ShaderCompiler's laps and counts by position: the names must line up.
    [Fact]
    public void TheProbeStepsAndCountersLineUpWithShaderCompilers()
    {
        foreach (var step in Enum.GetValues<MaterializeProbe.Step>())
            Assert.Equal("Materialize" + step, ((DrawCostProbe.Step)((int)DrawCostProbe.Step.MaterializeSetup + (int)step)).ToString());
        foreach (var counter in Enum.GetValues<MaterializeProbe.Counter>())
            Assert.Equal("Mat" + counter, ((DrawCostProbe.Counter)((int)DrawCostProbe.Counter.MatRuns + (int)counter)).ToString());
    }
}
