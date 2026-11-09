// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_ATTACH_REPEAT (1 by default; =0 turns it off): a draw whose resolved targets are byte for byte those of the
// previous draw, while the previous draw's rendering scope is still open in the same tick and no barrier changed an
// attachment's backing since (IRenderHost.TryRepeatAttachments), takes the previous acquisition: the same images, views,
// layouts and rendering state, with only the bookkeeping replayed. Acquisitions that consumed a metadata clear or
// clear a depth target are never repeated. =2 runs the full acquisition on every repeat it would take and counts
// the draws whose outputs differ or whose full path changed the image state.
// [local] Ported from RenderExecutor.AttachRepeat (3368666f) to the recorder after the upstream merge: upstream's in-place
// memo (RepeatsPreviousTargets) keys on the context and dynamic register stamps, which GTA V moves between most draws,
// so it repeats about half the acquisitions this one does (DrawAttachmentPreparation 584 vs 270 per frame). The render
// thread's recorder only: a worker's host never repeats.
internal sealed partial class RenderRecorder
{
    private static readonly int AttachRepeatMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ATTACH_REPEAT"), out var attachRepeat) ? attachRepeat : 1;

    private sealed class AttachRecord
    {
        public bool Valid;
        public uint ColorCount;
        public readonly ColorTargetState[] Inputs = new ColorTargetState[ContextRegisters.ColorTargetCount];
        public readonly ColorTargetState[] Outputs = new ColorTargetState[ContextRegisters.ColorTargetCount];
        public readonly ImageLayout[] ColorLayouts = new ImageLayout[ContextRegisters.ColorTargetCount];
        public DepthAttachmentState DepthInput;
        public DepthAttachmentState DepthOutput;
        public ImageLayout DepthLayout;
        public RenderingState Rendering;
        public (ulong Tick, long BeginCalls) Token;
        public long Registrations;
        public readonly (object? Backing, long Version)[] Versions = new (object?, long)[ContextRegisters.ColorTargetCount + 1];
    }

    private readonly AttachRecord _attachRecord = new();

    // [local] SHARPEMU_ATTACH_REPEAT_INPLACE (on by default, =0 off): an in-place draw (upstream's TryRestoreAttachments)
    // with the record's rendering state keeps the record's chain. Its BeginRendering, a no-op on the open scope, counted
    // as another draw's and refused the next repeat (ag02: 85-87 % of the host refusals, ~20 % of the acquisitions). The
    // token moves only when the chain was intact up to this draw; the next repeat still checks the scope, the rendering
    // state and the attachments' state versions, and =2 still compares it with the full acquisition (ag03: repeats
    // 57 -> 76 %, 0 mismatches; ag04-ag07: draw_attachments -0.2 us per draw).
    private static readonly bool AttachRepeatInPlace = Environment.GetEnvironmentVariable("SHARPEMU_ATTACH_REPEAT_INPLACE") != "0";

    private void NoteInPlaceAttachments(in RenderingState rendering)
    {
        if (!AttachRepeatInPlace || AttachRepeatMode == 0 || OnWorker)
            return;
        var record = _attachRecord;
        if (!record.Valid || record.Rendering != rendering)
            return;
        var token = _host.AttachmentRepeatToken;
        if (token.Tick == record.Token.Tick && token.BeginCalls == record.Token.BeginCalls + 1)
            record.Token = token;
    }
    // [local] No record to repeat (the last acquisition had clears), and targets that differ from the record.
    internal static readonly long[] InputMisses = new long[4]; // no record, other targets, image registrations moved, host refused
    public static Func<string>? HostMissReport { get; set; }
    private static long _attachDraws, _attachWindow = System.Diagnostics.Stopwatch.GetTimestamp();
    // [local] SHARPEMU_ATTACH_REPORT_TICK (on by default, =0 off): the per-draw check for the 5 s report reads the
    // millisecond tick count instead of the performance counter; the window is still timed with the counter.
    private static readonly bool AttachReportTick = Environment.GetEnvironmentVariable("SHARPEMU_ATTACH_REPORT_TICK") != "0";
    private static long _attachWindowTick = Environment.TickCount64;

