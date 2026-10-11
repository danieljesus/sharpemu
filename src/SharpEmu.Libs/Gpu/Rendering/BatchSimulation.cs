// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_BATCH_SIM=1: a dry run of batched recording (P0 in transiciones-diferidas.md). It records nothing
// differently; it replays each draw's image uses against the batch it would join and reports how large the batches
// would be if transitions were decided per batch: a batch is the run of draws with one rendering state, cut by a
// layout change or repeated write on an image the batch already used (C1/C3), a draw that writes memory (C7) or a
// dispatch, clear, resolve or global barrier (C9). Buffer uploads (C6) are counted both as cuts (pessimistic) and as
// harmless (optimistic). Render thread only.
internal static class BatchSimulation
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_BATCH_SIM") == "1";

    private readonly record struct ImageUse(ulong Image, bool Cuts);

    private sealed class Simulation(bool uploadsCut)
    {
        public readonly bool UploadsCut = uploadsCut;
        public readonly HashSet<ulong> Touched = new();
        public RenderingState State;
        public bool Open;
        public bool CutNext;
        public int Draws;
        public readonly long[] Sizes = new long[5];
        public readonly long[] DrawsBySize = new long[5];
        public long Layout, Upload, Writes, Hard, Scope;
    }

    private static readonly List<ImageUse> DrawUses = new();
    private static bool _drawUploaded;
    private static readonly Simulation Pessimistic = new(uploadsCut: true);
    private static readonly Simulation Optimistic = new(uploadsCut: false);
    private static long _reportStarted = Stopwatch.GetTimestamp();

    // One image use: whether its barriers would break a batch that already used the image.
    public static void NoteImage(ulong image, List<ImageMemoryBarrier2> barriers, bool attachmentOnly)
    {
        var cuts = false;
        if (barriers.Count != 0 && !attachmentOnly)
        {
            cuts = true;
        }

        DrawUses.Add(new ImageUse(image, cuts));
    }

    public static void NoteUpload() => _drawUploaded = true;

    // The draw is recorded with this rendering state: decide whether it joins the open batch.
    public static void NoteDraw(in RenderingState state)
    {
        Place(Pessimistic, in state);
        Place(Optimistic, in state);
        DrawUses.Clear();
        _drawUploaded = false;
        MaybeReport();
    }

    public static void CutAfterDraw()
    {
        Pessimistic.CutNext = true;
        Optimistic.CutNext = true;
    }

    public static void HardCut()
    {
        Close(Pessimistic);
        Close(Optimistic);
        Pessimistic.Hard++;
        Optimistic.Hard++;
        DrawUses.Clear();
        _drawUploaded = false;
    }

    private static void Place(Simulation sim, in RenderingState state)
    {
        var cut = false;
        if (!sim.Open || sim.State != state)
        {
            if (sim.Open) sim.Scope++;
            cut = true;
        }
        else if (sim.CutNext)
        {
            sim.Writes++;
            cut = true;
        }
        else if (sim.UploadsCut && _drawUploaded)
        {
            sim.Upload++;
            cut = true;
        }
        else
        {
            foreach (var use in DrawUses)
            {
                if (use.Cuts && sim.Touched.Contains(use.Image))
                {
                    sim.Layout++;
                    cut = true;
                    break;
                }
            }
        }

        if (cut)
        {
            Close(sim);
            sim.Open = true;
            sim.State = state;
        }

        sim.CutNext = false;
        foreach (var use in DrawUses)
        {
            sim.Touched.Add(use.Image);
        }

        sim.Draws++;
    }

    private static void Close(Simulation sim)
    {
        if (sim.Open && sim.Draws != 0)
        {
            var bucket = sim.Draws switch { 1 => 0, < 4 => 1, < 8 => 2, < 16 => 3, _ => 4 };
            sim.Sizes[bucket]++;
            sim.DrawsBySize[bucket] += sim.Draws;
        }

        sim.Open = false;
        sim.Draws = 0;
        sim.Touched.Clear();
    }

    private static void MaybeReport()
    {
        if (Stopwatch.GetElapsedTime(_reportStarted).TotalSeconds < 5)
        {
            return;
        }

        _reportStarted = Stopwatch.GetTimestamp();
        Report("pessimistic", Pessimistic);
        Report("optimistic", Optimistic);
    }

    private static void Report(string name, Simulation sim)
    {
        var draws = sim.DrawsBySize.Sum();
        if (draws == 0)
        {
            return;
        }

        Console.Error.WriteLine(FormattableString.Invariant(
            $"[BATCH_SIM] {name} batches={sim.Sizes.Sum()} draws={draws} draws_in_1={100.0 * sim.DrawsBySize[0] / draws:F1}% 2-3={100.0 * sim.DrawsBySize[1] / draws:F1}% 4-7={100.0 * sim.DrawsBySize[2] / draws:F1}% 8-15={100.0 * sim.DrawsBySize[3] / draws:F1}% 16+={100.0 * sim.DrawsBySize[4] / draws:F1}% cuts_scope={sim.Scope} cuts_layout={sim.Layout} cuts_upload={sim.Upload} cuts_writes={sim.Writes} cuts_hard={sim.Hard}"));
        Array.Clear(sim.Sizes);
        Array.Clear(sim.DrawsBySize);
        sim.Scope = sim.Layout = sim.Upload = sim.Writes = sim.Hard = 0;
    }
}
