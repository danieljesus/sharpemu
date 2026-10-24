// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.Gpu.Pipelines;

// [local] SHARPEMU_PROGRAM_GATE_VERIFY=1: counts the lock-free program hits checked against a lookup under the gate and
// the ones that differ, by kind, and prints them every 20 s as [PROGRAM_GATE_VERIFY] lines (totals since start).
internal static class ProgramGateVerify
{
    private const int LoggedPerKind = 6;
    private const int LoggedErrors = 16;

    // Not a difference: the snapshots agree except for device-range handles, which are labels.
    internal const string LabelsOnly = "labels_only";

    private static long _checked;
    private static long _labelsOnly;
    private static long _mismatches;
    private static long _errors;
    private static int _loggedErrors;
    private static readonly ConcurrentDictionary<string, long> Kinds = new();

    static ProgramGateVerify()
    {
        new Thread(Report) { IsBackground = true, Name = "ProgramGateVerify" }.Start();
    }

    internal static long Checked => Interlocked.Read(ref _checked);

    internal static long Mismatches => Interlocked.Read(ref _mismatches);

    internal static void Note(ShaderSource source, string? difference)
    {
        Interlocked.Increment(ref _checked);
        if (difference is null)
        {
            return;
        }

        if (ReferenceEquals(difference, LabelsOnly))
        {
            Interlocked.Increment(ref _labelsOnly);
            return;
        }

        Interlocked.Increment(ref _mismatches);
        var kind = difference.Split(' ', 2)[0];
        if (Kinds.AddOrUpdate(kind, 1, static (_, count) => count + 1) <= LoggedPerKind)
        {
            Console.Error.WriteLine($"[PROGRAM_GATE_VERIFY][MISMATCH] stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} {(difference.Length > 1200 ? difference[..1200] : difference)}");
        }
    }

    internal static void NoteError(ShaderSource source, Exception exception)
    {
        Interlocked.Increment(ref _errors);
        if (Interlocked.Increment(ref _loggedErrors) <= LoggedErrors)
        {
            Console.Error.WriteLine($"[PROGRAM_GATE_VERIFY][ERROR] stage={source.Label} hash=0x{source.Hash:X16} {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Report()
    {
        for (;;)
        {
            Thread.Sleep(20000);
            var kinds = string.Join(",", Kinds.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}:{pair.Value}"));
            Console.Error.WriteLine(
                $"[PROGRAM_GATE_VERIFY] checked={Interlocked.Read(ref _checked)} mismatches={Interlocked.Read(ref _mismatches)} " +
                $"errors={Interlocked.Read(ref _errors)} labels_only={Interlocked.Read(ref _labelsOnly)} kinds=[{kinds}]");
        }
    }
}