    private static void ReportAttachRepeat()
    {
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
            $"[ATTACH_REPEAT] window_s={seconds:F1} draws={_attachDraws} repeats={AttachRepeatHits} ({100.0 * AttachRepeatHits / Math.Max(1, _attachDraws):F1}%) checked={AttachRepeatChecked} mismatches={AttachRepeatMismatches} mismatches_total={AttachRepeatMismatchTotal} generation={GuestImageCache.StateGeneration} no_record={InputMisses[0]} other_targets={InputMisses[1]} registrations={InputMisses[2]} host_refused={InputMisses[3]} registration_generation={GuestImageCache.RegistrationGeneration} host=[{HostMissReport?.Invoke()}]"));
        Array.Clear(InputMisses);
        _attachDraws = AttachRepeatHits = AttachRepeatChecked = AttachRepeatMismatches = 0;
        _attachWindow = now;
    }
    internal static long AttachRepeatHits, AttachRepeatChecked, AttachRepeatMismatches, AttachRepeatMismatchTotal;

    // Raw bytes, references included: equal bytes mean the same values and the same objects.
    private static bool SameBytes<T>(in T left, in T right)
    {
        var size = Unsafe.SizeOf<T>();
        var a = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in left)), size);
        var b = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in right)), size);
        return a.SequenceEqual(b);
    }

    private bool SameAttachmentInputs(ref DrawState state)
    {
        var record = _attachRecord;
        if (!record.Valid || record.ColorCount != state.ColorCount || state.Depth.HasTarget != record.DepthInput.HasTarget)
            return false;
        for (var index = 0; index < state.ColorCount; index++)
        {
            if (!SameBytes(in state.Colors[index], in record.Inputs[index]))
                return false;
        }

        return !state.Depth.HasTarget || SameBytes(in state.Depth, in record.DepthInput);
    }

    private RenderingState AcquireOrRepeatAttachments(ref DrawState state)
    {
        if (AttachRepeatMode == 0 || OnWorker)
            return AcquireAttachments(ref state);
        ReportAttachRepeat();
        var record = _attachRecord;
        var sameInputs = SameAttachmentInputs(ref state); // [local] compared once; false without a record
        if (!record.Valid) InputMisses[0]++;
        else if (!sameInputs) InputMisses[1]++;
        else if (record.Registrations != GuestImageCache.RegistrationGeneration) InputMisses[2]++;
        var repeat = sameInputs && record.Registrations == GuestImageCache.RegistrationGeneration && _host.TryRepeatAttachments(
            ((ReadOnlySpan<ColorTargetState>)record.Outputs)[..(int)record.ColorCount],
            ((ReadOnlySpan<ImageLayout>)record.ColorLayouts)[..(int)record.ColorCount],
            in record.DepthInput, record.DepthLayout, in record.Rendering, record.Token,
            ((ReadOnlySpan<(object? Backing, long Version)>)record.Versions)[..((int)record.ColorCount + 1)], apply: AttachRepeatMode != 2);
        if (sameInputs && !repeat && record.Registrations == GuestImageCache.RegistrationGeneration) InputMisses[3]++;
        if (repeat && AttachRepeatMode != 2)
        {
            AttachRepeatHits++;
            for (var index = 0; index < state.ColorCount; index++)
                state.Colors[index] = record.Outputs[index];
            if (state.Depth.HasTarget)
                state.Depth = record.DepthOutput;
            record.Token = _host.AttachmentRepeatToken;
            return record.Rendering;
        }

        var inputs = record.Inputs;
        var count = state.ColorCount;
        for (var index = 0; index < count; index++)
            inputs[index] = state.Colors[index];
        var depthInput = state.Depth;
        var rendering = AcquireAttachments(ref state);
        if (repeat)
        {
            AttachRepeatHits++;
            AttachRepeatChecked++;
            // The full path must have left every attachment's access state as the repeat would have.
            var versionsKept = true;
            for (var index = 0; versionsKept && index < count; index++)
                versionsKept = _host.AttachmentStateVersion(state.Colors[index].Image) == record.Versions[index];
            if (versionsKept && state.Depth.HasTarget)
                versionsKept = _host.AttachmentStateVersion(state.Depth.Image) == record.Versions[count];
            var same = versionsKept && rendering == record.Rendering &&
                (!state.Depth.HasTarget || SameBytes(in state.Depth, in record.DepthOutput));
            for (var index = 0; same && index < count; index++)
                same = SameBytes(in state.Colors[index], in record.Outputs[index]);
            if (!same)
            {
                AttachRepeatMismatches++;
                if (++AttachRepeatMismatchTotal <= 20)
                    Console.Error.WriteLine($"[ATTACH_REPEAT_MISMATCH] total={AttachRepeatMismatchTotal} versions_kept={versionsKept} rendering={rendering == record.Rendering}");
            }
        }

        // Only an acquisition without clears can be taken again.
        var clears = state.Depth.HasTarget && (state.Depth.MetadataClear || state.Depth.LoadClear || state.Depth.Target.Target.DepthClearEnabled);
        for (var index = 0; !clears && index < count; index++)
            clears = rendering.ColorAttachments[index].IsClear;
        record.Valid = !clears;
        if (!record.Valid)
            return rendering;
        record.ColorCount = count;
        for (var index = 0; index < count; index++)
        {
            record.Outputs[index] = state.Colors[index];
            record.ColorLayouts[index] = ImageLayout.General; // [local] upstream keeps every attachment in General
        }

        record.DepthInput = depthInput;
        record.DepthOutput = state.Depth;
        record.DepthLayout = state.Depth.HasTarget ? ImageLayout.General : default;
        record.Rendering = rendering;
        for (var index = 0; index < count; index++)
            record.Versions[index] = _host.AttachmentStateVersion(state.Colors[index].Image);
        record.Versions[count] = state.Depth.HasTarget ? _host.AttachmentStateVersion(state.Depth.Image) : default;
        record.Token = _host.AttachmentRepeatToken;
        record.Registrations = GuestImageCache.RegistrationGeneration;
        return rendering;
    }
}
