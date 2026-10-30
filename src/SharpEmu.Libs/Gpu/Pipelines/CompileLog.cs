// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Gpu.Pipelines;

// [local] SHARPEMU_COMPILE_LOG=1 (off): every shader translation and pipeline creation of the session, with its time and
// the thread it ran on, and how long a dispatch waited for its asynchronous compute pipeline. One [PERF][COMPILE] line
// per event of at least EventMilliseconds; one [PERF][COMPILES] line per 5 s window that had any (count and ms per kind
// and thread). Kinds: translate (SPIR-V from the guest program plus its module), graphics (vkCreateGraphicsPipelines,
// inline), compute (inline), compute-async (the worker's vkCreateComputePipelines), dispatch-wait (the command stream
// spinning until that compile finishes), emulator (the emulator's own pipelines).
internal static class CompileLog
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_COMPILE_LOG") == "1";
    private const double EventMilliseconds = 20;
    private static readonly long Started = Stopwatch.GetTimestamp();
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Kind, string Thread), (long Count, double Milliseconds, double Maximum)> Window = new();
    private static long _windowStart = Stopwatch.GetTimestamp();

    internal static string ThreadLabel()
    {
        var name = Thread.CurrentThread.Name;
        return string.IsNullOrEmpty(name) ? $"tid{Environment.CurrentManagedThreadId}" : name.Replace(' ', '_');
    }

    internal static void Note(string kind, double milliseconds, ulong hash, string detail = "", string? thread = null)
    {
        if (!Enabled)
            return;
        thread ??= ThreadLabel();
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - Started) / (double)Stopwatch.Frequency;
        if (milliseconds >= EventMilliseconds)
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[PERF][COMPILE] t={seconds:F1} kind={kind} ms={milliseconds:F1} hash=0x{hash:X16} thread={thread} {detail}"));
        string? report = null;
        lock (Gate)
        {
            Window.TryGetValue((kind, thread), out var sum);
            Window[(kind, thread)] = (sum.Count + 1, sum.Milliseconds + milliseconds, Math.Max(sum.Maximum, milliseconds));
            if ((now - _windowStart) / (double)Stopwatch.Frequency >= 5)
                report = TakeReport(seconds, now);
        }

        if (report is not null)
            Console.Error.WriteLine(report);
    }

    private static string TakeReport(double seconds, long now)
    {
        var builder = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"[PERF][COMPILES] t={seconds:F1} window_s={(now - _windowStart) / (double)Stopwatch.Frequency:F1}"));
        foreach (var ((kind, thread), sum) in Window.OrderBy(static pair => pair.Key.Kind, StringComparer.Ordinal))
            builder.Append(CultureInfo.InvariantCulture, $" {kind}@{thread}={sum.Count}/{sum.Milliseconds:F1}ms/max{sum.Maximum:F1}");
        Window.Clear();
        _windowStart = now;
        return builder.ToString();
    }
}
