// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE;

// [local] SHARPEMU_LOCK_PROBE=1: counts contended acquisitions and wait time of named lock sites and
// prints them every 5 s as [LOCK_PROBE] lines. Call Probe right before the lock statement; it takes and
// releases the lock once to observe contention. Disabled, it does nothing.
public static class LockProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_LOCK_PROBE") == "1";

    private const int SiteCount = 32;
    private static readonly string?[] Names = new string?[SiteCount];
    private static readonly long[] Calls = new long[SiteCount];
    private static readonly long[] Contended = new long[SiteCount];
    private static readonly long[] WaitTicks = new long[SiteCount];

    static LockProbe()
    {
        if (Enabled)
        {
            new Thread(Report) { IsBackground = true, Name = "LockProbe" }.Start();
        }
    }

    public static void Probe(object gate, int site, string name)
    {
        if (!Enabled)
        {
            return;
        }

        Names[site] ??= name;
        Interlocked.Increment(ref Calls[site]);
        if (Monitor.TryEnter(gate))
        {
            Monitor.Exit(gate);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        Monitor.Enter(gate);
        Monitor.Exit(gate);
        Interlocked.Increment(ref Contended[site]);
        Interlocked.Add(ref WaitTicks[site], Stopwatch.GetTimestamp() - start);
    }

    private static void Report()
    {
        for (;;)
        {
            Thread.Sleep(5000);
            for (var site = 0; site < SiteCount; site++)
            {
                var calls = Interlocked.Exchange(ref Calls[site], 0);
                if (calls == 0)
                {
                    continue;
                }

                var contended = Interlocked.Exchange(ref Contended[site], 0);
                var wait = Interlocked.Exchange(ref WaitTicks[site], 0);
                Console.Error.WriteLine(
                    $"[LOCK_PROBE] site={Names[site]} calls={calls} contended={contended} wait_ms={wait * 1000.0 / Stopwatch.Frequency:F1}");
            }
        }
    }
}
