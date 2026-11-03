// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] SHARPEMU_ARGS_WRITER_PROBE=1 (off by default): a soak diagnostic for GTA V's rare 19-27 s GPU stall, a
// thread-unit indirect dispatch of 0x8039D9800 (hash 7CCD8D47F15B82BD) with a count in ~[3e8, 2^31) (larger counts
// are past maxComputeWorkGroupCount and cost nothing). Its counts come from 0x8039D8A00 (hash F6F44A551620914E):
// for each of its threads i < count it stores (P[i+1] - P[i], 1, 1) at out + 32*i, and (total - P[last], 1, 1) for
// the last, where the struct at s[4:5] holds P's offset (+124, host-materialized), total (+140, read on the GPU
// through the device-address page table) and count (+152, host-materialized); P is a bound buffer. Such a count needs
// foreign bytes in P or +140 (positive float bits and pointer words fall in the window), so the probe compares what
// each layer saw: the CPU's view after the dispatch is recorded, the materialized table, the page-table entry and
// the bytes behind it, P through the very view the shader read, and the outputs the shader stored; and for every
// 0x8039D9800 dispatch, the 12 argument bytes it read against its producer's stored output. Only anomalies print
// (plus a summary every 64 writers, and the latest records in a [GPU][STALL] report). ~1 writer and ~3 consumers
// per frame in the BVH bursts, so the cost is a few small copies.
internal static class ArgsWriterProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_ARGS_WRITER_PROBE") == "1";

    internal const ulong WriterHash = 0xF6F44A551620914E;
    internal const ulong ConsumerHash = 0x7CCD8D47F15B82BD;

    // The hash of the program whose dispatch is being recorded (set by the render executor).
    [ThreadStatic] internal static ulong CurrentProgram;

    internal const int HeaderBase = 112;
    internal const int HeaderBytes = 64;
    internal const int PrefixOffsetField = 124 - HeaderBase;
    internal const int TotalField = 140 - HeaderBase;
    internal const int CountField = 152 - HeaderBase;
    internal const int MaxPrefixWords = 64;
    internal const int MaxOutputs = 16;
    internal const int OutputStride = 32;
    internal const uint BigCount = 1u << 24;
    internal const uint DroppedCount = 1u << 31;

    [Flags]
    internal enum Finding
    {
        None = 0,
        // A materialized table word differs from the CPU's +124/+152, or the packet's thread count from +152.
        StaleTable = 1 << 0,
        ThreadsMismatch = 1 << 1,
        // The bytes behind the page-table entry differ from the CPU's header, or the entry is not the registered buffer.
        StaleHeader = 1 << 2,
        PageTable = 1 << 3,
        // P through the bound view differs from the CPU's P.
        StalePrefix = 1 << 4,
        // The stored outputs are not what the GPU's own inputs give.
        Output = 1 << 5,
        // A stored count past BigCount: below DroppedCount it is the stall.
        Big = 1 << 6,
        Huge = 1 << 7,
        // The backing (what uploads and stream copies read) differs from the guest view of the header or P.
        BackingView = 1 << 8,
    }

    // What the CPU saw when the writer was recorded (guest view, and the backing when it could be read); ExpectedEntry
    // and HeaderCopied describe the copies recorded with it.
    internal sealed record WriterCpu(ulong Header, ulong Output, uint Threads, uint TablePrefixOffset, uint TableCount, byte[] HeaderBytes, uint[] Prefix,
        ulong ExpectedEntry = 0, bool HeaderCopied = false, byte[]? BackingHeader = null, uint[]? BackingPrefix = null);

    // What the GPU copied after the writer ran. PageTableEntry/ExpectedEntry are 0 when unknown; Header is null when the
    // registered buffer did not hold the whole window.
    internal sealed record WriterGpu(ulong PageTableEntry, ulong ExpectedEntry, byte[]? Header, uint[] Prefix, uint[] Outputs);

    internal static uint Word(ReadOnlySpan<byte> bytes, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    // The X count the writer stores for thread index, from its inputs.
    internal static uint ExpectedX(ReadOnlySpan<uint> prefix, uint count, uint total, int index) =>
        (uint)index + 1 < count ? prefix[index + 1] - prefix[index] : total - prefix[index];

    // How many outputs and prefix words the comparison covers.
    internal static int Outputs(uint count, int prefixWords) => (int)Math.Min(Math.Min(count, (uint)MaxOutputs), (uint)prefixWords);

    internal static Finding Analyze(WriterCpu cpu, WriterGpu gpu)
    {
        var findings = Finding.None;
        var cpuPrefixOffset = Word(cpu.HeaderBytes, PrefixOffsetField);
        var cpuCount = Word(cpu.HeaderBytes, CountField);
        if (cpu.TablePrefixOffset != cpuPrefixOffset || cpu.TableCount != cpuCount)
            findings |= Finding.StaleTable;
        if (cpu.Threads != cpu.TableCount)
            findings |= Finding.ThreadsMismatch;
        if ((cpu.BackingHeader is { } backingHeader && !backingHeader.AsSpan().SequenceEqual(cpu.HeaderBytes)) ||
            (cpu.BackingPrefix is { } backingPrefix && !backingPrefix.AsSpan().SequenceEqual(cpu.Prefix)))
            findings |= Finding.BackingView;
        if (gpu.ExpectedEntry != 0 && gpu.PageTableEntry != gpu.ExpectedEntry)
            findings |= Finding.PageTable;
        if (gpu.Header is { } header && !header.AsSpan().SequenceEqual(cpu.HeaderBytes))
            findings |= Finding.StaleHeader;
        var words = Math.Min(cpu.Prefix.Length, gpu.Prefix.Length);
        if (!gpu.Prefix.AsSpan(0, words).SequenceEqual(cpu.Prefix.AsSpan(0, words)))
            findings |= Finding.StalePrefix;

        // The shader's own inputs: the table count, P through its view, and +140 behind the page table.
        var count = cpu.TableCount;
        var total = gpu.Header is { } gpuHeader ? Word(gpuHeader, TotalField) : Word(cpu.HeaderBytes, TotalField);
        var outputs = Outputs(count, gpu.Prefix.Length);
        for (var index = 0; index < outputs && index * 3 + 2 < gpu.Outputs.Length; index++)
        {
            var x = gpu.Outputs[index * 3];
            if (x >= BigCount)
                findings |= x >= DroppedCount ? Finding.Huge : Finding.Big;
            // The last thread's total is only checked when the window behind the page table was copied.
            var checkable = (uint)index + 1 < count || gpu.Header is not null;
            if (checkable && (x != ExpectedX(gpu.Prefix, count, total, index) || gpu.Outputs[index * 3 + 1] != 1 || gpu.Outputs[index * 3 + 2] != 1))
                findings |= Finding.Output;
        }

        return findings;
    }

    internal static string Describe(ulong sequence, ulong tick, ulong submit, Finding findings, WriterCpu cpu, WriterGpu? gpu)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture,
            $"seq={sequence} tick={tick} submit={submit} findings={findings} header=0x{cpu.Header:X} out=0x{cpu.Output:X} threads={cpu.Threads} " +
            $"table=[{cpu.TablePrefixOffset},{cpu.TableCount}] cpu_hdr=[poff={Word(cpu.HeaderBytes, PrefixOffsetField)},total={Word(cpu.HeaderBytes, TotalField)},count={Word(cpu.HeaderBytes, CountField)}]");
        if (cpu.BackingPrefix is { } backingPrefix && !backingPrefix.AsSpan().SequenceEqual(cpu.Prefix))
            builder.Append(CultureInfo.InvariantCulture, $" backing_p=[{string.Join(",", backingPrefix.Take(16))}]");
        if (cpu.BackingHeader is { } backingHeader && !backingHeader.AsSpan().SequenceEqual(cpu.HeaderBytes))
            builder.Append(CultureInfo.InvariantCulture, $" backing_total={Word(backingHeader, TotalField)}");
        if (gpu is null)
        {
            builder.Append(" gpu=pending");
            return builder.ToString();
        }

        builder.Append(CultureInfo.InvariantCulture, $" pte=0x{gpu.PageTableEntry:X}/0x{gpu.ExpectedEntry:X}");
        if (gpu.Header is { } header)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $" gpu_hdr=[poff={Word(header, PrefixOffsetField)},total={Word(header, TotalField)},count={Word(header, CountField)}]");
            for (var offset = 0; offset < HeaderBytes; offset += 4)
            {
                if (Word(header, offset) != Word(cpu.HeaderBytes, offset))
                    builder.Append(CultureInfo.InvariantCulture, $" +{HeaderBase + offset}:cpu=0x{Word(cpu.HeaderBytes, offset):X},gpu=0x{Word(header, offset):X}");
            }
        }

        var shown = Math.Min(Math.Min(cpu.Prefix.Length, gpu.Prefix.Length), 16);
        builder.Append(" p=[");
        for (var index = 0; index < shown; index++)
            builder.Append(cpu.Prefix[index] == gpu.Prefix[index]
                ? string.Create(CultureInfo.InvariantCulture, $"{gpu.Prefix[index]},")
                : string.Create(CultureInfo.InvariantCulture, $"{cpu.Prefix[index]}/gpu=0x{gpu.Prefix[index]:X},"));
        builder.Append("] out=[");
        var outputs = Outputs(cpu.TableCount, gpu.Prefix.Length);
        for (var index = 0; index < outputs && index * 3 + 2 < gpu.Outputs.Length; index++)
            builder.Append(CultureInfo.InvariantCulture, $"{gpu.Outputs[index * 3]}:{gpu.Outputs[index * 3 + 1]}:{gpu.Outputs[index * 3 + 2]},");
        builder.Append(']');
        return builder.ToString();
    }
}

