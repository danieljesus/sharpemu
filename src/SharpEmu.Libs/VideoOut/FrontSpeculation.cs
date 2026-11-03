// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_FRONT_SPECULATE (off by default; 1 on, 2 verify): the front prepares the dispatches whose programs read
// data the GPU has written. Without it, such a read (NeedsBackRead) drops the front's preparation and the render thread
// prepares the dispatch itself when it executes it, in stream order and through the synchronized readers; GTA V pans
// with ~150 of those a frame (the BVH chain's indirect dispatches, which the front never tried, and the programs the
// front skips because their last preparations all deferred). With the switch, the front reads those bytes as they are
// in guest memory now (what the last synchronized read or CPU write left there), goes on preparing, and records every
// such read in order: the reader (word, clean word, resident bytes), its address and size, the bytes and whether it
// succeeded, plus the program and table base the read ran under. When the render thread executes the dispatch it
// replays each recorded read through the render thread's own reader, at that point of the stream, before anything
// else of the dispatch: the same synchronizing read a render-thread preparation would make there. The preparation is
// used only if every replayed read returns the same success and the same bytes; the preparation is a pure function of
// the dispatch's registers and the bytes it read, and the bytes it read directly stay covered by
// SHARPEMU_FRONT_PREP_CHECK/RANGES and the store-table check, so a kept preparation is the one the render thread would
// have made. On the first difference the render thread prepares as before (the replayed reads already synchronized).
// A speculative preparation that reaches a fatal check (stale bytes can look like a broken header) is abandoned
// instead. Mismatches count as empty front preparations, so a program whose bytes change every time stops being
// speculated (SHARPEMU_FRONT_SKIP_DEFERRING). =2 also prepares every kept dispatch again on the render thread, compares
// the two (FrontPrepVerify fingerprints) and executes the render thread's result; [PERF][FRONT_SPEC] counts it all.
internal static class FrontSpeculation
{
    private static readonly int DefaultMode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SPECULATE"), out var mode) && mode is 1 or 2 ? mode : 0;
    private static readonly AsyncLocal<int?> ModeOverride = new();

    // Tests set it per async flow.
    public static int Mode
    {
        get => ModeOverride.Value ?? DefaultMode;
        set => ModeOverride.Value = value;
    }

    public static bool Enabled => Mode != 0;
    public static bool Verify => Mode == 2;

    internal enum Kind : byte
    {
        Word,
        CleanWord,
        Resident,
        ResidentClean,
    }

    internal readonly record struct Read(Kind Kind, ulong Address, int Offset, int Length, bool Ok, ulong Reader, ulong BaseAddress);

    // The speculated reads of one preparation, in the order made.
    internal sealed class Reads
    {
        public readonly List<Read> Items = new(16);
        public byte[] Bytes = new byte[256];
        public int Used;
        public ulong Program;

        public void Clear()
        {
            Items.Clear();
            Used = 0;
            Program = 0;
        }

        public void Add(Kind kind, ulong address, ReadOnlySpan<byte> bytes, bool ok, ulong reader, ulong baseAddress)
        {
            if (Used + bytes.Length > Bytes.Length)
                Array.Resize(ref Bytes, Math.Max(Bytes.Length * 2, Used + bytes.Length));
            bytes.CopyTo(Bytes.AsSpan(Used));
            Items.Add(new Read(kind, address, Used, bytes.Length, ok, reader, baseAddress));
            Used += bytes.Length;
        }

        public ReadOnlySpan<byte> BytesOf(in Read read) => Bytes.AsSpan(read.Offset, read.Length);
    }

    // The render thread's readers, with the context the read ran under.
    internal interface IReplay
    {
        bool ReadWord(ulong address, out uint word);
        bool ReadCleanWord(ulong address, out uint word);
        bool ReadResident(ulong address, Span<byte> destination, bool clean);
        (ulong Reader, ulong BaseAddress) Enter(ulong reader, ulong baseAddress);
        void Leave((ulong Reader, ulong BaseAddress) previous);
    }

    internal enum Outcome : byte
    {
        Same,
        Value,
        Success,
    }

