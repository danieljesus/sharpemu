// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_PLAN_PROBE=1 (off by default): the gate of a plan per draw shape (DRAW_PLAN, option B in
// sharpemu-rediseno-draws.md §8.2), in shadow: what a plan keyed by value would have found, measured on every draw
// RecordDrawCommands records on the render thread. Nothing recorded changes.
//
// The shape of a draw is what PipelineKey takes that is not a context register: the vertex and pixel program ids and
// hashes and their binding layouts, the draw class (indexed, indirect, mesh, tessellation, memory writes, pixel stage,
// depth target), the color target count with the bound slot mask, the topology, primitive restart, the program flags
// (blending off, unwritten targets dropped, solid clear), the vertex layout (per buffer its stride and rate, per
// attribute its V# words without the base and record count and its registers, fetch index, buffer and offset) and the pixel
// interpolator settings. A store keeps, for the 200 shapes used last (LRU), what the last draw of each shape had:
//
//   targets   the value hashes (PlanGroupHashes, kept by RegisterBanks from the raw context dwords written) of the
//             ColorTargets and DepthTargets register groups, with the composite depth extent
//   pipeline  the PipelineState and ShaderInterface groups and the target groups without their addresses and clear
//             values (TargetFormats)
//   dynamic   the ContextDynamic group
//   slots     per stage one hash per T#, per S# and per V# (read-only V#s without their base)
//
// and the graphics pipeline handle, RenderingState and DynamicDrawState the generic path then resolved. For each draw
// whose shape is in the store: state_equal per group, slots_changed (how many slots differ), and the exactness checks:
// pipeline_key_mismatch = targets and pipeline equal, the RenderingState equal, and a different handle (a register the
// pipeline depends on is missing from the groups: must be 0; rendering_changed counts the same with another
// RenderingState, which the plan compares apart); dynamic_mismatch = dynamic and targets equal and another
// DynamicDrawState. The first mismatches are printed as [GPU][PLAN_MISMATCH] with the typed context fields that differ.
// gate_ns is the probe's own shape key, slot hashes, lookup and comparison per draw (hashes of the groups instead of
// the SequenceEqual over their dwords a plan would run). Runs: consecutive draws in the same rendering scope (the
// DrawScope after BeginRendering) with the same targets.
//
// One [PERF][PLAN] line per window, with the [PERF][DRAWCOST] window when that probe is on (SHARPEMU_PLAN_REPORT_S,
// 5 by default, otherwise):
//
//   [PERF][PLAN] window_s=5.0 frames=25 draws_per_frame=3793 shapes=812 shapes_per_frame=430 top10=30.1 top50=71.2
//       top200=93.0 covered=92.1 store_hit=96.0 rt=0.0 unsupported=0.4
//       state_equal=targets:95.1,pipeline:93.0,dynamic:88.2,all:84.0 slots_changed=0:40.1,1:30.2,2:15.0,3+:14.7
//       pipeline_key_mismatch=0 pipeline_key_mismatch_total=0 pipeline_checked=82.1 rendering_changed=0.1
//       dynamic_mismatch=0 dynamic_checked=80.0 run_len=p50:3,p90:24 runs_per_frame=900 draws_in_runs16=41.0
//       run_start_share=0.24 gate_ns=420 ts_ns=19 evictions=12
//
// top-N = % of draws of the N shapes with most draws in the window; covered = % of draws of the top-200 shapes that
// are supported (no mesh, no tessellation) and not ray tracing (LiteObtainProbe.IsRtProgram); store_hit = % of draws
// whose shape was in the store. state_equal, slots_changed, pipeline_checked and dynamic_checked are % of the store
// hits; rendering_changed % of pipeline_checked. run_len percentiles are over runs, draws_in_runs16 % of the draws in
// runs of 16 or more, run_start_share the runs started per draw.
internal static class PlanProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PLAN_PROBE") == "1"; // [local] the shadow it reads is on with it

    private static readonly long ReportTicks = (long)((double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PLAN_REPORT_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 5.0) * Stopwatch.Frequency);

    internal const int StoreCapacity = 200;
    private const int DumpLimit = 20;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(ref ulong hash, ulong value) => RepeatProbe.Add(ref hash, value);

    private static ulong Words(ReadOnlySpan<uint> words) => XxHash3.HashToUInt64(MemoryMarshal.AsBytes(words)) ^ (ulong)words.Length;

    // ---- The shape key.

    [Flags]
    internal enum DrawClass : uint
    {
        None = 0,
        Indexed = 1,
        Indirect = 2,
        Mesh = 4,
        Tessellation = 8,
        WritesMemory = 16,
        PixelActive = 32,
        DepthTarget = 64,
        DisableBlending = 128,
        DropUnwrittenTargets = 256,
        SolidClear = 512,
    }

    internal static bool Supported(DrawClass drawClass) => (drawClass & (DrawClass.Mesh | DrawClass.Tessellation)) == 0;

    // The vertex layout the pipeline is built from: buffer strides and rates, attribute formats and registers.
    internal static ulong VertexLayout(VertexInputInfo vertexInput)
    {
        var hash = RepeatProbe.Mix(0x7E57);
        Add(ref hash, (ulong)vertexInput.Buffers.Length);
        foreach (var buffer in vertexInput.Buffers)
        {
            Add(ref hash, buffer.Stride | (buffer.PerInstance ? 1UL << 32 : 0));
        }

        Add(ref hash, (ulong)vertexInput.Attributes.Length);
        foreach (var attribute in vertexInput.Attributes)
        {
            var masked = attribute.Descriptor.WithAddress(0); // and without its record count: the draw's, not the layout's
            Add(ref hash, masked.Word1 | ((ulong)masked.Word3 << 32));
            Add(ref hash, (uint)attribute.RegisterStart | ((ulong)(uint)attribute.RegisterCount << 32));
            Add(ref hash, (uint)attribute.AttributeId | ((ulong)attribute.FetchIndex << 32));
            Add(ref hash, (uint)attribute.BufferIndex | ((ulong)attribute.OffsetBytes << 32));
        }

        Add(ref hash, (vertexInput.FetchEmbedded ? 1UL : 0) | ((ulong)(uint)vertexInput.FetchAttributeRegister << 8) |
            ((ulong)(uint)vertexInput.FetchBufferRegister << 24));
        return hash;
    }

    internal static ulong ShapeKey(ulong vertexProgram, ulong pixelProgram, ulong vertexHash, ulong pixelHash, ulong layouts, DrawClass drawClass,
        uint colorCount, uint boundSlots, int topology, bool primitiveRestart, ulong vertexLayout, ReadOnlySpan<uint> interpolators)
    {
        var hash = RepeatProbe.Mix(0x5A4E);
        Add(ref hash, vertexProgram);
        Add(ref hash, pixelProgram);
        Add(ref hash, vertexHash);
        Add(ref hash, pixelHash);
        Add(ref hash, layouts);
        Add(ref hash, (ulong)drawClass | ((ulong)colorCount << 32) | ((ulong)boundSlots << 40));
        Add(ref hash, (uint)topology | (primitiveRestart ? 1UL << 32 : 0));
        Add(ref hash, vertexLayout);
        Add(ref hash, Words(interpolators));
        return hash;
    }

    internal static ulong ShapeKey(ResolvedDraw record, ref DrawState state, VertexInputInfo vertexInput, PixelInputInfo? pixelInput, out DrawClass drawClass,
        out bool rt)
    {
        var programs = state.Programs;
        drawClass = (record.Emission.Indexed ? DrawClass.Indexed : 0) | (record.Emission.IndirectArgumentsAddress != 0 ? DrawClass.Indirect : 0) |
            (programs.MeshInput is not null ? DrawClass.Mesh : 0) |
            (programs.Tessellation is not null || vertexInput.Tessellation is not null || record.Kind == ResolvedDrawKind.Tessellation ? DrawClass.Tessellation : 0) |
            (record.WritesMemory ? DrawClass.WritesMemory : 0) | (state.PixelActive ? DrawClass.PixelActive : 0) |
            (state.Depth.HasTarget ? DrawClass.DepthTarget : 0) | (programs.DisableBlending ? DrawClass.DisableBlending : 0) |
            (programs.DropUnwrittenColorTargets ? DrawClass.DropUnwrittenTargets : 0) | (programs.SolidClear is not null ? DrawClass.SolidClear : 0);
        var boundSlots = 0u;
        for (var index = 0; index < state.ColorCount; index++)
        {
            boundSlots |= 1u << (int)state.Colors[index].Slot;
        }

        var vertexInfo = vertexInput.Stage.Program;
        var pixelInfo = pixelInput?.Stage.Program;
        rt = LiteObtainProbe.IsRtProgram(vertexInfo?.Hash ?? 0) || LiteObtainProbe.IsRtProgram(pixelInfo?.Hash ?? 0);
        var layouts = (ulong)(uint)RuntimeHelpers.GetHashCode(vertexInfo?.Bindings) | ((ulong)(uint)RuntimeHelpers.GetHashCode(pixelInfo?.Bindings) << 32);
        return ShapeKey(programs.Vertex.Id, programs.Pixel.Id, vertexInfo?.Hash ?? 0, pixelInfo?.Hash ?? 0, layouts, drawClass, state.ColorCount, boundSlots,
            (int)record.Topology, record.PrimitiveRestart, VertexLayout(vertexInput), pixelInput?.InterpolatorSettings ?? []);
    }

    // One hash per resource slot of a stage, appended at count: T#s, S#s, then V#s (read-only ones without base).
    internal static int StageSlots(in ShaderStageResources stage, ref ulong[] slots, int count)
    {
        var program = stage.Program;
        var snapshot = stage.Resources;
        if (program is null || snapshot is null)
        {
            return count;
        }

        var needed = count + snapshot.Images.Length + snapshot.Samplers.Length + snapshot.Buffers.Length;
        if (slots.Length < needed)
        {
            Array.Resize(ref slots, Math.Max(needed, slots.Length * 2));
        }

        foreach (var words in snapshot.Images)
        {
            slots[count++] = Words(words) ^ 0x1000;
        }

        foreach (var words in snapshot.Samplers)
        {
            slots[count++] = Words(words) ^ 0x2000;
        }

        var buffers = program.Resources?.Info.Buffers;
        for (var index = 0; index < snapshot.Buffers.Length; index++)
        {
            var words = snapshot.Buffers[index];
            var resource = buffers is not null && index < buffers.Count ? buffers[index] : null;
            if (words.Length < 4 || resource is not null && (resource.Written || resource.Atomic))
            {
                slots[count++] = Words(words) ^ 0x3000;
                continue;
            }

            var masked = BufferDescriptorWords.From(words).WithAddress(0);
            var hash = RepeatProbe.Mix(0x4000);
            Add(ref hash, masked.Word0 | ((ulong)masked.Word1 << 32));
            Add(ref hash, masked.Word2 | ((ulong)masked.Word3 << 32));
            if (words.Length > 4)
            {
                Add(ref hash, Words(words.AsSpan(4)));
            }

            slots[count++] = hash;
        }

        return count;
    }

    internal static int ChangedSlots(ReadOnlySpan<ulong> previous, ReadOnlySpan<ulong> current)
    {
        var changed = Math.Abs(previous.Length - current.Length);
        var common = Math.Min(previous.Length, current.Length);
        for (var index = 0; index < common; index++)
        {
            if (previous[index] != current[index])
            {
                changed++;
            }
        }

        return changed;
    }

    internal static ulong TargetsHash(in PlanGroupHashes groups)
    {
        var hash = groups.ColorTargets;
        Add(ref hash, groups.DepthTargets);
        return hash;
    }

    internal static ulong PipelineHash(in PlanGroupHashes groups)
    {
        var hash = groups.PipelineState;
        Add(ref hash, groups.ShaderInterface);
        Add(ref hash, groups.TargetFormats);
        return hash;
    }

    // ---- The store of the shapes used last.

    internal sealed class Entry
    {
        public ulong Key;
        public bool Valid;
        public ulong Targets;
        public ulong Pipeline;
        public ulong Dynamic;
        public bool Resolved;
        public ulong Handle;
        public RenderingState Rendering;
        public DynamicDrawState DynamicState;
        public ulong[] Slots = new ulong[32];
        public int SlotCount;
        public Gpu.GpuCommands.Registers.ContextRegisters? Context;
        public ulong VertexHash;
        public ulong PixelHash;
        public Entry? Newer;
        public Entry? Older;

        public ReadOnlySpan<ulong> SlotSpan => Slots.AsSpan(0, SlotCount);
    }

    // A fixed set of entries, most recently used first; a new shape takes the least recently used one.
    internal sealed class Store(int capacity)
    {
        private readonly Dictionary<ulong, Entry> _entries = new(capacity);
        private Entry? _newest;
        private Entry? _oldest;
        public long Evictions;

        public int Count => _entries.Count;

        public bool TryGet(ulong key, out Entry entry)
        {
            if (_entries.TryGetValue(key, out entry!))
            {
                Touch(entry);
                return true;
            }

            return false;
        }

        // The entry for a shape not in the store: a free one, or the least recently used one cleared.
        public Entry Take(ulong key)
        {
            Entry entry;
            if (_entries.Count < capacity)
            {
                entry = new Entry();
            }
            else
            {
                entry = _oldest!;
                Unlink(entry);
                _entries.Remove(entry.Key);
                Evictions++;
            }

            entry.Key = key;
            entry.Valid = false;
            entry.Resolved = false;
            entry.SlotCount = 0;
            entry.Context = null;
            _entries[key] = entry;
            LinkNewest(entry);
            return entry;
        }

        public bool Contains(ulong key) => _entries.ContainsKey(key);

        private void Touch(Entry entry)
        {
            if (ReferenceEquals(entry, _newest))
            {
                return;
            }

            Unlink(entry);
            LinkNewest(entry);
        }

        private void Unlink(Entry entry)
        {
            if (entry.Newer is { } newer) newer.Older = entry.Older;
            else _newest = entry.Older;
            if (entry.Older is { } older) older.Newer = entry.Newer;
            else _oldest = entry.Newer;
            entry.Newer = entry.Older = null;
        }

        private void LinkNewest(Entry entry)
        {
            entry.Older = _newest;
            entry.Newer = null;
            if (_newest is not null) _newest.Newer = entry;
            _newest = entry;
            _oldest ??= entry;
        }
    }

    // ---- Runs of draws in one rendering scope with the same targets.

    internal sealed class Runs
    {
        public const int MaxLength = 4096;
        private long _scope;
        private ulong _targets;
        private int _length;
        public readonly long[] Lengths = new long[MaxLength + 1];
        public long Count;
        public long Started;
        public long Draws;
        public long DrawsIn16;

        public void Note(long scope, ulong targets)
        {
            Draws++;
            if (_length != 0 && scope == _scope && scope != 0 && targets == _targets)
            {
                _length++;
                return;
            }

            Close();
            _scope = scope;
            _targets = targets;
            _length = 1;
            Started++;
        }

        // Ends the open run (the next draw starts another).
        public void Close()
        {
            if (_length == 0)
            {
                return;
            }

            Lengths[Math.Min(_length, MaxLength)]++;
            Count++;
            if (_length >= 16)
            {
                DrawsIn16 += _length;
            }

            _length = 0;
        }

        // The smallest run length with at least fraction of the closed runs at or under it.
        public int Percentile(double fraction)
        {
            if (Count == 0)
            {
                return 0;
            }

            var wanted = (long)Math.Ceiling(Count * fraction);
            var seen = 0L;
            for (var length = 1; length <= MaxLength; length++)
            {
                seen += Lengths[length];
                if (seen >= Math.Max(wanted, 1))
                {
                    return length;
                }
            }

            return MaxLength;
        }

        public void ClearWindow()
        {
            Array.Clear(Lengths);
            Count = Started = Draws = DrawsIn16 = 0;
        }
    }

    // ---- The window.

    internal sealed class ShapeCount
    {
        public long Draws;
        public bool Covered;
        public long Frame = -1;
    }

    internal sealed class Window
    {
        public long Frames;
        public long Draws;
        public long Hits;
        public long Rt;
        public long Unsupported;
        public long TargetsEqual;
        public long PipelineEqual;
        public long DynamicEqual;
        public long AllEqual;
        public readonly long[] SlotsChanged = new long[4];
        public long PipelineChecked;
        public long PipelineMismatch;
        public long RenderingChanged;
        public long DynamicChecked;
        public long DynamicMismatch;
        public long GateTicks;
        public long ShapesPerFrame;
        public readonly Dictionary<ulong, ShapeCount> Shapes = new();
        public readonly Runs Runs = new();

        public void NoteShape(ulong key, bool covered, long frame)
        {
            if (!Shapes.TryGetValue(key, out var count))
            {
                Shapes[key] = count = new ShapeCount { Covered = covered };
            }

            count.Draws++;
            if (count.Frame != frame)
            {
                count.Frame = frame;
                ShapesPerFrame++;
            }
        }

        public void Clear()
        {
            Frames = Draws = Hits = Rt = Unsupported = TargetsEqual = PipelineEqual = DynamicEqual = AllEqual = 0;
            PipelineChecked = PipelineMismatch = RenderingChanged = DynamicChecked = DynamicMismatch = GateTicks = ShapesPerFrame = 0;
            Array.Clear(SlotsChanged);
            Shapes.Clear();
            Runs.ClearWindow();
        }
    }

    // The % of draws of the top N shapes by draws, and of the covered shapes among the top N.
    internal static (double Top, double Covered) TopShare(Dictionary<ulong, ShapeCount> shapes, int top, long draws, List<ShapeCount> scratch)
    {
        if (draws <= 0)
        {
            return (0, 0);
        }

        scratch.Clear();
        scratch.AddRange(shapes.Values);
        scratch.Sort(static (left, right) => right.Draws.CompareTo(left.Draws));
        long sum = 0, covered = 0;
        for (var index = 0; index < scratch.Count && index < top; index++)
        {
            sum += scratch[index].Draws;
            if (scratch[index].Covered)
            {
                covered += scratch[index].Draws;
            }
        }

        return (sum * 100.0 / draws, covered * 100.0 / draws);
    }

    // ---- The render thread's state.

    private static int _thread;
    private static long _frames;
    private static long _frameSerial;
    private static long _windowStart;
    private static long _mismatchTotal;
    private static int _dumps;
    private static readonly Store _store = new(StoreCapacity);
    private static readonly Window _window = new();
    private static readonly List<ShapeCount> _scratch = new();
    private static ulong[] _slots = new ulong[64];

    // The draw between NoteDraw and NotePipeline.
    private static bool _pending;
    private static Entry? _pendingEntry;
    private static bool _pendingHit;
    private static bool _pendingTargetsEqual;
    private static bool _pendingPipelineEqual;
    private static bool _pendingDynamicEqual;
    private static int _pendingSlots;
    private static ulong _pendingTargets;
    private static ulong _pendingPipeline;
    private static ulong _pendingDynamic;
    private static ulong _pendingVertexHash;
    private static ulong _pendingPixelHash;

    internal static Window WindowForTests => _window;

    internal static void NoteFrame()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _frames);
            Interlocked.Increment(ref _frameSerial);
        }
    }

    private static bool Owns()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (_thread == 0)
        {
            _thread = thread;
            _windowStart = Stopwatch.GetTimestamp();
        }

        return thread == _thread;
    }

    // A draw before its bindings: its shape, the comparison with the last draw of the shape (timed: the gate).
    internal static void NoteDraw(ResolvedDraw record, ref DrawState state, VertexInputInfo vertexInput, PixelInputInfo? pixelInput)
    {
        if (!Owns())
        {
            return;
        }

        if (!DrawCostProbe.Enabled && Stopwatch.GetTimestamp() - _windowStart >= ReportTicks)
        {
            Report();
        }

        var started = Stopwatch.GetTimestamp();
        var key = ShapeKey(record, ref state, vertexInput, pixelInput, out var drawClass, out var rt);
        var groups = record.PlanGroups;
        var targets = TargetsHash(in groups);
        var pipeline = PipelineHash(in groups);
        var dynamic = groups.ContextDynamic;
        var slotCount = StageSlots(vertexInput.Stage, ref _slots, 0);
        if (pixelInput is not null)
        {
            slotCount = StageSlots(pixelInput.Stage, ref _slots, slotCount);
        }

        var found = _store.TryGet(key, out var entry);
        var hit = found && entry.Valid;
        var targetsEqual = hit && entry.Targets == targets;
        var pipelineEqual = hit && entry.Pipeline == pipeline;
        var dynamicEqual = hit && entry.Dynamic == dynamic;
        var changed = hit ? ChangedSlots(entry.SlotSpan, _slots.AsSpan(0, slotCount)) : 0;
        _window.GateTicks += Stopwatch.GetTimestamp() - started;

        var window = _window;
        window.Draws++;
        var covered = Supported(drawClass) && !rt;
        if (rt) window.Rt++;
        if (!Supported(drawClass)) window.Unsupported++;
        window.NoteShape(key, covered, Volatile.Read(ref _frameSerial));
        if (hit)
        {
            window.Hits++;
            if (targetsEqual) window.TargetsEqual++;
            if (pipelineEqual) window.PipelineEqual++;
            if (dynamicEqual) window.DynamicEqual++;
            if (targetsEqual && pipelineEqual && dynamicEqual) window.AllEqual++;
            window.SlotsChanged[Math.Min(changed, 3)]++;
        }

        _pending = true;
        _pendingEntry = found ? entry : _store.Take(key);
        _pendingHit = hit;
        _pendingTargetsEqual = targetsEqual;
        _pendingPipelineEqual = pipelineEqual;
        _pendingDynamicEqual = dynamicEqual;
        _pendingSlots = slotCount;
        _pendingTargets = targets;
        _pendingPipeline = pipeline;
        _pendingDynamic = dynamic;
        _pendingVertexHash = vertexInput.Stage.Program?.Hash ?? 0;
        _pendingPixelHash = pixelInput?.Stage.Program?.Hash ?? 0;
    }

    // The pipeline the generic path resolved: the exactness checks, then the store takes this draw's values.
    internal static void NotePipeline(ResolvedDraw record, ulong handle, in RenderingState rendering)
    {
        if (!_pending || Environment.CurrentManagedThreadId != _thread || _pendingEntry is not { } entry)
        {
            return;
        }

        _pending = false;
        var window = _window;
        if (_pendingHit && entry.Resolved && _pendingTargetsEqual && _pendingPipelineEqual)
        {
            window.PipelineChecked++;
            if (!entry.Rendering.Equals(rendering))
            {
                window.RenderingChanged++;
            }
            else if (entry.Handle != handle)
            {
                window.PipelineMismatch++;
                _mismatchTotal++;
                if (_dumps++ < DumpLimit)
                {
                    Console.Error.WriteLine(FormattableString.Invariant(
                        $"[GPU][PLAN_MISMATCH] kind=pipeline shape=0x{entry.Key:X16} vs=0x{_pendingVertexHash:X16} ps=0x{_pendingPixelHash:X16} ") +
                        FormattableString.Invariant($"handle=0x{entry.Handle:X}/0x{handle:X} targets=0x{_pendingTargets:X16} pipeline=0x{_pendingPipeline:X16} ") +
                        $"context_diff={ContextDiff(entry.Context, record.Context)}");
                }
            }
        }

        if (_pendingHit && entry.Resolved && _pendingDynamicEqual && _pendingTargetsEqual)
        {
            window.DynamicChecked++;
            var current = record.Dynamic;
            if (RepeatProbe.Bytes(in entry.DynamicState) != RepeatProbe.Bytes(in current))
            {
                window.DynamicMismatch++;
                _mismatchTotal++;
                if (_dumps++ < DumpLimit)
                {
                    Console.Error.WriteLine(FormattableString.Invariant(
                        $"[GPU][PLAN_MISMATCH] kind=dynamic shape=0x{entry.Key:X16} vs=0x{_pendingVertexHash:X16} ps=0x{_pendingPixelHash:X16} dynamic=0x{_pendingDynamic:X16} ") +
                        $"dynamic_diff={FieldDiff(entry.DynamicState, current, 0)} context_diff={ContextDiff(entry.Context, record.Context)}");
                }
            }
        }

        entry.Valid = true;
        entry.Resolved = true;
        entry.Targets = _pendingTargets;
        entry.Pipeline = _pendingPipeline;
        entry.Dynamic = _pendingDynamic;
        entry.Handle = handle;
        entry.Rendering = rendering;
        entry.DynamicState = record.Dynamic;
        entry.Context = record.Context;
        entry.VertexHash = _pendingVertexHash;
        entry.PixelHash = _pendingPixelHash;
        if (entry.Slots.Length < _pendingSlots)
        {
            entry.Slots = new ulong[Math.Max(_pendingSlots, entry.Slots.Length * 2)];
        }

        _slots.AsSpan(0, _pendingSlots).CopyTo(entry.Slots);
        entry.SlotCount = _pendingSlots;
    }

    // After the draw's BeginRendering: its rendering scope, for the runs.
    internal static void NoteRendering(ResolvedDraw record, long scope)
    {
        if (Environment.CurrentManagedThreadId == _thread)
        {
            var groups = record.PlanGroups;
            _window.Runs.Note(scope, TargetsHash(in groups));
        }
    }

    // Called by DrawCostProbe at its own report when it is on, so the line shares its window.
    internal static void Report()
    {
        if (!Enabled || _thread == 0 || Environment.CurrentManagedThreadId != _thread)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _window.Frames = Interlocked.Exchange(ref _frames, 0);
        if (_window.Draws != 0)
        {
            Console.Error.WriteLine(Report(_window, (now - _windowStart) / (double)Stopwatch.Frequency, LiteObtainProbe.CalibrateTimestampTicks(), _mismatchTotal,
                _store.Evictions, _scratch));
        }

        _window.Clear();
        _store.Evictions = 0;
        _windowStart = Stopwatch.GetTimestamp();
    }

    internal static string Report(Window window, double seconds, double timestampTicks, long mismatchTotal, long evictions, List<ShapeCount> scratch)
    {
        var toNs = 1e9 / Stopwatch.Frequency;
        var frames = Math.Max(window.Frames, 1);
        var draws = window.Draws;
        var hits = window.Hits;
        var (top10, _) = TopShare(window.Shapes, 10, draws, scratch);
        var (top50, _) = TopShare(window.Shapes, 50, draws, scratch);
        var (top200, covered) = TopShare(window.Shapes, StoreCapacity, draws, scratch);
        var runs = window.Runs;
        var line = new StringBuilder(768);
        line.Append(FormattableString.Invariant(
            $"[PERF][PLAN] window_s={seconds:F1} frames={window.Frames} draws_per_frame={draws / (double)frames:F0} shapes={window.Shapes.Count} "));
        line.Append(FormattableString.Invariant(
            $"shapes_per_frame={window.ShapesPerFrame / (double)frames:F0} top10={top10:F1} top50={top50:F1} top200={top200:F1} covered={covered:F1} "));
        line.Append(FormattableString.Invariant(
            $"store_hit={Percent(hits, draws):F1} rt={Percent(window.Rt, draws):F1} unsupported={Percent(window.Unsupported, draws):F1} "));
        line.Append(FormattableString.Invariant(
            $"state_equal=targets:{Percent(window.TargetsEqual, hits):F1},pipeline:{Percent(window.PipelineEqual, hits):F1},dynamic:{Percent(window.DynamicEqual, hits):F1},all:{Percent(window.AllEqual, hits):F1} "));
        line.Append(FormattableString.Invariant(
            $"slots_changed=0:{Percent(window.SlotsChanged[0], hits):F1},1:{Percent(window.SlotsChanged[1], hits):F1},2:{Percent(window.SlotsChanged[2], hits):F1},3+:{Percent(window.SlotsChanged[3], hits):F1} "));
        line.Append(FormattableString.Invariant(
            $"pipeline_key_mismatch={window.PipelineMismatch} pipeline_key_mismatch_total={mismatchTotal} pipeline_checked={Percent(window.PipelineChecked, hits):F1} "));
        line.Append(FormattableString.Invariant(
            $"rendering_changed={Percent(window.RenderingChanged, window.PipelineChecked):F1} dynamic_mismatch={window.DynamicMismatch} dynamic_checked={Percent(window.DynamicChecked, hits):F1} "));
        line.Append(FormattableString.Invariant(
            $"run_len=p50:{runs.Percentile(0.5)},p90:{runs.Percentile(0.9)} runs_per_frame={runs.Count / (double)frames:F0} draws_in_runs16={Percent(runs.DrawsIn16, runs.Draws):F1} "));
        line.Append(FormattableString.Invariant(
            $"run_start_share={(runs.Draws == 0 ? 0 : runs.Started / (double)runs.Draws):F3} gate_ns={(draws == 0 ? 0 : Math.Max(0, window.GateTicks / (double)draws - timestampTicks) * toNs):F0} "));
        line.Append(FormattableString.Invariant($"ts_ns={timestampTicks * toNs:F0} evictions={evictions}"));
        return line.ToString();
    }

    private static double Percent(long part, long whole) => whole <= 0 ? 0 : part * 100.0 / whole;

    // ---- Mismatch dumps: the typed fields that differ.

    private const BindingFlags FieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static string ContextDiff(object? previous, object? current)
    {
        if (previous is null || current is null)
        {
            return "unknown";
        }

        if (ReferenceEquals(previous, current))
        {
            return "same_object";
        }

        var text = FieldDiff(previous, current, 0);
        return text.Length == 0 ? "none" : text;
    }

    // The paths of the fields that differ (nested types of the register namespace and arrays walked), at most 16.
    internal static string FieldDiff(object? previous, object? current, int depth)
    {
        var differences = new List<string>();
        Walk(previous, current, "", depth, differences);
        return string.Join(",", differences);
    }

    private static void Walk(object? previous, object? current, string path, int depth, List<string> differences)
    {
        if (differences.Count >= 16)
        {
            return;
        }

        if (previous is null || current is null)
        {
            if (!ReferenceEquals(previous, current)) differences.Add(path.Length == 0 ? "null" : path);
            return;
        }

        var type = previous.GetType();
        if (type != current.GetType())
        {
            differences.Add(path + ":type");
            return;
        }

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || depth > 4)
        {
            if (!SafeEquals(type, previous, current)) differences.Add(path.Length == 0 ? "value" : path);
            return;
        }

        if (previous is Array left && current is Array right)
        {
            if (left.Length != right.Length)
            {
                differences.Add(path + ".Length");
                return;
            }

            for (var index = 0; index < left.Length; index++)
            {
                Walk(left.GetValue(index), right.GetValue(index), $"{path}[{index}]", depth + 1, differences);
            }

            return;
        }

        if (previous is System.Collections.IDictionary leftMap && current is System.Collections.IDictionary rightMap)
        {
            if (leftMap.Count != rightMap.Count)
            {
                differences.Add(path + ".Count");
                return;
            }

            foreach (System.Collections.DictionaryEntry pair in leftMap)
            {
                if (!rightMap.Contains(pair.Key) || !Equals(rightMap[pair.Key], pair.Value))
                {
                    differences.Add($"{path}[{pair.Key}]");
                }
            }

            return;
        }

        var fields = type.GetFields(FieldFlags);
        if (fields.Length == 0)
        {
            if (!SafeEquals(type, previous, current)) differences.Add(path);
            return;
        }

        var before = differences.Count;
        foreach (var field in fields)
        {
            Walk(field.GetValue(previous), field.GetValue(current), path.Length == 0 ? field.Name : path + "." + field.Name, depth + 1, differences);
        }

        // An inline array shows reflection its first element only (and has no built-in Equals): its bytes are compared.
        if (differences.Count == before && type.IsDefined(typeof(InlineArrayAttribute), false) && !BytesEqual(type, previous, current))
        {
            differences.Add(path.Length == 0 ? "value" : path);
        }
    }

    private static readonly MethodInfo BytesEqualMethod = typeof(PlanProbe).GetMethod(nameof(BytesEqualOf), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static bool SafeEquals(Type type, object previous, object current) =>
        type.IsDefined(typeof(InlineArrayAttribute), false) ? BytesEqual(type, previous, current) : previous.Equals(current);

    private static bool BytesEqual(Type type, object previous, object current)
    {
        try
        {
            return (bool)BytesEqualMethod.MakeGenericMethod(type).Invoke(null, [previous, current])!;
        }
        catch (Exception)
        {
            return true; // a struct with references: its first element was walked
        }
    }

    private static bool BytesEqualOf<T>(object previous, object current) where T : struct
    {
        var left = (T)previous;
        var right = (T)current;
        return MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref left)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref right)));
    }

    // [local] For tests: clears the thread ownership, the store and the window.
    internal static void ResetForTests()
    {
        _thread = 0;
        _frames = 0;
        _mismatchTotal = 0;
        _dumps = 0;
        _pending = false;
        _pendingEntry = null;
        _window.Clear();
        _store.Evictions = 0;
    }
}
