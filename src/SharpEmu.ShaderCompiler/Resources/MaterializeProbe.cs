// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// [local] SHARPEMU_DRAWCOST_PROBE: laps and counts inside the materialization program for the probe, which lives in
// SharpEmu.Libs (Diagnostics.DrawCostProbe) and installs the hooks when it times its first item. The probe keeps only
// its own thread's laps, so the front's materializations call the hooks and are dropped there. Without the probe each
// site is one static readonly test the JIT folds away.
//
// Laps (each is the time since the lap before it): Setup = from the program resolution's materialization lap to the
// program's first level (MaterializeFast's entry, the output and scratch, the branch selection, the constants); Reads
// = a level's guest reads (ReadLevel); Steps = a level's scalar steps; Emit = the output words, the table and ranges,
// the descriptor normalizers and image checks (Emit and the tail of EvaluateInto); Output = MaterializeFromOutput (the
// specialization memo search, the snapshot's arrays). A revalidation of a kept program (RevalidateFast) laps Setup,
// Reads and Steps too.
//
// Counts, per level read: Runs/RunWords = the reads of adjacent words as one resident read and their words, RunRefused
// = the runs the resident reader refused (their words then went one by one), Words = the words read one by one
// (singles and refused runs). Spans/SpanWords/SpanRefused = SHARPEMU_PAGE_SPAN_READS's span reads, their words and the
// spans refused; SpanCallsSaved = the reader calls the read spans replaced (units - 1 each); SpanCallsPossible = with
// the switch off, the calls spans would have saved at the configured span size.
public static class MaterializeProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_DRAWCOST_PROBE") == "1";

    public enum Step
    {
        Setup,
        Reads,
        Steps,
        Emit,
        Output,
    }

    public enum Counter
    {
        Runs,
        RunWords,
        RunRefused,
        Words,
        Spans,
        SpanWords,
        SpanRefused,
        SpanCallsSaved,
        SpanCallsPossible,
    }

    public static Action<Step>? LapHook;
    public static Action<Counter, long>? CountHook;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Lap(Step step)
    {
        if (Enabled)
        {
            LapHook?.Invoke(step);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Count(Counter counter, long amount = 1)
    {
        if (Enabled)
        {
            CountHook?.Invoke(counter, amount);
        }
    }
}
