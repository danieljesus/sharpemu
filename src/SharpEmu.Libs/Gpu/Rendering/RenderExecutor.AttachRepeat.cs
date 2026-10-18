// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_ATTACH_REPEAT (0 off by default): a draw whose resolved targets are byte for byte those of the
// previous draw, while the previous draw's rendering scope is still open in the same tick and no image-state event
// happened since (IRenderHost.TryRepeatAttachments), takes the previous acquisition: the same images, views,
// layouts and rendering state, with only the bookkeeping replayed. Acquisitions that consumed a metadata clear or
// clear a depth target are never repeated. =2 runs the full acquisition on every repeat it would take and counts
// the draws whose outputs differ or whose full path changed the image state.
public sealed partial class RenderExecutor
{
    private static readonly int AttachRepeatMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ATTACH_REPEAT"), out var attachRepeat) ? attachRepeat : 0;

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
        public long Generation;
    }

    private readonly AttachRecord _attachRecord = new();
    private static long _attachDraws, _attachWindow = System.Diagnostics.Stopwatch.GetTimestamp();

    private static void ReportAttachRepeat()
    {
        _attachDraws++;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var seconds = (now - _attachWindow) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (seconds < 5)
            return;
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[ATTACH_REPEAT] window_s={seconds:F1} draws={_attachDraws} repeats={AttachRepeatHits} ({100.0 * AttachRepeatHits / Math.Max(1, _attachDraws):F1}%) checked={AttachRepeatChecked} mismatches={AttachRepeatMismatches} mismatches_total={AttachRepeatMismatchTotal} generation={GuestImageCache.StateGeneration}"));
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
        if (AttachRepeatMode == 0)
            return AcquireAttachments(ref state);
        ReportAttachRepeat();
        var record = _attachRecord;
        var repeat = SameAttachmentInputs(ref state) && _host.TryRepeatAttachments(
            ((ReadOnlySpan<ColorTargetState>)record.Outputs)[..(int)record.ColorCount],
            ((ReadOnlySpan<ImageLayout>)record.ColorLayouts)[..(int)record.ColorCount],
            in record.DepthInput, record.DepthLayout, in record.Rendering, record.Token, record.Generation, apply: AttachRepeatMode != 2);
        if (repeat && AttachRepeatMode != 2)
        {
            AttachRepeatHits++;
            for (var index = 0; index < state.ColorCount; index++)
                state.Colors[index] = record.Outputs[index];
            if (state.Depth.HasTarget)
                state.Depth = record.DepthOutput;
            record.Token = _host.AttachmentRepeatToken;
            record.Generation = GuestImageCache.StateGeneration;
            return record.Rendering;
        }

        var inputs = record.Inputs;
        var count = state.ColorCount;
        for (var index = 0; index < count; index++)
            inputs[index] = state.Colors[index];
        var depthInput = state.Depth;
        var generationBefore = GuestImageCache.StateGeneration;
        var rendering = AcquireAttachments(ref state);
        var generationAfter = GuestImageCache.StateGeneration;
        if (repeat)
        {
            AttachRepeatHits++;
            AttachRepeatChecked++;
            var same = generationBefore == generationAfter && rendering == record.Rendering &&
                (!state.Depth.HasTarget || SameBytes(in state.Depth, in record.DepthOutput));
            for (var index = 0; same && index < count; index++)
                same = SameBytes(in state.Colors[index], in record.Outputs[index]);
            if (!same)
            {
                AttachRepeatMismatches++;
                if (++AttachRepeatMismatchTotal <= 20)
                    Console.Error.WriteLine($"[ATTACH_REPEAT_MISMATCH] total={AttachRepeatMismatchTotal} generation={generationBefore}->{generationAfter} rendering={rendering == record.Rendering}");
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
            record.ColorLayouts[index] = rendering.ColorAttachments[index].Layout;
        }

        record.DepthInput = depthInput;
        record.DepthOutput = state.Depth;
        record.DepthLayout = state.Depth.HasTarget ? rendering.DepthStencilAttachment.Layout : default;
        record.Rendering = rendering;
        record.Token = _host.AttachmentRepeatToken;
        record.Generation = generationAfter;
        return rendering;
    }
}
