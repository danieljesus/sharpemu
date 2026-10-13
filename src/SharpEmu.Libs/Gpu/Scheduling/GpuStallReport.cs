// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// [local] A host wait on the GPU (the main timeline, a readback, the submit thread, the record ring) that lasts longer
// than TimeoutNs prints one [GPU][STALL] line naming what it waits for, followed by where every queue stands: the
// tick being recorded, the sealed and submitted ticks, the main timeline's completed value, the priority worker's
// head, the record ring's replay position and the readback timelines. A freeze then tells the GPU (submitted >
// completed and completed not moving), the submit thread (sealed > submitted) and the recording side (wait tick not
// sealed) apart without a debugger. Reports repeat every TimeoutNs up to MaxReportsPerWait per wait.
internal static class GpuStallReport
{
    internal const ulong TimeoutNs = 5_000_000_000;
    internal const int TimeoutMs = 5_000;
    internal const int MaxReportsPerWait = 6;

    // Set by the presenter once its scheduler, ring and readbacks exist.
    internal static Func<string>? Describe;

    // The last submissions (tick, last recorded operation, shader, semaphore waits), printed with the first reports.
    internal static Func<IEnumerable<string>>? History;

    private static int _reports;

    internal static void Report(string what)
    {
        var report = Interlocked.Increment(ref _reports);
        if (report > 256)
        {
            return;
        }

        if (report <= 3 && History is { } history)
        {
            try
            {
                foreach (var line in history())
                {
                    Console.Error.WriteLine($"[GPU][STALL] history {line}");
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[GPU][STALL] history_failed={exception.GetType().Name}");
            }
        }

        string state;
        try
        {
            state = Describe?.Invoke() ?? "";
        }
        catch (Exception exception)
        {
            state = $"describe_failed={exception.GetType().Name}:{exception.Message}";
        }

        Console.Error.WriteLine($"[GPU][STALL] {what} thread={Environment.CurrentManagedThreadId}:{Thread.CurrentThread.Name} {state}");
    }
}
