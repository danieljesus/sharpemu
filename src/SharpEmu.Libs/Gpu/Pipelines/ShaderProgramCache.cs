// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using ImageResourceClass = SharpEmu.Libs.Gpu.Rendering.ImageResourceClass;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Gpu.Pipelines;

// One program as a draw names it: the registered code, its identity and the user data of the draw.
public sealed record ShaderSource(RegisteredShader Registered, ulong Hash, uint[] UserData, uint UserDataBase, ShaderStage Stage)
{
    public ulong Address => Registered.CodeAddress;

    public uint CodeSize => Registered.TotalCodeSizeBytes;

    public string Label => Stage switch
    {
        ShaderStage.Vertex => "vertex",
        ShaderStage.Pixel => "pixel",
        _ => "compute",
    };
}

// The stage-specific compile inputs one lookup carries beside its static key. Nothing keeps
// them past the lookup, so the pipeline cache refills one instance per stage.
public sealed class StageCompileOptions
{
    public VertexInputInfo? VertexInfo { get; set; }
    public int RequiredVertexOutputCount { get; set; }
    public PixelInputInfo? PixelInfo { get; set; }
    public IReadOnlyList<Gen5PixelOutputBinding> PixelOutputs { get; set; } = [];
    public uint PixelInputEnable { get; set; }
    public uint PixelInputAddress { get; set; }
    public ComputeInputInfo? ComputeInfo { get; set; }
    public Gen5ComputeSystemRegisters? ComputeSystemRegisters { get; set; }

    public StageCompileOptions Reset()
    {
        VertexInfo = null;
        RequiredVertexOutputCount = 0;
        PixelInfo = null;
        PixelOutputs = [];
        PixelInputEnable = 0;
        PixelInputAddress = 0;
        ComputeInfo = null;
        ComputeSystemRegisters = null;
        return this;
    }
}

// The key of a program entry: what the emitter reads besides the resource specialization.
public sealed class ProgramKey : IEquatable<ProgramKey>
{
    public ProgramKey(ShaderStage stage, ulong hash, uint userDataCount, uint codeSize, uint[] staticState)
    {
        Stage = stage;
        Hash = hash;
        UserDataCount = userDataCount;
        CodeSize = codeSize;
        StaticState = staticState;
    }

    public ShaderStage Stage { get; }
    public ulong Hash { get; }
    public uint UserDataCount { get; }
    public uint CodeSize { get; }
    public uint[] StaticState { get; }

    public bool Equals(ProgramKey? other) =>
        other is not null && Stage == other.Stage && Hash == other.Hash && UserDataCount == other.UserDataCount &&
        CodeSize == other.CodeSize && StaticState.AsSpan().SequenceEqual(other.StaticState);

    public override bool Equals(object? obj) => Equals(obj as ProgramKey);

    // Same-shape variants share a bucket; equality does the one exact comparison of the state words.
    public override int GetHashCode() => HashCode.Combine(Stage, Hash, UserDataCount, CodeSize, StaticState.Length);
}

// The same key over a span of state words, so that a lookup copies nothing: every draw looks
// its programs up, and a key with its own array is only made when a program is first seen.
internal readonly ref struct ProgramKeyLookup(ShaderStage stage, ulong hash, uint userDataCount, uint codeSize, ReadOnlySpan<uint> staticState)
{
    public readonly ShaderStage Stage = stage;
    public readonly ulong Hash = hash;
    public readonly uint UserDataCount = userDataCount;
    public readonly uint CodeSize = codeSize;
    public readonly ReadOnlySpan<uint> StaticState = staticState;
}

internal sealed class ProgramKeyComparer : IEqualityComparer<ProgramKey>, IAlternateEqualityComparer<ProgramKeyLookup, ProgramKey>
{
    public static readonly ProgramKeyComparer Instance = new();

    public bool Equals(ProgramKey? x, ProgramKey? y) => x is null ? y is null : x.Equals(y);

    public int GetHashCode(ProgramKey key) => key.GetHashCode();

    public bool Equals(ProgramKeyLookup alternate, ProgramKey other) =>
        alternate.Stage == other.Stage && alternate.Hash == other.Hash && alternate.UserDataCount == other.UserDataCount &&
        alternate.CodeSize == other.CodeSize && alternate.StaticState.SequenceEqual(other.StaticState);

    public int GetHashCode(ProgramKeyLookup alternate) =>
        HashCode.Combine(alternate.Stage, alternate.Hash, alternate.UserDataCount, alternate.CodeSize, alternate.StaticState.Length);

    public ProgramKey Create(ProgramKeyLookup alternate) =>
        new(alternate.Stage, alternate.Hash, alternate.UserDataCount, alternate.CodeSize, alternate.StaticState.ToArray());
}

// One compiled module of a program entry for one specialization and push-data start.
internal sealed class ProgramPermutation
{
    public required ResourceSpecialization Specialization { get; init; }
    public required ShaderProgramInfo Program { get; init; }
    public required ShaderProgram Handle { get; init; }
    public required IGuestCompiledShader Compiled { get; init; }

    public BindingLayout Bindings => Program.Bindings!;
}

// One decoded program with its resource plan and every permutation compiled from it.
internal sealed class ProgramSourceEntry
{
    public required ShaderResourcePlan Plan { get; init; }
    public required Gen5ShaderProgram Program { get; init; }
    public required bool HasBitwiseExclusiveOr { get; init; }
    public ConstantFill? ConstantFill { get; init; }
    public BoundedFill? BoundedFill { get; init; }
    public BoundedCopy? BoundedCopy { get; init; }
    public EmbeddedVertexFetchPlan? EmbeddedFetch { get; init; }
    public ShaderVertexInput[] VertexInputs { get; init; } = [];
    public List<ProgramPermutation> Permutations { get; } = new(8);
    // [local] SHARPEMU_PROGRAM_GATE: the permutations as an array replaced whole under the gate, so a lookup scans it
    // without the gate (Permutations itself is only touched under it).
    public ProgramPermutation[] Published = [];
}

// Programs keyed by identity and static state; each draw materializes its resources and reuses
// the permutation whose specialization and push-data start match, else compiles one more.
internal sealed class ShaderProgramCache
{
    private const uint MaxInstructionScan = 16384;

