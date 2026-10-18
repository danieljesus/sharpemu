// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_MEMCOPY_PROBE=1: bytes and calls of the guest-memory writes and fills the render thread does, by
// site, reported every 5 s as [MEMCOPY_PROBE] (per second).
internal static class MemCopyProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_MEMCOPY_PROBE") == "1";

    public enum Site
    {
        ReadbackAsync,
        ReadbackPieces,
        ReadbackPending,
        ReadbackSync,
        ImageDownload,
        FillFast,
        FillSmall,
        FillStackPattern,
        Count,
    }

    private static readonly long[] Bytes = new long[(int)Site.Count];
    private static readonly long[] Calls = new long[(int)Site.Count];
    private static long _reportAt;

    public static void Add(Site site, long bytes)
    {
        Interlocked.Add(ref Bytes[(int)site], bytes);
        Interlocked.Increment(ref Calls[(int)site]);
        var now = Stopwatch.GetTimestamp();
        var due = Volatile.Read(ref _reportAt);
        if (due == 0)
        {
            Interlocked.CompareExchange(ref _reportAt, now + Stopwatch.Frequency * 5, 0);
            return;
        }

        if (now < due || Interlocked.CompareExchange(ref _reportAt, now + Stopwatch.Frequency * 5, due) != due)
        {
            return;
        }

        var seconds = (now - due) / (double)Stopwatch.Frequency + 5.0;
        var text = new System.Text.StringBuilder("[MEMCOPY_PROBE] window_s=").Append(seconds.ToString("F1"));
        for (var index = 0; index < (int)Site.Count; index++)
        {
            var bytesNow = Interlocked.Exchange(ref Bytes[index], 0);
            var callsNow = Interlocked.Exchange(ref Calls[index], 0);
            text.Append(' ').Append((Site)index).Append("=").Append((bytesNow / 1024.0 / seconds).ToString("F0")).Append("KB/s,")
                .Append((callsNow / seconds).ToString("F0")).Append("/s");
        }

        Console.Error.WriteLine(text.ToString());
    }
}