// [local] The bookkeeping of ArgsWriterProbe without Vulkan: pending writer and consumer records, the scan of the
// copies the GPU has written, and the lines to print. Single-threaded (render thread).
internal sealed class ArgsWriterLedger
{
    private const int MaxLines = 200;
    private const int SummaryEvery = 64;
    private const int RecentProducers = 64;

    internal readonly record struct Pending(ulong Sequence, int Slot, ulong Tick, ulong Submit, ArgsWriterProbe.WriterCpu Cpu);

    internal readonly record struct Consumer(ulong Sequence, int Slot, ulong Tick, ulong Submit, ulong Address, ulong WriterSequence);

    internal sealed record Done(ulong Sequence, ulong Tick, ulong Submit, ArgsWriterProbe.WriterCpu Cpu, ArgsWriterProbe.WriterGpu Gpu, ArgsWriterProbe.Finding Findings);

    private readonly Queue<Pending> _writers = new();
    private readonly Queue<Consumer> _consumers = new();
    private readonly LinkedList<Done> _recent = new();
    private readonly Dictionary<ArgsWriterProbe.Finding, long> _findingCounts = new();
    private ulong _nextWriter = 1, _nextConsumer = 1;
    private long _writersDone, _consumersDone, _consumerMismatches, _orphans, _beyondCopy, _sinceSummary;
    private uint _maxX;
    private int _lines;

