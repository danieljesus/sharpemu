// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_LABEL_POLL_SPLIT=1: where each guest CPU-fault download (ReadbackSource.StoreDownload) of the
// SHARPEMU_LABEL_PROBE label (of every one without the probe) spends its time, every 5 s as [LABEL_POLL_SPLIT]:
// relay (until the GPU queue thread runs the begin), begin (collect, flush, submit), writer (until the main timeline
// reaches the copy's wait tick), queue (until the copies submitted before it on the readback queue are done), exec
// (the copy itself) and complete (the second relay that publishes the bytes). The readback wait is observed by
// polling both timelines. Also where the writer ticks stand: of the pieces over the 8 bytes, over their 4 KB page
// and over the whole window, as lag behind the current tick (0: the batch being recorded, flushed by the poll), and
// how many published pieces changed the backing bytes.
internal static class LabelPollSplit
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_LABEL_POLL_SPLIT") == "1";

    public static bool Measures(ulong address, ulong size) =>
        Enabled && (!LabelLatencyProbe.Enabled || LabelLatencyProbe.Overlaps(address, size));

    internal sealed class Sample
    {
        public long Started, Entered, WaitStarted, WriterAt, AheadAt, DoneAt, Completed;
        public ulong Current, CompletedTick, WaitTick, BytesTick, PageTick, AheadTick, AheadCount;
        public int Pieces, PagePieces, BytesPieces, SamePieces, ChangedPieces;
        public bool Flushed, Eager, Pending, Second, Applied;
    }

    private static readonly object Gate = new();
    private static readonly List<Sample> Samples = new();
    private static long _reportAt;

    public static void Add(Sample sample)
    {
        lock (Gate)
        {
            Samples.Add(sample);
            var now = Stopwatch.GetTimestamp();
            if (_reportAt == 0) { _reportAt = now + Stopwatch.Frequency * 5; return; }
            if (now < _reportAt) return;
            _reportAt = now + Stopwatch.Frequency * 5;
            Report();
            Samples.Clear();
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Stat(List<double> values)
    {
        if (values.Count == 0) return "-";
        values.Sort();
        return FormattableString.Invariant($"{values.Average():F2}/{values[values.Count / 2]:F2}/{values[(int)(values.Count * 0.9)]:F2}");
    }

    private static void Report()
    {
        var pending = Samples.Where(sample => sample.Pending && !sample.Eager).ToList();
        var total = Samples.Select(sample => Ms(sample.Completed - sample.Started)).ToList();
        var relay = pending.Select(sample => Ms(sample.Entered - sample.Started)).ToList();
        var begin = pending.Select(sample => Ms(sample.WaitStarted - sample.Entered)).ToList();
        var writer = new List<double>();
        var queue = new List<double>();
        var exec = new List<double>();
        var complete = new List<double>();
        foreach (var sample in pending)
        {
            var writerEnd = Math.Max(sample.WaitStarted, sample.WriterAt);
            var aheadEnd = Math.Max(writerEnd, sample.AheadAt);
            writer.Add(Ms(writerEnd - sample.WaitStarted));
            queue.Add(Ms(aheadEnd - writerEnd));
            exec.Add(Ms(sample.DoneAt - aheadEnd));
            complete.Add(Ms(sample.Completed - sample.DoneAt));
        }

        string Lag(Func<Sample, ulong> tick, Func<Sample, bool> has)
        {
            var with = pending.Where(has).ToList();
            if (with.Count == 0) return "-";
            var current = with.Count(sample => tick(sample) >= sample.Current);
            var inFlight = with.Count(sample => tick(sample) < sample.Current && tick(sample) > sample.CompletedTick);
            return FormattableString.Invariant($"n={with.Count},cur={current},flight={inFlight},done={with.Count - current - inFlight},lag={with.Average(sample => (double)(sample.Current - Math.Min(sample.Current, tick(sample)))):F1}");
        }

        Console.Error.WriteLine(FormattableString.Invariant(
            $"[LABEL_POLL_SPLIT] downloads={Samples.Count} pending={pending.Count} eager={Samples.Count(sample => sample.Eager)} immediate={Samples.Count(sample => !sample.Pending)} second_queue={pending.Count(sample => sample.Second)} flushed={pending.Count(sample => sample.Flushed)} not_applied={Samples.Count(sample => !sample.Applied)} ms_mean/p50/p90: total={Stat(total)} relay={Stat(relay)} begin={Stat(begin)} writer={Stat(writer)} queue={Stat(queue)} exec={Stat(exec)} complete={Stat(complete)}"));
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[LABEL_POLL_SPLIT] ticks: window={Lag(sample => sample.WaitTick, _ => true)} page4k={Lag(sample => sample.PageTick, sample => sample.PagePieces != 0)} bytes={Lag(sample => sample.BytesTick, sample => sample.BytesPieces != 0)} ahead={Lag(sample => sample.AheadTick, sample => sample.AheadCount != 0)} pieces_mean={(pending.Count == 0 ? 0 : pending.Average(sample => sample.Pieces)):F1} page_pieces_mean={(pending.Count == 0 ? 0 : pending.Average(sample => sample.PagePieces)):F1} bytes_pieces_mean={(pending.Count == 0 ? 0 : pending.Average(sample => sample.BytesPieces)):F1} ahead_mean={(pending.Count == 0 ? 0 : pending.Average(sample => (double)sample.AheadCount)):F1} pieces_same={pending.Sum(sample => sample.SamePieces)} pieces_changed={pending.Sum(sample => sample.ChangedPieces)} in_flight_at_begin={(pending.Count == 0 ? 0 : pending.Average(sample => (double)(sample.Current - Math.Min(sample.Current, sample.CompletedTick)))):F1}"));
        Console.Error.WriteLine("[LABEL_POLL_SPLIT] " + ChainReport());
    }

    // A guest read of the label faults until its 4 KB page is clean: the faults of one thread until the page is left
    // clean are one chain (one guest read), reported with the downloads.
    [ThreadStatic] private static long t_chainStarted;
    [ThreadStatic] private static int t_chainFaults;
    private static readonly List<(int Faults, double Ms)> Chains = new();

    public static void NoteFault(long started, bool pageStillDirty)
    {
        if (t_chainFaults == 0) t_chainStarted = started;
        t_chainFaults++;
        if (pageStillDirty && t_chainFaults < 1000) return;
        var ms = Ms(Stopwatch.GetTimestamp() - t_chainStarted);
        lock (Gate) Chains.Add((t_chainFaults, ms));
        t_chainFaults = 0;
    }

    private static string ChainReport()
    {
        if (Chains.Count == 0) return "chains=0";
        var faults = Chains.Select(chain => (double)chain.Faults).ToList();
        var ms = Chains.Select(chain => chain.Ms).ToList();
        var count = Chains.Count;
        var single = Chains.Count(chain => chain.Faults == 1);
        Chains.Clear();
        return FormattableString.Invariant($"chains={count} single={single} faults_mean/p50/p90={Stat(faults)} chain_ms_mean/p50/p90={Stat(ms)} long_chains_over_10ms={ms.Count(value => value > 10)}");
    }

    private static int _pieceLogs;

    public static bool LogPieces() => Interlocked.Increment(ref _pieceLogs) <= 12;
}