    private readonly CpuContext _context;
    private readonly IGuestGpuBackend _compiler;
    private readonly IShaderPipelineHost _host;
    // [local] SHARPEMU_PROGRAM_GATE (on by default): a lookup that finds its entry and permutation takes no lock. Entries
    // and permutations are only added (under _gate), the per-lookup scratch is per thread, and each thread materializes
    // with its own cache (plans with store tables: see StoreTableMode). =0 is the old behaviour: the caller holds the gate
    // for the whole lookup, with one shared cache. The front lost 6.4 ms/frame (static) and 7.6 (pan) waiting for the
    // gate; static 26.81 -> 28.28 fps, pan 24.17 -> 24.31 (3 alternating pairs each).
    internal static readonly bool LockFreeDefault = Environment.GetEnvironmentVariable("SHARPEMU_PROGRAM_GATE") != "0";
    internal bool LockFree { get; }
    // [local] SHARPEMU_PROGRAM_GATE_VERIFY=1: every lock-free hit is looked up again under the gate the old way (shared
    // cache, the published list) and compared; ProgramGateVerify reports the differences every 20 s.
    // =2 instead checks every hit, gated or not, against a fresh walk without a materialization cache, so both modes
    // can be measured against the same reference.
    private static readonly string? VerifyRequested = Environment.GetEnvironmentVariable("SHARPEMU_PROGRAM_GATE_VERIFY");
    private readonly bool _verify;
    private readonly bool _walkCheck = VerifyRequested == "2";
    private readonly object _gate;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProgramKey, ProgramSourceEntry> _programs = new(ProgramKeyComparer.Instance);
    // [local] Decode runs outside ShaderPipelineCache's program gate, from the front thread and the render thread at
    // once: both tables are concurrent (a plain Dictionary threw "same key already added" here, 2026-10-12 p1a).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Hash, uint CodeSize), Gen5ShaderProgram> _decoded = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Hash, uint CodeSize), ShaderCodeCapture> _codeCaptures = new();
    // Draws that re-bind unchanged resources reuse the last materialization.
    // SHARPEMU_RESOURCE_CACHE=0 materializes every draw, for A/B comparisons.
    private readonly ResourceMaterializationCache? _materializations = Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZATION_CACHE") == "0" ? null : (Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_CACHE") == "0" ? null : new());
    // [local] SHARPEMU_PROGRAM_GATE: one cache per thread (a cache is not thread safe); _materializations is then only
    // used under the gate, by the verify.
    private readonly ThreadLocal<ResourceMaterializationCache>? _threadMaterializations;
    // [local] SHARPEMU_PROGRAM_GATE_TABLES: where a plan with store tables materializes when the gate is off. A hit's
    // store-table ranges come from a scan cache whose words a cached walk does not record, so they can be older than
    // a fresh walk's; which cache serves decides how often.
    //   0: the shared cache under the gate, as before the change (the same results; the front still waits for these
    //      few dispatches);
    //   1: the thread's cache, which refuses a hit once a scan it used was dropped (a fresh walk's result always,
    //      at the cost of rescanning the tables after every invalidation);
    //   2: the thread's cache as it is (stale more often than the shared one);
    //   3 (default): the thread's cache, whose entries keep none of the scanned table words and evaluate the tables
    //      again on every hit from the thread's scan cache (a dropped scan is rescanned once, as a fresh walk would): a
    //      fresh walk's ranges without re-walking the plan (ResourceMaterializationCache.CurrentTables). With
    //      SHARPEMU_STORE_TABLE_DIFF the rescan of an unchanged table is a read and a compare.
    internal static readonly int StoreTableMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_PROGRAM_GATE_TABLES"), out var storeTableMode) ? storeTableMode : 3;
    private ulong _nextProgramId;

    public ResourceMaterializationCache? Materializations => _materializations;

    // Read once: the draw path asked the environment on every draw and dispatch
    // (~4 % of the Demon's Souls render thread) for a debug dump that is almost never on.
    private readonly bool _spirvDumpEnabled = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"), "1", StringComparison.Ordinal);

    private readonly GuestWordReader _readGuestWord;
    private readonly GuestWordReader _readCleanGuestWord;
    // Refilled per lookup: the readers never change and nothing keeps the instance. [local] One per thread, with the
    // static-state scratch.
    private sealed class LookupScratch(ResourceRuntimeInputs inputs)
    {
        public readonly ResourceRuntimeInputs Inputs = inputs;
        public readonly List<uint> StaticState = new(StageStaticKey.MaxWords);
        public readonly List<uint> VerifyState = new(StageStaticKey.MaxWords);
    }

    private readonly ThreadLocal<LookupScratch> _scratch;
    // The materializer replaces both outputs on success and the caller stops on failure,
    // so no lookup builds its own to be thrown away.
    private static readonly ResourceSnapshot EmptySnapshot = new();
    private static readonly ResourceSpecialization EmptySpecialization = new();
    private readonly ResidentGuestBytesReader _readResidentGuestBytes;
    private readonly ResidentGuestBytesComparer? _compareResidentGuestBytes; // [local] SHARPEMU_VALIDATE_IN_PLACE
    private readonly ResidentGuestBytesReader? _prefetchResidentGuestBytes;

    private static readonly bool PrefetchEnabled = Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_PREFETCH") != "0";

    public ShaderProgramCache(CpuContext context, IGuestGpuBackend compiler, IShaderPipelineHost host, object? gate = null, bool? lockFree = null)
    {
        LockFree = lockFree ?? LockFreeDefault;
        _verify = LockFree && VerifyRequested == "1";
        _context = context;
        _compiler = compiler;
        _host = host;
        _gate = gate ?? new object();
        _readGuestWord = host.TryReadGuestWord;
        _readCleanGuestWord = host.TryReadCleanGuestWord;
        _readResidentGuestBytes = host.TryReadResidentGuestBytes;
        _compareResidentGuestBytes = ResourceMaterializationCache.ValidateInPlaceMode != 0 ? host.TryCompareResidentGuestBytes : null; // [local]
        _prefetchResidentGuestBytes = PrefetchEnabled ? _readResidentGuestBytes : null;
        var synchronizeRanges = ResourceMaterializationCache.SyncValidateMode != 0 ? host.TrySynchronizeGuestRanges : (GuestRangeSynchronizer?)null; // [local]
        _scratch = new ThreadLocal<LookupScratch>(() => new LookupScratch(new ResourceRuntimeInputs
        {
            ReadMemory = _readGuestWord,
            ReadCleanMemory = _readCleanGuestWord,
            ReadResidentMemory = _prefetchResidentGuestBytes,
            SynchronizeRanges = synchronizeRanges,
            SynchronizeTableRange = SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.TableSyncMode != 0 ? host.TrySynchronizeTableRange : null, // [local]
        }));
        _threadMaterializations = LockFree && _materializations is not null
            ? new ThreadLocal<ResourceMaterializationCache>(static () => new(stampStoreTables: StoreTableMode == 1, currentStoreTables: StoreTableMode == 3))
            : null;
    }

    public int ProgramCount => _programs.Count;

    public IEnumerable<ProgramSourceEntry> Entries => _programs.Values;

    private void DumpFailedShader(ShaderSource source)
    {
        var dumpDir = Environment.GetEnvironmentVariable("LOCAL_DUMP_FAILED_SHADER");
        if (string.IsNullOrEmpty(dumpDir)) return;
        var bytes = new byte[Math.Max(source.CodeSize, 64u * 1024u)];
        var read = 0;
        while (read < bytes.Length && _context.Memory.TryRead(source.Address + (ulong)read, bytes.AsSpan(read, 4))) read += 4;
        File.WriteAllBytes(Path.Combine(dumpDir, $"shader_{source.Hash:X16}.bin"), bytes.AsSpan(0, read).ToArray());
    }

    private static readonly HashSet<ulong> DumpHashes = (Environment.GetEnvironmentVariable("LOCAL_DUMP_SHADER_HASHES") ?? "") // [local] shader binaries to dump on first decode
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(h => Convert.ToUInt64(h.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? h[2..] : h, 16)).ToHashSet();
    private readonly HashSet<ulong> _dumpedHashes = new(); // [local]

    private bool AddDumpedHash(ulong hash)
    {
        lock (_dumpedHashes)
        {
            return _dumpedHashes.Add(hash);
        }
    }

    // The decoded instructions of a program, shared by every static variant of the same code.
    public Gen5ShaderProgram Decode(ShaderSource source)
    {
        if (DumpHashes.Contains(source.Hash) && AddDumpedHash(source.Hash)) { DumpFailedShader(source); Console.Error.WriteLine($"[DUMPHASH] hash=0x{source.Hash:X16} address=0x{source.Address:X} size={source.CodeSize}"); } // [local]
        var key = (source.Hash, source.CodeSize);
        if (_decoded.TryGetValue(key, out var program))
        {
            return program;
        }

        var recording = _host.ShaderPrewarm is not null ? new RecordingCpuMemory(_context.Memory) : null;
        var context = recording is null ? _context : new CpuContext(recording, _context.TargetGeneration);
        if (Environment.GetEnvironmentVariable("LOCAL_DUMP_SHADER_HASH") is { Length: > 0 } wanted &&
            wanted.Contains($"{source.Hash:X16}", StringComparison.OrdinalIgnoreCase))
        {
            DumpFailedShader(source);
        }

        if (!Gen5ShaderTranslator.TryDecodeProgram(context, source.Address, out program, out var error))
        {
            DumpFailedShader(source);
            throw SubmissionScheduler.Fatal($"The shader program cannot be decoded: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={error}.");
        }

        if (!_decoded.TryAdd(key, program))
        {
            // Another thread decoded the same code meanwhile: every caller shares its program.
            return _decoded[key];
        }

        if (Vulkan.VulkanCommandProfile.PassesEnabled && program.Instructions.Any(static i => i.Opcode is "ImageBvhIntersectRay" or "ImageBvh64IntersectRay")) // [local] SHARPEMU_PROFILE_GPU_PASSES: ray-tracing programs
            Console.Error.WriteLine($"[PERF][GPU_SHADER_RT] hash=0x{source.Hash:X16} stage={source.Label} instructions={program.Instructions.Count}");
        if (Environment.GetEnvironmentVariable("SHARPEMU_LOG_GLOBAL_STORES") == "1") // [local] programs that store through global/flat addresses
        {
            var stores = program.Instructions.Where(static i => i.Opcode.StartsWith("GlobalStore", StringComparison.Ordinal) || i.Opcode.StartsWith("FlatStore", StringComparison.Ordinal) || i.Opcode.StartsWith("GlobalAtomic", StringComparison.Ordinal) || i.Opcode.StartsWith("FlatAtomic", StringComparison.Ordinal)).Select(static i => i.Opcode).Distinct().ToArray();
            if (stores.Length != 0) Console.Error.WriteLine($"[GLOBAL_STORES] hash=0x{source.Hash:X16} stage={source.Label} instructions={program.Instructions.Count} ops={string.Join(",", stores)}");
        }
        if (recording is not null)
        {
            _codeCaptures[key] = new ShaderCodeCapture
            {
                Hash = source.Hash,
                CodeSize = source.CodeSize,
                Address = source.Address,
                Generation = _context.TargetGeneration,
                Fused = Gen5ShaderTranslator.TryGetFusedProgramParts(
                    _context, source.Address, out var entryHeader, out var continuation, out var continuationHeader)
                    ? new FusedCodeParts(entryHeader, continuation, continuationHeader)
                    : null,
                Ranges = recording.TakeRanges(),
            };
        }

        return program;
    }

    public ShaderProgram GetOrCompile(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor, out ShaderStageResources stage)
    {
        try
        {
            return GetOrCompileCore(source, options, ref pushDataCursor, out stage);
        }
        catch (ShaderProgramRejectedException exception)
        {
            throw SubmissionScheduler.Fatal(exception.Message);
        }
    }

    public bool TryGetProgram(ShaderSource source, StageCompileOptions options, bool strictShaders,
        ref uint pushDataCursor, out ShaderProgram program, out ShaderStageResources stage, out string rejection)
    {
        rejection = string.Empty;
        if (strictShaders)
        {
            program = GetOrCompile(source, options, ref pushDataCursor, out stage);
            return true;
        }

        try
        {
            program = GetOrCompileCore(source, options, ref pushDataCursor, out stage);
            return true;
        }
        catch (ShaderProgramRejectedException exception)
        {
            program = default;
            stage = default;
            rejection = exception.Message;
            return false;
        }
    }

    private sealed class ShaderProgramRejectedException(string message) : Exception(message);

    private ShaderProgram GetOrCompileCore(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor, out ShaderStageResources stage)
    {
        var scratch = _scratch.Value!;
        var staticState = scratch.StaticState;
        ProgramKey? key = null;
        ProgramSourceEntry? entry;
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramCacheLookup))
        using (SharpEmu.Libs.VideoOut.FrontProfile.Measure(SharpEmu.Libs.VideoOut.FrontProfile.Phase.ProgramKey))
        {
            BuildStaticState(source.Stage, options, staticState);
            var lookup = LookupOf(source, staticState);
            if (!_programs.GetAlternateLookup<ProgramKeyLookup>().TryGetValue(lookup, out entry))
            {
                key = ProgramKeyComparer.Instance.Create(lookup);
            }
        }
        var sourceWasCached = entry is not null;
        if (RenderTrace.Enabled && RenderTrace.Pipeline())
        {
            RenderTrace.Write($"ProgramCache lookup stage={source.Label} hash=0x{source.Hash:X16} cached={entry is not null} permutations={entry?.Permutations.Count ?? 0}");
        }

        var inputs = scratch.Inputs;
        inputs.UserData = source.UserData;
        inputs.ShaderBase = source.Address;
        inputs.ComputeState = source.Stage == ShaderStage.Compute && options.ComputeInfo is { } computeState
            ? new ComputeSelectorState(computeState.WaveSize, Math.Max(computeState.ThreadsX, 1),
                Math.Max(computeState.ThreadsY, 1), Math.Max(computeState.ThreadsZ, 1), computeState.DispatchThreadDimensions,
                computeState.LocalDataShareDwords, computeState.ThreadIdCount)
            : null;
        if (entry is null)
        {
            // [local] Decoded and planned outside the gate: an entry is a pure function of its key, and a thread that
            // loses the race uses the entry that won.
            var created = CreateEntry(source, options);
            entry = _programs.GetOrAdd(key!, created);
            if (ReferenceEquals(entry, created))
            {
                ShaderCacheCounters.CountProgram();
            }
            else
            {
                key = null;
            }
        }

        var materializations = _threadMaterializations is { } perThread ? perThread.Value : _materializations;
        var sharedForTables = _threadMaterializations is not null && StoreTableMode == 0 && entry.Plan.DeviceStoreTables.Count != 0;
        if (sharedForTables)
        {
            materializations = _materializations;
        }

        var snapshot = EmptySnapshot;
        var specialization = EmptySpecialization;
        var captureIndirectImageFailure = _spirvDumpEnabled ? ShaderPermutationDump.CreateFailureCapture(source) : null;
        if (Gpu.Buffers.ConservativeRecords.Tracking) Gpu.Buffers.ConservativeRecords.CurrentReader = source.Hash; // [local]
        if (Diagnostics.GpuReadTrace.Enabled || Diagnostics.TableReadCensus.Enabled) // [local] census
        {
            Diagnostics.GpuReadTrace.CurrentShader = source.Hash;
            Diagnostics.GpuReadTrace.CurrentStage = source.Label;
        }

        if (RenderPhaseProfile.DetailMeasurementsEnabled) // [local] the render thread materializes: which programs
        {
            BackMaterializations.Note(source.Stage, source.Hash);
        }

        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ResourceMaterialization))
        using (SharpEmu.Libs.VideoOut.FrontProfile.Measure(SharpEmu.Libs.VideoOut.FrontProfile.Phase.Materialize))
        {
            // Failure capture needs the full walk, so a dump run bypasses the cache.
            var frontStarted = SharpEmu.Libs.VideoOut.FrontProfile.Active ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
            var hitsBefore = materializations?.Hits ?? 0;
            var refreshesBefore = materializations?.TableRefreshes ?? 0;
            // [local] SHARPEMU_PROFILE_RESOLVE: this materialization's time and completion waits, by program.
            var resolveStarted = SharpEmu.Libs.VideoOut.ResolveProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var waitsBefore = resolveStarted != 0 ? SharpEmu.Libs.VideoOut.ResolveProfile.CompletionWaits : 0;
            var waitTicksBefore = resolveStarted != 0 ? SharpEmu.Libs.VideoOut.ResolveProfile.CompletionWaitTicks : 0;
            bool materialized;
            ResourceMaterializationFailure materializationFailure;
            if (sharedForTables)
            {
                SharpEmu.Libs.VideoOut.FrontProfile.Switch(SharpEmu.Libs.VideoOut.FrontProfile.Phase.ProgramGate);
                lock (_gate)
                {
                    SharpEmu.Libs.VideoOut.FrontProfile.Switch(SharpEmu.Libs.VideoOut.FrontProfile.Phase.Materialize);
                    materialized = MaterializeWith(materializations, entry, inputs, ref snapshot, ref specialization, out materializationFailure, captureIndirectImageFailure);
                }
            }
            else
            {
                materialized = MaterializeWith(materializations, entry, inputs, ref snapshot, ref specialization, out materializationFailure, captureIndirectImageFailure);
            }

            if (IndirectImageDiag.Hash != 0 && source.Hash == IndirectImageDiag.Hash) // [local] SHARPEMU_INDIRECT_IMAGE_DIAG
            {
                if (materialized)
                    IndirectImageDiag.NoteSuccess(specialization, entry.Plan.Info.Images.Count);
                else if (materializationFailure == ResourceMaterializationFailure.IncompatibleImageCandidates && IndirectImageDiag.ShouldCapture())
                {
                    var diagSnapshot = EmptySnapshot;
                    var diagSpecialization = EmptySpecialization;
                    ResourceMaterializer.Materialize(entry.Plan, inputs, ref diagSnapshot, ref diagSpecialization, out _, IndirectImageDiag.Write);
                }
            }

            if (resolveStarted != 0)
                SharpEmu.Libs.VideoOut.ResolveProfile.NoteMaterialize(source.Hash, System.Diagnostics.Stopwatch.GetTimestamp() - resolveStarted,
                    materializations is { } hitCache && hitCache.Hits != hitsBefore,
                    SharpEmu.Libs.VideoOut.ResolveProfile.CompletionWaits - waitsBefore,
                    SharpEmu.Libs.VideoOut.ResolveProfile.CompletionWaitTicks - waitTicksBefore);
            if (frontStarted != 0)
                SharpEmu.Libs.VideoOut.FrontProfile.NoteMaterialize(
                    materializations is { } cache && cache.Hits != hitsBefore ? 0 : materializations is { } refreshed && refreshed.TableRefreshes != refreshesBefore ? 1 : 2,
                    System.Diagnostics.Stopwatch.GetTimestamp() - frontStarted);
            if (!materialized)
            {
                var message = $"The shader resources could not be materialized: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} reason={materializationFailure}.";
                if (materializationFailure is ResourceMaterializationFailure.IncompatibleImageCandidates or ResourceMaterializationFailure.ImageCapacityExceeded)
                    throw new ShaderProgramRejectedException(message);
                throw SubmissionScheduler.Fatal(message);
            }
        }

        // [local] An indirect dispatch by threads needs the layout that keeps its limits in ShaderData.
        var needsShaderDataLimits = IndirectLimitsInShaderData && source.Stage == ShaderStage.Compute && options.ComputeInfo is { } computeInfo &&
            IsIndirectThreadDispatch(computeInfo);
        var published = Volatile.Read(ref entry.Published);
        ProgramPermutation? found;
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPermutationLookup))
        using (SharpEmu.Libs.VideoOut.FrontProfile.Measure(SharpEmu.Libs.VideoOut.FrontProfile.Phase.Permutation))
        {
            found = FindPermutation(published, 0, needsShaderDataLimits, pushDataCursor, specialization);
        }

        if (found is not null)
        {
            if (_verify)
            {
                VerifyHit(source, options, scratch, entry, found, snapshot, specialization, pushDataCursor, needsShaderDataLimits);
            }

            if (_walkCheck)
            {
                WalkCheckHit(source, scratch, entry, snapshot, specialization);
            }

            return UsePermutation(found, snapshot, source, options, ref pushDataCursor, out stage);
        }

        // A miss compiles under the gate; another thread may have compiled the same permutation meanwhile.
        if (LockFree) SharpEmu.Libs.VideoOut.FrontProfile.Switch(SharpEmu.Libs.VideoOut.FrontProfile.Phase.ProgramGate);
        lock (_gate)
        {
            if (LockFree) SharpEmu.Libs.VideoOut.FrontProfile.Switch(SharpEmu.Libs.VideoOut.FrontProfile.Phase.Permutation);
            found = FindPermutation(entry.Published, published.Length, needsShaderDataLimits, pushDataCursor, specialization);
            if (found is not null)
            {
                return UsePermutation(found, snapshot, source, options, ref pushDataCursor, out stage);
            }

            key ??= new ProgramKey(source.Stage, source.Hash, (uint)source.UserData.Length, source.CodeSize, staticState.ToArray());
            var permutation = CompilePermutation(source, options, entry, specialization, pushDataCursor, key, sourceWasCached);
            entry.Permutations.Add(permutation);
            Volatile.Write(ref entry.Published, entry.Permutations.ToArray());
            ShaderCacheCounters.CountPermutation();
            stage = CreateStageResources(permutation.Program, snapshot, source, options);
            permutation.Bindings.AdvancePushData(ref pushDataCursor);
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"ProgramCache insert stage={source.Label} hash=0x{source.Hash:X16} id=0x{permutation.Handle.Id:X16} permutation={entry.Permutations.Count - 1}");
            }

            return permutation.Handle;
        }
    }

    private bool MaterializeWith(ResourceMaterializationCache? materializations, ProgramSourceEntry entry, ResourceRuntimeInputs inputs,
        ref ResourceSnapshot snapshot, ref ResourceSpecialization specialization, out ResourceMaterializationFailure failure,
        Action<IndirectImageFailure>? captureIndirectImageFailure) =>
        materializations is not null && captureIndirectImageFailure is null
            ? materializations.Materialize(entry.Plan, inputs, _readResidentGuestBytes, ref snapshot, ref specialization, out failure, _compareResidentGuestBytes)
            : ResourceMaterializer.Materialize(entry.Plan, inputs, ref snapshot, ref specialization, out failure, captureIndirectImageFailure);

    private static ProgramKeyLookup LookupOf(ShaderSource source, List<uint> staticState) =>
        new(source.Stage, source.Hash, (uint)source.UserData.Length, source.CodeSize, CollectionsMarshal.AsSpan(staticState));

    // The first permutation from start on whose specialization and push-data start the draw matches.
    private static ProgramPermutation? FindPermutation(ProgramPermutation[] permutations, int start, bool needsShaderDataLimits,
        uint pushDataCursor, ResourceSpecialization specialization)
    {
        for (var index = start; index < permutations.Length; index++)
        {
            var candidate = permutations[index];
            var layout = candidate.Bindings;
            if ((!needsShaderDataLimits || layout.LimitsInShaderData) &&
                layout.PushDataStartDword == (layout.LimitsInShaderData ? PushData.NoStart : PushData.StartFor(pushDataCursor, layout.ShaderDataDwordCount)) && candidate.Specialization.Equals(specialization)) // [local] a layout forced to ShaderData has no push start
            {
                return candidate;
            }
        }

        return null;
    }

    private static ShaderProgram UsePermutation(ProgramPermutation candidate, ResourceSnapshot snapshot, ShaderSource source,
        StageCompileOptions options, ref uint pushDataCursor, out ShaderStageResources stage)
    {
        stage = CreateStageResources(candidate.Program, snapshot, source, options);
        candidate.Bindings.AdvancePushData(ref pushDataCursor);
        if (RenderTrace.Enabled && RenderTrace.Pipeline())
        {
            RenderTrace.Write($"ProgramCache hit stage={source.Label} hash=0x{source.Hash:X16} id=0x{candidate.Handle.Id:X16}");
        }

        return candidate.Handle;
    }

    // [local] SHARPEMU_PROGRAM_GATE_VERIFY: the hit found without the gate, looked up again under it as before the
    // change (the shared cache, the entry list) without compiling anything.
    private void VerifyHit(ShaderSource source, StageCompileOptions options, LookupScratch scratch, ProgramSourceEntry entry,
        ProgramPermutation found, ResourceSnapshot snapshot, ResourceSpecialization specialization, uint pushDataCursor,
        bool needsShaderDataLimits)
    {
        string? difference;
        lock (_gate)
        {
            try
            {
                difference = VerifyUnderGate(source, options, scratch, entry, found, snapshot, specialization, pushDataCursor, needsShaderDataLimits);
            }
            catch (Exception exception)
            {
                ProgramGateVerify.NoteError(source, exception);
                return;
            }
        }

        ProgramGateVerify.Note(source, difference);
    }

    private void WalkCheckHit(ShaderSource source, LookupScratch scratch, ProgramSourceEntry entry, ResourceSnapshot snapshot,
        ResourceSpecialization specialization)
    {
        string? difference;
        lock (_gate)
        {
            try
            {
                var walkSnapshot = EmptySnapshot;
                var walkSpecialization = EmptySpecialization;
                if (!ResourceMaterializer.Materialize(entry.Plan, scratch.Inputs, ref walkSnapshot, ref walkSpecialization, out var failure, null))
                    difference = $"walk_failed_{failure}";
                else if (!walkSpecialization.Equals(specialization))
                    difference = "walk_specialization";
                else if (SameIgnoringLabels(snapshot, walkSnapshot))
                    difference = null;
                else
                {
                    var detail = ResourceMaterializationCache.SnapshotDifference(snapshot, walkSnapshot)!;
                    var field = new string(detail.TakeWhile(static c => char.IsLetter(c) || c == '_').ToArray());
                    difference = $"walk_{field} thread={Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString()} " +
                        $"{RangesDifference(snapshot.DeviceAddressRanges, walkSnapshot.DeviceAddressRanges)}";
                }
            }
            catch (Exception exception)
            {
                ProgramGateVerify.NoteError(source, exception);
                return;
            }
        }

        ProgramGateVerify.Note(source, difference);
    }

    private string? VerifyUnderGate(ShaderSource source, StageCompileOptions options, LookupScratch scratch, ProgramSourceEntry entry,
        ProgramPermutation found, ResourceSnapshot snapshot, ResourceSpecialization specialization, uint pushDataCursor,
        bool needsShaderDataLimits)
    {
        var staticState = scratch.VerifyState;
        BuildStaticState(source.Stage, options, staticState);
        if (!CollectionsMarshal.AsSpan(staticState).SequenceEqual(CollectionsMarshal.AsSpan(scratch.StaticState)))
            return "static_state";
        if (!_programs.GetAlternateLookup<ProgramKeyLookup>().TryGetValue(LookupOf(source, staticState), out var reference))
            return "entry_missing";
        if (!ReferenceEquals(reference, entry))
            return "entry";
        var referenceSnapshot = EmptySnapshot;
        var referenceSpecialization = EmptySpecialization;
        var materialized = _materializations is not null
            ? _materializations.Materialize(entry.Plan, scratch.Inputs, _readResidentGuestBytes, ref referenceSnapshot, ref referenceSpecialization, out var failure)
            : ResourceMaterializer.Materialize(entry.Plan, scratch.Inputs, ref referenceSnapshot, ref referenceSpecialization, out failure, null);
        if (!materialized)
            return $"materialize_failed_{failure}";
        if (!referenceSpecialization.Equals(specialization))
            return "specialization";
        var labelsOnly = false;
        if (ResourceMaterializationCache.SnapshotDifference(snapshot, referenceSnapshot) is { } snapshotDifference)
        {
            // A device range's handle is a label (diagnostics name it, nothing binds by it) that the store-table scan
            // cache replays from the scan that filled it, so it depends on history with or without the gate.
            if (!snapshotDifference.StartsWith("device_ranges", StringComparison.Ordinal) ||
                !snapshot.DeviceAddressRanges.AsSpan().SequenceEqual(referenceSnapshot.DeviceAddressRanges, RangeWithoutHandle.Instance))
            {
                // Which of the two a fresh walk (no materialization cache) agrees with now.
                var walkSnapshot = EmptySnapshot;
                var walkSpecialization = EmptySpecialization;
                var walked = ResourceMaterializer.Materialize(entry.Plan, scratch.Inputs, ref walkSnapshot, ref walkSpecialization, out _, null);
                var lockFreeAgrees = walked && SameIgnoringLabels(snapshot, walkSnapshot);
                var sharedAgrees = walked && SameIgnoringLabels(referenceSnapshot, walkSnapshot);
                var field = new string(snapshotDifference.TakeWhile(static c => char.IsLetter(c) || c == '_').ToArray());
                return $"snapshot_{field}_walk_{(!walked ? "failed" : lockFreeAgrees ? sharedAgrees ? "both" : "lockfree" : sharedAgrees ? "shared" : "neither")} " +
                    $"thread={Thread.CurrentThread.Name ?? Environment.CurrentManagedThreadId.ToString()} " +
                    $"{RangesDifference(snapshot.DeviceAddressRanges, referenceSnapshot.DeviceAddressRanges)} ({snapshotDifference})";
            }

            labelsOnly = true;
        }

        ProgramPermutation? referenceFound = null;
        foreach (var candidate in entry.Permutations)
        {
            if (FindPermutation([candidate], 0, needsShaderDataLimits, pushDataCursor, referenceSpecialization) is not null)
            {
                referenceFound = candidate;
                break;
            }
        }

        if (referenceFound is null)
            return "permutation_missing";
        if (!ReferenceEquals(referenceFound, found))
            return "permutation";
        return labelsOnly ? ProgramGateVerify.LabelsOnly : null;
    }

    private static bool SameIgnoringLabels(ResourceSnapshot left, ResourceSnapshot right) =>
        ResourceMaterializationCache.SnapshotDifference(left, right) is not { } difference ||
        (difference.StartsWith("device_ranges", StringComparison.Ordinal) &&
            left.DeviceAddressRanges.AsSpan().SequenceEqual(right.DeviceAddressRanges, RangeWithoutHandle.Instance));

    // The counts and the first entries that differ, handles aside.
    private static string RangesDifference(DeviceAddressRange[] left, DeviceAddressRange[] right)
    {
        var index = 0;
        while (index < left.Length && index < right.Length && RangeWithoutHandle.Instance.Equals(left[index], right[index]))
            index++;
        static string At(DeviceAddressRange[] ranges, int index) =>
            index < ranges.Length ? $"{ranges[index].Base:X}+{ranges[index].Size:X}{(ranges[index].Written ? "w" : "")}" : "-";
        return $"counts={left.Length}/{right.Length} first_diff={index} lockfree={At(left, index)},{At(left, index + 1)} shared={At(right, index)},{At(right, index + 1)}";
    }

    private sealed class RangeWithoutHandle : IEqualityComparer<DeviceAddressRange>
    {
        public static readonly RangeWithoutHandle Instance = new();

        public bool Equals(DeviceAddressRange x, DeviceAddressRange y) => (x with { Handle = 0 }) == (y with { Handle = 0 });

        public int GetHashCode(DeviceAddressRange range) => (range with { Handle = 0 }).GetHashCode();
    }

    private static ShaderStageResources CreateStageResources(
        ShaderProgramInfo program, ResourceSnapshot snapshot, ShaderSource source, StageCompileOptions options)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramResourceAssembly);
        using var frontScope = SharpEmu.Libs.VideoOut.FrontProfile.Measure(SharpEmu.Libs.VideoOut.FrontProfile.Phase.StageResources);
        return new(program, snapshot, source.Address)
        {
            ThreadLimits = source.Stage == ShaderStage.Compute && options.ComputeInfo is { DispatchThreadDimensions: true } compute
                ? new DispatchThreadLimits(compute.DispatchThreadsX, compute.DispatchThreadsY, compute.DispatchThreadsZ)
                : null,
        };
    }

    private static void BuildStaticState(ShaderStage stage, StageCompileOptions options, List<uint> staticState)
    {
        switch (stage)
        {
            case ShaderStage.Vertex:
                StageStaticKey.Build(options.VertexInfo ?? throw new ArgumentException("The vertex lookup has no vertex input info."), options.RequiredVertexOutputCount, staticState);
                break;
            case ShaderStage.Pixel:
                StageStaticKey.Build(options.PixelInfo ?? throw new ArgumentException("The pixel lookup has no pixel input info."), staticState);
                break;
            default:
                StageStaticKey.Build(options.ComputeInfo ?? throw new ArgumentException("The compute lookup has no compute input info."), staticState);
                break;
        }
    }

    private ProgramSourceEntry CreateEntry(ShaderSource source, StageCompileOptions options)
    {
        var program = Decode(source);
        var dumpPlanning = CompiledShaderDump.ShouldWrite(source.Address, source.Hash);
        if (dumpPlanning) ShaderPlanningDump.WriteInput(source, program);
        EmbeddedVertexFetchPlan? fetch = null;
        ShaderVertexInput[] vertexInputs = [];
        if (source.Stage == ShaderStage.Vertex && options.VertexInfo is { FetchEmbedded: true } vertexInfo)
        {
            fetch = EmbeddedVertexFetchDetector.Detect(
                program,
                (int)source.UserDataBase + vertexInfo.FetchAttributeRegister,
                (int)source.UserDataBase + vertexInfo.FetchBufferRegister,
                source.UserDataBase,
                (uint)source.UserData.Length,
                waveSize: 32);
            vertexInputs = BuildVertexInputs(fetch, vertexInfo, source);
            program = fetch.RemoveReplacedTableLoads(program);
        }

        ShaderResourcePlan plan;
        try
        {
            plan = ShaderResourcePlan.Extract(program, source.Stage, source.Hash, source.UserDataBase, (uint)source.UserData.Length,
                fetch?.Loads.Select(load => load.Pc).ToHashSet(),
                beforeResourceTracking: dumpPlanning ? resourcePlan => ShaderPlanningDump.WriteGraph(source, resourcePlan) : null,
                // Graphics stages compile as wave32 (see the compile request); compute follows the dispatch.
                waveSize: source.Stage == ShaderStage.Compute ? options.ComputeInfo?.WaveSize ?? 64u : 32u);
        }
        catch (ResourcePlanException exception)
        {
            if (dumpPlanning) ShaderPlanningDump.WriteFailure(source, exception.Message);
            DumpFailedShader(source);
            throw new ShaderProgramRejectedException($"The shader resource plan is invalid: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={exception.Message}.");
        }

        var exclusiveOr = false;
        foreach (var instruction in program.Instructions)
        {
            exclusiveOr |= instruction.Opcode.Contains("Xor", StringComparison.Ordinal);
        }

        return new ProgramSourceEntry
        {
            Plan = plan,
            Program = program,
            HasBitwiseExclusiveOr = exclusiveOr,
            ConstantFill = source.Stage == ShaderStage.Compute ? ConstantFillDetector.Detect(program) : null,
            BoundedFill = source.Stage == ShaderStage.Compute ? BoundedFillDetector.Detect(program) : null,
            BoundedCopy = source.Stage == ShaderStage.Compute ? BoundedFillDetector.DetectCopy(program) : null,
            EmbeddedFetch = fetch,
            VertexInputs = vertexInputs,
        };
    }

    // One fixed-function input per fetched attribute; later loads of the same attribute alias the first.
    private static ShaderVertexInput[] BuildVertexInputs(EmbeddedVertexFetchPlan fetch, VertexInputInfo info, ShaderSource source)
    {
        var inputs = new List<ShaderVertexInput>();
        var inputByLocation = new Dictionary<int, int>();
        foreach (var load in fetch.Loads)
        {
            var location = -1;
            if ((uint)load.AttributeId < (uint)info.Attributes.Length &&
                info.Attributes[load.AttributeId].AttributeId == load.AttributeId)
            {
                location = load.AttributeId;
            }

            if (location < 0)
            {
                for (var index = 0; index < info.Attributes.Length; index++)
                {
                    if (info.Attributes[index].AttributeId == load.AttributeId &&
                        info.Attributes[index].RegisterCount >= load.Components)
                    {
                        location = index;
                        break;
                    }
                }
            }

            if (location < 0)
            {
                for (var index = 0; index < info.Attributes.Length; index++)
                {
                    if (info.Attributes[index].AttributeId == load.AttributeId)
                    {
                        location = index;
                        break;
                    }
                }
            }

            if (location < 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The vertex program fetches an attribute the tables do not declare: hash=0x{source.Hash:X16} shader=0x{source.Address:X16} attribute={load.AttributeId} pc=0x{load.Pc:X}.");
            }

            var attribute = info.Attributes[location];
            var numberFormat = 0u;
            if (Gfx10UnifiedFormat.TryDecode(attribute.Descriptor.Format, out _, out var decodedNumberFormat))
            {
                numberFormat = decodedNumberFormat;
            }

            var requiredComponents = 0u;
            var destinationSelect = attribute.Descriptor.DestinationSelectXYZW;
            for (uint component = 0; component < load.Components; component++)
            {
                var selector = (destinationSelect >> (int)(component * 3)) & 0x7u;
                if (selector is >= 4u and <= 7u)
                {
                    requiredComponents = Math.Max(requiredComponents, selector - 3u);
                    continue;
                }

                if (selector is 0u or 1u)
                {
                    continue;
                }

                throw SubmissionScheduler.Fatal(
                    $"The vertex program uses an unsupported attribute destination selector: hash=0x{source.Hash:X16} shader=0x{source.Address:X16} attribute={load.AttributeId} pc=0x{load.Pc:X} component={component} selector={selector}.");
            }

            if (requiredComponents == 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The vertex program fetches an attribute without a memory component: hash=0x{source.Hash:X16} shader=0x{source.Address:X16} attribute={load.AttributeId} pc=0x{load.Pc:X}.");
            }

            if (inputByLocation.TryGetValue(location, out var existingIndex))
            {
                var existing = inputs[existingIndex];
                ((List<uint>)existing.AliasPcs).Add(load.Pc);
                inputs[existingIndex] = existing with
                {
                    FetchComponentCount = Math.Max(existing.FetchComponentCount, load.Components),
                    ComponentCount = Math.Max(existing.ComponentCount, requiredComponents),
                };
                continue;
            }

            var aliasPcs = new List<uint>();
            inputByLocation.Add(location, inputs.Count);
            inputs.Add(new ShaderVertexInput(
                load.Pc,
                (uint)location,
                load.Components,
                requiredComponents,
                numberFormat,
                destinationSelect,
                attribute.FetchIndex != 0,
                aliasPcs));
        }

        return inputs.ToArray();
    }

    // [local] SHARPEMU_COMPILE_LOG: the indirect candidates of a specialization by class, in order (class:run,...).
    private static string IndirectSignature(ResourceSpecialization specialization)
    {
        var runs = new List<string>();
        string? last = null;
        var length = 0;
        for (var index = 0; index < specialization.Images.Count; index++)
        {
            var image = specialization.Images[index];
            if (image.IndirectRoot == DescriptorConstants.NoIndex || image.IndirectRoot == index)
                continue;
            var name = $"{image.NumericClass}/{image.Dimension}";
            if (name == last)
            {
                length++;
                continue;
            }

            if (last is not null)
                runs.Add($"{last}:{length}");
            last = name;
            length = 1;
        }

        if (last is not null)
            runs.Add($"{last}:{length}");
        return runs.Count == 0 ? "-" : string.Join(',', runs);
    }

    private ProgramPermutation CompilePermutation(
        ShaderSource source,
        StageCompileOptions options,
        ProgramSourceEntry entry,
        ResourceSpecialization specialization,
        uint pushDataCursor,
        ProgramKey key,
        bool sourceWasCached)
    {
        var program = entry.Program;
        var plan = entry.Plan;
        SpecializedResourceInfo resources;
        BindingLayout layout;
        try
        {
            resources = ResourceMaterializer.ApplyTo(plan, specialization);
            layout = AllocateLayout(program, plan, resources, source.UserDataBase, (uint)source.UserData.Length, pushDataCursor,
                source.Stage == ShaderStage.Compute && options.ComputeInfo!.DispatchThreadDimensions,
                source.Stage == ShaderStage.Compute && IsIndirectThreadDispatch(options.ComputeInfo!));
        }
        catch (ResourcePlanException exception)
        {
            throw SubmissionScheduler.Fatal($"The shader binding layout is invalid: stage={source.Label} hash=0x{source.Hash:X16} error={exception.Message}.");
        }

        var compileStarted = CompileLog.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local] SHARPEMU_COMPILE_LOG
        var request = BuildRequest(source, options, entry, resources, layout);
        var permutationDump = ShaderPermutationDump.WriteInputs(
            source, key, sourceWasCached, _programs.Keys, entry.Permutations,
            specialization, request, pushDataCursor, _nextProgramId + 1);
        if (!_compiler.TryCompileProgram(request, out var compiled, out var error) || compiled is null)
        {
            throw new ShaderProgramRejectedException($"The shader program cannot be compiled: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={error}.");
        }

        ShaderCacheCounters.CountCompile();
        if (resources.Info.UsesDeviceAddresses)
        {
            ShaderCacheCounters.CountDeviceAddressProgram();
            if (VideoOut.BufferUploadProfile.Enabled || SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled)
            {
                var accesses = plan.Memory.Entries.Where(memory => !memory.PlanningOnly &&
                    memory.Kind is MemoryResourceKind.ScalarAddress or MemoryResourceKind.Flat or MemoryResourceKind.Global).ToArray();
                var instructions = accesses.Select(memory => $"0x{memory.Pc:X}:{memory.Kind}:{memory.Access}").Distinct().ToArray();
                Console.Error.WriteLine($"[PERF][DEVICE_ADDRESS_PROGRAM] stage={source.Stage} hash=0x{source.Hash:X16} " +
                    $"address=0x{source.Address:X16} accesses={accesses.Length} " +
                    $"bounded_ranges={plan.DeviceAddressRanges.Count(range => range.Bounded && range.Plannable)} " +
                    $"unbounded_ranges={plan.DeviceAddressRanges.Count(range => !range.Bounded || !range.Plannable)} " +
                    $"instructions={string.Join(',', instructions.Take(32))} omitted={Math.Max(0, instructions.Length - 32)}");
            }
        }

        if (!layout.UsesPushData)
        {
            ShaderCacheCounters.CountShaderDataFallback();
        }

        _compiler.CountShaderCompilation();
        ShaderPermutationDump.WriteModule(permutationDump, compiled.Payload, compiled.PayloadFileExtension);
        CompiledShaderDump.Write(source.Label, source.Address, source.Hash, compiled, program);
        var id = ++_nextProgramId;
        var module = _host.CreateShaderModule(compiled, source.Stage, source.Hash, id);
        if (CompileLog.Enabled) // [local]
        {
            var indirect = IndirectSignature(specialization);
            CompileLog.Note("translate", System.Diagnostics.Stopwatch.GetElapsedTime(compileStarted).TotalMilliseconds, source.Hash,
                $"stage={source.Stage} spirv_bytes={compiled.Payload.Length} images={specialization.Images.Count} indirect={indirect}", always: indirect != "-");
        }
        var info = CreateProgramInfo(source, entry, resources, layout, request);
        if (source.Stage == ShaderStage.Compute &&
            _host.ShaderPrewarm is { } prewarm &&
            _codeCaptures.TryGetValue((source.Hash, source.CodeSize), out var capture))
        {
            prewarm.RecordCompute(capture, new ComputePrewarmRecord
            {
                Hash = source.Hash,
                CodeSize = source.CodeSize,
                Address = capture.Address,
                UserDataBase = source.UserDataBase,
                UserDataCount = (uint)source.UserData.Length,
                PushDataCursor = pushDataCursor,
                Info = options.ComputeInfo!,
                SystemRegisters = options.ComputeSystemRegisters,
                Specialization = specialization.Clone(),
            });
        }

        return new ProgramPermutation
        {
            Specialization = specialization,
            Program = info,
            Handle = new ShaderProgram(id, module),
            Compiled = compiled,
        };
    }

    private ShaderCompileRequest BuildRequest(
        ShaderSource source,
        StageCompileOptions options,
        ProgramSourceEntry entry,
        SpecializedResourceInfo resources,
        BindingLayout layout)
    {
        var enableGraphicsSubgroups = _host.GraphicsSubgroupOperationsEnabled;
        var sharedInt64Atomics = _host.SharedInt64AtomicsEnabled;
        var bufferInt64Atomics = _host.BufferInt64AtomicsEnabled;
        var float16Conversions = _host.Float16ConversionsEnabled;
        var signedZeroInfNanPreserve = _host.ShaderSignedZeroInfNanPreserveFloat32Supported && Environment.GetEnvironmentVariable("SHARPEMU_SIGNED_ZERO_INF_NAN") != "0"; // [local] A/B switch
        switch (source.Stage)
        {
            case ShaderStage.Vertex:
            {
                var info = options.VertexInfo!;
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = 32,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    ScratchDwords = info.ScratchDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    SupportsSharedInt64Atomics = sharedInt64Atomics,
                    SupportsBufferInt64Atomics = bufferInt64Atomics,
                    ShaderSignedZeroInfNanPreserveFloat32Supported = signedZeroInfNanPreserve,
                    RequiredVertexOutputCount = options.RequiredVertexOutputCount,
                    VertexInputs = entry.VertexInputs,
                    PositionExportControl = info.PositionExportControl,
                    SupportsClipDistance = _host.ClipDistanceEnabled,
                    ClipSpace = new ShaderClipSpaceTransform(
                        info.ClipSpace.Enabled,
                        info.ClipSpace.ScaleX,
                        info.ClipSpace.ScaleY,
                        info.ClipSpace.OffsetX,
                        info.ClipSpace.OffsetY,
                        info.ClipSpace.HalfExtentX,
                        info.ClipSpace.HalfExtentY),
                };
            }

            case ShaderStage.Pixel:
            {
                var info = options.PixelInfo!;
                var interpolators = new uint[info.InputCount];
                Array.Copy(info.InterpolatorSettings, interpolators, interpolators.Length);
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = 32,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    ScratchDwords = info.ScratchDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    SupportsSharedInt64Atomics = sharedInt64Atomics,
                    SupportsBufferInt64Atomics = bufferInt64Atomics,
                    ShaderSignedZeroInfNanPreserveFloat32Supported = signedZeroInfNanPreserve,
                    PixelOutputs = options.PixelOutputs,
                    PixelInputEnable = options.PixelInputEnable,
                    PixelCustomInterpolationMask = info.CustomInterpolationMask,
                    SupportsPerVertexPixelInputs = _host.PerVertexPixelInputsSupported,
                    PixelInputAddress = options.PixelInputAddress,
                    PixelInputCntl = interpolators,
                };
            }

            default:
                return BuildComputeRequest(entry.Plan, resources, layout, options.ComputeInfo!, options.ComputeSystemRegisters,
                    sharedInt64Atomics, bufferInt64Atomics, float16Conversions, _host.ExecGuardElisionEnabled, signedZeroInfNanPreserve);
        }
    }

    private static BindingLayout AllocateLayout(Gen5ShaderProgram program, ShaderResourcePlan plan, SpecializedResourceInfo resources,
        uint userDataBase, uint userDataCount, uint pushDataCursor, bool usesDispatchThreadLimits, bool indirectThreads) =>
        BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, userDataBase, userDataCount),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            pushDataCursor,
            usesDispatchThreadLimits: usesDispatchThreadLimits,
            limitsInShaderData: (IndirectLimitsInShaderData && indirectThreads) || LimitsInShaderDataHashes.Contains(plan.Hash));

    // [local] An indirect dispatch by threads reaches preparation with zero counts (they stay in GPU memory). Its
    // limits go in the ShaderData buffer so the GPU copies the counts there; with push constants the render thread had
    // to read the counts back and prepare the program again for every dispatch (GTA V's BVH chain, ~80 per frame).
    // SHARPEMU_INDIRECT_LIMITS_IN_SHADER_DATA=0 turns it off.
    private static readonly bool IndirectLimitsInShaderData = Environment.GetEnvironmentVariable("SHARPEMU_INDIRECT_LIMITS_IN_SHADER_DATA") != "0";

    private static bool IsIndirectThreadDispatch(ComputeInputInfo info) =>
        info.DispatchThreadDimensions && info.DispatchThreadsX == 0 && info.DispatchThreadsY == 0 && info.DispatchThreadsZ == 0;

    // [local experiment] SHARPEMU_LIMITS_IN_SHADER_DATA_HASHES=<hex>,...: these programs keep their shader data
    // in the ShaderData buffer, so their indirect thread dispatches take the counts on the GPU.
    private static readonly HashSet<ulong> LimitsInShaderDataHashes = new(
        (Environment.GetEnvironmentVariable("SHARPEMU_LIMITS_IN_SHADER_DATA_HASHES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(hash => Convert.ToUInt64(hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hash[2..] : hash, 16)));

    private static ShaderCompileRequest BuildComputeRequest(ShaderResourcePlan plan, SpecializedResourceInfo resources, BindingLayout layout,
        ComputeInputInfo info, Gen5ComputeSystemRegisters? systemRegisters, bool sharedInt64Atomics, bool bufferInt64Atomics, bool float16Conversions,
        bool execGuardElision, bool signedZeroInfNanPreserve) =>
        new(plan, resources, layout)
        {
            WaveSize = info.WaveSize,
            EnableExecGuardElision = info.WaveSize != 64 || execGuardElision,
            TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
            ScratchDwords = info.ScratchDwords,
            SupportsSharedInt64Atomics = sharedInt64Atomics,
            SupportsBufferInt64Atomics = bufferInt64Atomics,
            SupportsFloat16Conversions = float16Conversions,
            ShaderSignedZeroInfNanPreserveFloat32Supported = signedZeroInfNanPreserve,
            ComputeSystemRegisters = systemRegisters,
            LocalDataShareDwords = info.LocalDataShareDwords,
            LocalSizeX = Math.Max(info.ThreadsX, 1),
            LocalSizeY = Math.Max(info.ThreadsY, 1),
            LocalSizeZ = Math.Max(info.ThreadsZ, 1),
        };

    internal static bool TryCompilePrewarm(ComputePrewarmRecord record, ShaderCodeCapture code, IGuestGpuBackend compiler,
        bool sharedInt64Atomics, bool bufferInt64Atomics, bool float16Conversions, bool execGuardElision, bool signedZeroInfNanPreserve,
        out IGuestCompiledShader? compiled, out BindingLayout? layout, out string error)
    {
        compiled = null;
        layout = null;
        try
        {
            if (!Gen5ShaderTranslator.TryDecodeProgram(code.CreateContext(), code.Address, out var program, out error))
            {
                return false;
            }

            var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, record.Hash, record.UserDataBase, record.UserDataCount,
                waveSize: record.Info.WaveSize);
            var resources = ResourceMaterializer.ApplyTo(plan, record.Specialization);
            layout = AllocateLayout(program, plan, resources, record.UserDataBase, record.UserDataCount, record.PushDataCursor,
                record.Info.DispatchThreadDimensions, IsIndirectThreadDispatch(record.Info));
            var request = BuildComputeRequest(plan, resources, layout, record.Info, record.SystemRegisters,
                sharedInt64Atomics, bufferInt64Atomics, float16Conversions, execGuardElision, signedZeroInfNanPreserve);
            return compiler.TryCompileProgram(request, out compiled, out error) && compiled is not null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    // A fill kernel computes its thread index, moves one constant into a VGPR and stores it once.
    internal static uint? FindConstantStoreValue(Gen5ShaderProgram program)
    {
        var constants = new Dictionary<uint, uint>();
        uint? stored = null;
        foreach (var instruction in program.Instructions)
        {
            switch (instruction.Opcode)
            {
                case "SEndpgm" or "SWaitcnt" or "SNop" or "SInstPrefetch":
                    continue;
                case "VLshlAddU32" or "VMadU32U24" or "VAddU32" or "VAddI32" or "VAddNcU32" or "VLshlrevB32":
                    // Index arithmetic; a register it writes is no longer a known constant.
                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.VectorRegister) constants.Remove(destination.Value);
                    }

                    continue;
                case "VMovB32":
                {
                    if (instruction.Destinations.Count != 1 || instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister ||
                        instruction.Sources.Count != 1 || !TryDecodeIntegerConstant(instruction.Sources[0], out var value))
                    {
                        return null;
                    }

                    constants[instruction.Destinations[0].Value] = value;
                    continue;
                }

                case "BufferStoreFormatX" or "BufferStoreDword":
                {
                    if (stored.HasValue || instruction.Control is not Gen5BufferMemoryControl control ||
                        !constants.TryGetValue(control.VectorData, out var value))
                    {
                        return null;
                    }

                    stored = value;
                    continue;
                }

                default:
                    return null;
            }
        }

        return stored;
    }

    private static bool TryDecodeIntegerConstant(Gen5Operand operand, out uint value)
    {
        value = 0;
        if (operand.Kind == Gen5OperandKind.LiteralConstant)
        {
            value = operand.Value;
            return true;
        }

        if (operand.Kind != Gen5OperandKind.EncodedConstant)
        {
            return false;
        }

        // Inline integers: 128..192 are 0..64, 193..208 are -1..-16.
        if (operand.Value is >= 128 and <= 192)
        {
            value = operand.Value - 128;
            return true;
        }

        if (operand.Value is >= 193 and <= 208)
        {
            value = unchecked((uint)(192 - (int)operand.Value));
            return true;
        }

        return false;
    }

    private static ShaderProgramInfo CreateProgramInfo(
        ShaderSource source,
        ProgramSourceEntry entry,
        SpecializedResourceInfo resources,
        BindingLayout layout,
        ShaderCompileRequest request)
    {
        var info = resources.Info;
        var buffers = new BufferResourceInfo[info.Buffers.Count];
        for (var index = 0; index < buffers.Length; index++)
        {
            var buffer = info.Buffers[index];
            buffers[index] = new BufferResourceInfo(buffer.Read, buffer.Written, buffer.Atomic, buffer.Formatted, buffer.Scalar, buffer.MaxByteExtent, buffer.PackedStride);
        }

        var images = new ImageResourceInfo[info.Images.Count];
        for (var index = 0; index < images.Length; index++)
        {
            var image = info.Images[index];
            var resourceClass = image.ResourceClass switch
            {
                ShaderCompiler.Resources.ImageResourceClass.Sampled => ImageResourceClass.Sampled,
                ShaderCompiler.Resources.ImageResourceClass.Storage => ImageResourceClass.Storage,
                _ => ImageResourceClass.None,
            };
            images[index] = new ImageResourceInfo(resourceClass, image.Written);
        }

        var fetchComponents = new byte[VertexInputInfo.MaxBuffers];
        foreach (var input in entry.VertexInputs)
        {
            if (input.Location < (uint)fetchComponents.Length)
            {
                fetchComponents[input.Location] = (byte)input.ComponentCount;
            }
        }

        return new ShaderProgramInfo
        {
            Stage = source.Stage switch
            {
                ShaderStage.Vertex => ShaderStageKind.Vertex,
                ShaderStage.Pixel => ShaderStageKind.Pixel,
                _ => ShaderStageKind.Compute,
            },
            Hash = source.Hash,
            UserDataBase = source.UserDataBase,
            UserDataCount = (uint)source.UserData.Length,
            ParameterExportMask = entry.Program.ParameterExportMask,
            PixelColorExportMasks = entry.Program.PixelColorExportMasks,
            VertexOffsetScalarRegister = entry.EmbeddedFetch?.VertexOffsetScalarRegister ?? ShaderProgramInfo.NoScalarRegister,
            InstanceOffsetScalarRegister = entry.EmbeddedFetch?.InstanceOffsetScalarRegister ?? ShaderProgramInfo.NoScalarRegister,
            UsesDeviceAddresses = info.UsesDeviceAddresses,
            ConstantStoreValue = FindConstantStoreValue(entry.Program),
            HasBitwiseExclusiveOr = entry.HasBitwiseExclusiveOr,
            ConstantFill = entry.ConstantFill,
            BoundedFill = entry.BoundedFill,
            BoundedCopy = entry.BoundedCopy,
            Buffers = buffers,
            Images = images,
            SamplerCount = info.Samplers.Count,
            WaveSize = request.WaveSize,
            ScratchDwords = request.ScratchDwords,
            VertexFetchComponents = fetchComponents,
            Resources = resources,
            Bindings = layout,
        };
    }
}

// Writes each compiled module and its decoded listing when the dump switch is on.
internal static class CompiledShaderDump
{
    internal static bool ShouldWrite(ulong shaderAddress, ulong shaderHash)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var addressFilter = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV_ADDRESS");
        if (!string.IsNullOrWhiteSpace(addressFilter))
        {
            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (!ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var filteredAddress) ||
                shaderAddress != filteredAddress)
            {
                return false;
            }
        }

        var hashFilter = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV_HASH");
        if (!string.IsNullOrWhiteSpace(hashFilter))
        {
            foreach (var filter in hashFilter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var span = filter.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var filteredHash) &&
                    shaderHash == filteredHash)
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    internal static string GetBasePath(string stage, ulong shaderAddress, ulong hash)
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(AppContext.BaseDirectory, "shader-dumps");
        }

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{shaderAddress:X16}-{hash:X16}.{stage}");
    }

    public static void Write(string stage, ulong shaderAddress, ulong hash, IGuestCompiledShader shader, Gen5ShaderProgram program)
    {
        if (shader.Payload.Length == 0 || !ShouldWrite(shaderAddress, hash)) return;
        var basePath = GetBasePath(stage, shaderAddress, hash);
        File.WriteAllBytes($"{basePath}.{shader.PayloadFileExtension}", shader.Payload);
        var lines = new List<string>(program.Instructions.Count + 2)
        {
            $"address=0x{program.Address:X16}",
            "pc words opcode destinations <- sources control",
        };
        foreach (var instruction in program.Instructions)
        {
            lines.Add(
                $"0x{instruction.Pc:X4} {string.Join('_', instruction.Words.Select(static word => $"{word:X8}"))} {instruction.Opcode} " +
                $"{string.Join(',', instruction.Destinations)} <- {string.Join(',', instruction.Sources)} {instruction.Control}");
        }

        File.WriteAllLines($"{basePath}.ir.txt", lines);
    }
}