    internal int PendingWriters => _writers.Count;
    internal int PendingConsumers => _consumers.Count;
    internal ulong LastWriter => _nextWriter - 1;
    internal long OrphanCount => _orphans;
    internal long ConsumerMismatchCount => _consumerMismatches;

    // 0 (nothing reserved) while half the ring is still pending, so a slot is never reused before it is read.
    internal ulong ReserveWriter(int slots, ulong tick, ulong submit, ArgsWriterProbe.WriterCpu cpu, out int slot)
    {
        slot = -1;
        if (_writers.Count >= slots / 2)
            return 0;
        var sequence = _nextWriter++;
        slot = (int)(sequence % (ulong)slots);
        _writers.Enqueue(new Pending(sequence, slot, tick, submit, cpu));
        return sequence;
    }

    internal ulong ReserveConsumer(int slots, ulong tick, ulong submit, ulong address, out int slot)
    {
        slot = -1;
        if (_consumers.Count >= slots / 2)
            return 0;
        var sequence = _nextConsumer++;
        slot = (int)(sequence % (ulong)slots);
        _consumers.Enqueue(new Consumer(sequence, slot, tick, submit, address, _nextWriter - 1));
        return sequence;
    }

    // Reads a writer slot: null while the GPU has not written it.
    internal delegate ArgsWriterProbe.WriterGpu? WriterReader(Pending pending);

    // Reads a consumer slot: null while the GPU has not written it.
    internal delegate (uint X, uint Y, uint Z)? ConsumerReader(Consumer consumer);

    internal List<string> Drain(WriterReader readWriter, ConsumerReader readConsumer)
    {
        var lines = new List<string>();
        while (_writers.Count > 0)
        {
            var pending = _writers.Peek();
            if (readWriter(pending) is not { } gpu)
                break;
            _writers.Dequeue();
            var findings = ArgsWriterProbe.Analyze(pending.Cpu, gpu);
            _writersDone++;
            _sinceSummary++;
            for (var index = 0; index + 2 < gpu.Outputs.Length && index < ArgsWriterProbe.Outputs(pending.Cpu.TableCount, gpu.Prefix.Length); index++)
                _maxX = Math.Max(_maxX, gpu.Outputs[index * 3]);
            foreach (var flag in Enum.GetValues<ArgsWriterProbe.Finding>())
            {
                if (flag != ArgsWriterProbe.Finding.None && findings.HasFlag(flag))
                    _findingCounts[flag] = _findingCounts.GetValueOrDefault(flag) + 1;
            }

            var done = new Done(pending.Sequence, pending.Tick, pending.Submit, pending.Cpu, gpu, findings);
            _recent.AddLast(done);
            if (_recent.Count > RecentProducers)
                _recent.RemoveFirst();
            if (findings != ArgsWriterProbe.Finding.None && _lines++ < MaxLines)
                lines.Add($"[GPU][ARGS_WRITER] {ArgsWriterProbe.Describe(pending.Sequence, pending.Tick, pending.Submit, findings, pending.Cpu, gpu)}");
        }

        while (_consumers.Count > 0)
        {
            var consumer = _consumers.Peek();
            // A consumer recorded after a writer still pending waits for it.
            if (_writers.Count > 0 && _writers.Peek().Sequence <= consumer.WriterSequence)
                break;
            if (readConsumer(consumer) is not { } args)
                break;
            _consumers.Dequeue();
            _consumersDone++;
            NoteConsumer(consumer, args, lines);
        }

        if (_sinceSummary >= SummaryEvery)
        {
            _sinceSummary = 0;
            lines.Add(Summary());
        }

        return lines;
    }

