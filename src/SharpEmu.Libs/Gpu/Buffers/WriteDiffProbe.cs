// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_WRITE_DIFF=<hash>/<hash>...|all: what a dispatch really writes inside the ranges the host marks written
// for it. For one dispatch in SHARPEMU_WRITE_DIFF_EVERY (16) of each listed program, every range its bindings obtain
// written (storage buffers and device-address ranges, up to SHARPEMU_WRITE_DIFF_MAX bytes, 64 MiB) is copied from its
// device buffer just before the dispatch and again just after it, in stream order; at completion the two copies are
// compared and the changed bytes logged as runs relative to the range ([WRITE_DIFF]). A store of the value already there
// leaves no trace, and needs none: the memory reads the same either way. Diagnostic only; nothing changes what is
// recorded except the two copies.
// SHARPEMU_WRITE_DIFF_EXACT=1 also checks the exact ranges declared for the dispatch (SHARPEMU_EXACT_WRITTEN_RANGES):
// every changed byte outside them is counted ([WRITE_DIFF_EXACT] outside=...), and has to stay 0.
internal static class WriteDiffProbe
{
    private static readonly bool All = Environment.GetEnvironmentVariable("SHARPEMU_WRITE_DIFF") is "all";
    private static readonly HashSet<ulong> Hashes = ParseHashes(Environment.GetEnvironmentVariable("SHARPEMU_WRITE_DIFF"));
    public static readonly bool Enabled = All || Hashes.Count != 0;
    private static readonly int Every = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_WRITE_DIFF_EVERY"), out var every) && every > 0 ? every : 16;
    private static readonly ulong MaxBytes = ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_WRITE_DIFF_MAX"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var max) ? max : 64UL << 20;
    private const long InFlightLimit = 512L << 20;
    private const int RunGap = 16;
    private const int LoggedRuns = 48;

    internal static GuestBufferCache? Cache;

    private static readonly Dictionary<ulong, int> Seen = new();
    private static readonly ConcurrentDictionary<ulong, Summary> Summaries = new();
    private static ulong _armedHash;
    private static int _armedIndex;
    private static bool _armed;
    private static readonly List<Pending> _ranges = new();
    private static readonly List<(ulong Base, ulong Size)> _declared = new();
    private static bool _declaredExact;
    private static long _inFlight;
    // Words the host read synchronously from shader tables (address -> reader program), checked against every diff.
    private static readonly ConcurrentDictionary<ulong, ulong> Watched = new();
    private static readonly ConcurrentDictionary<(ulong Writer, ulong Reader, ulong Address), long> Hits = new();

    private sealed class Pending
    {
        public ulong Address, Size, Offset;
        public bool Conservative;
        public GpuBuffer Source = null!;
        public GpuBuffer? Download;
    }

    private sealed class Summary
    {
        public long Samples, Changed, ChangedBytes, Outside, Checked;
        public readonly HashSet<ulong> Pages = new();
    }

    private static HashSet<ulong> ParseHashes(string? value)
    {
        var set = new HashSet<ulong>();
        if (string.IsNullOrEmpty(value) || value is "all")
            return set;
        foreach (var part in value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(Convert.ToUInt64(part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part, 16));
        return set;
    }

    // A table word the host read synchronously for a program (any thread).
    public static void NoteRead(ulong reader, ulong address)
    {
        if (Watched.Count < 8192)
            Watched[address & ~3UL] = reader;
    }

    // Before the dispatch's bindings are obtained: whether this one is sampled.
    public static void Arm(ulong hash)
    {
        _armed = false;
        _ranges.Clear();
        _declared.Clear();
        _declaredExact = false;
        if (!All && !Hashes.Contains(hash))
            return;
        var index = Seen.GetValueOrDefault(hash);
        Seen[hash] = index + 1;
        if (index % Every != 0 || Interlocked.Read(ref _inFlight) > InFlightLimit)
            return;
        _armed = true;
        _armedHash = hash;
        _armedIndex = index;
    }

    // A range obtained written for the armed dispatch, in its device buffer.
    public static void NoteWritten(GpuBuffer buffer, ulong offset, ulong address, ulong size, bool conservative)
    {
        if (!_armed || size == 0 || size > MaxBytes)
            return;
        foreach (var range in _ranges)
        {
            if (range.Address == address && range.Size == size)
                return;
        }

        _ranges.Add(new Pending { Address = address, Size = size, Offset = offset, Source = buffer, Conservative = conservative });
    }

    // [local] SHARPEMU_EXACT_WRITTEN_RANGES: the exact ranges the dispatch declared, checked against what changed.
    public static void NoteDeclared(ulong address, ulong size, bool exact)
    {
        if (!_armed)
            return;
        _declaredExact |= exact;
        _declared.Add((address, size));
    }

    // Just before the dispatch is recorded.
    public static void BeforeDispatch()
    {
        if (!_armed || Cache is not { } cache)
            return;
        var (scheduler, device) = (cache.ProbeScheduler, cache.ProbeDevice);
        foreach (var range in _ranges)
        {
            range.Download = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, range.Size * 2);
            Interlocked.Add(ref _inFlight, (long)range.Size * 2);
            range.Download.CopyFrom(scheduler.Current, range.Source, range.Offset, 0, range.Size, destinationAfter: AccessFlags.HostReadBit);
        }
    }

