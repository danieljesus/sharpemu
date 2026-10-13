// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_LABEL_TRACE=1: a timeline of the memory waits that block the front and of everything that could
// release them, over one window (SHARPEMU_LABEL_TRACE_START seconds after process start, default 220, for
// SHARPEMU_LABEL_TRACE_SECONDS, default 3). Every address a front wait suspended on is watched: its stream writes
// (queued, applied), end-of-pipe records, a poller thread's view of its value (any writer: CP, guest CPU, shaders),
// plus every interrupt, flip completion, seal, submit and completed tick, guest graphics events delivered and guest
// submissions. Written to SHARPEMU_LABEL_TRACE_OUT (default label-trace.txt) when the window ends.
internal static class LabelTrace
{
    internal enum Kind : byte
    {
        Suspend = 1,
        Resume,
        CpQueued,
        CpApplied,
        NowQueued,
        NowApplied,
        EopFront,
        EopBack,
        InterruptQueued,
        Interrupt,
        FlipQueued,
        Flip,
        Seal,
        Submitted,
        PriorityDone,
        Poll,
        EventDelivered,
        GuestSubmit,
        RetryOnTick,
        FrontTrigger,
        // [local] Critical-path states (2026-10-14): the front loop's state on every change (value = 0 work,
        // 1 every head blocked, 2 no work; on blocked -> work the address and kind of the wait the progressing head
        // left), the render thread's idle waits (value = phase) and its GPU waits (value = waited tick or ticket,
        // extra = recording tick; address 1 = readback, 2 = timeline, 3 = buffer download).
        FrontState,
        RenderIdle,
        RenderIdleEnd,
        GpuWait,
        GpuWaitEnd,
    }

    private struct Event
    {
        public long Ticks;
        public Kind Kind;
        public int Queue;
        public ulong Address;
        public ulong Value;
        public ulong Extra;
    }

    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_LABEL_TRACE") == "1";
    private static readonly long WindowStart;
    private static readonly long WindowEnd;
    private static readonly Event[] _events = new Event[Enabled ? 1 << 21 : 1];
    private static int _count;
    private static ulong[] _watched = [];
    private static readonly object _watchGate = new();
    private static readonly (ulong Address, ulong Reference)[] _episodes = new (ulong, ulong)[64];

    // Reads guest memory for the poller (set by the presenter).
    internal static Func<ulong, ulong>? Reader;

    static LabelTrace()
    {
        if (!Enabled)
            return;
        var start = double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_LABEL_TRACE_START"), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 220;
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_LABEL_TRACE_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 3;
        var sinceStart = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
        var now = Stopwatch.GetTimestamp();
        WindowStart = now + (long)((start - sinceStart) * Stopwatch.Frequency);
        WindowEnd = WindowStart + (long)(seconds * Stopwatch.Frequency);
        new Thread(Run) { IsBackground = true, Name = "SharpEmu label trace", Priority = ThreadPriority.AboveNormal }.Start();
    }

    private static bool InWindow(long now) => now >= WindowStart && now < WindowEnd;

    internal static bool IsWatched(ulong address)
    {
        var key = address & ~3UL;
        foreach (var watched in Volatile.Read(ref _watched))
        {
            if (watched == key)
                return true;
        }

        return false;
    }

    private static void Watch(ulong address)
    {
        if (IsWatched(address))
            return;
        lock (_watchGate)
        {
            if (IsWatched(address) || _watched.Length >= 32)
                return;
            Volatile.Write(ref _watched, [.. _watched, address & ~3UL]);
        }
    }

    internal static void Note(Kind kind, int queue, ulong address, ulong value, ulong extra = 0) =>
        NoteAt(Stopwatch.GetTimestamp(), kind, queue, address, value, extra);

    internal static void NoteAt(long now, Kind kind, int queue, ulong address, ulong value, ulong extra = 0)
    {
        if (!InWindow(now))
            return;
        var index = Interlocked.Increment(ref _count) - 1;
        if (index >= _events.Length)
            return;
        _events[index] = new Event { Ticks = now, Kind = kind, Queue = queue, Address = address, Value = value, Extra = extra };
    }

    internal static void NoteIfWatched(Kind kind, int queue, ulong address, ulong value, ulong extra = 0)
    {
        if (IsWatched(address))
            Note(kind, queue, address, value, extra);
    }

    // A front memory wait polled: unmet starts an episode (logged once per queue, address and reference), met ends it.
    internal static void WaitPolled(int queue, ulong address, ulong value, ulong reference, uint compare, bool satisfied)
    {
        var slot = queue & 63;
        if (!satisfied)
        {
            Watch(address);
            if (_episodes[slot] != (address, reference))
            {
                _episodes[slot] = (address, reference);
                Note(Kind.Suspend, queue, address, value, reference | ((ulong)compare << 60));
            }

            return;
        }

        if (_episodes[slot].Address == address && _episodes[slot].Reference == reference)
        {
            _episodes[slot] = default;
            Note(Kind.Resume, queue, address, value, reference);
        }
    }

    private static void Run()
    {
        while (Stopwatch.GetTimestamp() < WindowStart)
            Thread.Sleep(50);
        var last = new Dictionary<ulong, ulong>();
        while (Stopwatch.GetTimestamp() < WindowEnd)
        {
            if (Reader is { } reader)
            {
                foreach (var address in Volatile.Read(ref _watched))
                {
                    ulong value;
                    try
                    {
                        value = reader(address);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!last.TryGetValue(address, out var previous) || previous != value)
                    {
                        last[address] = value;
                        Note(Kind.Poll, -1, address, value, previous);
                    }
                }
            }

            Thread.SpinWait(50);
        }

        var count = Math.Min(Volatile.Read(ref _count), _events.Length);
        var output = Environment.GetEnvironmentVariable("SHARPEMU_LABEL_TRACE_OUT") is { Length: > 0 } path ? path : "label-trace.txt";
        using var writer = new StreamWriter(output);
        writer.WriteLine($"# frequency={Stopwatch.Frequency} events={count} watched={string.Join(",", _watched.Select(a => $"0x{a:X}"))}");
        var events = _events.AsSpan(0, count).ToArray();
        Array.Sort(events, (a, b) => a.Ticks.CompareTo(b.Ticks));
        foreach (var e in events)
        {
            writer.WriteLine(FormattableString.Invariant(
                $"{(e.Ticks - WindowStart) * 1_000_000.0 / Stopwatch.Frequency:F1} {e.Kind} q={e.Queue} a=0x{e.Address:X} v=0x{e.Value:X} x=0x{e.Extra:X}"));
        }

        Console.Error.WriteLine($"[LOADER][INFO] label trace written: {output} events={count}");
    }
}