    private void NoteConsumer(Consumer consumer, (uint X, uint Y, uint Z) args, List<string> lines)
    {
        Done? producer = null;
        for (var node = _recent.Last; node is not null; node = node.Previous)
        {
            var cpu = node.Value.Cpu;
            if (node.Value.Sequence <= consumer.WriterSequence && consumer.Address >= cpu.Output &&
                consumer.Address < cpu.Output + (ulong)cpu.TableCount * ArgsWriterProbe.OutputStride)
            {
                producer = node.Value;
                break;
            }
        }

        var big = args.X >= ArgsWriterProbe.BigCount;
        if (producer is null)
        {
            _orphans++;
            if ((big || _orphans <= 4) && _lines++ < MaxLines)
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"[GPU][ARGS_WRITER] consumer_orphan seq={consumer.Sequence} tick={consumer.Tick} submit={consumer.Submit} address=0x{consumer.Address:X} args={args.X},{args.Y},{args.Z}"));
            return;
        }

        var index = (int)((consumer.Address - producer.Cpu.Output) / ArgsWriterProbe.OutputStride);
        var aligned = (consumer.Address - producer.Cpu.Output) % ArgsWriterProbe.OutputStride == 0;
        if (!aligned || index * 3 + 2 >= producer.Gpu.Outputs.Length)
        {
            _beyondCopy++;
            if (big && _lines++ < MaxLines)
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"[GPU][ARGS_WRITER] consumer_big_uncopied seq={consumer.Sequence} tick={consumer.Tick} address=0x{consumer.Address:X} args={args.X},{args.Y},{args.Z} producer={producer.Sequence} index={index}"));
            return;
        }

        var stored = (producer.Gpu.Outputs[index * 3], producer.Gpu.Outputs[index * 3 + 1], producer.Gpu.Outputs[index * 3 + 2]);
        if (stored != args || big)
        {
            if (stored != args)
                _consumerMismatches++;
            if (_lines++ < MaxLines)
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"[GPU][ARGS_WRITER] consumer{(stored != args ? "_mismatch" : "_big")} seq={consumer.Sequence} tick={consumer.Tick} submit={consumer.Submit} " +
                    $"address=0x{consumer.Address:X} args={args.X},{args.Y},{args.Z} stored={stored.Item1},{stored.Item2},{stored.Item3} " +
                    $"producer={producer.Sequence} producer_tick={producer.Tick} producer_submit={producer.Submit} index={index} producer_findings={producer.Findings}"));
        }
    }

    internal string Summary()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture,
            $"[GPU][ARGS_WRITER] summary writers={_writersDone} consumers={_consumersDone} max_x={_maxX} consumer_mismatches={_consumerMismatches} " +
            $"orphans={_orphans} beyond_copy={_beyondCopy} pending={_writers.Count}/{_consumers.Count}");
        foreach (var (flag, count) in _findingCounts.OrderBy(pair => pair.Key))
            builder.Append(CultureInfo.InvariantCulture, $" {flag}={count}");
        return builder.ToString();
    }

    // The latest writers, done or pending, for a stall report.
    internal List<string> DescribeLatest(int count)
    {
        var lines = new List<string>();
        foreach (var done in _recent.Skip(Math.Max(0, _recent.Count - count)))
            lines.Add($"args_writer {ArgsWriterProbe.Describe(done.Sequence, done.Tick, done.Submit, done.Findings, done.Cpu, done.Gpu)}");
        foreach (var pending in _writers.Take(count))
            lines.Add($"args_writer {ArgsWriterProbe.Describe(pending.Sequence, pending.Tick, pending.Submit, ArgsWriterProbe.Finding.None, pending.Cpu, null)}");
        lines.Add(Summary());
        return lines;
    }
}
