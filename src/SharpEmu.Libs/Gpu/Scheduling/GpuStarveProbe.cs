// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Scheduling;

// [local] SHARPEMU_GPU_STARVE_PROBE=1 (diagnostic): a sampler thread (~200 us, spins one core) that splits the wall
// clock by GPU state and by what the render thread does. GPU: busy (a submitted tick is not complete), queued (idle
// with sealed ticks the submit thread has not handed over) or idle. Render: in a GPU wait (drain: the waited tick is
// the newest sealed one, so the queue is empty when it returns; partial otherwise), idle waiting for work, or running.
// With SHARPEMU_PROFILE_RENDER=1 the running time while the GPU is idle is also split by the render thread's phase.
// GPU idle spans are counted by how they began: right after a render GPU wait, while it ran, or while it idled.
internal static class GpuStarveProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_GPU_STARVE_PROBE") == "1";

    private static int _renderThread;
    private static int _idle;
    private static long _waitTick;
    private static int _phase = -1;
    private static long _frames;
    private static Thread? _thread;
    private static volatile bool _stop;

    private const int RenderRunning = 0, RenderIdle = 1, RenderWaitPartial = 2, RenderWaitDrain = 3, RenderStates = 4;
    private static readonly string[] GpuNames = ["busy", "queued", "idle"];
    private static readonly string[] RenderNames = ["run", "idle", "wpart", "wdrain"];

    private static long _submits, _replayTicks, _targetTicks, _submitTicks;

    // The submit thread's time per sealed buffer: record-ring replay, deferred-upload join, vkQueueSubmit2.
    public static void NoteSubmit(long replay, long target, long submit)
    {
        Interlocked.Increment(ref _submits);
        Interlocked.Add(ref _replayTicks, replay);
        Interlocked.Add(ref _targetTicks, target);
        Interlocked.Add(ref _submitTicks, submit);
    }

    public static void NoteFrame()
    {
        if (Enabled)
            Interlocked.Increment(ref _frames);
    }

    public static void SetRenderIdle(bool idle)
    {
        if (!Enabled)
            return;
        _renderThread = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _idle, idle ? 1 : 0);
    }

    // The tick a render-thread GPU wait waits for (0 when it ends).
    public static bool EnterGpuWait(ulong tick)
    {
        if (!Enabled || Environment.CurrentManagedThreadId != Volatile.Read(ref _renderThread))
            return false;
        Volatile.Write(ref _waitTick, (long)Math.Max(tick, 1));
        return true;
    }

    public static void ExitGpuWait(bool entered)
    {
        if (entered)
            Volatile.Write(ref _waitTick, 0);
    }

    public static void NotePhase(int phase)
    {
        if (Environment.CurrentManagedThreadId == _renderThread)
            Volatile.Write(ref _phase, phase);
    }

    public static void Start(SubmissionScheduler scheduler, IGpuTickDevice device)
    {
        if (!Enabled || _thread != null)
            return;
        _thread = new Thread(() => Run(scheduler, device)) { IsBackground = true, Name = "SharpEmu GPU starve probe" };
        _thread.Start();
    }

    public static void Stop() => _stop = true;

    private static void Run(SubmissionScheduler scheduler, IGpuTickDevice device)
    {
        var bins = new long[GpuNames.Length * RenderStates];
        var phases = new long[512];
        var spanCount = new long[RenderStates];
        var spanTicks = new long[RenderStates];
        var spanHist = new long[6];
        var frequency = (double)Stopwatch.Frequency;
        var period = Stopwatch.Frequency / 5000;
        var last = Stopwatch.GetTimestamp();
        var windowStart = last;
        var windowFrames = Interlocked.Read(ref _frames);
        var previousGpu = 0;
        var previousRender = RenderRunning;
        var spanStart = 0L;
        var spanKind = 0;
        while (!_stop)
        {
            while (Stopwatch.GetTimestamp() - last < period)
                Thread.Sleep(0);
            var now = Stopwatch.GetTimestamp();
            var dt = now - last;
            last = now;
            ulong completed;
            try
            {
                completed = device.ReadTimeline();
            }
            catch
            {
                return;
            }

            var sealedTick = scheduler.CurrentTick - 1;
            var submitted = scheduler.SubmittedTick;
            var gpu = completed < submitted ? 0 : submitted < sealedTick ? 1 : 2;
            var waitTick = (ulong)Volatile.Read(ref _waitTick);
            var render = waitTick != 0 ? (waitTick >= sealedTick ? RenderWaitDrain : RenderWaitPartial)
                : Volatile.Read(ref _idle) != 0 ? RenderIdle : RenderRunning;
            bins[gpu * RenderStates + render] += dt;
            if (gpu != 0 && render == RenderRunning)
            {
                var phase = Volatile.Read(ref _phase);
                if (phase >= 0 && phase < phases.Length)
                    phases[phase] += dt;
            }

            if (gpu == 2 && previousGpu != 2)
            {
                spanStart = now;
                spanKind = previousRender;
            }
            else if (gpu != 2 && previousGpu == 2)
            {
                var length = now - spanStart;
                spanCount[spanKind]++;
                spanTicks[spanKind] += length;
                var ms = length * 1000.0 / frequency;
                spanHist[ms < 0.5 ? 0 : ms < 1 ? 1 : ms < 2 ? 2 : ms < 4 ? 3 : ms < 8 ? 4 : 5]++;
            }

            previousGpu = gpu;
            previousRender = render;
            var seconds = (now - windowStart) / frequency;
            if (seconds < 5)
                continue;
            var frames = Interlocked.Read(ref _frames);
            var count = Math.Max(1, frames - windowFrames);
            var perFrame = 1000.0 / frequency / count;
            var parts = new List<string>();
            for (var g = 0; g < GpuNames.Length; g++)
                for (var r = 0; r < RenderStates; r++)
                    if (bins[g * RenderStates + r] != 0)
                        parts.Add(FormattableString.Invariant($"{GpuNames[g]}_{RenderNames[r]}={bins[g * RenderStates + r] * perFrame:F2}"));
            var spans = new List<string>();
            for (var r = 0; r < RenderStates; r++)
                if (spanCount[r] != 0)
                    spans.Add(FormattableString.Invariant($"after_{RenderNames[r]}={spanCount[r] / (double)count:F2}x{spanTicks[r] * 1000.0 / frequency / spanCount[r]:F2}ms"));
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[GPU_STARVE] window_s={seconds:F1} frames={count} ms_per_frame: {string.Join(" ", parts)} | idle_spans {string.Join(" ", spans)} hist(<.5,<1,<2,<4,<8,>=8)={string.Join("/", spanHist.Select(value => (value / (double)count).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} | submits={Interlocked.Exchange(ref _submits, 0) / (double)count:F1} replay={Interlocked.Exchange(ref _replayTicks, 0) * perFrame:F2} uploads={Interlocked.Exchange(ref _targetTicks, 0) * perFrame:F2} vksubmit={Interlocked.Exchange(ref _submitTicks, 0) * perFrame:F2}"));
            if (phases.Any(value => value != 0))
            {
                var top = phases.Select((value, index) => (value, index)).Where(pair => pair.value != 0).OrderByDescending(pair => pair.value).Take(12)
                    .Select(pair => FormattableString.Invariant($"{(SharpEmu.Libs.VideoOut.RenderPhaseProfile.Phase)pair.index}={pair.value * perFrame:F2}"));
                Console.Error.WriteLine($"[GPU_STARVE] gpu_not_busy_render_running_by_phase: {string.Join(" ", top)}");
            }

            Array.Clear(bins);
            Array.Clear(phases);
            Array.Clear(spanCount);
            Array.Clear(spanTicks);
            Array.Clear(spanHist);
            windowStart = now;
            windowFrames = frames;
        }
    }
}
