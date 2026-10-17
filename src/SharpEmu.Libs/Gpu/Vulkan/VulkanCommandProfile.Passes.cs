// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] SHARPEMU_PROFILE_GPU_PASSES=1 (sondas2, 2026-10-16): the GPU busy time of the guest command buffers split by
// what ran. On top of the per-draw markers of SHARPEMU_PROFILE_GPU_DRAWS (which it turns on):
//   - VkRecord's transfer commands (copy buffer/image, fill/update, blit, resolve, buffer->image upload, image->buffer
//     readback, clears) recorded into the profiled buffer get a marker before (kind Gap: what came since the last
//     marker) and after (their own kind), so their completion interval is their own;
//   - every interval carries the mask of the other commands recorded inside it (barriers, rendering begin/end with
//     clear or resolve attachments, draws and dispatches that are not the guest draw the interval ends at, i.e. the
//     emulator's own shader passes), so Preparation/Gap/Tail time splits into barrier-only waits, render-pass setup,
//     emulator shaders and the rest;
//   - every draw interval carries its pass: the attachments of the vkCmdBeginRendering it runs in (views, area), so the
//     time groups by guest pass and by pass shape (extent, colour count, depth: colour 0 + depth = depth-only, the
//     shadow and depth-prepass work).
// Intervals are bottom-of-pipe completion deltas (as GPU_DRAWS): overlapping work is charged to whichever command
// completes last. Lines every 5 s window, per presented frame: [PERF][GPU_PASSES] (categories), [PERF][GPU_PASSES_MASK]
// (mask classes), [PERF][GPU_PASSES_PIPE] (every pipeline >= 0.02 ms/frame), [PERF][GPU_PASSES_PASS] (top passes),
// [PERF][GPU_PASSES_SHAPE].
internal sealed unsafe partial class VulkanCommandProfile
{
    [Flags]
    internal enum OpBit : ushort
    {
        Barrier = 1,
        BeginRendering = 2,
        EndRendering = 4,
        Dispatch = 8,
        Draw = 16,
        RenderingClear = 32,
        RenderingResolve = 64,
        Transfer = 128,
        RenderPass = 256,
    }

    [ThreadStatic] private static VulkanCommandProfile? t_profile;
    [ThreadStatic] private static nint t_current;
    [ThreadStatic] private static ushort t_mask;
    [ThreadStatic] private static ulong t_pass;

    // [local] SHARPEMU_PROFILE_GPU_SITES=1 (with GPU_PASSES): which emulator code recorded the barriers, transfers and
    // emulator shaders of an interval. One interval in SiteSampleEvery is sampled: its first such command takes a
    // managed stack (the callers above VkRecord), and the interval's GPU time is charged to that site. Lines
    // [PERF][GPU_SITES] per category and site, ms per frame of the sampled intervals times SiteSampleEvery.
    internal static readonly bool SitesEnabled = PassesEnabled && Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GPU_SITES") == "1";
    internal const int SiteSampleEvery = 8;
    [ThreadStatic] private static int t_site;
    [ThreadStatic] private static bool t_siteSampled;
    [ThreadStatic] private static int t_siteCounter;
    private static readonly Dictionary<string, int> _siteIds = new();
    private static readonly List<string> _siteNames = ["unsampled"];
    private readonly Dictionary<(string Category, int Site), Sum> _sites = new();

    private static void NoteSite()
    {
        if (!SitesEnabled || !t_siteSampled || t_site != 0)
            return;
        var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
        var names = new List<string>(7);
        foreach (var frame in frames)
        {
            if (frame.GetMethod() is not { } method)
                continue;
            var type = method.DeclaringType?.Name ?? "?";
            if (names.Count == 0 && type is nameof(VkRecord) or nameof(VulkanCommandProfile))
                continue;
            names.Add($"{type}.{method.Name}");
            if (names.Count == 7)
                break;
        }

        var site = string.Join(" < ", names);
        lock (_siteIds)
        {
            if (!_siteIds.TryGetValue(site, out var id))
            {
                id = _siteNames.Count;
                _siteNames.Add(site);
                _siteIds.Add(site, id);
            }

            t_site = id;
        }
    }

    private static long _passFrames;

    internal static void CountFrame() => Interlocked.Increment(ref _passFrames);

    private readonly record struct PassShape(uint Width, uint Height, uint Colors, bool Depth);

    private sealed class PassTotal
    {
        public PassShape Shape;
        public double DrawMilliseconds;
        public double PreparationMilliseconds;
        public long Draws;
        public readonly Dictionary<ulong, double> Pipelines = new();
    }

