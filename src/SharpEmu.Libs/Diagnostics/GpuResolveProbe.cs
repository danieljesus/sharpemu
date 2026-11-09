// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_GPU_RESOLVE_PROBE=1 (off by default): gate G1 of "descriptors resolved on the GPU" (track9 §6,
// sharpemu-rediseno-draws.md §13), in shadow. For every draw and dispatch the render thread records, each resource
// entry the shader reads is classified by what moving its resolution into the shader would need. Nothing recorded
// changes: the probe reads the plans, the snapshots, the GPU write tracking and the image cache without touching them
// (the image lookup runs the slice lookup with its memo and its failure statistics left alone).
//
// Entries, per stage of the item:
//   v    the V#s of the program (ShaderResourceInfo.Buffers, with the candidates of bounded runtime V# tables)
//   bda  the device-address ranges of the snapshot (V#s and pointers the shader reaches by address)
//   t, s the T#s and S#s
//   w    the table words the materialization evaluates (plan.TableReads, plus plan.DynamicReads)
//
// Static origin, once per plan, from the value graph of the descriptor's dwords (or the word's value):
//   immediate  only user data, constants and the shader base: class 1
//   memory     some dword is a scalar load (ResourceTableWord, ScalarBufferWord, ScalarAddressWord): class 2 or 3
//   indexed    a material-table image (IndirectImage, resolved on the GPU among bounded candidates today) or a
//              candidate of a bounded runtime V# table: class 4a
//   waterfall  a FirstLane or a Phi in the chain, a call record, a store-table range: class 4b
//
// Class 2 against 3 for memory entries: the guest reads of the materialization that produced the snapshot (the three
// readers ShaderProgramCache passes to it are wrapped while the probe is on), by bytes and classified before the read:
//   2   never written by the GPU (EverGpuWrittenRanges false)
//   3w  written by the GPU at some point, CPU copy current now
//   3s  GPU-dirty, last writer (the tighter of the precise write history and the 16 KB block tick) in the open batch
//   3f  GPU-dirty, writer submitted and still in flight
//   3d  GPU-dirty, writer completed, bytes not downloaded yet
//   3u  written by the GPU, read on the front (lot unknown: the read goes to the render thread or is answered by memo)
// Memo answers (TABLE_WORD_MEMO) pass through the readers and count by the bytes' state, as data the GPU wrote. A
// snapshot the probe did not see materialized falls back to the last one of the same program; with no reads at all
// (fast path, hits that read nothing) the memory entries count as class 2 and memdist_missing says how many.
//
// Class 5, what keeps the CPU in the loop even with the entry resolved in the shader:
//   5a  the T# needs host work: no registered image (create, grow, park), another format or view, an image the CPU
//       dirtied (upload, detile) or GPU buffer writes over it (copy), a display surface
//   5m  the T# needs metadata work (DCC sync or clear, texture metadata)
//   5c  the T# needed an image layout transition in this item (RecordStageTextureTransitions recorded barriers)
//   5b  the program's resource specialization (image class, dimension, format, swizzle; buffer stride and format)
//       has taken more than one value for this plan: the descriptor decides the SPIR-V variant
//   5e  a V# or range the shader writes whose address comes from memory: the CPU has to mark the store
// Translatable = classes 1 to 4. A store through a V# from user data stays class 1: the CPU knows its range.
//
// Cost, when SHARPEMU_DRAWCOST_PROBE is on (same window): the DRAWCOST steps go in buckets, V (bind_memo_buffers,
// bind_memo_ranges, bda, res_buffers, res_ranges, res_upload_end, draw_buffers), T (bind_memo_find, bind_memo_images,
// res_images, draw_stage_images, bind_miss), W (program_materialize, program_rebind, program_stage) and D (commit_build,
// commit_push, commit_tail, bind_memo_rent, bind_prelude); every other step stays on the CPU. A bucket goes with the
// translatable share of its entries (V: v+bda, T: t+s, W: w, D: all), down to a floor it keeps anyway (track9 §3: draw
// V 0.4, T 0.8, D 0.2; dispatch V 2.5, T 0.2, W 1.0, D 1.0 µs). residual_us = item - saved; residual_cal halves the
// saving (the agents' estimates ran ~2x optimistic). translatable_cost weights the buckets' shares by their µs. The
// probe's own time is taken out of the DRAWCOST laps.
//
// SHARPEMU_GPU_RESOLVE_SAMPLE=N classifies one item in N (default 1). One line per kind and window:
//
//   [PERF][GPU_RESOLVE] kind=draw window_s=5.0 frames=25 items_per_frame=3800 sampled=95000
//       entries_per_item=v:6.6,bda:0.1,t:2.3,s:2.0,w:9.4 n=1:..,2:..,3w:..,3s:..,3f:..,3d:..,3u:..,4a:..,4b:..,5a:..,5m:..,5c:..,5b:..,5e:..
//       translatable=93.1 by_kind=v:..,bda:..,t:..,s:..,w:.. tex=reg:..,regw:..,null:..,upload:..,bufw:..,create:..,format:..,meta:..,display:..,other:..
//       transitions=0.4 spec_multi=img:..,buf:.. untallied=.. memdist_missing=.. reads=2:..,3w:..,3s:..,3f:..,3d:..,3u:.. read_kb_per_item=..
//       probe_us=.. cost=V:4.1/93.0,T:2.0/80.0,W:0.1/90.0,D:1.5/88.0,stay:12.0 translatable_cost=.. item_us=.. residual_us=.. residual_cal=..
//
// n = % of the entries by class; by_kind = translatable % per kind; tex and transitions per item; spec_multi = % of the
// sampled items with a stage whose plan has taken several image (buffer) specializations; reads = % of the bytes the
// materializations of programs of this kind read, by class.
internal static class GpuResolveProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_GPU_RESOLVE_PROBE") == "1";

    private static readonly int SampleEvery =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_GPU_RESOLVE_SAMPLE"), out var every) && every > 0 ? every : 1;

    private static readonly long ReportTicks = (long)((double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_GPU_RESOLVE_REPORT_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 5.0) * Stopwatch.Frequency);

    internal enum ReadOrigin : byte
    {
        Clean,
        GpuWrittenClean,
        GpuSameBatch,
        GpuInFlight,
        GpuCompleted,
        GpuUnknown,
        Count,
    }

    internal enum Origin : byte
    {
        Immediate,
        Memory,
        Indexed,
        Waterfall,
    }

    internal enum Kind
    {
        Vsharp,
        Bda,
        Tsharp,
        Ssharp,
        Word,
        Count,
    }

    internal enum Class
    {
        C1,
        C2,
        C3Clean,
        C3Same,
        C3Flight,
        C3Done,
        C3Unknown,
        C4a,
        C4b,
        C5Host,
        C5Meta,
        C5Transition,
        C5Variant,
        C5Store,
        Count,
    }

    internal enum TextureOutcome
    {
        Registered,
        RegisteredGpuWritten,
        Null,
        Upload,
        BufferWritten,
        Create,
        Format,
        Meta,
        Display,
        Other,
        Count,
    }

    private const int Kinds = (int)Kind.Count;
    private const int Classes = (int)Class.Count;
    private const int Reads = (int)ReadOrigin.Count;
    private const int Textures = (int)TextureOutcome.Count;
    private const int FirstUntranslatable = (int)Class.C5Host;

    private static readonly string[] KindNames = ["v", "bda", "t", "s", "w"];
    private static readonly string[] ClassNames = ["1", "2", "3w", "3s", "3f", "3d", "3u", "4a", "4b", "5a", "5m", "5c", "5b", "5e"];
    private static readonly string[] ReadNames = ["2", "3w", "3s", "3f", "3d", "3u"];
    private static readonly string[] TextureNames = ["reg", "regw", "null", "upload", "bufw", "create", "format", "meta", "display", "other"];

    private static readonly Class[] ReadClass = [Class.C2, Class.C3Clean, Class.C3Same, Class.C3Flight, Class.C3Done, Class.C3Unknown];

    // Set by the buffer cache's owner: where the bytes of a guest read stand, asked before the read.
    internal static Func<ulong, ulong, ReadOrigin>? Classifier;

    // ---- Static classification of a plan.

    internal sealed class PlanInfo
    {
        public Origin[] Buffers = [];
        public Origin[] Images = [];
        public Origin[] Samplers = [];
        public Dictionary<uint, Origin> Ranges = new();
        public int[] Words = new int[4];
        public bool Compute;
        // Per image and per base buffer: the first specialization seen, and whether another one has been seen since.
        private ImageSpecialization?[] _imageSpecs = [];
        private BufferSpecialization?[] _bufferSpecs = [];
        public bool[] MultiImage = [];
        public bool[] MultiBuffer = [];

        public void NoteSpecialization(ResourceSpecialization specialization)
        {
            lock (this)
            {
                Note(specialization.Images, ref _imageSpecs, ref MultiImage);
                var buffers = specialization.Buffers;
                var count = Math.Min(buffers.Count, Math.Max(specialization.BaseBufferCount, 0));
                Note(count == buffers.Count ? buffers : buffers.GetRange(0, count), ref _bufferSpecs, ref MultiBuffer);
            }
        }

        private static void Note<T>(List<T> values, ref T?[] first, ref bool[] multi) where T : struct, IEquatable<T>
        {
            if (first.Length < values.Count)
            {
                Array.Resize(ref first, values.Count);
                var grown = new bool[values.Count];
                multi.CopyTo(grown, 0);
                Volatile.Write(ref multi, grown);
            }

            for (var index = 0; index < values.Count; index++)
            {
                if (first[index] is not { } seen)
                    first[index] = values[index];
                else if (!seen.Equals(values[index]))
                    multi[index] = true;
            }
        }

        public bool ImageVaries(int index) => Volatile.Read(ref MultiImage) is var multi && index < multi.Length && multi[index];
        public bool BufferVaries(int index) => Volatile.Read(ref MultiBuffer) is var multi && index < multi.Length && multi[index];
        public bool AnyImageVaries => Array.IndexOf(Volatile.Read(ref MultiImage), true) >= 0;
        public bool AnyBufferVaries => Array.IndexOf(Volatile.Read(ref MultiBuffer), true) >= 0;
    }

    private static readonly ConditionalWeakTable<ShaderResourcePlan, PlanInfo> _plans = new();
    private static readonly ConcurrentDictionary<ulong, PlanInfo> _plansByHash = new();

    internal static PlanInfo PlanOf(ShaderResourcePlan plan) => _plans.GetValue(plan, Build);

    private static Origin Max(Origin left, Origin right) => left > right ? left : right;

    internal static Origin OriginOf(ScalarValue? value, Dictionary<ScalarValue, Origin> memo, int depth = 0)
    {
        if (value is null)
            return Origin.Immediate;
        if (memo.TryGetValue(value, out var known))
            return known;
        Origin origin;
        switch (value.Kind)
        {
            case ScalarValueKind.Constant:
            case ScalarValueKind.Undefined:
            case ScalarValueKind.UserData:
            case ScalarValueKind.ShaderBase:
            case ScalarValueKind.MemoryAperture:
                origin = Origin.Immediate;
                break;
            case ScalarValueKind.FirstLane:
            case ScalarValueKind.Phi:
                origin = Origin.Waterfall;
                break;
            default:
                origin = value.Kind is ScalarValueKind.ResourceTableWord or ScalarValueKind.ScalarBufferWord or ScalarValueKind.ScalarAddressWord
                    ? Origin.Memory
                    : Origin.Immediate;
                if (depth > 512)
                {
                    origin = Max(origin, Origin.Memory);
                    break;
                }

                foreach (var operand in value.Operands)
                {
                    origin = Max(origin, OriginOf(operand, memo, depth + 1));
                    if (origin == Origin.Waterfall)
                        break;
                }

                break;
        }

        memo[value] = origin;
        return origin;
    }

    private static Origin SourceOrigin(ShaderResourcePlan plan, uint source, Dictionary<ScalarValue, Origin> memo)
    {
        if (source >= plan.DescriptorSources.Count)
            return Origin.Memory;
        var descriptor = plan.DescriptorSources[(int)source];
        if (descriptor.CallRecord is not null)
            return Origin.Waterfall;
        if (descriptor.IndirectImage is not null)
            return Origin.Indexed;
        var origin = Origin.Immediate;
        foreach (var dword in descriptor.Dwords)
            origin = Max(origin, OriginOf(dword, memo));
        return origin;
    }

    private static PlanInfo Build(ShaderResourcePlan plan)
    {
        var memo = new Dictionary<ScalarValue, Origin>();
        var info = new PlanInfo { Compute = plan.Stage == ShaderStage.Compute };
        try
        {
            var buffers = plan.Info.Buffers;
            info.Buffers = new Origin[buffers.Count];
            for (var index = 0; index < buffers.Count; index++)
                info.Buffers[index] = SourceOrigin(plan, buffers[index].Source, memo);
            foreach (var table in plan.Info.BufferCandidateTables)
                for (var candidate = table.FirstCandidate; candidate < table.FirstCandidate + table.CandidateCount && candidate < info.Buffers.Length; candidate++)
                    info.Buffers[candidate] = Max(info.Buffers[candidate], Origin.Indexed);
            info.Images = new Origin[plan.Info.Images.Count];
            for (var index = 0; index < info.Images.Length; index++)
                info.Images[index] = SourceOrigin(plan, plan.Info.Images[index].Source, memo);
            info.Samplers = new Origin[plan.Info.Samplers.Count];
            for (var index = 0; index < info.Samplers.Length; index++)
                info.Samplers[index] = SourceOrigin(plan, plan.Info.Samplers[index].Source, memo);
            foreach (var range in plan.DeviceAddressRanges)
                info.Ranges[range.Handle] = Max(OriginOf(range.BaseLow, memo), OriginOf(range.BaseHigh, memo));
            foreach (var read in plan.TableReads)
                info.Words[(int)Max(Origin.Memory, OriginOf(read.Value, memo))]++;
            info.Words[(int)Origin.Waterfall] += plan.DynamicReads.Count;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[GPU][GPU_RESOLVE] plan 0x{plan.Hash:X16} not classified: {exception.GetType().Name}: {exception.Message}");
        }

        _plansByHash[plan.Hash] = info;
        return info;
    }

    // ---- Reads of one materialization.

    internal sealed class Tally
    {
        public PlanInfo? Plan;
        public readonly long[] Bytes = new long[Reads];
        // Reads classified GpuUnknown off the GPU queue thread (the front): asked again when an item that binds the
        // snapshot is recorded, where the writer's tick can be placed. Bounded; the bytes beyond stay unknown.
        public readonly List<(ulong Address, ulong Size)> Unknown = new();
        public const int UnknownCapacity = 128;

        public bool HasReads
        {
            get
            {
                foreach (var bytes in Bytes)
                    if (bytes != 0)
                        return true;
                return false;
            }
        }
    }

    internal readonly record struct MaterializeToken(Tally? Previous, Tally? Current);

    [ThreadStatic] private static Tally? _current;

    private static readonly ConditionalWeakTable<ResourceSnapshot, Tally> _snapshots = new();
    private static readonly ConcurrentDictionary<ulong, Tally> _lastByHash = new();
    private static readonly long[][] _readBytes = [new long[Reads], new long[Reads]];

    internal static MaterializeToken BeginMaterialize()
    {
        if (!Enabled)
            return default;
        var current = new Tally();
        _current = current;
        return new MaterializeToken(null, current); // a materialization that threw leaves its tally behind; the next one replaces it
    }

    internal static void EndMaterialize(in MaterializeToken token, ShaderResourcePlan plan, ResourceSnapshot? snapshot,
        ResourceSpecialization? specialization, bool materialized)
    {
        if (token.Current is not { } tally)
            return;
        _current = token.Previous;
        if (!materialized || snapshot is null)
            return;
        var started = Stopwatch.GetTimestamp();
        tally.Plan = PlanOf(plan);
        if (specialization is not null)
            tally.Plan.NoteSpecialization(specialization);
        var kind = tally.Plan.Compute ? 1 : 0;
        for (var origin = 0; origin < Reads; origin++)
            if (tally.Bytes[origin] != 0)
                Interlocked.Add(ref _readBytes[kind][origin], tally.Bytes[origin]);
        _snapshots.AddOrUpdate(snapshot, tally);
        if (tally.HasReads)
            _lastByHash[plan.Hash] = tally;
        DrawCostProbe.Exclude(Stopwatch.GetTimestamp() - started);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NoteRead(ulong address, ulong size)
    {
        if (_current is { } tally)
            NoteReadCore(tally, address, size);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NoteReadCore(Tally tally, ulong address, ulong size)
    {
        if (size == 0)
            return;
        var started = Stopwatch.GetTimestamp();
        var origin = ReadOrigin.Clean;
        try
        {
            origin = Classifier?.Invoke(address, size) ?? ReadOrigin.Clean;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            origin = ReadOrigin.GpuUnknown;
        }

        tally.Bytes[(int)origin] += (long)size;
        if (origin == ReadOrigin.GpuUnknown && tally.Unknown.Count < Tally.UnknownCapacity)
            tally.Unknown.Add((address, size));
        DrawCostProbe.Exclude(Stopwatch.GetTimestamp() - started);
    }

    // ---- Items on the recording thread.

    private sealed class Window
    {
        public readonly double[] Entries = new double[2 * Kinds * Classes];
        public readonly long[] Items = new long[2];
        public readonly long[] Sampled = new long[2];
        public readonly long[] TextureOutcomes = new long[2 * Textures];
        public readonly long[] Transitions = new long[2];
        public readonly long[] MultiImage = new long[2];
        public readonly long[] MultiBuffer = new long[2];
        public readonly double[] Untallied = new double[2];
        public readonly double[] MemDistMissing = new double[2];
        public readonly long[] ProbeTicks = new long[2];

        public void Clear()
        {
            Array.Clear(Entries);
            Array.Clear(Items);
            Array.Clear(Sampled);
            Array.Clear(TextureOutcomes);
            Array.Clear(Transitions);
            Array.Clear(MultiImage);
            Array.Clear(MultiBuffer);
            Array.Clear(Untallied);
            Array.Clear(MemDistMissing);
            Array.Clear(ProbeTicks);
        }
    }

    private static readonly Window _window = new();
    private static int _thread;
    private static int _depth;
    private static int _kind = -1;
    private static bool _sampledItem;
    private static long _sequence;
    private static long _windowStart;
    private static long _frames;
    private static readonly double[] _item = new double[Kinds * Classes];
    private static double _itemTranslatableTextures;
    private static long _itemTransitions;
    private static bool _itemMultiImage, _itemMultiBuffer;

    // Image barriers recorded by CachedImage on this thread (RecordStageTextureTransitions takes the difference).
    [ThreadStatic] internal static long ImageBarriers;

    internal static void NoteFrame()
    {
        if (Enabled)
            Interlocked.Increment(ref _frames);
    }

    internal static void Begin(bool dispatch)
    {
        var thread = Environment.CurrentManagedThreadId;
        if (_thread == 0)
        {
            _thread = thread;
            _windowStart = Stopwatch.GetTimestamp();
        }

        if (thread != _thread || _depth++ != 0)
            return;
        _kind = dispatch ? 1 : 0;
        _window.Items[_kind]++;
        _sampledItem = ++_sequence % SampleEvery == 0;
        if (_sampledItem)
        {
            Array.Clear(_item);
            _itemTranslatableTextures = 0;
            _itemTransitions = 0;
            _itemMultiImage = _itemMultiBuffer = false;
        }
    }

    internal static bool Owns
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Enabled && _kind >= 0 && _sampledItem && Environment.CurrentManagedThreadId == _thread;
    }

    internal static void End()
    {
        if (Environment.CurrentManagedThreadId != _thread || _depth == 0 || --_depth != 0)
            return;
        var kind = _kind;
        _kind = -1;
        if (kind >= 0 && _sampledItem)
            CloseItem(kind);
        if (!DrawCostProbe.Enabled && Stopwatch.GetTimestamp() - _windowStart >= ReportTicks)
            Report(null, null);
    }

    private static void CloseItem(int kind)
    {
        // The T# entries the item transitioned move to 5c, taken evenly from their translatable classes.
        var moved = Math.Min(_itemTransitions, _itemTranslatableTextures);
        if (moved > 0)
        {
            var keep = 1 - moved / _itemTranslatableTextures;
            var row = (int)Kind.Tsharp * Classes;
            for (var @class = 0; @class < FirstUntranslatable; @class++)
                _item[row + @class] *= keep;
            _item[row + (int)Class.C5Transition] += moved;
        }

        var offset = kind * Kinds * Classes;
        for (var index = 0; index < _item.Length; index++)
            _window.Entries[offset + index] += _item[index];
        _window.Sampled[kind]++;
        _window.Transitions[kind] += _itemTransitions;
        if (_itemMultiImage) _window.MultiImage[kind]++;
        if (_itemMultiBuffer) _window.MultiBuffer[kind]++;
    }

    internal static void NoteTransitions(long barriers)
    {
        if (Owns && barriers > 0)
            _itemTransitions += barriers;
    }

    private static void Add(Kind kind, Class @class, double weight = 1) => _item[(int)kind * Classes + (int)@class] += weight;

    private static void AddOrigin(Kind kind, Origin origin, double[] distribution, double weight = 1)
    {
        switch (origin)
        {
            case Origin.Immediate:
                Add(kind, Class.C1, weight);
                break;
            case Origin.Indexed:
                Add(kind, Class.C4a, weight);
                break;
            case Origin.Waterfall:
                Add(kind, Class.C4b, weight);
                break;
            default:
                for (var read = 0; read < Reads; read++)
                    if (distribution[read] != 0)
                        Add(kind, ReadClass[read], weight * distribution[read]);
                break;
        }
    }

    private static readonly double[] _distribution = new double[Reads];
    // Per kind: the read classes of the stages recorded (each stage's distribution counted once, with the front's
    // unknown reads placed at record time).
    private static readonly double[] _recordedReads = new double[2 * Reads];

    // One stage of the item, at the host's PrepareBindings on the recording thread. texture classifies one T# against
    // the image cache (null when there is none).
    internal static void NoteStage(ulong programHash, ResourceSnapshot snapshot, ShaderResourceInfo info, bool[] writtenBuffers,
        bool[] writtenImages, Func<int, TextureOutcome>? texture)
    {
        if (!Owns)
            return;
        var started = Stopwatch.GetTimestamp();
        var kind = _kind;
        _snapshots.TryGetValue(snapshot, out var tally);
        var plan = tally?.Plan ?? (_plansByHash.TryGetValue(programHash, out var byHash) ? byHash : null);
        var entries = snapshot.Buffers.Length + snapshot.DeviceAddressRanges.Length + snapshot.Images.Length + snapshot.Samplers.Length;
        if (plan is null)
        {
            _window.Untallied[kind] += entries;
            DrawCostProbe.Exclude(Stopwatch.GetTimestamp() - started);
            return;
        }

        var source = tally is not null && tally.HasReads ? tally : _lastByHash.TryGetValue(programHash, out var last) ? last : null;
        Array.Clear(_distribution);
        double total = 0;
        if (source is not null)
        {
            for (var read = 0; read < Reads; read++)
                _distribution[read] = source.Bytes[read];
            // The front's unknown reads, placed now that this item is being recorded. The tally is complete once it is
            // published (EndMaterialize), so its list no longer changes.
            foreach (var (address, size) in source.Unknown)
            {
                ReadOrigin origin;
                try
                {
                    origin = Classifier?.Invoke(address, size) ?? ReadOrigin.GpuUnknown;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    origin = ReadOrigin.GpuUnknown;
                }

                _distribution[(int)ReadOrigin.GpuUnknown] -= size;
                _distribution[(int)origin] += size;
            }

            for (var read = 0; read < Reads; read++)
                total += _distribution[read];
        }

        var missing = total <= 0;
        if (missing)
        {
            Array.Clear(_distribution);
            _distribution[(int)ReadOrigin.Clean] = 1;
        }
        else
        {
            for (var read = 0; read < Reads; read++)
            {
                _distribution[read] /= total;
                _recordedReads[kind * Reads + read] += _distribution[read];
            }
        }

        _itemMultiImage |= plan.AnyImageVaries;
        _itemMultiBuffer |= plan.AnyBufferVaries;
        double memoryEntries = 0;

        for (var index = 0; index < snapshot.Buffers.Length; index++)
        {
            var origin = index < plan.Buffers.Length ? plan.Buffers[index] : Origin.Indexed;
            var written = index < writtenBuffers.Length && writtenBuffers[index];
            if (written && origin != Origin.Immediate)
                Add(Kind.Vsharp, Class.C5Store);
            else if (origin != Origin.Immediate && plan.BufferVaries(index))
                Add(Kind.Vsharp, Class.C5Variant);
            else
                AddOrigin(Kind.Vsharp, origin, _distribution);
            if (origin == Origin.Memory) memoryEntries++;
        }

        foreach (var range in snapshot.DeviceAddressRanges)
        {
            var origin = plan.Ranges.TryGetValue(range.Handle, out var known) ? known : Origin.Waterfall;
            if (range.Written && origin != Origin.Immediate)
                Add(Kind.Bda, Class.C5Store);
            else
                AddOrigin(Kind.Bda, origin, _distribution);
            if (origin == Origin.Memory) memoryEntries++;
        }

        var textures = _window.TextureOutcomes;
        for (var index = 0; index < snapshot.Images.Length; index++)
        {
            var origin = index < plan.Images.Length ? plan.Images[index] : Origin.Indexed;
            var outcome = texture?.Invoke(index) ?? TextureOutcome.Other;
            textures[kind * Textures + (int)outcome]++;
            if (outcome is TextureOutcome.Meta)
                Add(Kind.Tsharp, Class.C5Meta);
            else if (outcome is not (TextureOutcome.Registered or TextureOutcome.RegisteredGpuWritten or TextureOutcome.Null))
                Add(Kind.Tsharp, Class.C5Host);
            else if (origin != Origin.Immediate && plan.ImageVaries(index))
                Add(Kind.Tsharp, Class.C5Variant);
            else if (index < writtenImages.Length && writtenImages[index] && origin != Origin.Immediate)
                Add(Kind.Tsharp, Class.C5Store);
            else
            {
                AddOrigin(Kind.Tsharp, origin, _distribution);
                _itemTranslatableTextures++;
            }

            if (origin == Origin.Memory) memoryEntries++;
        }

        for (var index = 0; index < snapshot.Samplers.Length; index++)
        {
            var origin = index < plan.Samplers.Length ? plan.Samplers[index] : Origin.Indexed;
            AddOrigin(Kind.Ssharp, origin, _distribution);
            if (origin == Origin.Memory) memoryEntries++;
        }

        AddOrigin(Kind.Word, Origin.Memory, _distribution, plan.Words[(int)Origin.Memory]);
        AddOrigin(Kind.Word, Origin.Immediate, _distribution, plan.Words[(int)Origin.Immediate]);
        AddOrigin(Kind.Word, Origin.Indexed, _distribution, plan.Words[(int)Origin.Indexed]);
        AddOrigin(Kind.Word, Origin.Waterfall, _distribution, plan.Words[(int)Origin.Waterfall]);
        memoryEntries += plan.Words[(int)Origin.Memory];
        if (missing)
            _window.MemDistMissing[kind] += memoryEntries;
        var elapsed = Stopwatch.GetTimestamp() - started;
        _window.ProbeTicks[kind] += elapsed;
        DrawCostProbe.Exclude(elapsed);
    }

    // ---- Report.

    private static readonly DrawCostProbe.Step[][] Buckets =
    [
        [
            DrawCostProbe.Step.BindMemoBuffers, DrawCostProbe.Step.BindMemoRanges, DrawCostProbe.Step.Bda, DrawCostProbe.Step.ResBuffers,
            DrawCostProbe.Step.ResRanges, DrawCostProbe.Step.ResUploadEnd, DrawCostProbe.Step.DrawBuffers,
        ],
        [
            DrawCostProbe.Step.BindMemoFind, DrawCostProbe.Step.BindMemoImages, DrawCostProbe.Step.ResImages, DrawCostProbe.Step.DrawStageImages,
            DrawCostProbe.Step.BindMiss,
        ],
        [DrawCostProbe.Step.ProgramMaterialize, DrawCostProbe.Step.ProgramRebind, DrawCostProbe.Step.ProgramStage],
        [
            DrawCostProbe.Step.CommitBuild, DrawCostProbe.Step.CommitPush, DrawCostProbe.Step.CommitTail, DrawCostProbe.Step.BindMemoRent,
            DrawCostProbe.Step.BindPrelude,
        ],
    ];

    private static readonly string[] BucketNames = ["V", "T", "W", "D"];

    // Floors a translated bucket keeps (track9 §3), per kind.
    private static readonly double[][] Floors = [[0.4, 0.8, 0.0, 0.2], [2.5, 0.2, 1.0, 1.0]];

    internal readonly record struct Gate(double TranslatableCost, double ItemUs, double ResidualUs, double ResidualCalibrated);

    // Translatable share of the entries of the given kinds in a window row; -1 when there are none.
    private static double Share(double[] entries, int kind, params Kind[] kinds)
    {
        double all = 0, translatable = 0;
        foreach (var entryKind in kinds)
        {
            var row = (kind * Kinds + (int)entryKind) * Classes;
            for (var @class = 0; @class < Classes; @class++)
            {
                all += entries[row + @class];
                if (@class < FirstUntranslatable)
                    translatable += entries[row + @class];
            }
        }

        return all == 0 ? -1 : translatable / all;
    }

    // The gate's arithmetic over one kind's step costs (µs per item, indexed by DrawCostProbe.Step) and its item cost.
    internal static Gate Evaluate(int kind, double[] shares, double[] stepUs, double itemUs)
    {
        double bucketsUs = 0, weighted = 0, saved = 0;
        for (var bucket = 0; bucket < Buckets.Length; bucket++)
        {
            double us = 0;
            foreach (var step in Buckets[bucket])
                us += stepUs[(int)step];
            var share = shares[bucket] < 0 ? 1 : shares[bucket];
            bucketsUs += us;
            weighted += us * share;
            saved += share * Math.Max(0, us - Floors[kind][bucket]);
        }

        var residual = itemUs - saved;
        return new Gate(bucketsUs == 0 ? 0 : weighted / bucketsUs, itemUs, residual, itemUs - saved * 0.5);
    }

    private static double[] BucketShares(double[] entries, int kind) =>
    [
        Share(entries, kind, Kind.Vsharp, Kind.Bda),
        Share(entries, kind, Kind.Tsharp, Kind.Ssharp),
        Share(entries, kind, Kind.Word),
        Share(entries, kind, Kind.Vsharp, Kind.Bda, Kind.Tsharp, Kind.Ssharp, Kind.Word),
    ];

    // Called by DrawCostProbe at its report (stepUs[kind][step] in µs per timed item, itemUs[kind]), or by End when
    // that probe is off (both null).
    internal static void Report(double[][]? stepUs, double[]? itemUs)
    {
        if (!Enabled || _thread == 0)
            return;
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        var frames = Interlocked.Exchange(ref _frames, 0);
        var perFrame = frames > 0 ? frames : 1;
        var toUs = 1e6 / Stopwatch.Frequency;
        var entries = _window.Entries;
        for (var kind = 0; kind < 2; kind++)
        {
            var sampled = _window.Sampled[kind];
            var reads = new long[Reads];
            for (var read = 0; read < Reads; read++)
                reads[read] = Interlocked.Exchange(ref _readBytes[kind][read], 0);
            if (_window.Items[kind] == 0)
                continue;
            var line = new StringBuilder(1024);
            line.Append(FormattableString.Invariant(
                $"[PERF][GPU_RESOLVE] kind={(kind == 0 ? "draw" : "dispatch")} window_s={seconds:F1} frames={frames} items_per_frame={_window.Items[kind] / (double)perFrame:F0} sampled={sampled}"));
            var per = Math.Max(sampled, 1);
            double all = 0, translatable = 0;
            var byClass = new double[Classes];
            line.Append(" entries_per_item=");
            for (var entryKind = 0; entryKind < Kinds; entryKind++)
            {
                double kindAll = 0;
                for (var @class = 0; @class < Classes; @class++)
                {
                    var value = entries[(kind * Kinds + entryKind) * Classes + @class];
                    kindAll += value;
                    byClass[@class] += value;
                    if (@class < FirstUntranslatable)
                        translatable += value;
                }

                all += kindAll;
                line.Append(FormattableString.Invariant($"{(entryKind == 0 ? "" : ",")}{KindNames[entryKind]}:{kindAll / per:F2}"));
            }

            line.Append(" n=");
            for (var @class = 0; @class < Classes; @class++)
                line.Append(FormattableString.Invariant($"{(@class == 0 ? "" : ",")}{ClassNames[@class]}:{(all == 0 ? 0 : byClass[@class] * 100 / all):F1}"));
            line.Append(FormattableString.Invariant($" translatable={(all == 0 ? 0 : translatable * 100 / all):F1} by_kind="));
            for (var entryKind = 0; entryKind < Kinds; entryKind++)
            {
                var share = Share(entries, kind, (Kind)entryKind);
                line.Append(FormattableString.Invariant($"{(entryKind == 0 ? "" : ",")}{KindNames[entryKind]}:{(share < 0 ? -1 : share * 100):F1}"));
            }

            line.Append(" tex=");
            for (var outcome = 0; outcome < Textures; outcome++)
                line.Append(FormattableString.Invariant($"{(outcome == 0 ? "" : ",")}{TextureNames[outcome]}:{_window.TextureOutcomes[kind * Textures + outcome] / (double)per:F3}"));
            line.Append(FormattableString.Invariant(
                $" transitions={_window.Transitions[kind] / (double)per:F2} spec_multi=img:{_window.MultiImage[kind] * 100.0 / per:F1},buf:{_window.MultiBuffer[kind] * 100.0 / per:F1}"));
            line.Append(FormattableString.Invariant(
                $" untallied={_window.Untallied[kind] / per:F2} memdist_missing={_window.MemDistMissing[kind] / per:F2}"));
            long readTotal = 0;
            foreach (var bytes in reads)
                readTotal += bytes;
            line.Append(" reads=");
            for (var read = 0; read < Reads; read++)
                line.Append(FormattableString.Invariant($"{(read == 0 ? "" : ",")}{ReadNames[read]}:{(readTotal == 0 ? 0 : reads[read] * 100.0 / readTotal):F1}"));
            line.Append(FormattableString.Invariant(
                $" read_kb_per_item={readTotal / 1024.0 / Math.Max(_window.Items[kind], 1):F3} recorded="));
            double recordedTotal = 0;
            for (var read = 0; read < Reads; read++)
                recordedTotal += _recordedReads[kind * Reads + read];
            for (var read = 0; read < Reads; read++)
                line.Append(FormattableString.Invariant(
                    $"{(read == 0 ? "" : ",")}{ReadNames[read]}:{(recordedTotal == 0 ? 0 : _recordedReads[kind * Reads + read] * 100 / recordedTotal):F1}"));
            line.Append(FormattableString.Invariant($" probe_us={_window.ProbeTicks[kind] * toUs / per:F2}"));
            line.Append(FormattableString.Invariant($" translatable_vts={TranslatableVts(entries, kind)}"));
            if (stepUs is not null && itemUs is not null)
            {
                var shares = BucketShares(entries, kind);
                var gate = Evaluate(kind, shares, stepUs[kind], itemUs[kind]);
                line.Append(" cost=");
                double bucketsUs = 0;
                for (var bucket = 0; bucket < Buckets.Length; bucket++)
                {
                    double us = 0;
                    foreach (var step in Buckets[bucket])
                        us += stepUs[kind][(int)step];
                    bucketsUs += us;
                    line.Append(FormattableString.Invariant($"{BucketNames[bucket]}:{us:F2}/{(shares[bucket] < 0 ? -1 : shares[bucket] * 100):F1},"));
                }

                line.Append(FormattableString.Invariant(
                    $"stay:{itemUs[kind] - bucketsUs:F2} translatable_cost={gate.TranslatableCost * 100:F1} item_us={itemUs[kind]:F2} residual_us={gate.ResidualUs:F2} residual_cal={gate.ResidualCalibrated:F2}"));
                var planned = PlanSaving(kind, stepUs[kind]);
                line.Append(FormattableString.Invariant(
                    $" residual_plan={gate.ResidualUs - planned:F2} residual_plan_cal={gate.ResidualCalibrated - planned * 0.5:F2}"));
            }

            Console.Error.WriteLine(line.ToString());
        }

        _window.Clear();
        Array.Clear(_recordedReads);
        _windowStart = Stopwatch.GetTimestamp();
    }

    private static string TranslatableVts(double[] entries, int kind)
    {
        var share = Share(entries, kind, Kind.Vsharp, Kind.Bda, Kind.Tsharp, Kind.Ssharp);
        return FormattableString.Invariant($"{(share < 0 ? -1 : share * 100):F1}");
    }

    // [local] The stay side as track9 §3 projects it on top of C3 (a stated assumption, not measured here): a plan per
    // draw shape and MDI emission bring the targets to 0.6 µs, the pipeline to 0.3, the emission to 0.3 and the plumbing
    // (entry, preparation, hand-over, closing) to 2.5 per draw; a dispatch keeps its scheduler and relay and brings its
    // emission to 0.4. What that would save beyond the C3 buckets.
    private static readonly (DrawCostProbe.Step[] Steps, double Cap)[][] PlanGroups =
    [
        [
            ([DrawCostProbe.Step.ExecTargets, DrawCostProbe.Step.RecTargets, DrawCostProbe.Step.DrawAttachments], 0.6),
            ([DrawCostProbe.Step.DrawPipeline, DrawCostProbe.Step.ExecPrograms, DrawCostProbe.Step.PlanLookup, DrawCostProbe.Step.PlanCompare], 0.3),
            ([DrawCostProbe.Step.DrawDynamic, DrawCostProbe.Step.DrawBeginRendering, DrawCostProbe.Step.DrawEmit, DrawCostProbe.Step.DrawTail,
              DrawCostProbe.Step.DrawBindVertex], 0.3),
            ([DrawCostProbe.Step.Snapshots, DrawCostProbe.Step.ExecPrelude, DrawCostProbe.Step.ExecAdapt, DrawCostProbe.Step.ExecFill,
              DrawCostProbe.Step.RecHandOver, DrawCostProbe.Step.PrepBegin, DrawCostProbe.Step.BindMemoShaderData, DrawCostProbe.Step.PrepEnd,
              DrawCostProbe.Step.RecReturn, DrawCostProbe.Step.Exit], 2.5),
        ],
        [
            ([DrawCostProbe.Step.DispatchBarrier, DrawCostProbe.Step.DispatchEmit, DrawCostProbe.Step.DispatchTail], 0.4),
        ],
    ];

    internal static double PlanSaving(int kind, double[] stepUs)
    {
        double saved = 0;
        foreach (var (steps, cap) in PlanGroups[kind])
        {
            double us = 0;
            foreach (var step in steps)
                us += stepUs[(int)step];
            saved += Math.Max(0, us - cap);
        }

        return saved;
    }

    internal static void ResetForTests()
    {
        _thread = 0;
        _depth = 0;
        _kind = -1;
        _sequence = 0;
        _window.Clear();
    }
}
