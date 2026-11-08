// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_EARLYOUT_PROBE=1: how many fully resolved draws the GPU would discard whole: an empty scissor, a
// zero-area viewport, or fewer vertices than one primitive of the topology. Those that do not write memory from the
// vertex stage (a pixel stage does not run for them) could return before target and program resolution with the same
// result. Printed every 5 s; the draws per second give the share of the ~19 us a draw costs that an early exit saves.
internal static class EarlyOutProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_EARLYOUT_PROBE") == "1";

    private static long _draws, _scissor, _viewport, _short, _skippable, _vertexWrites;
    private static long _window = Stopwatch.GetTimestamp();

    public static void NoteDraw(in DynamicDrawState dynamic, uint count, PrimitiveTopology topology, bool vertexWrites)
    {
        Interlocked.Increment(ref _draws);
        var emptyScissor = !dynamic.Scissor.IsValid;
        var emptyViewport = dynamic.ViewportWidth == 0f || dynamic.ViewportHeight == 0f;
        var tooShort = count < MinimumVertices(topology);
        if (emptyScissor)
            Interlocked.Increment(ref _scissor);
        if (emptyViewport)
            Interlocked.Increment(ref _viewport);
        if (tooShort)
            Interlocked.Increment(ref _short);
        if (emptyScissor || emptyViewport || tooShort)
            Interlocked.Increment(ref vertexWrites ? ref _vertexWrites : ref _skippable);
        Report();
    }

    internal static uint MinimumVertices(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => 1,
        PrimitiveTopology.LineList or PrimitiveTopology.LineStrip => 2,
        PrimitiveTopology.TriangleList or PrimitiveTopology.TriangleStrip or PrimitiveTopology.TriangleFan => 3,
        _ => 1,
    };

    private static void Report()
    {
        var now = Stopwatch.GetTimestamp();
        var started = Volatile.Read(ref _window);
        var seconds = (now - started) / (double)Stopwatch.Frequency;
        if (seconds < 5 || Interlocked.CompareExchange(ref _window, now, started) != started)
            return;
        var draws = Interlocked.Exchange(ref _draws, 0);
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[EARLYOUT_PROBE] window_s={seconds:F1} draws_per_s={draws / seconds:F0} empty_scissor={Interlocked.Exchange(ref _scissor, 0)} zero_viewport={Interlocked.Exchange(ref _viewport, 0)} too_short={Interlocked.Exchange(ref _short, 0)} skippable={Interlocked.Exchange(ref _skippable, 0)} vertex_writes={Interlocked.Exchange(ref _vertexWrites, 0)} draws={draws}"));
    }
}
