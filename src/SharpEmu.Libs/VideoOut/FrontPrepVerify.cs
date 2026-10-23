// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_FRONT_PREP_VERIFY (diagnostic, off by default): every draw and dispatch whose preparation the
// SHARPEMU_FRONT_PREP_WORKER worker delivered is prepared again on the render thread when it executes, as it would be
// without the worker, and the two results are compared field by field (programs, vertex and pixel inputs, the
// materialized resources of each stage, the draw inputs). =1 executes with the worker's result, =2 with the render
// thread's. Mismatches are counted, the first 200 printed with the part that differs; totals every 20 s.
internal static class FrontPrepVerify
{
    public static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONT_PREP_VERIFY"), out var mode) ? mode : 0;
    public static bool Enabled => Mode != 0;
    public static bool UseRender => Mode == 2;

    internal const int Parts = 6;
    internal static readonly string[] PartNames = ["programs", "vertex_input", "vertex_stage", "pixel_input", "pixel_stage", "draw_inputs"];

    private static long _checked, _mismatched, _renderEmpty;
    private static readonly long[] _partMismatches = new long[Parts];
    private static int _logs;
    private static long _lastReport = System.Diagnostics.Stopwatch.GetTimestamp();

    internal static void Fingerprint(GraphicsPrograms programs, in RenderExecutor.DrawProgramInputs inputs, Span<ulong> parts)
    {
        var hash = new HashCode();
        hash.Add(programs.Vertex);
        hash.Add(programs.Pixel);
        hash.Add(programs.Available);
        hash.Add(programs.SolidClear);
        hash.Add(programs.PositionStream);
        hash.Add(programs.DisableBlending);
        parts[0] = (uint)hash.ToHashCode();

        hash = new HashCode();
        var vertex = programs.VertexInput;
        foreach (var buffer in vertex.Buffers) hash.Add(buffer);
        foreach (var attribute in vertex.Attributes) hash.Add(attribute);
        hash.Add(vertex.FetchEmbedded);
        hash.Add(vertex.FetchAttributeRegister);
        hash.Add(vertex.FetchBufferRegister);
        hash.Add(vertex.ScratchDwords);
        hash.Add(vertex.PositionExportControl);
        hash.Add(vertex.ClipSpace);
        parts[1] = (uint)hash.ToHashCode();
        parts[2] = Stage(vertex.Stage);

        hash = new HashCode();
        var pixel = programs.PixelInput;
        hash.Add(pixel.InputCount);
        hash.Add(pixel.SystemInputBase);
        hash.Add(pixel.CustomInterpolationMask);
        hash.Add(pixel.PerspectiveCenterRegister);
        foreach (var setting in pixel.InterpolatorSettings) hash.Add(setting);
        foreach (var outputMode in pixel.TargetOutputModes) hash.Add(outputMode);
        foreach (var mapping in pixel.TargetExportMappings) hash.Add(mapping);
        hash.Add(pixel.ScratchDwords);
        hash.Add(pixel.KillEnable);
        hash.Add(pixel.DepthExportEnable);
        hash.Add(pixel.SampleMaskExportEnable);
        hash.Add(pixel.EarlyDepth);
        parts[3] = (uint)hash.ToHashCode();
        parts[4] = Stage(pixel.Stage);

        hash = new HashCode();
        hash.Add(inputs.PixelActive);
        hash.Add(inputs.DepthBound);
        foreach (var mapping in (ReadOnlySpan<ColorComponentMap>)inputs.ExportMapping) hash.Add(mapping);
        parts[5] = (uint)hash.ToHashCode();
    }

    internal static void Fingerprint(ComputeProgram program, Span<ulong> parts)
    {
        parts.Clear();
        var hash = new HashCode();
        hash.Add(program.Program);
        hash.Add(program.Available);
        hash.Add(program.Consumed);
        var input = program.Input;
        hash.Add(input.ThreadsX);
        hash.Add(input.ThreadsY);
        hash.Add(input.ThreadsZ);
        hash.Add(input.DispatchThreadDimensions);
        hash.Add(input.DispatchThreadsX);
        hash.Add(input.DispatchThreadsY);
        hash.Add(input.DispatchThreadsZ);
        hash.Add(input.WaveSize);
        hash.Add(input.LocalDataShareDwords);
        hash.Add(input.ScratchDwords);
        parts[0] = (uint)hash.ToHashCode();
        parts[2] = Stage(input.Stage);
    }

    private static ulong Stage(in ShaderStageResources stage)
    {
        var hash = new HashCode();
        hash.Add(stage.Program?.Hash ?? 0);
        hash.Add(stage.ShaderBase);
        hash.Add(stage.ThreadLimits);
        if (stage.Resources is { } resources)
        {
            Add(ref hash, resources.Buffers);
            Add(ref hash, resources.Images);
            Add(ref hash, resources.Samplers);
            foreach (var word in resources.FlattenedResourceTable) hash.Add(word);
            foreach (var word in resources.UserData) hash.Add(word);
            foreach (var range in resources.DeviceAddressRanges) hash.Add(range);
        }

        return (uint)hash.ToHashCode();
    }

    private static void Add(ref HashCode hash, uint[][] tables)
    {
        hash.Add(tables.Length);
        foreach (var table in tables)
        {
            hash.Add(table?.Length ?? -1);
            if (table is null) continue;
            foreach (var word in table) hash.Add(word);
        }
    }

    // On the render thread. True when the two agree.
    internal static bool Compare(ReadOnlySpan<ulong> worker, ReadOnlySpan<ulong> render, string kind, ulong shader, ulong pixelShader)
    {
        Interlocked.Increment(ref _checked);
        var same = true;
        for (var part = 0; part < Parts; part++)
        {
            if (worker[part] != render[part])
            {
                same = false;
                Interlocked.Increment(ref _partMismatches[part]);
            }
        }

        if (!same)
        {
            Interlocked.Increment(ref _mismatched);
            if (Interlocked.Increment(ref _logs) <= 200)
            {
                var differing = string.Join(",", Enumerable.Range(0, Parts).Where(part => worker[part] != render[part]).Select(part => PartNames[part]));
                Console.Error.WriteLine($"[FRONT_PREP_VERIFY] mismatch kind={kind} shader=0x{shader:X} pixel=0x{pixelShader:X} parts={differing}");
            }
        }

        Report();
        return same;
    }

    internal static void NoteRenderEmpty()
    {
        Interlocked.Increment(ref _renderEmpty);
        Report();
    }

    private static void Report()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref _lastReport) < 20 * System.Diagnostics.Stopwatch.Frequency)
            return;
        Volatile.Write(ref _lastReport, now);
        Console.Error.WriteLine($"[FRONT_PREP_VERIFY] checked={Volatile.Read(ref _checked)} mismatched={Volatile.Read(ref _mismatched)} render_empty={Volatile.Read(ref _renderEmpty)} " +
            string.Join(" ", Enumerable.Range(0, Parts).Select(part => $"{PartNames[part]}={Volatile.Read(ref _partMismatches[part])}")));
    }
}
