// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// [local] SHARPEMU_ARGS_WRITER_PROBE analysis and bookkeeping: pure functions and a ledger per test, no switches.
public sealed class ArgsWriterProbeTests
{
    private const ulong Header = 0x6630EB200;
    private const ulong Output = 0x60A360080;

    private static byte[] HeaderBytes(uint prefixOffset, uint total, uint count)
    {
        var bytes = new byte[ArgsWriterProbe.HeaderBytes];
        BitConverter.TryWriteBytes(bytes.AsSpan(ArgsWriterProbe.PrefixOffsetField), prefixOffset);
        BitConverter.TryWriteBytes(bytes.AsSpan(ArgsWriterProbe.TotalField), total);
        BitConverter.TryWriteBytes(bytes.AsSpan(ArgsWriterProbe.CountField), count);
        return bytes;
    }

    private static ArgsWriterProbe.WriterCpu Cpu(uint[] prefix, uint total, uint threads = 0) =>
        new(Header, Output, threads == 0 ? (uint)prefix.Length : threads, 0x30B0, (uint)prefix.Length,
            HeaderBytes(0x30B0, total, (uint)prefix.Length), prefix, 0x1000, true);

    // What the writer stores for the inputs (what a correct GPU copies back).
    private static uint[] Stored(uint[] prefix, uint total)
    {
        var outputs = new uint[prefix.Length * 3];
        for (var index = 0; index < prefix.Length; index++)
        {
            outputs[index * 3] = ArgsWriterProbe.ExpectedX(prefix, (uint)prefix.Length, total, index);
            outputs[index * 3 + 1] = 1;
            outputs[index * 3 + 2] = 1;
        }

        return outputs;
    }

    private static ArgsWriterProbe.WriterGpu Gpu(uint[] prefix, uint total, uint[]? outputs = null, ulong entry = 0x1000) =>
        new(entry, 0x1000, HeaderBytes(0x30B0, total, (uint)prefix.Length), prefix, outputs ?? Stored(prefix, total));

    [Fact]
    public void ExpectedCountsAreDifferencesAndTheLastTakesTheTotal()
    {
        uint[] prefix = [0, 54, 74, 76, 153];
        Assert.Equal([54u, 20, 2, 77, 2], Enumerable.Range(0, 5).Select(index => ArgsWriterProbe.ExpectedX(prefix, 5, 155, index)));
    }

    [Fact]
    public void AgreeingViewsHaveNoFindings()
    {
        uint[] prefix = [0, 154];
        Assert.Equal(ArgsWriterProbe.Finding.None, ArgsWriterProbe.Analyze(Cpu(prefix, 390), Gpu(prefix, 390)));
    }

    [Fact]
    public void ZeroedPrefixOnTheGpuIsStaleButNotBig()
    {
        // sl18: the GPU's P was zero where the CPU had the prefix; the writer then stores 0s and the total.
        uint[] cpuPrefix = [0, 282, 834, 1376, 1910, 2843];
        var gpuPrefix = new uint[cpuPrefix.Length];
        var findings = ArgsWriterProbe.Analyze(Cpu(cpuPrefix, 4037), Gpu(gpuPrefix, 4037));
        Assert.Equal(ArgsWriterProbe.Finding.StalePrefix, findings);
    }

    [Fact]
    public void ForeignFloatBitsInTheTotalGiveAStallSizedCount()
    {
        uint[] prefix = [0, 154];
        var foreignTotal = BitConverter.SingleToUInt32Bits(1.5f);
        var findings = ArgsWriterProbe.Analyze(Cpu(prefix, 390), Gpu(prefix, foreignTotal));
        Assert.True(findings.HasFlag(ArgsWriterProbe.Finding.StaleHeader));
        Assert.True(findings.HasFlag(ArgsWriterProbe.Finding.Big));
        Assert.False(findings.HasFlag(ArgsWriterProbe.Finding.Huge));
        Assert.False(findings.HasFlag(ArgsWriterProbe.Finding.Output));
    }

    [Fact]
    public void ANegativeDifferenceIsHugeNotBig()
    {
        uint[] prefix = [0, 500];
        var findings = ArgsWriterProbe.Analyze(Cpu(prefix, 400), Gpu(prefix, 400));
        Assert.Equal(ArgsWriterProbe.Finding.Huge, findings);
    }

    [Fact]
    public void OutputsUnlikeTheGpuInputsAreReported()
    {
        uint[] prefix = [0, 154];
        var outputs = Stored(prefix, 390);
        outputs[0] = 7;
        Assert.Equal(ArgsWriterProbe.Finding.Output, ArgsWriterProbe.Analyze(Cpu(prefix, 390), Gpu(prefix, 390, outputs)));
    }

