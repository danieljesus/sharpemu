// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_LABEL_TIMELINE=<file> (off by default; needs SHARPEMU_LABEL_PROBE): one line per event of the label's
// life and of what paces the game around it, with the time in ms since the first event, written to the file:
// Q queue (a guest submission created), F (the front ran a label write: value), R (the render thread applied it in
// tick), S (tick submitted), C (ticks complete up to), g (a guest poll of the label began: current, completed, size),
// w (its readback ran on the GPU queue: block write tick, precise write tick with SHARPEMU_PRECISE_WRITE_TICKS, current
// tick, pieces), a (its download finished: completed tick), b (its completion ran on the GPU queue: current tick), G (a guest poll of the label ended: value, current, completed, ms),
// W (a GPU-queue-thread wait over 1 ms: tick, kind, site, ms), K (render loop: 0 start, 2 guest work flushed, 4 presentation taken, 6 presented), V (flip reserved by the front: request), P (flip presented: request).
// X (the front's state changed: 0 ran, 1 all blocked, 2 no work; the last run head's wait kind and address), Y (the 100 ms
// all-blocked retry fired: ms since progress), Z (the queue heads then: queue:blocked:kind:wait address:packet address),
// U (sceAgcRewindPatchSetRewindState: body address, valid, heads woken), and with SHARPEMU_GUEST_CALL_TIMELINE T and H (see
// DirectExecutionBackend.LabelPacing.cs). The first SHARPEMU_LABEL_TIMELINE_LINES lines
// (default 400000) only; SHARPEMU_LABEL_TIMELINE_SKIP=<kinds> leaves those kinds out.
internal static class LabelTimeline
{
    private static readonly StreamWriter? Writer = Open();
    public static readonly bool Enabled = Writer is not null;
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly long Limit = long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_LABEL_TIMELINE_LINES"), out var limit) ? limit : 400_000;
    private static readonly string Skip = Environment.GetEnvironmentVariable("SHARPEMU_LABEL_TIMELINE_SKIP") ?? "";
    private static long _lines;
    private static readonly object Gate = new();

    private static StreamWriter? Open()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_LABEL_TIMELINE");
        if (string.IsNullOrEmpty(path)) return null;
        StreamWriter writer;
        try
        {
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
        }
        catch (IOException)
        {
            return null;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (Gate) writer.Flush(); };
        return writer;
    }

    private static readonly HashSet<int> Sites = new();

    // A wait of more than 1 ms on the thread that owns the GPU queue: W (tick, kind 0 timeline / 1 readback, site, ms),
    // with the site's stack printed once to stderr as [LABEL_TIMELINE_SITE].
    public static void NoteWait(long started, ulong tick, int kind, bool gpuQueueThread)
    {
        if (Writer is null || !gpuQueueThread) return;
        var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        if (ms < 1.0) return;
        var trace = new StackTrace(2, false);
        var text = new System.Text.StringBuilder();
        for (var index = 0; index < Math.Min(trace.FrameCount, 8); index++)
        {
            var method = trace.GetFrame(index)?.GetMethod();
            text.Append(method?.DeclaringType?.Name).Append('.').Append(method?.Name).Append(" < ");
        }

        var site = text.ToString().GetHashCode() & 0x7FFFFFFF;
        lock (Gate)
        {
            if (Sites.Count < 64 && Sites.Add(site)) Console.Error.WriteLine($"[LABEL_TIMELINE_SITE] {site:X} {text}");
        }

        Log('W', tick, (ulong)kind, (ulong)site, ms);
    }

    // A line with free text after the kind (SHARPEMU_GUEST_CALL_TIMELINE's T).
    public static void LogText(char kind, string text)
    {
        if (Writer is null || Skip.Contains(kind)) return;
        var ms = (Stopwatch.GetTimestamp() - Origin) * 1000.0 / Stopwatch.Frequency;
        lock (Gate)
        {
            if (_lines >= Limit) return;
            _lines++;
            Writer.Write(string.Create(CultureInfo.InvariantCulture, $"{ms:F3} {kind} {text}\n"));
            if ((_lines & 1023) == 0) Writer.Flush();
            if (_lines == Limit) Writer.Flush();
        }
    }

    public static void Log(char kind, ulong a = 0, ulong b = 0, ulong c = 0, double d = 0)
    {
        if (Writer is null || Skip.Contains(kind)) return;
        var ms = (Stopwatch.GetTimestamp() - Origin) * 1000.0 / Stopwatch.Frequency;
        lock (Gate)
        {
            if (_lines >= Limit) return;
            _lines++;
            Writer.Write(string.Create(CultureInfo.InvariantCulture, $"{ms:F3} {kind} {a:X} {b:X} {c:X} {d:F3}\n"));
            if ((_lines & 1023) == 0) Writer.Flush();
            if (_lines == Limit) Writer.Flush();
        }
    }
}
