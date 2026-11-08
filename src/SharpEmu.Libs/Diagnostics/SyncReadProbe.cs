// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_DRAWCOST_PROBE: the render thread's synchronizing table-word reads (the read_sync lap of the
// materialization), by program and word. One read is split into: flush = submitting the open batch (or the batch that
// holds the writer) so the readback has a signal to wait for (TryDownloadAsync, the ReadbackFlush phase); wait = the
// GPU completion waits inside it (the readback queue's and the timeline's, the GpuCompletionWait phase that
// RESOLVE_MATERIALIZE's wait column counts); other = the rest (memo miss, mirror, the copy into guest memory, the memo
// store and learning). writer = where the readback's writer tick stood at the flush: open (in the batch being
// recorded, so the read cut it), sent (submitted, not finished) or done; changed = the guest word differed after the
// read (the synchronization was needed for this word's value). One line per window with the probe's lines, the top
// TopKeys (program, word) by time:
//
//   [PERF][SYNC_READ] frames=N reads_per_frame=N ms_per_frame=N flush_ms=N wait_ms=N other_ms=N
//       top=<program>+<word>:n=<per frame>/us=<per read>/flush=<us>/wait=<us>/open=<%>/sent=<%>/done=<%>/changed=<%>,...
//
// Only on the probe's own thread and its timed items.
internal static class SyncReadProbe
{
    private const int TopKeys = 12;
    private const int MaxKeys = 4096;

    [ThreadStatic] private static bool _active;
    [ThreadStatic] private static long _flushTicks;
    [ThreadStatic] private static long _waitTicks;
    [ThreadStatic] private static int _writer;

    private sealed class Stat
    {
        public long Reads, Ticks, Flush, Wait, Open, Sent, Done, Changed;
    }

    private static readonly Dictionary<(ulong Program, ulong Word), Stat> _stats = new();
    private static long _reads, _ticks, _flush, _wait;

    internal readonly struct Scope
    {
        internal readonly long Started;
        internal readonly ulong Address;
        internal readonly uint Before;
        internal readonly bool HadBefore;

        internal Scope(long started, ulong address, uint before, bool hadBefore)
        {
            Started = started;
            Address = address;
            Before = before;
            HadBefore = hadBefore;
        }

        internal bool IsActive => Started != 0;
    }

    // A synchronizing read starts; before = the guest word as the CPU copy holds it.
    internal static Scope Begin(ulong address, bool hadBefore, uint before)
    {
        if (!DrawCostProbe.OnProbeThreadTiming)
            return default;
        _active = true;
        _flushTicks = _waitTicks = 0;
        _writer = -1;
        return new Scope(Stopwatch.GetTimestamp(), address, before, hadBefore);
    }

    internal static void End(in Scope scope, bool hasAfter, uint after)
    {
        if (!scope.IsActive)
            return;
        _active = false;
        var ticks = Stopwatch.GetTimestamp() - scope.Started;
        var key = (Gpu.Buffers.ConservativeRecords.CurrentReader, scope.Address);
        if (!_stats.TryGetValue(key, out var stat))
        {
            if (_stats.Count >= MaxKeys)
                key = (0, 0);
            if (!_stats.TryGetValue(key, out stat))
                _stats[key] = stat = new Stat();
        }

        stat.Reads++;
        stat.Ticks += ticks;
        stat.Flush += _flushTicks;
        stat.Wait += _waitTicks;
        switch (_writer)
        {
            case 0: stat.Open++; break;
            case 1: stat.Sent++; break;
            case 2: stat.Done++; break;
        }

        if (scope.HadBefore && hasAfter && after != scope.Before)
            stat.Changed++;
        _reads++;
        _ticks += ticks;
        _flush += _flushTicks;
        _wait += _waitTicks;
    }

    // The readback's flush of the batch holding the writer (0 open batch, 1 sent, 2 done).
    internal static void NoteFlush(long ticks, int writer)
    {
        if (!_active)
            return;
        _flushTicks += ticks;
        if (_writer < 0)
            _writer = writer;
    }

    internal static bool Active => _active;

    internal static void NoteWait(long ticks)
    {
        if (_active)
            _waitTicks += ticks;
    }

    internal static void Report(long frames)
    {
        if (_reads == 0)
            return;
        var perFrame = frames > 0 ? frames : 1;
        var toMs = 1000.0 / Stopwatch.Frequency;
        var line = new StringBuilder(1024);
        line.Append(FormattableString.Invariant(
            $"[PERF][SYNC_READ] frames={frames} reads_per_frame={_reads / (double)perFrame:F2} ms_per_frame={_ticks * toMs / perFrame:F2} "));
        line.Append(FormattableString.Invariant(
            $"flush_ms={_flush * toMs / perFrame:F2} wait_ms={_wait * toMs / perFrame:F2} other_ms={(_ticks - _flush - _wait) * toMs / perFrame:F2} top="));
        var first = true;
        foreach (var (key, stat) in _stats.OrderByDescending(pair => pair.Value.Ticks).Take(TopKeys))
        {
            if (!first)
                line.Append(',');
            first = false;
            var reads = Math.Max(stat.Reads, 1);
            line.Append(FormattableString.Invariant(
                $"{key.Program:X16}+{key.Word:X}:n={stat.Reads / (double)perFrame:F2}/us={stat.Ticks * toMs * 1000 / reads:F0}/flush={stat.Flush * toMs * 1000 / reads:F0}/wait={stat.Wait * toMs * 1000 / reads:F0}/"));
            line.Append(FormattableString.Invariant(
                $"open={100.0 * stat.Open / reads:F0}/sent={100.0 * stat.Sent / reads:F0}/done={100.0 * stat.Done / reads:F0}/changed={100.0 * stat.Changed / reads:F0}"));
        }

        Console.Error.WriteLine(line.ToString());
        _stats.Clear();
        _reads = _ticks = _flush = _wait = 0;
    }
}
