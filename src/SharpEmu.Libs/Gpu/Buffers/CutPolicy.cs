// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_CUT_POLICY=<bits> (off by default; 0 off): where the render thread ends the open batch. A cut only
// moves a batch boundary to a place where any full batch could put one (right after a compute dispatch, or before a
// preparation outside a render pass): no command, barrier or order changes, so it is exact by construction.
//   1 (words): a table word the render thread reads while its writer is in the open batch (a synchronizing read, or a
//     memo hit that SHARPEMU_TABLE_WORD_MEMO_LEARN kept) becomes hot; a compute dispatch with a write binding of at
//     most SHARPEMU_CUT_WORD_MAX bytes (hex, default 100) covering a hot word submits the batch right after itself.
//   2 (guest windows): the 64 KB readback window of a guest thread's GPU read (an async download, or an emulated
//     read of a page still being written) whose writer has not completed becomes hot for 4096 batches; a compute
//     dispatch with a write binding of at most SHARPEMU_CUT_WINDOW_MAX bytes (hex, default 10000) overlapping one
//     submits the batch right after itself, so the writer's tick completes without the rest of the batch.
//   4 (duration): a preparation outside a render pass submits the open batch once it has been open for
//     SHARPEMU_CUT_MAX_US microseconds (default 2000).
// 2026-11-02 (gta/cutpolicy): with LEARN=8 the 994A read no longer cuts the batch after 5981's refit, and the guest
// threads find that work later in the queue.
internal sealed class CutPolicy
{
    public static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_CUT_POLICY"), out var mode) ? mode & 7 : 0;
    public static readonly ulong WordMaxBytes = ParseHex("SHARPEMU_CUT_WORD_MAX", 0x100);
    public static readonly ulong WindowMaxBytes = ParseHex("SHARPEMU_CUT_WINDOW_MAX", 0x10000);
    public static readonly long MaxOpenTicks = (long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_CUT_MAX_US"), out var us) && us > 0 ? us : 2000) *
        Stopwatch.Frequency / 1_000_000;

    public static bool Enabled => Mode != 0;
    public static bool Words => (Mode & 1) != 0;
    public static bool GuestWindows => (Mode & 2) != 0;
    public static bool Duration => (Mode & 4) != 0;

    // Set by the cache when a write binding hits; the compute executor submits right after the dispatch.
    internal static bool DispatchCutPending;
    internal static Action? DispatchCut;

    public const ulong WindowBytes = 64 * 1024;
    public const long WindowLifetime = 4096;
    public const int MaxHotWords = 64;
    public const int MaxHotWindows = 64;

    private readonly ulong _wordMax;
    private readonly ulong _windowMax;
    private readonly ulong[] _words = new ulong[MaxHotWords];
    private int _wordCount;
    private int _wordNext;
    private readonly ulong[] _windows = new ulong[MaxHotWindows];
    private readonly long[] _windowHits = new long[MaxHotWindows];
    private int _windowCount;

    public long WordCuts, WindowCuts, DurationCuts, WordRejects, WindowRejects, WordsNoted, WindowsNoted;
    public readonly long[] RejectSizes = new long[8];
    private long _report = Stopwatch.GetTimestamp();

    public CutPolicy(ulong wordMax, ulong windowMax)
    {
        _wordMax = wordMax;
        _windowMax = windowMax;
    }

    public int HotWordCount => _wordCount;

    public int HotWindowCount => _windowCount;

    private static ulong ParseHex(string name, ulong fallback) =>
        ulong.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.HexNumber, null, out var value) && value != 0 ? value : fallback;

    // A render table word read with its writer still in the open batch.
    public void NoteWord(ulong address)
    {
        if (address == 0 || Array.IndexOf(_words, address, 0, _wordCount) >= 0)
        {
            return;
        }

        WordsNoted++;
        if (_wordCount < MaxHotWords)
        {
            _words[_wordCount++] = address;
            return;
        }

        _words[_wordNext] = address;
        _wordNext = (_wordNext + 1) % MaxHotWords;
    }

    // A guest thread's GPU read of [address, ...) whose writer has not completed; serial is the cache's batch serial.
    public void NoteWindow(ulong address, long serial)
    {
        var key = address & ~(WindowBytes - 1);
        var oldest = 0;
        for (var index = 0; index < _windowCount; index++)
        {
            if (_windows[index] == key)
            {
                _windowHits[index] = serial;
                return;
            }

            if (_windowHits[index] < _windowHits[oldest])
            {
                oldest = index;
            }
        }

        WindowsNoted++;
        if (_windowCount < MaxHotWindows)
        {
            _windows[_windowCount] = key;
            _windowHits[_windowCount++] = serial;
            return;
        }

        _windows[oldest] = key;
        _windowHits[oldest] = serial;
    }

    // A write binding of [address, address + size) recorded into the open batch: true when it should end the batch
    // right after its dispatch.
    public bool NoteWrite(ulong address, ulong size, long serial, bool words, bool windows)
    {
        if (size == 0)
        {
            return false;
        }

        var end = address + size;
        if (words)
        {
            for (var index = 0; index < _wordCount; index++)
            {
                var word = _words[index];
                if (address <= word && word < end)
                {
                    if (size <= _wordMax)
                    {
                        WordCuts++;
                        return true;
                    }

                    WordRejects++;
                    NoteRejectSize(size);
                    break;
                }
            }
        }

        if (windows)
        {
            for (var index = 0; index < _windowCount; index++)
            {
                var key = _windows[index];
                if (key < end && address < key + WindowBytes && serial - _windowHits[index] <= WindowLifetime)
                {
                    if (size <= _windowMax)
                    {
                        WindowCuts++;
                        return true;
                    }

                    WindowRejects++;
                    NoteRejectSize(size);
                    break;
                }
            }
        }

        return false;
    }

    private void NoteRejectSize(ulong size)
    {
        // 0: <= 4 KB, then x4 per bucket; the last one is > 16 MB.
        var bucket = 0;
        for (var limit = 4096UL; bucket < RejectSizes.Length - 1 && size > limit; limit <<= 2)
        {
            bucket++;
        }

        RejectSizes[bucket]++;
    }

    public void Report()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _report < 20 * Stopwatch.Frequency)
        {
            return;
        }

        _report = now;
        Console.Error.WriteLine(
            $"[CUT_POLICY] mode={Mode} word_cuts={WordCuts} window_cuts={WindowCuts} duration_cuts={DurationCuts} word_rejects={WordRejects} window_rejects={WindowRejects} " +
            $"words={_wordCount} windows={_windowCount} words_noted={WordsNoted} windows_noted={WindowsNoted} reject_sizes={string.Join(",", RejectSizes)}");
        WordCuts = WindowCuts = DurationCuts = WordRejects = WindowRejects = WordsNoted = WindowsNoted = 0;
        Array.Clear(RejectSizes);
    }
}
