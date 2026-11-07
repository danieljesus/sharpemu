// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.HLE.GuestMemory;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_RELAY_PROBE=1: what the GPU queue thread's relay commands are (QueueRelay in the render profile,
// with ReadbackFlush and ReadbackSubmit nested in it), one [PERF][RELAY] line per 5 s of presented frames, per frame:
// - commands drained per RunPendingCommands call (1 / 2 / 3+) and their time; begin and complete are the two hops of a
//   guest read of GPU-written memory (GuestBufferCache.ReadMemoryOrAwaitShutdownCore), rest is every other command plus
//   the handoff (the SemaphoreSlim release that wakes the guest);
// - begins by ReadbackSource and by outcome (pending copy submitted on the queue thread, pending copy handed to the
//   guest thread, nothing to download, synchronous download, every slot busy), with the writer tick of the copies
//   (the batch being recorded, so a cut; in flight; completed) and their pieces and bytes;
// - the copy submits on the queue thread and off it (SHARPEMU_READBACK_GUEST_SUBMIT) with their time, and the copies
//   handed back to the queue thread;
// - completes applied and not clean (retried), with the pieces they published and kept stale.
internal static class RelayProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_RELAY_PROBE") == "1";

    internal enum Outcome { Pending, OffQueue, NoDownload, Synchronous, SlotsBusy, Count }

    internal enum Writer { CurrentBatch, InFlight, Completed, Count }

    private const int SourceCount = (int)GuestMemoryProfile.ReadbackSource.CpuWriteInvalidation + 1;

    internal sealed class Counters
    {
        public long Drains, Drain1, Drain2, Drain3Plus, Commands, CommandTicks;
        public long Begins, BeginTicks, Completes, CompleteTicks;
        public readonly long[] SourceBegins = new long[SourceCount];
        public readonly long[] SourceTicks = new long[SourceCount];
        public readonly long[] Outcomes = new long[(int)Outcome.Count];
        public readonly long[] Writers = new long[(int)Writer.Count];
        public long Pieces, Bytes;
        public long QueueSubmits, QueueSubmitTicks, GuestSubmits, GuestSubmitTicks, GuestFallbacks;
        public long Applied, NotClean, PublishedPieces, StalePieces;
    }

    private static readonly object Gate = new();
    private static Counters _counters = new();
    private static long _windowStart;
    private static long _frames;

    public static Writer ClassifyWriter(ulong waitTick, ulong currentTick, ulong completedTick) =>
        waitTick >= currentTick ? Writer.CurrentBatch : waitTick > completedTick ? Writer.InFlight : Writer.Completed;

    public static void NoteDrain(int commands, long ticks)
    {
        if (commands == 0)
            return;
        lock (Gate)
        {
            var counters = _counters;
            counters.Drains++;
            if (commands == 1) counters.Drain1++;
            else if (commands == 2) counters.Drain2++;
            else counters.Drain3Plus++;
            counters.Commands += commands;
            counters.CommandTicks += ticks;
        }
    }

    public static void NoteBegin(GuestMemoryProfile.ReadbackSource source, Outcome outcome, long ticks)
    {
        lock (Gate)
        {
            var counters = _counters;
            counters.Begins++;
            counters.BeginTicks += ticks;
            var index = Math.Clamp((int)source, 0, SourceCount - 1);
            counters.SourceBegins[index]++;
            counters.SourceTicks[index] += ticks;
            counters.Outcomes[(int)outcome]++;
        }
    }

    public static void NoteCopies(Writer writer, int pieces, ulong bytes)
    {
        lock (Gate)
        {
            var counters = _counters;
            counters.Writers[(int)writer]++;
            counters.Pieces += pieces;
            counters.Bytes += (long)bytes;
        }
    }

    public static void NoteSubmit(bool offQueue, long ticks)
    {
        lock (Gate)
        {
            var counters = _counters;
            if (offQueue)
            {
                counters.GuestSubmits++;
                counters.GuestSubmitTicks += ticks;
            }
            else
            {
                counters.QueueSubmits++;
                counters.QueueSubmitTicks += ticks;
            }
        }
    }

    public static void NoteGuestFallback()
    {
        lock (Gate)
        {
            _counters.GuestFallbacks++;
        }
    }

    public static void NoteComplete(bool applied, int published, int stale, long ticks)
    {
        lock (Gate)
        {
            var counters = _counters;
            counters.Completes++;
            counters.CompleteTicks += ticks;
            if (applied) counters.Applied++;
            else counters.NotClean++;
            counters.PublishedPieces += published;
            counters.StalePieces += stale;
        }
    }

    // Called once per presented frame (FpsLog.FramePresented, on the render thread).
    public static void NoteFrame()
    {
        if (!Enabled)
            return;
        var now = Stopwatch.GetTimestamp();
        if (_windowStart == 0)
            _windowStart = now;
        _frames++;
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (seconds < 5.0)
            return;
        Counters taken;
        lock (Gate)
        {
            taken = _counters;
            _counters = new Counters();
        }

        Console.Error.WriteLine(Format(taken, seconds, _frames));
        _windowStart = now;
        _frames = 0;
    }

    internal static string Format(Counters c, double seconds, long frames)
    {
        var f = (double)Math.Max(frames, 1);
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / f;
        double Us(long ticks, long count) => count == 0 ? 0 : ticks * 1e6 / Stopwatch.Frequency / count;
        double Pf(long count) => count / f;
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"[PERF][RELAY] window_s={seconds:F1} frames={frames} drains={Pf(c.Drains):F1} drain_depth=1:{Pf(c.Drain1):F1},2:{Pf(c.Drain2):F1},3+:{Pf(c.Drain3Plus):F1} ");
        text.Append(CultureInfo.InvariantCulture,
            $"commands={Pf(c.Commands):F1}/{Ms(c.CommandTicks):F3}ms begin={Pf(c.Begins):F1}/{Ms(c.BeginTicks):F3}ms/{Us(c.BeginTicks, c.Begins):F1}us ");
        text.Append(CultureInfo.InvariantCulture,
            $"complete={Pf(c.Completes):F1}/{Ms(c.CompleteTicks):F3}ms/{Us(c.CompleteTicks, c.Completes):F1}us rest={Pf(c.Commands - c.Begins - c.Completes):F1}/{Ms(c.CommandTicks - c.BeginTicks - c.CompleteTicks):F3}ms sources=");
        var first = true;
        for (var index = 0; index < SourceCount; index++)
        {
            if (c.SourceBegins[index] == 0)
                continue;
            text.Append(first ? "" : ",");
            first = false;
            text.Append(CultureInfo.InvariantCulture,
                $"{(GuestMemoryProfile.ReadbackSource)index}:{Pf(c.SourceBegins[index]):F1}/{Ms(c.SourceTicks[index]):F3}ms");
        }

        text.Append(" outcomes=");
        for (var index = 0; index < (int)Outcome.Count; index++)
            text.Append(CultureInfo.InvariantCulture, $"{(index == 0 ? "" : ",")}{(Outcome)index}:{Pf(c.Outcomes[index]):F1}");
        text.Append(" writer=");
        for (var index = 0; index < (int)Writer.Count; index++)
            text.Append(CultureInfo.InvariantCulture, $"{(index == 0 ? "" : ",")}{(Writer)index}:{Pf(c.Writers[index]):F1}");
        var copies = c.Writers.Sum();
        text.Append(CultureInfo.InvariantCulture,
            $" pieces_per_copy={(copies == 0 ? 0 : (double)c.Pieces / copies):F2} kb_per_copy={(copies == 0 ? 0 : c.Bytes / 1024.0 / copies):F1} ");
        text.Append(CultureInfo.InvariantCulture,
            $"submits=queue:{Pf(c.QueueSubmits):F1}/{Ms(c.QueueSubmitTicks):F3}ms/{Us(c.QueueSubmitTicks, c.QueueSubmits):F1}us,guest:{Pf(c.GuestSubmits):F1}/{Ms(c.GuestSubmitTicks):F3}ms/{Us(c.GuestSubmitTicks, c.GuestSubmits):F1}us,handed_back:{Pf(c.GuestFallbacks):F2} ");
        text.Append(CultureInfo.InvariantCulture,
            $"completes=applied:{Pf(c.Applied):F1},not_clean:{Pf(c.NotClean):F2},published_pieces:{Pf(c.PublishedPieces):F1},stale_pieces:{Pf(c.StalePieces):F2}");
        return text.ToString();
    }
}