    [Fact]
    public void TableThreadAndPageTableMismatchesAreReported()
    {
        uint[] prefix = [0, 154];
        var cpu = Cpu(prefix, 390, threads: 3) with { TableCount = 2, TablePrefixOffset = 0x40 };
        var findings = ArgsWriterProbe.Analyze(cpu, Gpu(prefix, 390, entry: 0x2000));
        Assert.Equal(ArgsWriterProbe.Finding.StaleTable | ArgsWriterProbe.Finding.ThreadsMismatch | ArgsWriterProbe.Finding.PageTable, findings);
    }

    [Fact]
    public void ABackingUnlikeTheGuestViewIsReported()
    {
        uint[] prefix = [0, 154];
        var cpu = Cpu(prefix, 390) with { BackingHeader = HeaderBytes(0x30B0, 390, 2), BackingPrefix = [0, 0] };
        Assert.Equal(ArgsWriterProbe.Finding.BackingView, ArgsWriterProbe.Analyze(cpu, Gpu(prefix, 390)));
        Assert.Equal(ArgsWriterProbe.Finding.None, ArgsWriterProbe.Analyze(cpu with { BackingPrefix = [0, 154] }, Gpu(prefix, 390)));
    }

    [Fact]
    public void LedgerReportsOnlyAnomaliesAndMatchesConsumersToTheirProducer()
    {
        var ledger = new ArgsWriterLedger();
        var gpu = new Dictionary<int, ArgsWriterProbe.WriterGpu>();
        var consumers = new Dictionary<int, (uint, uint, uint)>();
        uint[] good = [0, 154];
        var first = ledger.ReserveWriter(8, 10, 1, Cpu(good, 390), out var firstSlot);
        var consumerA = ledger.ReserveConsumer(8, 11, 1, Output, out var slotA);
        var consumerB = ledger.ReserveConsumer(8, 11, 1, Output + ArgsWriterProbe.OutputStride, out var slotB);
        var orphan = ledger.ReserveConsumer(8, 11, 1, 0x1234, out var slotC);
        Assert.Equal(1UL, first);
        Assert.True(consumerA != 0 && consumerB != 0 && orphan != 0);

        // Nothing has run yet: everything waits, consumers behind their writer.
        Assert.Empty(ledger.Drain(p => gpu.GetValueOrDefault(p.Slot), c => consumers.TryGetValue(c.Slot, out var v) ? v : null));
        consumers[slotA] = (154, 1, 1);
        Assert.Empty(ledger.Drain(p => gpu.GetValueOrDefault(p.Slot), c => consumers.TryGetValue(c.Slot, out var v) ? v : null));
        Assert.Equal(1, ledger.PendingWriters);

        gpu[firstSlot] = Gpu(good, 390);
        consumers[slotB] = (999, 1, 1);
        consumers[slotC] = (5, 1, 1);
        var lines = ledger.Drain(p => gpu.GetValueOrDefault(p.Slot), c => consumers.TryGetValue(c.Slot, out var v) ? v : null);
        Assert.Equal(0, ledger.PendingWriters);
        Assert.Equal(0, ledger.PendingConsumers);
        Assert.Equal(2, lines.Count);
        Assert.Contains("consumer_mismatch", lines[0]);
        Assert.Contains("stored=236,1,1", lines[0]);
        Assert.Contains("consumer_orphan", lines[1]);
        Assert.Equal(1, ledger.ConsumerMismatchCount);
        Assert.Equal(1, ledger.OrphanCount);
    }

    [Fact]
    public void LedgerStopsReservingWhileHalfTheRingIsPending()
    {
        var ledger = new ArgsWriterLedger();
        uint[] prefix = [0, 1];
        Assert.NotEqual(0UL, ledger.ReserveWriter(4, 1, 1, Cpu(prefix, 2), out _));
        Assert.NotEqual(0UL, ledger.ReserveWriter(4, 1, 1, Cpu(prefix, 2), out _));
        Assert.Equal(0UL, ledger.ReserveWriter(4, 1, 1, Cpu(prefix, 2), out var slot));
        Assert.Equal(-1, slot);
    }

    [Fact]
    public void AnomalousWriterPrintsBothViews()
    {
        var ledger = new ArgsWriterLedger();
        uint[] prefix = [0, 154];
        ledger.ReserveWriter(8, 10, 3, Cpu(prefix, 390), out var slot);
        var lines = ledger.Drain(_ => Gpu(prefix, BitConverter.SingleToUInt32Bits(1.5f)), _ => null);
        var line = Assert.Single(lines);
        Assert.Contains("[GPU][ARGS_WRITER] seq=1", line);
        Assert.Contains("StaleHeader", line);
        Assert.Contains("+140:cpu=0x186,gpu=0x3FC00000", line);
        Assert.Contains(ledger.DescribeLatest(4), l => l.StartsWith("args_writer seq=1", StringComparison.Ordinal));
    }
}