    private sealed class Sum
    {
        public double Milliseconds;
        public long Count;
    }

    private readonly Dictionary<ulong, PassShape> _passShapes = new();
    private readonly Dictionary<ulong, PassTotal> _passTotals = new();
    private readonly Dictionary<string, Sum> _categories = new();
    private readonly Dictionary<(IntervalKind Kind, ushort Mask), Sum> _masks = new();
    private readonly Dictionary<(IntervalKind Kind, ulong Pipeline), Sum> _pipelines = new();
    private readonly Dictionary<PassShape, Sum> _shapes = new();

    private void StartPassBuffer(CommandBuffer command)
    {
        t_profile = this;
        t_current = command.Handle;
        t_mask = 0;
        t_pass = 0;
    }

    private Marker TakePassMarker(CommandBuffer command, IntervalKey key, uint first, uint second, uint third)
    {
        var mask = t_current == command.Handle ? t_mask : (ushort)0;
        if (t_current == command.Handle)
            t_mask = 0;
        var marker = new Marker(key, first, second, third, mask, t_current == command.Handle ? t_pass : 0, t_site);
        if (SitesEnabled)
        {
            t_site = 0;
            t_siteSampled = ++t_siteCounter % SiteSampleEvery == 0;
        }

        if (key.Kind == IntervalKind.Tail && t_current == command.Handle)
        {
            t_current = 0;
            t_profile = null;
        }

        return marker;
    }

    // [local] Commands recorded so far on this thread (barriers, draws, dispatches, transfers, rendering begin/end), with
    // GPU_PASSES: two command-processor writes with the same count in between have nothing recorded between them.
    [ThreadStatic] internal static long t_opSequence;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NoteOp(CommandBuffer command, OpBit bit)
    {
        t_opSequence++;
        if (Buffers.CpWriteDeferProbe.Enabled && (bit & (OpBit.Barrier | OpBit.Dispatch | OpBit.Transfer)) != 0) Buffers.CpWriteDeferProbe.OnOtherOp(); // [local]
        if (Buffers.CpWriteDeferProbe.Enabled && bit == OpBit.Barrier) Buffers.CpWriteDeferProbe.OnBarrier(); // [local]
        if (Buffers.CpWriteDeferProbe.Enabled && (bit & (OpBit.Draw | OpBit.Dispatch)) != 0) Buffers.CpWriteDeferProbe.OnCommand(); // [local]
        if (t_current == command.Handle && command.Handle != 0)
        {
            t_mask |= (ushort)bit;
            if (SitesEnabled && (bit & (OpBit.Barrier | OpBit.Dispatch | OpBit.Draw)) != 0)
                NoteSite();
        }
    }

    // Before a transfer command: closes what the buffer recorded since the last marker.
    internal static void BeforeTransfer(CommandBuffer command)
    {
        t_opSequence++;
        if (Buffers.CpWriteDeferProbe.Enabled) Buffers.CpWriteDeferProbe.OnOtherOp(); // [local]
        if (t_current == command.Handle && command.Handle != 0 && t_profile is { } profile)
        {
            profile.WriteMarker(command, IntervalKind.Gap);
            NoteSite();
        }
    }

    // After a transfer command: its own interval.
    internal static void AfterTransfer(CommandBuffer command, IntervalKind kind)
    {
        if (t_current == command.Handle && command.Handle != 0 && t_profile is { } profile)
        {
            t_mask |= (ushort)OpBit.Transfer;
            profile.WriteMarker(command, kind);
        }
    }

    internal static void NoteRendering(CommandBuffer command, RenderingInfo* info)
    {
        t_opSequence++;
        if (t_current != command.Handle || command.Handle == 0 || t_profile is not { } profile)
            return;
        var bits = OpBit.BeginRendering;
        var signature = 0xCBF29CE484222325UL;
        void Mix(ulong value) => signature = (signature ^ value) * 0x100000001B3UL;
        Mix(info->RenderArea.Extent.Width | ((ulong)info->RenderArea.Extent.Height << 32));
        Mix(info->LayerCount);
        for (var index = 0u; index < info->ColorAttachmentCount; index++)
        {
            var attachment = info->PColorAttachments[index];
            Mix((ulong)attachment.ImageView.Handle);
            if (attachment.LoadOp == AttachmentLoadOp.Clear)
                bits |= OpBit.RenderingClear;
            if ((uint)attachment.ResolveMode != 0)
                bits |= OpBit.RenderingResolve;
        }

        var depth = info->PDepthAttachment != null && info->PDepthAttachment->ImageView.Handle != 0;
        if (depth)
        {
            Mix((ulong)info->PDepthAttachment->ImageView.Handle ^ 0xD0D0UL);
            if (info->PDepthAttachment->LoadOp == AttachmentLoadOp.Clear)
                bits |= OpBit.RenderingClear;
            if ((uint)info->PDepthAttachment->ResolveMode != 0)
                bits |= OpBit.RenderingResolve;
        }

        if (info->PStencilAttachment != null && info->PStencilAttachment->LoadOp == AttachmentLoadOp.Clear)
            bits |= OpBit.RenderingClear;
        t_mask |= (ushort)bits;
        t_pass = signature;
        profile._passShapes.TryAdd(signature, new PassShape(info->RenderArea.Extent.Width, info->RenderArea.Extent.Height, info->ColorAttachmentCount, depth));
    }

