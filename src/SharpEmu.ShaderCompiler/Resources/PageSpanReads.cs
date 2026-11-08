// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.ShaderCompiler.Resources;

// [local] What a span read did: read, refused (the caller reads the span's words as it did before), or refused because
// the calling thread does not read spans at all (the front, a preparation worker, the command thread, a resolve
// worker), which the caller remembers for the thread.
public enum ResidentSpanRead : byte
{
    Read,
    Refused,
    OtherThread,
}

// [local] SHARPEMU_PAGE_SPAN_READS: the bytes of [address, address + destination.Length), all in one 4 KB page, as the
// resident reader would copy them, or a refusal.
public delegate ResidentSpanRead ResidentGuestSpanReader(ulong address, Span<byte> destination);

// [local] SHARPEMU_PAGE_SPAN_READS (0 off by default, 1 on, 2 verify; SHARPEMU_PAGE_SPAN_READS_BYTES caps a span,
// default 1024, 8 to 4096): a level of the materialization program sorts its guest reads by address and reads each run
// of adjacent words with one resident read (ReadRun) and every other word with one word read (ReadWord), so the words
// of one table that are not adjacent (fields of a structure, a V# among unused ones) each pay the reader's ownership
// checks on the render thread (thread role, pending publications, the clean-read page, the alias lease). With the
// switch a sequence of two or more of those units (runs and singles) that lies in one 4 KB page within the cap, all
// with a usable base (the null-base words keep their own read and its validity rule), is read as one span through
// ResourceRuntimeInputs.ReadResidentSpan; the words are taken from it as ReadRun takes a run's.
//
// Exactness: the host reads a span only on the render thread, where the word reader and the resident reader read the
// same bytes for a word that no pending publication overlaps, that the GPU does not own (HasGpuDirtyBytes) and whose
// page is mapped: both copy guest memory through the clean-read page's alias or the guest memory, and both note the
// page clean. The span refuses when a publication overlaps any byte of it, when any byte is GPU-owned or unmapped, and
// whenever the word reader would take one of its side paths (TLAS HLE consumer notes, conservative records, watched
// bounds); a refused span is read unit by unit exactly as without the switch (the same units: a span is a whole
// number of the units the old loop forms). The gap bytes between the words only make a span refuse more often, never
// answer differently. Races: GPU-dirty marks are set by the recording thread itself; the publications a read must see
// were registered before its item was queued; what is left is a guest CPU write to a word during the materialization,
// which no fence orders before it, so either value is one the guest may observe (=2 counts it as raced).
//
// =2 reads every span's units the old way first (the answer used), then the span, and compares word by word: a word
// the span answered differently is read again through the word reader; equal to the span's answer = raced (the word
// changed between the two reads), otherwise wrong, as is a word the old path left invalid that the span read. The
// first DumpLimit are printed as [GPU][PAGE_SPAN_WRONG]. One line every 5 s from the reading thread, =1 and =2:
//
//   [PERF][PAGE_SPAN_READS] mode=1 spans=N words=N calls_saved=N refused=N verified=N wrong=N raced=N wrong_total=N
//
// The tests set the mode per async flow and supply their own span reader.
public static class PageSpanReads
{
    public static readonly int DefaultMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PAGE_SPAN_READS"), out var mode) && mode is 1 or 2 ? mode : 0;

    private static readonly AsyncLocal<int?> ModeOverride = new();

    public static int Mode
    {
        get => ModeOverride.Value ?? DefaultMode;
        set => ModeOverride.Value = value;
    }

    public static readonly int SpanBytes =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PAGE_SPAN_READS_BYTES"), out var bytes) ? Math.Clamp(bytes, 8, (int)PageBytes) : 1024;

    internal const ulong PageBytes = 0x1000;

    private const int DumpLimit = 20;

    // Set when the host answered OtherThread: this thread reads no more spans.
    [ThreadStatic] internal static bool OtherThread;

    private static readonly ThreadCounter _spans = new();
    private static readonly ThreadCounter _words = new();
    private static readonly ThreadCounter _callsSaved = new();
    private static readonly ThreadCounter _refused = new();
    private static readonly ThreadCounter _verified = new();
    private static readonly ThreadCounter _raced = new();
    private static readonly ThreadCounter _wrong = new();
    private static long _wrongTotal;
    private static int _dumps;
    private static long _lastReport = Stopwatch.GetTimestamp();
    [ThreadStatic] private static int _reportCheck;

    public static long WrongTotal => Interlocked.Read(ref _wrongTotal);

    internal static void NoteRead(int words, int units)
    {
        _spans.Increment();
        _words.Add(words);
        _callsSaved.Add(units - 1);
        MaterializeProbe.Count(MaterializeProbe.Counter.Spans);
        MaterializeProbe.Count(MaterializeProbe.Counter.SpanWords, words);
        MaterializeProbe.Count(MaterializeProbe.Counter.SpanCallsSaved, units - 1);
        MaybeReport();
    }

    internal static void NoteRefused(ResidentSpanRead outcome)
    {
        if (outcome == ResidentSpanRead.OtherThread)
        {
            OtherThread = true;
            return;
        }

        _refused.Increment();
        MaterializeProbe.Count(MaterializeProbe.Counter.SpanRefused);
        MaybeReport();
    }

    internal static void NoteVerified() => _verified.Increment();

    internal static void NoteRaced() => _raced.Increment();

    // A wrong word: counted, and true while the first ones are still to be printed.
    internal static bool NoteWrong()
    {
        _wrong.Increment();
        Interlocked.Increment(ref _wrongTotal);
        return Interlocked.Increment(ref _dumps) <= DumpLimit;
    }

    internal static void Dump(string kind, ulong planHash, ulong spanAddress, int spanLength, int spanWords, ulong address, ulong baseAddress,
        bool realValid, uint realWord, uint spanWord, bool rereadOk, uint reread) =>
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[GPU][PAGE_SPAN_WRONG] kind={kind} plan=0x{planHash:X16} span=0x{spanAddress:X}+{spanLength} span_words={spanWords} address=0x{address:X} ") +
            FormattableString.Invariant(
                $"base=0x{baseAddress:X} real_valid={realValid} real=0x{realWord:X8} span=0x{spanWord:X8} reread_ok={rereadOk} reread=0x{reread:X8}"));

    private static void MaybeReport()
    {
        if ((++_reportCheck & 255) != 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastReport);
        if (now - last < 5 * Stopwatch.Frequency || Interlocked.CompareExchange(ref _lastReport, now, last) != last)
        {
            return;
        }

        Console.Error.WriteLine(TakeReport());
    }

    public static string TakeReport() => FormattableString.Invariant(
        $"[PERF][PAGE_SPAN_READS] mode={DefaultMode} spans={_spans.Take()} words={_words.Take()} calls_saved={_callsSaved.Take()} refused={_refused.Take()} ") +
        FormattableString.Invariant($"verified={_verified.Take()} wrong={_wrong.Take()} raced={_raced.Take()} wrong_total={WrongTotal} span_bytes={SpanBytes}");
}
