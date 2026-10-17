// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_ATTACH_PROBE=1: how many draws acquire exactly the attachments of the draw before (same color
// images, slots, views and extents, same depth image, view and load state) and what AcquireAttachments costs
// them, against the draws whose attachments changed. Render thread only; printed every 5 s.
internal static class AttachProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_ATTACH_PROBE") == "1";

    private const int MaxColors = 8;
    private static readonly ColorTargetState[] PreviousColors = new ColorTargetState[MaxColors];
    private static uint _previousColorCount = uint.MaxValue;
    private static DepthAttachmentState _previousDepth;
    private static bool _previousHasDepth;
    private static long _draws, _same, _ticks, _sameTicks, _frames;
    private static long _window = Stopwatch.GetTimestamp();

    public static void NoteFrame() => _frames++;

    public static bool SameAsPrevious(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth)
    {
        var same = (uint)colors.Length == _previousColorCount && depth.HasTarget == _previousHasDepth;
        for (var index = 0; same && index < (uint)colors.Length; index++)
        {
            var now = colors[index];
            ref var was = ref PreviousColors[index];
            same = now.Image == was.Image && now.Slot == was.Slot &&
                EqualityComparer<SharpEmu.Libs.Gpu.Images.ImageViewDescription>.Default.Equals(now.Resolution.Request.View, was.Resolution.Request.View) &&
                now.Resolution.Extent.Width == was.Resolution.Extent.Width && now.Resolution.Extent.Height == was.Resolution.Extent.Height;
        }

        if (same && depth.HasTarget)
        {
            same = depth.Image == _previousDepth.Image &&
                EqualityComparer<SharpEmu.Libs.Gpu.Images.ImageViewDescription>.Default.Equals(depth.Target.Target.Request.View, _previousDepth.Target.Target.Request.View) &&
                depth.LoadState.Equals(_previousDepth.LoadState);
        }

        _previousColorCount = (uint)colors.Length;
        for (var index = 0; index < (uint)colors.Length && index < MaxColors; index++)
            PreviousColors[index] = colors[index];
        _previousHasDepth = depth.HasTarget;
        _previousDepth = depth;
        return same;
    }

    public static void Note(bool same, long ticks)
    {
        _draws++;
        _ticks += ticks;
        if (same)
        {
            _same++;
            _sameTicks += ticks;
        }

        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _window) / (double)Stopwatch.Frequency;
        if (seconds < 5)
            return;
        double Ms(long value) => value * 1000.0 / Stopwatch.Frequency / seconds;
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[ATTACH_PROBE] window_s={seconds:F1} draws_per_s={_draws / seconds:F0} same={100.0 * _same / Math.Max(1, _draws):F1}% acquire_ms_per_s={Ms(_ticks):F2} same_ms_per_s={Ms(_sameTicks):F2} us_per_draw={(_ticks * 1e6 / Stopwatch.Frequency) / Math.Max(1, _draws):F2} us_per_same={(_sameTicks * 1e6 / Stopwatch.Frequency) / Math.Max(1, _same):F2}"));
        _draws = _same = _ticks = _sameTicks = 0;
        _window = now;
    }
}
