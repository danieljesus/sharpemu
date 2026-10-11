// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// [local] Programs whose resources the render thread materializes itself (the front's preparation did not reach it),
// by stage and program, reported with the render profile.
internal static class BackMaterializations
{
    private static readonly Dictionary<(ShaderStage Stage, ulong Hash), long> _counts = new();
    private static readonly object _gate = new();
    private static readonly bool StackLog = Environment.GetEnvironmentVariable("SHARPEMU_BACK_MATERIALIZE_STACKS") == "1";
    private static int _stacks;

    public static void Note(ShaderStage stage, ulong hash)
    {
        lock (_gate)
        {
            var count = _counts[(stage, hash)] = _counts.GetValueOrDefault((stage, hash)) + 1;
            if (StackLog && count == 50 && _stacks++ < 12)
            {
                Console.Error.WriteLine($"[BACK_MATERIALIZE_AT] {stage}:0x{hash:X16} at={new System.Diagnostics.StackTrace(2, false).ToString().Replace(Environment.NewLine, " | ")}");
            }
        }
    }

    public static string TakeReport()
    {
        lock (_gate)
        {
            var byStage = _counts.GroupBy(pair => pair.Key.Stage).Select(group => $"{group.Key}={group.Sum(pair => pair.Value)}");
            var top = _counts.OrderByDescending(pair => pair.Value).Take(8).Select(pair => $"{pair.Key.Stage}:0x{pair.Key.Hash:X16}={pair.Value}");
            var report = $"[PERF][BACK_MATERIALIZE] {string.Join(" ", byStage)} top: {string.Join(" ", top)}";
            _counts.Clear();
            return report;
        }
    }
}