    internal static void NoteEndRendering(CommandBuffer command)
    {
        t_opSequence++;
        if (t_current == command.Handle && command.Handle != 0)
        {
            t_mask |= (ushort)OpBit.EndRendering;
            t_pass = 0;
        }
    }

    // The class of a Preparation/Gap/Tail interval from the commands recorded inside it, by priority.
    private static string MaskClass(ushort mask)
    {
        var bits = (OpBit)mask;
        if ((bits & (OpBit.Dispatch | OpBit.Draw)) != 0)
            return "emu_shader";
        if ((bits & OpBit.RenderingResolve) != 0)
            return "rendering_resolve";
        if ((bits & OpBit.RenderingClear) != 0)
            return "rendering_clear";
        if ((bits & (OpBit.BeginRendering | OpBit.EndRendering | OpBit.RenderPass)) != 0)
            return (bits & OpBit.Barrier) != 0 ? "rendering_barrier" : "rendering";
        if ((bits & OpBit.Barrier) != 0)
            return "barrier";
        if ((bits & OpBit.Transfer) != 0)
            return "after_transfer";
        return "bind";
    }

    private static void Add<TKey>(Dictionary<TKey, Sum> sums, TKey key, double milliseconds) where TKey : notnull
    {
        if (!sums.TryGetValue(key, out var sum))
            sums.Add(key, sum = new Sum());
        sum.Milliseconds += milliseconds;
        sum.Count++;
    }

