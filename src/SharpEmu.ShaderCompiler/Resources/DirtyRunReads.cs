// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.ShaderCompiler.Resources;

// [local] What a run read did: the run's bytes (as the resident reader copies them), each word answered as the word reader
// answers it (words and ok), refused (the caller reads the run as before), or refused because the calling thread does
// not read runs this way (remembered for the thread).
public enum ResidentRunRead : byte
{
    Bytes,
    Words,
    Refused,
    OtherThread,
}

// [local] SHARPEMU_DIRTY_RUN_READS: one run of adjacent words, [address, address + destination.Length); wordAddresses are
// the run's word reads in order (a repeated address repeated), answered into words and ok when the outcome is Words.
public delegate ResidentRunRead ResidentGuestRunReader(ulong address, Span<byte> destination, ReadOnlySpan<ulong> wordAddresses, Span<uint> words,
    Span<bool> ok);

// [local] SHARPEMU_DIRTY_RUN_READS (0 off by default, 1 on, 2 verify): a run of adjacent words (ReadRun) is read with one
// resident read; when the GPU owns any of its bytes the resident reader refuses and every word is read again through the
// word reader, which repeats for each word the checks the resident read already made for the whole run (the thread's
// role, the pending publications, the mapping). In the open world that is ~1 refused run and ~2.6 GPU-owned words per
// dispatch (DRAWCOST dmb2). With the switch the run goes to ResourceRuntimeInputs.ReadResidentRun instead: the host makes
// the resident read's checks once; clean, it copies the bytes as the resident reader does; GPU-owned, it answers each
// word through the word reader's own render-thread body with the run's publication and mapping checks standing for the
// word's (SharpEmu.Libs' VulkanVideoPresenter.TryReadResidentGuestRun). Refused, the run is read as before.
//
// Exactness: the words take the values and the validity the old path's ReadWord would give them (AssignWord), in the
// same order; a run the host refuses reads exactly as without the switch. The host refuses whenever the word reader
// would take a side path (TLAS HLE consumer notes, conservative records, watched bounds), when a publication is still
// pending over the run after its completion, or when the run is not mapped; it reads on the render thread only. Races:
// the GPU-owned marks are set by the recording thread itself; a publication this item must see was registered before the
// item was queued; a concurrent unmap or guest CPU write is unordered with the materialization (=2 counts the latter as
// raced).
//
// =2 reads every run the old way first (the answer kept), then through the host, and compares word by word; a word the
// host answered differently is read again through the word reader: equal to the host's answer = raced, otherwise wrong
// (the first DumpLimit printed as [GPU][DIRTY_RUN_WRONG]). The second read of a GPU-owned word finds the memo the first
// one filled, which gives the same value. One line every 5 s from the reading thread, =1 and =2:
//
//   [PERF][DIRTY_RUN_READS] mode=1 runs=N bytes=N words=N word_runs=N refused=N verified=N wrong=N raced=N wrong_total=N
//
// The tests set the mode per async flow and supply their own run reader.
public static class DirtyRunReads
{
    public static readonly int DefaultMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DIRTY_RUN_READS"), out var mode) && mode is 1 or 2 ? mode : 0;

    private static readonly AsyncLocal<int?> ModeOverride = new();

    public static int Mode
    {
        get => ModeOverride.Value ?? DefaultMode;
        set => ModeOverride.Value = value;
    }

    private const int DumpLimit = 20;

    // Set when the host answered OtherThread: this thread reads no more runs this way.
    [ThreadStatic] internal static bool OtherThread;

    private static readonly ThreadCounter _runs = new();
    private static readonly ThreadCounter _bytes = new();
    private static readonly ThreadCounter _wordRuns = new();
    private static readonly ThreadCounter _words = new();
    private static readonly ThreadCounter _refused = new();
    private static readonly ThreadCounter _verified = new();
    private static readonly ThreadCounter _raced = new();
    private static readonly ThreadCounter _wrong = new();
    private static long _wrongTotal;
    private static int _dumps;
    private static long _lastReport = Stopwatch.GetTimestamp();
    [ThreadStatic] private static int _reportCheck;

    public static long WrongTotal => Interlocked.Read(ref _wrongTotal);

    internal static void Note(ResidentRunRead outcome, int words)
    {
        switch (outcome)
        {
            case ResidentRunRead.OtherThread:
                OtherThread = true;
                return;
            case ResidentRunRead.Bytes:
                _runs.Increment();
                _bytes.Increment();
                MaterializeProbe.Count(MaterializeProbe.Counter.RunReads);
                MaterializeProbe.Count(MaterializeProbe.Counter.RunReadBytes);
                break;
            case ResidentRunRead.Words:
                _runs.Increment();
                _wordRuns.Increment();
                _words.Add(words);
                MaterializeProbe.Count(MaterializeProbe.Counter.RunReads);
                MaterializeProbe.Count(MaterializeProbe.Counter.RunReadWords, words);
                break;
            default:
                _refused.Increment();
                MaterializeProbe.Count(MaterializeProbe.Counter.RunReadRefused);
                break;
        }

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

    internal static void Dump(string kind, ulong planHash, ulong runAddress, int runLength, ResidentRunRead outcome, ulong address, ulong baseAddress,
        bool realValid, uint realWord, bool hostValid, uint hostWord, bool rereadOk, uint reread) =>
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[GPU][DIRTY_RUN_WRONG] kind={kind} plan=0x{planHash:X16} run=0x{runAddress:X}+{runLength} outcome={outcome} address=0x{address:X} base=0x{baseAddress:X} ") +
            FormattableString.Invariant(
                $"real_valid={realValid} real=0x{realWord:X8} host_valid={hostValid} host=0x{hostWord:X8} reread_ok={rereadOk} reread=0x{reread:X8}"));

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
        $"[PERF][DIRTY_RUN_READS] mode={DefaultMode} runs={_runs.Take()} bytes={_bytes.Take()} words={_words.Take()} word_runs={_wordRuns.Take()} refused={_refused.Take()} ") +
        FormattableString.Invariant($"verified={_verified.Take()} wrong={_wrong.Take()} raced={_raced.Take()} wrong_total={WrongTotal}");
}
