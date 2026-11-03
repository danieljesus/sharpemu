// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_ATTACH_REPEAT (1 by default; =0 turns it off; =2 verifies) used to let a draw whose resolved targets
// were byte for byte those of the previous draw take the previous acquisition (IRenderHost.TryRepeatAttachments).
// [local] Superseded by upstream's RenderRecorder in-place draws (InPlaceDraw / TryRestoreAttachments, #1082): the
// executor now only resolves, attachments are acquired by RenderRecorder, and RenderingAttachment no longer carries
// the layouts the repeat record kept. The switches are still read but inert: RenderRecorder does not call
// ReportAttachRepeat, since its repeats/mismatches would stay 0 (upstream's in_place counts, TakeInPlaceReport, replace it).
public sealed partial class RenderExecutor
{
    internal static readonly int AttachRepeatMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ATTACH_REPEAT"), out var attachRepeat) ? attachRepeat : 1;

    // [local] No record to repeat (the last acquisition had clears), and targets that differ from the record.
    internal static readonly long[] InputMisses = new long[2];
    public static Func<string>? HostMissReport { get; set; }
    private static long _attachDraws, _attachWindow = System.Diagnostics.Stopwatch.GetTimestamp();
    // [local] SHARPEMU_ATTACH_REPORT_TICK (on by default, =0 off): the per-draw check for the 5 s report reads the
    // millisecond tick count instead of the performance counter; the window is still timed with the counter.
    private static readonly bool AttachReportTick = Environment.GetEnvironmentVariable("SHARPEMU_ATTACH_REPORT_TICK") != "0";
    private static long _attachWindowTick = Environment.TickCount64;
    internal static long AttachRepeatHits, AttachRepeatChecked, AttachRepeatMismatches, AttachRepeatMismatchTotal;

    // Counts a draw for the 5 s [ATTACH_REPEAT] report; a recorder that acquires attachments may call it.
    internal static void ReportAttachRepeat()
    {
        if (AttachRepeatMode == 0)
            return;
        _attachDraws++;
        if (AttachReportTick)
        {
            if (Environment.TickCount64 - _attachWindowTick < 5000)
                return;
            _attachWindowTick = Environment.TickCount64;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = (now - _attachWindow) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (seconds < 5 && !AttachReportTick)
            return;
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[ATTACH_REPEAT] window_s={seconds:F1} draws={_attachDraws} repeats={AttachRepeatHits} ({100.0 * AttachRepeatHits / Math.Max(1, _attachDraws):F1}%) checked={AttachRepeatChecked} mismatches={AttachRepeatMismatches} mismatches_total={AttachRepeatMismatchTotal} generation={GuestImageCache.StateGeneration} no_record={InputMisses[0]} other_targets={InputMisses[1]} host=[{HostMissReport?.Invoke()}]"));
        Array.Clear(InputMisses);
        _attachDraws = AttachRepeatHits = AttachRepeatChecked = AttachRepeatMismatches = 0;
        _attachWindow = now;
    }
}
