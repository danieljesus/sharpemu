// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_FRONTCOST_PROBE=1 (off by default): where the front thread's time goes, per packet. Under
// SHARPEMU_RENDER_PIPELINE=1 the front interprets the guest command streams, prepares the programs of every draw and
// dispatch from a snapshot of the register banks and queues the host work to the render thread; the frame time is the
// slower of the two threads, so the front is the ceiling of every render-side cut. As SHARPEMU_DRAWCOST_PROBE does on
// the render thread, the probe takes one timestamp per step boundary (a lap): a step is the time since the lap before
// it, so the steps of a packet add up to the packet. A step a packet does not reach costs nothing; its time lands in
// the next step the packet reaches. The program steps (Stage*) run once per stage, twice for a draw with a pixel stage.
// It does not need SHARPEMU_PROFILE_RENDER ([PERF][FRONT] and [PERF][PACKET_HANDLERS] do), so it can run on a base
// without the profiler.
//
// An item is one packet, from before its header and payload are read (Decode) to the handler's return. An indirect
// buffer packet ends at its decode: the packets it runs are items of their own. Only the bound front thread is
// measured. SHARPEMU_FRONTCOST_SAMPLE=N times one packet in N (default 1); SHARPEMU_FRONTCOST_REPORT_S (default 5) is
// the window; SHARPEMU_FRONTCOST_ALLOC=1 adds the bytes allocated per step (alloc_<step>=<bytes per timed item>).
// One header line per window and one line per packet kind, on the front thread:
//
//   [PERF][FRONTCOST] window_s=5.0 frames=43 sample=1 lap_ns=21 cpu_ms=44.3 slices_ms=48.9 packets_ms=46.2
//       outside_ms=2.7 slices=1093 gc=3/0/0 lock_contention=180
//   [PERF][FRONTCOST] kind=draw_indexed items_per_frame=2863 timed=123098 us_per_item=9.48 ms_per_frame=27.1
//       steps=<name>=<us per pass>/<ms per frame>/<passes per item>,... counts=<name>=<per timed item>,...
//       timers=<name>=<ns per call>/<calls per item>,...
//
// The header's values are per frame: cpu_ms the front thread's CPU time (kernel + user), slices_ms its time in slices
// (packets plus what the slice does around them), packets_ms the packets' time scaled from the timed ones, outside_ms
// the difference (queue selection, submission begin and end, the packets that end at their decode). ms_per_frame is
// scaled from the timed items to all items; lap_ns is the measured cost of one lap (inside every step once). A timer
// is a sub-measurement inside a step (two timestamps around the call), not a step of its own: read_check is the
// front's NeedsBackRead (pending-write overlay and ever-GPU-written range checks) on every direct guest read.
internal static class FrontCostProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_FRONTCOST_PROBE") == "1";

    private static readonly int SampleEvery =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONTCOST_SAMPLE"), out var every) && every > 0 ? every : 1;

    private static readonly double ReportSeconds = double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FRONTCOST_REPORT_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 5.0;

    private static readonly bool AllocPerStep = Enabled && Environment.GetEnvironmentVariable("SHARPEMU_FRONTCOST_ALLOC") == "1";

    internal enum Kind
    {
        DrawIndexed,
        DrawAuto,
        Dispatch,
        ContextRegisters,
        ContextTable,
        ShaderRegisters,
        ShaderTable,
        UserConfig,
        DrawState,
        Label,
        Wait,
        IndirectBuffer,
        Marker,
        Other,
        Count,
    }

    // In the order a draw (then a dispatch, a register packet, a label) reaches them; the report keeps this order.
    internal enum Step
    {
        // Every packet: the packet window, its header and payload, up to the handler.
        Decode,
        // Draws and dispatches: the register banks copied for the queued item (with the queued item's captures, which
        // the compiler allocates on the handler's entry), the geometry-snapshot shader registers.
        Snapshot,
        DrawRegisters,
        // The front preparation: the skip check (SHARPEMU_FRONT_SKIP_DEFERRING), the read notes and table mark.
        PrepSkip,
        // Draw: the provider inputs from the registers (targets, depth, dynamic state), the kept-programs check, the
        // vertex and pixel sources (pair, user data, header, identity) and their inputs.
        ProgramInputs,
        ProgramKept,
        VertexSource,
        VertexInput,
        PixelSource,
        PixelInput,
        // Dispatch: from the preparation's start to the stage (kept or rebound program, user data, inputs).
        ComputeSource,
        // Per stage: the program key lookup, the resource materialization, the permutation, the stage resources.
        StageKey,
        StageMaterialize,
        StagePermutation,
        StageResources,
        // After the stages, up to the provider's return.
        ProgramTail,
        // The preparation's end: its result noted, the read pages taken, the table mark closed.
        PrepEnd,
        // Register packets: the copy-on-write preparation of the bank, the table blocks read, the writers.
        RegPrepare,
        TableRead,
        RegWrite,
        // Labels and stream writes: the bytes copied, the pending-label entry and the blocked-head retry.
        WriteCopy,
        LabelPending,
        // The queued item built and handed to the render thread; waiting for room; waiting for a queued result.
        Enqueue,
        Backpressure,
        SyncWait,
        // The rest of the handler, up to its return.
        Handler,
        Count,
    }

    internal enum Counter
    {
        Snapshots,
        DrawRegisters,
        PrepSkipped,
        PrepEmpty,
        PrepDeferred,
        ProgramKept,
        Stages,
        DirectReads,
        BackReads,
        Enqueues,
        RegisterValues,
        TableEntries,
        LabelWrites,
        AllocatedBytes,
        Count,
    }

    internal enum Timer
    {
        ReadCheck,
        Count,
    }

    private static readonly Ledger _ledger = new(SampleEvery, (long)(ReportSeconds * Stopwatch.Frequency), AllocPerStep, null);

    internal static Ledger Instance => _ledger;

    // Called once, on the front thread.
    internal static void BindThread()
    {
        if (Enabled)
            _ledger.BindThread(Environment.CurrentManagedThreadId);
    }

    // Called once per presented frame, from any thread.
    internal static void NoteFrame()
    {
        if (Enabled)
            _ledger.NoteFrame();
    }

    internal static void NoteSlice(long ticks)
    {
        if (Enabled)
            _ledger.NoteSlice(ticks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Begin()
    {
        if (Enabled)
            _ledger.Begin();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Classify(uint opcode, uint header)
    {
        if (Enabled)
            _ledger.Classify(KindOf(opcode, header));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void End()
    {
        if (Enabled)
            _ledger.End();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Lap(Step step)
    {
        if (Enabled && _ledger.Timing)
            _ledger.LapCore(step);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Count(Counter counter, long amount = 1)
    {
        if (Enabled && _ledger.Timing)
            _ledger.CountCore(counter, amount);
    }

    // A timer's start, or 0 when this thread's packet is not timed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long TimerStart() => Enabled && _ledger.Timing ? _ledger.TimerStartCore() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void TimerEnd(Timer timer, long started)
    {
        if (started != 0)
            _ledger.TimerEndCore(timer, started);
    }

    internal static Kind KindOf(uint opcode, uint header) => opcode switch
    {
        PacketOpcode.DrawIndex2 or PacketOpcode.DrawIndexOffset2 or PacketOpcode.DispatchDrawPreamble or PacketOpcode.DrawIndexIndirect or
            PacketOpcode.DrawIndexIndirectMulti => Kind.DrawIndexed,
        PacketOpcode.DrawIndexAuto or PacketOpcode.DrawIndexMultiAuto or PacketOpcode.DrawIndirect or PacketOpcode.DrawIndirectMulti => Kind.DrawAuto,
        PacketOpcode.DispatchDirect or PacketOpcode.DispatchIndirect => Kind.Dispatch,
        PacketOpcode.SetContextRegister or PacketOpcode.ContextControl or PacketOpcode.ClearState => Kind.ContextRegisters,
        PacketOpcode.SetContextRegisterIndirect => Kind.ContextTable,
        PacketOpcode.SetShaderRegister => Kind.ShaderRegisters,
        PacketOpcode.SetShaderRegisterIndirect => Kind.ShaderTable,
        PacketOpcode.SetUserConfigRegister or PacketOpcode.SetUserConfigRegisterIndex or PacketOpcode.SetUserConfigRegisterIndirect => Kind.UserConfig,
        PacketOpcode.IndexBase or PacketOpcode.IndexBufferSize or PacketOpcode.IndexType or PacketOpcode.NumInstances or PacketOpcode.SetBase => Kind.DrawState,
        PacketOpcode.ReleaseMemory or PacketOpcode.EventWrite or PacketOpcode.EventWriteEndOfPipe or PacketOpcode.EventWriteEndOfShader or
            PacketOpcode.WriteData or PacketOpcode.AtomicMemory or PacketOpcode.CopyData or PacketOpcode.DmaData => Kind.Label,
        PacketOpcode.WaitRegisterMemory or PacketOpcode.WaitRegisterMemory64 or PacketOpcode.MemorySemaphore or PacketOpcode.WaitOnCeCounter or
            PacketOpcode.WaitOnDeCounterDiff => Kind.Wait,
        PacketOpcode.IndirectBuffer => Kind.IndirectBuffer,
        PacketOpcode.Nop => PacketHeader.CustomCode(header) switch
        {
            PacketCustomCode.DrawIndexAuto => Kind.DrawAuto,
            PacketCustomCode.ContextRegisterTable => Kind.ContextTable,
            PacketCustomCode.ShaderRegisterTable => Kind.ShaderTable,
            PacketCustomCode.UserConfigRegisterTable => Kind.UserConfig,
            PacketCustomCode.ReleaseMemory or PacketCustomCode.WriteData or PacketCustomCode.DmaData => Kind.Label,
            PacketCustomCode.WaitMemory32 or PacketCustomCode.WaitMemory64 or PacketCustomCode.WaitFlipDone => Kind.Wait,
            PacketCustomCode.Zero or PacketCustomCode.PushMarker or PacketCustomCode.PopMarker => Kind.Marker,
            _ => Kind.Other,
        },
        _ => Kind.Other,
    };

    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    // The calling thread's CPU time in 100 ns units, 0 where it cannot be read.
    private static long ThreadCpuTime() =>
        OperatingSystem.IsWindows() && GetThreadTimes(-2, out _, out _, out var kernel, out var user) ? kernel + user : 0;

    // The probe's state: one bound thread writes it (the reporting thread too), any thread notes frames and slices.
    internal sealed class Ledger(int sampleEvery, long reportTicks, bool allocPerStep, Func<long>? clock)
    {
        private const int Steps = (int)Step.Count;
        private const int Counters = (int)Counter.Count;
        private const int Timers = (int)Timer.Count;
        private const int Kinds = (int)Kind.Count;

        private static readonly string[] KindNames = Names<Kind>(Kinds);
        private static readonly string[] StepNames = Names<Step>(Steps);
        private static readonly string[] CounterNames = Names<Counter>(Counters);
        private static readonly string[] TimerNames = Names<Timer>(Timers);

        private readonly long[] _stepTicks = new long[Kinds * Steps];
        private readonly long[] _stepPasses = new long[Kinds * Steps];
        private readonly long[] _stepBytes = new long[Kinds * Steps];
        private readonly long[] _counts = new long[Kinds * Counters];
        private readonly long[] _timerTicks = new long[Kinds * Timers];
        private readonly long[] _timerCalls = new long[Kinds * Timers];
        private readonly long[] _items = new long[Kinds];
        private readonly long[] _timed = new long[Kinds];
        private readonly long[] _itemTicks = new long[Kinds];

        private int _thread;
        // 0 idle, 1 begun and timed (kind not known yet), 2 begun untimed, 3 classified and timed.
        private int _state;
        private Kind _kind;
        private long _last;
        private long _current;
        private long _allocated;
        private long _lastAllocated;
        private long _sequence;
        private long _windowStart;
        private long _frames;
        private long _sliceTicks;
        private long _slices;
        private long _cpu;
        private int _gen0, _gen1, _gen2;
        private long _contention;

        internal Action<string> Output { get; set; } = Console.Error.WriteLine;

        // Whether the bound thread's current packet is timed (other threads check the thread in the core calls).
        internal bool Timing => _state == 3;

        private long Now() => clock?.Invoke() ?? Stopwatch.GetTimestamp();

        internal void BindThread(int thread)
        {
            _thread = thread;
            _windowStart = Now();
            _cpu = ThreadCpuTime();
            TakeProcessCounters(out _, out _, out _, out _);
        }

        internal void NoteFrame() => Interlocked.Increment(ref _frames);

        internal void NoteSlice(long ticks)
        {
            if (Environment.CurrentManagedThreadId != _thread)
                return;
            _sliceTicks += ticks;
            _slices++;
        }

        internal void Begin()
        {
            if (_thread == 0 || Environment.CurrentManagedThreadId != _thread)
                return;
            if (_state == 3)
                End(); // a packet whose handler did not return (it threw): it ends here
            if ((_sequence + 1) % sampleEvery != 0)
            {
                _state = 2;
                return;
            }

            _state = 1;
            _current = 0;
            _last = Now();
            if (allocPerStep)
                _lastAllocated = GC.GetAllocatedBytesForCurrentThread();
            _allocated = GC.GetAllocatedBytesForCurrentThread();
        }

        // The packet's kind, once its header is known; its decode ends here.
        internal void Classify(Kind kind)
        {
            if (_state is 0 or 3 || Environment.CurrentManagedThreadId != _thread)
                return;
            _sequence++;
            _items[(int)kind]++;
            if (_state == 2)
            {
                _state = 0;
                return;
            }

            _kind = kind;
            _state = 3;
            LapCore(Step.Decode);
        }

        internal void End()
        {
            if (_state != 3 || Environment.CurrentManagedThreadId != _thread)
            {
                if (_state is 1 or 2 && Environment.CurrentManagedThreadId == _thread)
                    _state = 0;
                return;
            }

            LapCore(Step.Handler);
            var kind = (int)_kind;
            _timed[kind]++;
            _itemTicks[kind] += _current;
            _counts[kind * Counters + (int)Counter.AllocatedBytes] += GC.GetAllocatedBytesForCurrentThread() - _allocated;
            _state = 0;
            if (_last - _windowStart >= reportTicks)
                Report(_last);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void LapCore(Step step)
        {
            if (_state != 3 || Environment.CurrentManagedThreadId != _thread)
                return;
            var now = Now();
            var slot = (int)_kind * Steps + (int)step;
            var elapsed = now - _last;
            _stepTicks[slot] += elapsed;
            _stepPasses[slot]++;
            _current += elapsed;
            _last = now;
            if (allocPerStep)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                _stepBytes[slot] += allocated - _lastAllocated;
                _lastAllocated = allocated;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void CountCore(Counter counter, long amount)
        {
            if (_state == 3 && Environment.CurrentManagedThreadId == _thread)
                _counts[(int)_kind * Counters + (int)counter] += amount;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal long TimerStartCore() => _state == 3 && Environment.CurrentManagedThreadId == _thread ? Math.Max(Now(), 1) : 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void TimerEndCore(Timer timer, long started)
        {
            if (_state != 3 || Environment.CurrentManagedThreadId != _thread)
                return;
            var slot = (int)_kind * Timers + (int)timer;
            _timerTicks[slot] += Now() - started;
            _timerCalls[slot]++;
        }

        private void TakeProcessCounters(out int gen0, out int gen1, out int gen2, out long contention)
        {
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            var lockContention = Monitor.LockContentionCount;
            gen0 = g0 - _gen0;
            gen1 = g1 - _gen1;
            gen2 = g2 - _gen2;
            contention = lockContention - _contention;
            _gen0 = g0;
            _gen1 = g1;
            _gen2 = g2;
            _contention = lockContention;
        }

        // The cost of one lap, timed here with the ledger's arrays hot.
        private double CalibrateLapNanoseconds()
        {
            if (clock is not null)
                return 0;
            const int Laps = 4096;
            var sink = 0L;
            var started = Stopwatch.GetTimestamp();
            var last = started;
            for (var index = 0; index < Laps; index++)
            {
                if (Environment.CurrentManagedThreadId == _thread)
                {
                    var now = Stopwatch.GetTimestamp();
                    sink += now - last;
                    last = now;
                }
            }

            var elapsed = Stopwatch.GetTimestamp() - started;
            return sink < 0 ? 0 : elapsed * 1e9 / Stopwatch.Frequency / Laps;
        }

        private void Report(long now)
        {
            foreach (var line in TakeReport(now))
                Output(line);
        }

        internal List<string> TakeReport(long now)
        {
            var lines = new List<string>(Kinds + 1);
            var frequency = (double)Stopwatch.Frequency;
            var seconds = (now - _windowStart) / frequency;
            var frames = Interlocked.Exchange(ref _frames, 0);
            var perFrame = frames > 0 ? frames : 1;
            TakeProcessCounters(out var gen0, out var gen1, out var gen2, out var contention);
            var cpuNow = ThreadCpuTime();
            var cpu = cpuNow - _cpu;
            _cpu = cpuNow;
            var toMs = 1000.0 / frequency;
            var packetTicks = 0.0;
            for (var kind = 0; kind < Kinds; kind++)
            {
                if (_timed[kind] != 0)
                    packetTicks += _itemTicks[kind] * (_items[kind] / (double)_timed[kind]);
            }

            var sliceMs = _sliceTicks * toMs / perFrame;
            var packetsMs = packetTicks * toMs / perFrame;
            lines.Add(FormattableString.Invariant(
                $"[PERF][FRONTCOST] window_s={seconds:F1} frames={frames} sample={sampleEvery} lap_ns={CalibrateLapNanoseconds():F0} cpu_ms={cpu / 10000.0 / perFrame:F2} ") +
                FormattableString.Invariant(
                $"slices_ms={sliceMs:F2} packets_ms={packetsMs:F2} outside_ms={sliceMs - packetsMs:F2} slices={_slices / perFrame} gc={gen0}/{gen1}/{gen2} lock_contention={contention}"));
            for (var kind = 0; kind < Kinds; kind++)
            {
                var items = _items[kind];
                if (items == 0)
                    continue;
                var timed = _timed[kind];
                var scale = timed == 0 ? 0 : items / (double)timed;
                var perItem = (double)Math.Max(timed, 1);
                var line = new StringBuilder(1024);
                line.Append(FormattableString.Invariant(
                    $"[PERF][FRONTCOST] kind={KindNames[kind]} items_per_frame={items / (double)perFrame:F0} timed={timed} "));
                line.Append(FormattableString.Invariant(
                    $"us_per_item={(timed == 0 ? 0 : _itemTicks[kind] * toMs * 1000 / timed):F2} ms_per_frame={_itemTicks[kind] * toMs * scale / perFrame:F2} steps="));
                var first = true;
                for (var step = 0; step < Steps; step++)
                {
                    var slot = kind * Steps + step;
                    var passes = _stepPasses[slot];
                    if (passes == 0)
                        continue;
                    if (!first)
                        line.Append(',');
                    first = false;
                    line.Append(FormattableString.Invariant(
                        $"{StepNames[step]}={_stepTicks[slot] * toMs * 1000 / passes:F2}/{_stepTicks[slot] * toMs * scale / perFrame:F2}/{passes / perItem:F2}"));
                }

                line.Append(" counts=");
                first = true;
                for (var counter = 0; counter < Counters; counter++)
                {
                    var count = _counts[kind * Counters + counter];
                    if (count == 0)
                        continue;
                    if (!first)
                        line.Append(',');
                    first = false;
                    line.Append(FormattableString.Invariant($"{CounterNames[counter]}={count / perItem:F2}"));
                }

                for (var step = 0; allocPerStep && step < Steps; step++)
                {
                    var bytes = _stepBytes[kind * Steps + step];
                    if (bytes != 0)
                        line.Append(FormattableString.Invariant($",alloc_{StepNames[step]}={bytes / perItem:F1}"));
                }

                line.Append(" timers=");
                first = true;
                for (var timer = 0; timer < Timers; timer++)
                {
                    var calls = _timerCalls[kind * Timers + timer];
                    if (calls == 0)
                        continue;
                    if (!first)
                        line.Append(',');
                    first = false;
                    line.Append(FormattableString.Invariant(
                        $"{TimerNames[timer]}={_timerTicks[kind * Timers + timer] * 1e9 / frequency / calls:F0}/{calls / perItem:F2}"));
                }

                lines.Add(line.ToString());
            }

            Array.Clear(_stepTicks);
            Array.Clear(_stepPasses);
            Array.Clear(_stepBytes);
            Array.Clear(_counts);
            Array.Clear(_timerTicks);
            Array.Clear(_timerCalls);
            Array.Clear(_items);
            Array.Clear(_timed);
            Array.Clear(_itemTicks);
            _sliceTicks = 0;
            _slices = 0;
            _windowStart = Now();
            return lines;
        }

        private static string[] Names<T>(int count) where T : struct, Enum
        {
            var names = new string[count];
            for (var index = 0; index < count; index++)
            {
                var name = ((T)(object)index).ToString();
                var builder = new StringBuilder(name.Length + 4);
                for (var character = 0; character < name.Length; character++)
                {
                    if (char.IsUpper(name[character]) && character != 0)
                        builder.Append('_');
                    builder.Append(char.ToLowerInvariant(name[character]));
                }

                names[index] = builder.ToString();
            }

            return names;
        }
    }
}