    // Replays the reads in order and stops at the first one whose answer differs.
    internal static Outcome Validate(Reads reads, IReplay replay)
    {
        Span<byte> small = stackalloc byte[64];
        foreach (var read in reads.Items)
        {
            var previous = replay.Enter(read.Reader, read.BaseAddress);
            try
            {
                bool ok;
                scoped ReadOnlySpan<byte> answer;
                switch (read.Kind)
                {
                    case Kind.Word:
                    case Kind.CleanWord:
                    {
                        uint word;
                        ok = read.Kind == Kind.Word ? replay.ReadWord(read.Address, out word) : replay.ReadCleanWord(read.Address, out word);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(small, ok ? word : 0);
                        answer = small[..sizeof(uint)];
                        break;
                    }
                    default:
                    {
                        var destination = read.Length <= small.Length ? small[..read.Length] : (Span<byte>)new byte[read.Length];
                        destination.Clear();
                        ok = replay.ReadResident(read.Address, destination, read.Kind == Kind.ResidentClean);
                        answer = destination;
                        break;
                    }
                }

                if (ok != read.Ok)
                    return Outcome.Success;
                if (ok && !answer.SequenceEqual(reads.BytesOf(read)))
                    return Outcome.Value;
            }
            finally
            {
                replay.Leave(previous);
            }
        }

        return Outcome.Same;
    }

    [ThreadStatic] private static Reads? _current;
    [ThreadStatic] private static Reads? _last;
    private static readonly ConcurrentQueue<Reads> _pool = new();

    // The preparation in progress on this thread may speculate.
    public static bool Active => _current is not null;

    // A fatal check reached while preparing on stale bytes abandons the preparation instead of the process.
    public static bool AbandonsFatal => _current is { Items.Count: > 0 };

    public static void Begin()
    {
        if (!_pool.TryDequeue(out var reads))
            reads = new Reads();
        reads.Clear();
        _current = reads;
    }

    // Ends the preparation; its reads, or null when it speculated none, wait for Take.
    public static void End()
    {
        var reads = _current;
        _current = null;
        Release(_last);
        _last = null;
        if (reads is null)
            return;
        if (reads.Items.Count == 0)
        {
            Release(reads);
            return;
        }

        _last = reads;
    }

    // A finished preparation left reads for Take.
    public static bool Pending => _last is not null;

    public static Reads? Take()
    {
        var reads = _last;
        _last = null;
        return reads;
    }

    public static void Release(Reads? reads)
    {
        if (reads is not null && _pool.Count < 4096)
            _pool.Enqueue(reads);
    }

    public static void Note(Kind kind, ulong address, ReadOnlySpan<byte> bytes, bool ok, ulong reader, ulong baseAddress)
    {
        _current?.Add(kind, address, bytes, ok, reader, baseAddress);
        Interlocked.Increment(ref SpeculatedReads);
    }

    public sealed class AbandonException(string message) : Exception(message);

    internal static long SpeculatedPreps, SpeculatedReads, Kept, ValueMismatches, SuccessMismatches, Abandoned, Invalidated, VerifyChecked, VerifyMismatched, VerifyRenderEmpty;
    internal static long ValidateTicks;

    public static string TakeReport()
    {
        if (!Enabled)
            return string.Empty;
        var ticks = Interlocked.Exchange(ref ValidateTicks, 0);
        return FormattableString.Invariant(
            $"\n[PERF][FRONT_SPEC] preps={Interlocked.Exchange(ref SpeculatedPreps, 0)} reads={Interlocked.Exchange(ref SpeculatedReads, 0)} kept={Interlocked.Exchange(ref Kept, 0)} value_mismatch={Interlocked.Exchange(ref ValueMismatches, 0)} success_mismatch={Interlocked.Exchange(ref SuccessMismatches, 0)} abandoned={Interlocked.Exchange(ref Abandoned, 0)} invalidated={Interlocked.Exchange(ref Invalidated, 0)} validate_ms={ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} verify_checked={Interlocked.Read(ref VerifyChecked)} verify_mismatched={Interlocked.Read(ref VerifyMismatched)} verify_render_empty={Interlocked.Read(ref VerifyRenderEmpty)}");
    }
}
