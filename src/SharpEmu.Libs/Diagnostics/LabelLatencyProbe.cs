// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_LABEL_PROBE=<hex address>: the life of every command-processor write to that 8-byte label and of
// the guest's polls of it. Per write: the guest submission that carried it was enqueued (t0), the front ran the packet
// (t1, with its opcode and value), the render thread applied it to the backing and recorded its device copy in tick T
// (t2), T was submitted to the GPU queue (t3) and seen complete (t4); then the first guest poll that read the new value
// finished (t5). Per poll: its duration, the value read and the ticks in flight (recorded but not complete). Every 5 s as
// [LABEL_PROBE] with p50/p90 of each segment in ms, then the last values written and read.
internal static class LabelLatencyProbe
{
    public static readonly ulong Address = ParseAddress();
    public static readonly bool Enabled = Address != 0;

    private const int TickRing = 1 << 14;
    private static readonly long[] SubmittedAt = new long[TickRing];
    private static readonly ulong[] SubmittedTick = new ulong[TickRing];
    private static long _completedAt;
    private static ulong _completedTick;
    private static readonly long[] CompletedAtRing = new long[TickRing];
    private static readonly ulong[] CompletedTickRing = new ulong[TickRing];

    private sealed class Write
    {
        public long T0, T1, T2, T5;
        public ulong Tick, Value;
        public uint Opcode;
    }

    private static readonly object Gate = new();
    private static readonly Queue<Write> FrontPending = new();
    private static readonly List<Write> Applied = new();
    private static readonly List<Write> Finished = new();
    private static readonly List<(double Ms, long InFlight)> Polls = new();
    private static readonly Queue<string> Recent = new();
    private static long _reportAt;

    private static ulong ParseAddress()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_LABEL_PROBE");
        if (value is null) return 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var address) ? address : 0;
    }

    public static bool Overlaps(ulong address, ulong size) => address < Address + 8 && Address < address + size;

    // The front ran a packet that writes the label.
    public static void FrontWrite(ReadOnlySpan<byte> bytes, ulong address, uint opcode, long submissionEnqueued)
    {
        Span<byte> label = stackalloc byte[8];
        for (var index = 0; index < bytes.Length; index++)
        {
            var at = address + (ulong)index;
            if (at >= Address && at < Address + 8) label[(int)(at - Address)] = bytes[index];
        }

        lock (Gate)
        {
            FrontPending.Enqueue(new Write
            {
                T0 = submissionEnqueued,
                T1 = Stopwatch.GetTimestamp(),
                Value = BitConverter.ToUInt64(label),
                Opcode = opcode,
            });
        }
    }

    // The render thread applied the front's oldest pending write to the label, recording its copy in this tick.
    public static void RenderApply(ulong tick)
    {
        lock (Gate)
        {
            if (!FrontPending.TryDequeue(out var write)) return;
            write.T2 = Stopwatch.GetTimestamp();
            write.Tick = tick;
            Applied.Add(write);
        }
    }

    public static void NoteSubmitted(ulong tick)
    {
        var slot = (int)(tick & (TickRing - 1));
        SubmittedAt[slot] = Stopwatch.GetTimestamp();
        Volatile.Write(ref SubmittedTick[slot], tick);
    }

    public static void NoteCompleted(ulong counter)
    {
        var now = Stopwatch.GetTimestamp();
        lock (CompletedAtRing)
        {
            for (var tick = Math.Max(_completedTick + 1, counter > TickRing ? counter - TickRing + 1 : 0); tick <= counter; tick++)
            {
                var slot = (int)(tick & (TickRing - 1));
                CompletedAtRing[slot] = now;
                CompletedTickRing[slot] = tick;
            }

            _completedTick = Math.Max(_completedTick, counter);
            _completedAt = now;
        }
    }

    // A guest poll of the label finished (after any download it needed).
    public static void Poll(long started, ulong value, ulong current, ulong completed)
    {
        var now = Stopwatch.GetTimestamp();
        lock (Gate)
        {
            Polls.Add(((now - started) * 1000.0 / Stopwatch.Frequency, (long)(current - Math.Min(current, completed))));
            for (var index = 0; index < Applied.Count; index++)
            {
                var write = Applied[index];
                if (write.Value != value) continue;
                write.T5 = now;
                Finished.Add(write);
                Applied.RemoveRange(0, index + 1);
                break;
            }

            if (Applied.Count > 4096) Applied.RemoveRange(0, Applied.Count - 4096);
            Remember($"r{value:X}");
            Report(now);
        }
    }

    private static void Remember(string text)
    {
        Recent.Enqueue(text);
        while (Recent.Count > 24) Recent.Dequeue();
    }

    private static void Report(long now)
    {
        if (_reportAt == 0) { _reportAt = now + Stopwatch.Frequency * 5; return; }
        if (now < _reportAt) return;
        _reportAt = now + Stopwatch.Frequency * 5;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var segments = new List<double>[6];
        for (var index = 0; index < segments.Length; index++) segments[index] = new List<double>();
        var opcodes = new Dictionary<uint, int>();
        foreach (var write in Finished)
        {
            var slot = (int)(write.Tick & (TickRing - 1));
            var submitted = Volatile.Read(ref SubmittedTick[slot]) == write.Tick ? SubmittedAt[slot] : 0;
            long completed;
            lock (CompletedAtRing) completed = CompletedTickRing[slot] == write.Tick ? CompletedAtRing[slot] : 0;
            if (write.T0 != 0) segments[0].Add(Ms(write.T1 - write.T0));
            segments[1].Add(Ms(write.T2 - write.T1));
            if (submitted != 0) segments[2].Add(Ms(submitted - write.T2));
            if (submitted != 0 && completed != 0) segments[3].Add(Ms(completed - submitted));
            if (completed != 0) segments[4].Add(Ms(write.T5 - completed));
            if (write.T0 != 0) segments[5].Add(Ms(write.T5 - write.T0));
            opcodes[write.Opcode] = opcodes.GetValueOrDefault(write.Opcode) + 1;
        }

        string Stat(List<double> values)
        {
            if (values.Count == 0) return "-";
            values.Sort();
            return FormattableString.Invariant($"{values[values.Count / 2]:F2}/{values[(int)(values.Count * 0.9)]:F2}");
        }

        var pollMs = Polls.Select(p => p.Ms).ToList();
        var inFlight = Polls.Count == 0 ? 0 : Polls.Average(p => p.InFlight);
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[LABEL_PROBE] address=0x{Address:X} writes_seen={Finished.Count} polls={Polls.Count} poll_ms_p50/p90={Stat(pollMs)} ticks_in_flight_at_poll={inFlight:F1} " +
            $"ms_p50/p90: queue_to_front={Stat(segments[0])} front_to_record={Stat(segments[1])} record_to_gpu_submit={Stat(segments[2])} gpu_submit_to_complete={Stat(segments[3])} complete_to_guest_sees={Stat(segments[4])} total={Stat(segments[5])} " +
            $"opcodes=[{string.Join(",", opcodes.Select(pair => FormattableString.Invariant($"0x{pair.Key:X2}:{pair.Value}")))}] unseen_writes={Applied.Count} front_pending={FrontPending.Count}"));
        Console.Error.WriteLine($"[LABEL_PROBE] recent {string.Join(' ', Recent)}");
        Finished.Clear();
        Polls.Clear();
    }

    public static void RememberWrite(ulong value)
    {
        lock (Gate) Remember($"w{value:X}");
    }
}