    // Just after the dispatch is recorded.
    public static void AfterDispatch(uint groupsX, uint groupsY, uint groupsZ, ulong indirect, uint[] userData)
    {
        if (!_armed || Cache is not { } cache)
            return;
        var scheduler = cache.ProbeScheduler;
        _armed = false;
        if (_ranges.Count == 0)
            return;
        var ranges = _ranges.ToArray();
        _ranges.Clear();
        var declared = _declaredExact ? _declared.ToArray() : null;
        var hash = _armedHash;
        var index = _armedIndex;
        var tick = scheduler.CurrentTick;
        var user = string.Join(' ', userData.Take(8).Select(word => word.ToString("X8", CultureInfo.InvariantCulture)));
        var dims = indirect != 0 ? $"indirect=0x{indirect:X}" : $"groups={groupsX}x{groupsY}x{groupsZ}";
        foreach (var range in ranges)
            range.Download!.CopyFrom(scheduler.Current, range.Source, range.Offset, range.Size, range.Size, destinationAfter: AccessFlags.HostReadBit);
        scheduler.QueueCompletionAction(() =>
        {
            try
            {
                foreach (var range in ranges)
                    Compare(hash, index, tick, dims, user, range, declared);
            }
            finally
            {
                foreach (var range in ranges)
                {
                    Interlocked.Add(ref _inFlight, -(long)range.Size * 2);
                    range.Download!.Dispose();
                }
            }
        });
    }

    private static void Compare(ulong hash, int index, ulong tick, string dims, string user, Pending range, (ulong Base, ulong Size)[]? declared)
    {
        var download = range.Download!;
        download.Invalidate(0, range.Size * 2);
        var mapped = download.Mapped;
        var size = checked((int)range.Size);
        var before = mapped[..size];
        var after = mapped.Slice(size, size);
        var runs = new List<(int Offset, int Length)>();
        long changedBytes = 0;
        const int Chunk = 4096;
        for (var chunk = 0; chunk < size; chunk += Chunk)
        {
            var length = Math.Min(Chunk, size - chunk);
            var a = before.Slice(chunk, length);
            var b = after.Slice(chunk, length);
            if (a.SequenceEqual(b))
                continue;
            for (var offset = 0; offset < length; offset++)
            {
                if (a[offset] == b[offset])
                    continue;
                var at = chunk + offset;
                changedBytes++;
                if (runs.Count != 0 && at - (runs[^1].Offset + runs[^1].Length) <= RunGap)
                    runs[^1] = (runs[^1].Offset, at + 1 - runs[^1].Offset);
                else
                    runs.Add((at, 1));
            }
        }

        var summary = Summaries.GetOrAdd(hash, static _ => new Summary());
        long outside = 0;
        lock (summary)
        {
            summary.Samples++;
            if (changedBytes != 0)
                summary.Changed++;
            summary.ChangedBytes += changedBytes;
            foreach (var (offset, length) in runs)
            {
                for (var page = (range.Address + (ulong)offset) >> 12; page <= (range.Address + (ulong)(offset + length - 1)) >> 12; page++)
                    summary.Pages.Add(page);
            }

            if (declared is not null)
            {
                foreach (var (offset, length) in runs)
                {
                    for (var at = offset; at < offset + length; at++)
                    {
                        if (before[at] == after[at])
                            continue;
                        var address = range.Address + (ulong)at;
                        var inside = false;
                        foreach (var (b, s) in declared)
                        {
                            if (address >= b && address - b < s)
                            {
                                inside = true;
                                break;
                            }
                        }

                        if (!inside)
                            outside++;
                    }
                }

                summary.Checked++;
                summary.Outside += outside;
            }
        }

        var hits = 0;
        foreach (var (address, reader) in Watched)
        {
            if (address < range.Address || address - range.Address > range.Size - 4)
                continue;
            var at = (int)(address - range.Address);
            if (before.Slice(at, 4).SequenceEqual(after.Slice(at, 4)))
                continue;
            hits++;
            var count = Hits.AddOrUpdate((hash, reader, address), 1, static (_, value) => value + 1);
            if (count <= 4 || (count & (count - 1)) == 0)
                Console.Error.WriteLine($"[WRITE_DIFF_HIT] writer=0x{hash:X16} reader=0x{reader:X16} address=0x{address:X} n={index} tick={tick} count={count} before={Convert.ToHexString(before.Slice(at, 4))} after={Convert.ToHexString(after.Slice(at, 4))}");
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"[WRITE_DIFF] hash=0x{hash:X16} n={index} tick={tick} {dims} range=0x{range.Address:X}+0x{range.Size:X} conservative={range.Conservative} changed_bytes={changedBytes} runs={runs.Count} watched_hits={hits}");
        if (runs.Count != 0)
            text.Append(CultureInfo.InvariantCulture, $" span=0x{runs[0].Offset:X}..0x{runs[^1].Offset + runs[^1].Length:X}");
        text.Append(" ud=[").Append(user).Append("] top=[");
        foreach (var (offset, length) in runs.Take(LoggedRuns))
            text.Append(CultureInfo.InvariantCulture, $"{offset:X}+{length:X} ");
        text.Append(']');
        Console.Error.WriteLine(text.ToString());
        if (declared is not null)
            Console.Error.WriteLine($"[WRITE_DIFF_EXACT] hash=0x{hash:X16} n={index} range=0x{range.Address:X}+0x{range.Size:X} declared=[{string.Join(' ', declared.Select(d => $"{d.Base:X}+{d.Size:X}"))}] outside={outside} total_outside={summary.Outside} checked={summary.Checked}");
        if (summary.Samples % 16 == 0)
            Console.Error.WriteLine($"[WRITE_DIFF_SUMMARY] hash=0x{hash:X16} samples={summary.Samples} changed={summary.Changed} bytes_per_sample={summary.ChangedBytes / summary.Samples} pages_ever={summary.Pages.Count} checked={summary.Checked} outside={summary.Outside}");
    }
}