    private void AccumulatePass(in Marker marker, double milliseconds)
    {
        var kind = marker.Key.Kind;
        if (SitesEnabled && marker.Site != 0 && kind is not (IntervalKind.Draw or IntervalKind.DrawIndexed or IntervalKind.Dispatch))
        {
            var category = kind switch
            {
                IntervalKind.Preparation => "prep_" + MaskClass(marker.Mask),
                IntervalKind.Gap => "gap_" + MaskClass(marker.Mask),
                IntervalKind.Tail => "tail_" + MaskClass(marker.Mask),
                _ => "op_" + kind.ToString().ToLowerInvariant(),
            };
            Add(_sites, (category, marker.Site), milliseconds);
        }

        switch (kind)
        {
            case IntervalKind.Draw:
            case IntervalKind.DrawIndexed:
            {
                _passShapes.TryGetValue(marker.Pass, out var shape);
                var depthOnly = shape.Colors == 0 && shape.Depth;
                Add(_categories, depthOnly ? "draw_depth_only" : "draw_color", milliseconds);
                Add(_pipelines, (IntervalKind.Draw, marker.Key.Pipeline), milliseconds);
                Add(_shapes, shape, milliseconds);
                if (!_passTotals.TryGetValue(marker.Pass, out var pass))
                {
                    if (_passTotals.Count >= 1024)
                        break;
                    _passTotals.Add(marker.Pass, pass = new PassTotal { Shape = shape });
                }

                pass.DrawMilliseconds += milliseconds;
                pass.Draws++;
                pass.Pipelines[marker.Key.Pipeline] = pass.Pipelines.GetValueOrDefault(marker.Key.Pipeline) + milliseconds;
                break;
            }
            case IntervalKind.Dispatch:
                Add(_categories, "dispatch", milliseconds);
                Add(_pipelines, (IntervalKind.Dispatch, marker.Key.Pipeline), milliseconds);
                break;
            case IntervalKind.Preparation:
            case IntervalKind.Gap:
            case IntervalKind.Tail:
            {
                var prefix = kind == IntervalKind.Preparation ? "prep_" : kind == IntervalKind.Gap ? "gap_" : "tail_";
                Add(_categories, prefix + MaskClass(marker.Mask), milliseconds);
                Add(_masks, (kind, marker.Mask), milliseconds);
                if (kind == IntervalKind.Preparation && marker.Pass != 0 && _passTotals.TryGetValue(marker.Pass, out var pass))
                    pass.PreparationMilliseconds += milliseconds;
                break;
            }
            default:
                Add(_categories, "op_" + kind.ToString().ToLowerInvariant(), milliseconds);
                break;
        }
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private void ReportPasses(double windowSeconds, double busyMilliseconds, int submissions)
    {
        var frames = Interlocked.Exchange(ref _passFrames, 0);
        var f = (double)Math.Max(frames, 1);
        var total = _categories.Values.Sum(static sum => sum.Milliseconds);
        if (RunEnabled) { _write(TakeRunReport()); _write(TakeRunSpanReport()); } // [local]
        var line = new StringBuilder(Inv($"[PERF][GPU_PASSES] window_s={windowSeconds:F1} frames={frames} busy_ms={busyMilliseconds / f:F2} idle_ms={(windowSeconds * 1000.0 - busyMilliseconds) / f:F2} intervals_ms={total / f:F2} submissions={submissions / f:F1} skipped_markers={_skippedMarkers} unavailable_buffers={_unavailableBuffers}"));
        foreach (var (name, sum) in _categories.OrderByDescending(static pair => pair.Value.Milliseconds))
            line.Append(Inv($" {name}={sum.Milliseconds / f:F3}/n{sum.Count / f:F0}"));
        _write(line.ToString());

        line.Clear().Append(Inv($"[PERF][GPU_PASSES_MASK] frames={frames}"));
        foreach (var ((kind, mask), sum) in _masks.OrderByDescending(static pair => pair.Value.Milliseconds).Take(16))
            line.Append(Inv($" {kind}:0x{mask:X3}={sum.Milliseconds / f:F3}/n{sum.Count / f:F0}"));
        _write(line.ToString());

        line.Clear().Append(Inv($"[PERF][GPU_PASSES_PIPE] frames={frames}"));
        foreach (var ((kind, pipeline), sum) in _pipelines.OrderByDescending(static pair => pair.Value.Milliseconds))
        {
            if (sum.Milliseconds / f < 0.02)
                break;
            line.Append(Inv($" {(kind == IntervalKind.Dispatch ? "c" : "g")}{pipeline}={sum.Milliseconds / f:F3}/n{sum.Count / f:F1}"));
        }

        _write(line.ToString());

        var rank = 0;
        foreach (var (signature, pass) in _passTotals.OrderByDescending(static pair => pair.Value.DrawMilliseconds + pair.Value.PreparationMilliseconds).Take(25))
        {
            var top = pass.Pipelines.OrderByDescending(static pair => pair.Value).Take(3).ToArray();
            _write(Inv($"[PERF][GPU_PASSES_PASS] frames={frames} rank={rank++} pass=0x{signature:X16} extent={pass.Shape.Width}x{pass.Shape.Height} colors={pass.Shape.Colors} depth={(pass.Shape.Depth ? 1 : 0)} draws={pass.Draws / f:F1} draw_ms={pass.DrawMilliseconds / f:F3} prep_ms={pass.PreparationMilliseconds / f:F3} pipelines={pass.Pipelines.Count} top={string.Join(",", top.Select(pair => Inv($"g{pair.Key}:{pair.Value / f:F3}")))}"));
        }

        line.Clear().Append(Inv($"[PERF][GPU_PASSES_SHAPE] frames={frames}"));
        foreach (var (shape, sum) in _shapes.OrderByDescending(static pair => pair.Value.Milliseconds).Take(20))
            line.Append(Inv($" {shape.Width}x{shape.Height}c{shape.Colors}d{(shape.Depth ? 1 : 0)}={sum.Milliseconds / f:F3}/n{sum.Count / f:F0}"));
        _write(line.ToString());

        if (SitesEnabled)
        {
            string[] names;
            lock (_siteIds) names = _siteNames.ToArray();
            var siteRank = 0;
            foreach (var ((category, site), sum) in _sites.OrderByDescending(static pair => pair.Value.Milliseconds).Take(40))
                _write(Inv($"[PERF][GPU_SITES] frames={frames} rank={siteRank++} category={category} est_ms={sum.Milliseconds * SiteSampleEvery / f:F3} est_n={sum.Count * SiteSampleEvery / f:F1} site={names[site]}"));
            _sites.Clear();
        }

        _categories.Clear();
        _masks.Clear();
        _pipelines.Clear();
        _passTotals.Clear();
        _shapes.Clear();
        if (_passShapes.Count > 65536)
            _passShapes.Clear();
    }
}
