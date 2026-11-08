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

    public ulong ContinuationAddressOffset => Registered.IsFused
        ? unchecked(Registered.ContinuationAddress - Address)
        : 0;

    public string Label => Stage switch
    {
        ShaderStage.Vertex => "vertex",
        ShaderStage.Pixel => "pixel",
        ShaderStage.Mesh => "mesh",
        ShaderStage.TessellationEvaluation => "domain",
        _ => "compute",
    };
}

// The stage-specific compile inputs one lookup carries beside its static key. Nothing keeps
// them past the lookup, so the pipeline cache refills one instance per stage.
public sealed class StageCompileOptions
{
    public VertexInputInfo? VertexInfo { get; set; }
    public int RequiredVertexOutputCount { get; set; }
    public uint? MeshOutputLocationMask { get; set; }
    public PixelInputInfo? PixelInfo { get; set; }
    public IReadOnlyList<Gen5PixelOutputBinding> PixelOutputs { get; set; } = [];
    public uint PixelInputEnable { get; set; }
    public uint PixelInputAddress { get; set; }
    public ComputeInputInfo? ComputeInfo { get; set; }
    public Gen5ComputeSystemRegisters? ComputeSystemRegisters { get; set; }
    public MeshDrawConfiguration? MeshInfo { get; set; }
    public Gen5TessellationInfo? Tessellation { get; set; }
    public Gen5TessellationHullInfo? TessellationHull { get; set; }

    public StageCompileOptions Reset()
    {
        VertexInfo = null;
        RequiredVertexOutputCount = 0;
        MeshOutputLocationMask = null;
        PixelInfo = null;
        PixelOutputs = [];
        PixelInputEnable = 0;
        PixelInputAddress = 0;
        ComputeInfo = null;
        ComputeSystemRegisters = null;
        MeshInfo = null;
        Tessellation = null;
        TessellationHull = null;
        return this;
    }

    // [local] A copy for a record that outlives the lookup (a refilled instance changes with the next one).
    public StageCompileOptions Clone() => (StageCompileOptions)MemberwiseClone();
}

// The key of a program entry: what the emitter reads besides the resource specialization.
public sealed class ProgramKey : IEquatable<ProgramKey>
{
    public ProgramKey(ShaderStage stage, ulong hash, uint userDataCount, uint codeSize,
        ulong continuationAddressOffset, uint[] staticState)
    {
        Stage = stage;
        Hash = hash;
        UserDataCount = userDataCount;
        CodeSize = codeSize;
        ContinuationAddressOffset = continuationAddressOffset;
        StaticState = staticState;
    }

    public ShaderStage Stage { get; }
    public ulong Hash { get; }
    public uint UserDataCount { get; }
    public uint CodeSize { get; }
    public ulong ContinuationAddressOffset { get; }
    public uint[] StaticState { get; }

    public bool Equals(ProgramKey? other) =>
        other is not null && Stage == other.Stage && Hash == other.Hash && UserDataCount == other.UserDataCount &&
        CodeSize == other.CodeSize && ContinuationAddressOffset == other.ContinuationAddressOffset &&
        StaticState.AsSpan().SequenceEqual(other.StaticState);

    public override bool Equals(object? obj) => Equals(obj as ProgramKey);

    // Same-shape variants share a bucket; equality does the one exact comparison of the state words.
    public override int GetHashCode() =>
        HashCode.Combine(Stage, Hash, UserDataCount, CodeSize, ContinuationAddressOffset, StaticState.Length);
}

// A program key as a lookup sees it: the static state words stay in the caller's list, so a draw
// that finds its program does not build a key object or copy the words.
internal readonly ref struct ProgramKeyProbe(ShaderStage stage, ulong hash, uint userDataCount, uint codeSize,
    ulong continuationAddressOffset, ReadOnlySpan<uint> staticState)
{
    public readonly ShaderStage Stage = stage;
    public readonly ulong Hash = hash;
    public readonly uint UserDataCount = userDataCount;
    public readonly uint CodeSize = codeSize;
    public readonly ulong ContinuationAddressOffset = continuationAddressOffset;
    public readonly ReadOnlySpan<uint> StaticState = staticState;
}

internal sealed class ProgramKeyComparer : IEqualityComparer<ProgramKey>, IAlternateEqualityComparer<ProgramKeyProbe, ProgramKey>
{
    public static readonly ProgramKeyComparer Instance = new();

    public bool Equals(ProgramKey? x, ProgramKey? y) => x is null ? y is null : x.Equals(y);

    public int GetHashCode(ProgramKey key) => key.GetHashCode();

    public bool Equals(ProgramKeyProbe alternate, ProgramKey key) =>
        alternate.Stage == key.Stage && alternate.Hash == key.Hash && alternate.UserDataCount == key.UserDataCount &&
        alternate.CodeSize == key.CodeSize && alternate.ContinuationAddressOffset == key.ContinuationAddressOffset &&
        alternate.StaticState.SequenceEqual(key.StaticState);

    // The same value as ProgramKey.GetHashCode.
    public int GetHashCode(ProgramKeyProbe alternate) =>
        HashCode.Combine(alternate.Stage, alternate.Hash, alternate.UserDataCount, alternate.CodeSize,
            alternate.ContinuationAddressOffset, alternate.StaticState.Length);

    public ProgramKey Create(ProgramKeyProbe alternate) =>
        new(alternate.Stage, alternate.Hash, alternate.UserDataCount, alternate.CodeSize,
            alternate.ContinuationAddressOffset, alternate.StaticState.ToArray());
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
    public required bool HasShaderCalls { get; init; }

    // Linked with run-time record arguments: one copy of each callee per site (ShaderCallEntryControl.RuntimeArgument), whose
    // addresses the dispatch pushes in this order.
    public bool RuntimeCallRecords { get; init; }
    public ulong[] CallTargets { get; init; } = [];

    // The order of its link among all linked programs: a newer program of a caller replaces an older one as its fallback.
    public long Generation { get; init; }

    // With RuntimeCallRecords, the hash of the code linked for each of CallTargets, and each site's callees with their code.
    public byte[][] CallTargetCodes { get; init; } = [];
    public Dictionary<uint, UnitedCallee[]>? RuntimeCallees { get; init; }
    public ConstantFill? ConstantFill { get; init; }
    public BoundedFill? BoundedFill { get; init; }
    public BoundedCopy? BoundedCopy { get; init; }
    public bool StoresUniformValues { get; init; }
    public EmbeddedVertexFetchPlan? EmbeddedFetch { get; init; }
    public ShaderVertexInput[] VertexInputs { get; init; } = [];
    private ProgramPermutation[] _permutations = [];

    public IReadOnlyList<ProgramPermutation> Permutations => Volatile.Read(ref _permutations);

    public ProgramPermutation? LastPermutation;

    public void AddPermutation(ProgramPermutation permutation)
    {
        var current = _permutations;
        var grown = new ProgramPermutation[current.Length + 1];
        current.CopyTo(grown, 0);
        grown[^1] = permutation;
        Volatile.Write(ref _permutations, grown);
    }
}

// Programs keyed by identity and static state; each draw materializes its resources and reuses
// the permutation whose specialization and push-data start match, else compiles one more.
internal sealed class ShaderProgramCache
{
    private readonly ImageDescriptorTrace _imageDescriptorTrace = new();
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProgramKey, ProgramSourceEntry> _programs = new(ProgramKeyComparer.Instance);
    private readonly Dictionary<(ProgramKey Key, string Library), ProgramSourceEntry> _linkedPrograms = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Hash, uint CodeSize, ulong ContinuationAddressOffset), Gen5ShaderProgram> _decoded = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Hash, uint CodeSize, ulong ContinuationAddressOffset), ShaderCodeCapture> _codeCaptures = new();
    // [local] Shared with the pipeline cache's gate when it passes one (SHARPEMU_PROGRAM_GATE=0 holds it for a lookup).
    private readonly object _gate;
    [ThreadStatic]
    private static List<uint>? _threadStaticState;
    // Draws that re-bind unchanged resources reuse the last materialization; every thread that
    // resolves draws keeps its own cache. SHARPEMU_RESOURCE_CACHE=0 materializes every draw, for A/B comparisons.
    // [local] SHARPEMU_MATERIALIZATION_CACHE=0 too.
    private static readonly bool MaterializationCacheEnabled = Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_CACHE") != "0" &&
        Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZATION_CACHE") != "0";
    [ThreadStatic]
    private static ResourceMaterializationCache? _threadMaterializations;
    // [local] SHARPEMU_PROGRAM_GATE=0: one cache shared by every thread, used under the gate (the old behaviour); with
    // the gate off it only serves the verify and StoreTableMode 0.
    private readonly ResourceMaterializationCache? _materializations = MaterializationCacheEnabled ? new() : null;

    // Linking a program with a large hit-group table and planning it takes seconds (about 3 s for a table of 1,381
    // records), and a game that streams callees rebuilds its tables as it plays. With DeferLinking, TryGetProgram
    // links a new layout on a worker and reports the program as not ready, so its dispatches are skipped as they are
    // while its module compiles; each program links one layout at a time.
    public bool DeferLinking { get; init; }
    private readonly Dictionary<ulong, ((ProgramKey Key, string Library) LinkedKey, Task<ProgramSourceEntry?> Link)> _pendingLinks = new();

    // Callers whose callees cannot read their records at run time: they link a copy per record.
    private readonly HashSet<ulong> _perRecordCallers = [];

    // A caller linked with run-time call records keeps dispatching the newest of its programs whose pipeline is ready while a
    // newer one links, translates or compiles, with each dispatch's own records: a record whose callee that program lacks
    // returns from its call at once (ShaderCallFaultControl.ReturnPc), so only those hits go unshaded until the newer one is
    // ready, instead of every dispatch of the caller being skipped.
    private readonly Dictionary<ProgramKey, ProgramSourceEntry> _runtimeFallbacks = new(ProgramKeyComparer.Instance);

    // The programs of each runtime-record caller whose pipeline was still compiling when the caller moved on to a newer one;
    // each dispatch of the caller polls them, and one that is ready serves until a newer one is.
    private readonly Dictionary<ProgramKey, List<(ProgramSourceEntry Entry, ShaderProgram Handle, ShaderStageResources Stage)>> _runtimeCompiles =
        new(ProgramKeyComparer.Instance);

    // The callees each runtime-record caller's sites have named so far (ShaderCallLibrary.Unite), and the ones an earlier
    // session kept for it (ShaderCallUnionStore).
    private readonly Dictionary<ProgramKey, Dictionary<uint, Dictionary<ulong, UnitedCallee>>> _calleeUnions =
        new(ProgramKeyComparer.Instance);
    private readonly Dictionary<ProgramKey, Dictionary<uint, Dictionary<ulong, UnitedCallee>>> _keptCallees =
        new(ProgramKeyComparer.Instance);
    private ShaderCallUnionStore? _callUnionStore;

    // The callees each runtime-record caller's calls reach (ShaderCalleeTiers), when the host marks calls; without marks a
    // caller links every callee its tables name.
    private readonly Dictionary<ProgramKey, ShaderCalleeTiers> _calleeTiers = new(ProgramKeyComparer.Instance);

    // The linked programs and the call-linking state above belong to the thread holding this lock. It is taken before _gate
    // and never inside it: a link, a pipeline poll and a fallback's recursion happen under it, a permutation compile does not.
    private readonly object _callGate = new();

    private bool CalleeTiering => _host.CallMarksSupported;

    private const int MaxUnitedCallees = 192;

    private static long _linkGenerations;

    // Starts or polls the compute pipeline of a program for the stage it was prepared with and reports whether it is ready
    // (ShaderPipelineCache.TryCreateComputePipeline).
    public Func<ShaderProgram, ShaderStageResources, ComputeInputInfo, bool>? TryComputePipeline { get; set; }

    // A linked compute program's translation takes up to a second or two, and its module then compiles for minutes while
    // its dispatches are skipped. With DeferLinking the translation runs on a worker too, one at a time per program like
    // its link, so the command stream does not stop for it.
    private readonly Dictionary<ulong, PendingTranslation> _pendingTranslations = new();

    private sealed record DeferredTranslation(SpecializedResourceInfo Resources, BindingLayout Layout, ShaderCompileRequest Request,
        bool Translated, IGuestCompiledShader? Compiled, string Error);

    private sealed record PendingTranslation(ProgramSourceEntry Entry, ResourceSpecialization Specialization, uint PushDataCursor,
        Task<DeferredTranslation> Translation);
    private ulong _nextProgramId;

    private static List<uint> StaticState => _threadStaticState ??= new List<uint>(StageStaticKey.MaxWords);

    // [local] SHARPEMU_PROGRAM_GATE_TABLES picks how the thread's cache treats store tables (StoreTableMode).
    public static ResourceMaterializationCache? Materializations =>
        MaterializationCacheEnabled
            ? _threadMaterializations ??= new ResourceMaterializationCache(limitChurn: true,
                stampStoreTables: StoreTableMode == 1, currentStoreTables: StoreTableMode == 3)
            : null;

    // [local] The cache this lookup materializes with: the thread's, or with SHARPEMU_PROGRAM_GATE=0 the shared one
    // (the caller holds the gate), or the shared one under the gate for a plan with store tables in StoreTableMode 0.
    private ResourceMaterializationCache? MaterializationsFor(ProgramSourceEntry entry, out bool sharedUnderGate)
    {
        sharedUnderGate = LockFree && StoreTableMode == 0 && entry.Plan.DeviceStoreTables.Count != 0;
        return LockFree && !sharedUnderGate ? Materializations : _materializations;
    }

    public bool Revalidate(in ShaderStageResources stage)
    {
        if (stage.Program is null)
        {
            return false;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var fast = ResourceMaterializer.RevalidateFast(stage.Resources);
        var current = fast ?? (LockFree
            ? Materializations is { } materializations && materializations.Revalidate(stage.Resources, _readResidentGuestBytes)
            : RevalidateShared(stage.Resources)); // [local] SHARPEMU_PROGRAM_GATE=0: the snapshot came from the shared cache
        var outcome = current ? MaterializationTally.Outcome.KeptRevalidated
            : fast is not null && ResourceMaterializer.LastRevalidateRefused ? MaterializationTally.Outcome.RevalidateRefused
            : MaterializationTally.Outcome.KeptStale;
        MaterializationTally.Note(outcome, System.Diagnostics.Stopwatch.GetTimestamp() - started, stage.Resources);
        return current;
    }

    private bool RevalidateShared(ResourceSnapshot snapshot)
    {
        if (_materializations is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _materializations.Revalidate(snapshot, _readResidentGuestBytes);
        }
    }

    // Read once: the draw path asked the environment on every draw and dispatch
    // (~4 % of the Demon's Souls render thread) for a debug dump that is almost never on.
    private readonly bool _spirvDumpEnabled = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"), "1", StringComparison.Ordinal);

    private readonly GuestWordReader _readGuestWord;
    private readonly GuestWordReader _readCleanGuestWord;
    // Refilled per lookup: the readers never change and nothing keeps the instance. [local] One per thread, with the
    // verify's static-state scratch (the lookup's own is StaticState).
    private sealed class LookupScratch(ResourceRuntimeInputs inputs)
    {
        public readonly ResourceRuntimeInputs Inputs = inputs;
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

    // The functions the call tables name, decoded once while their code stays the same.
    private readonly ShaderFunctionCache _functions;

    private static readonly bool PrefetchEnabled = Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_PREFETCH") != "0";

    // On a host that reads call records at run time (descriptor set 1), a linked program keeps one copy of each callee per
    // call site and reads its records' images and samplers at run time, so tables that gain or lose records, or move their
    // arguments, link to the same program; only a new callee links a new one. [local] SHARPEMU_RUNTIME_CALL_RECORDS=1 turns
    // that on; off by default (a copy per record, as in 3368666f).
    private static readonly bool RuntimeCallRecordsEnabled = Environment.GetEnvironmentVariable("SHARPEMU_RUNTIME_CALL_RECORDS") == "1";

    private bool RuntimeCallRecords => _host.RuntimeCallRecordsSupported && RuntimeCallRecordsEnabled;

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
            // [local] SHARPEMU_PAGE_SPAN_READS: only beside the resident reader the spans stand in for.
            ReadResidentSpan = SharpEmu.ShaderCompiler.Resources.PageSpanReads.DefaultMode != 0 && _prefetchResidentGuestBytes is not null
                ? host.TryReadResidentGuestSpan
                : null,
            SynchronizeRanges = synchronizeRanges,
            SynchronizeTableRange = SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.TableSyncMode != 0 ? host.TrySynchronizeTableRange : null, // [local]
            CompareTableChunk = SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.CompareModeConfigured != 0 ? host.CompareResidentTableChunk : null, // [local] SHARPEMU_STORE_TABLE_COMPARE
        }));
        _functions = new ShaderFunctionCache(_readResidentGuestBytes);
    }

    private ImageDescriptorNormalizer? ImageNormalizer(IShaderFrontend? frontend)
    {
        if (frontend?.NormalizeImageDescriptor is not { } normalizer)
        {
            return null;
        }

        return frontend.RequiresOrderedImagePreparation
            ? (words, stage, hash, source) => _host.NormalizeImageDescriptor(frontend, words, stage, hash, source)
            : normalizer;
    }

    public int ProgramCount => _programs.Count;

    public IEnumerable<ProgramSourceEntry> Entries
    {
        get
        {
            lock (_callGate)
            {
                return [.. _programs.Values, .. _linkedPrograms.Values];
            }
        }
    }

    private readonly Dictionary<(BindingLayout Layout, Gen5TessellationDomain Domain), (ShaderProgram Vertex, ShaderProgram Control)> _tessellationBridges = [];

    // The fixed-function control stage beside a domain stage, and the vertex stage a tessellated pipeline requires.
    public (ShaderProgram Vertex, ShaderProgram Control) GetTessellationBridge(BindingLayout layout, Gen5TessellationDomain domain)
    {
        if (_tessellationBridges.TryGetValue((layout, domain), out var bridge)) return bridge;
        var shaders = _compiler.CompileTessellationBridge(layout, domain);
        ShaderProgram Create(IGuestCompiledShader shader, ShaderStage stage)
        {
            var id = ++_nextProgramId;
            return new(id, _host.CreateShaderModule(shader, stage, 0, id));
        }

        bridge = (Create(shaders.Vertex, ShaderStage.Vertex), Create(shaders.Control, ShaderStage.TessellationEvaluation));
        _tessellationBridges.Add((layout, domain), bridge);
        return bridge;
    }

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
        var key = (source.Hash, source.CodeSize, source.ContinuationAddressOffset);
        if (_decoded.TryGetValue(key, out var decoded))
        {
            return decoded;
        }

        var program = DecodeCore(source, key);
        if (!_decoded.TryAdd(key, program))
        {
            // [local] Another thread decoded the same code meanwhile: every caller shares its program.
            return _decoded[key];
        }

        if (Vulkan.VulkanCommandProfile.PassesEnabled && program.Instructions.Any(static i => i.Opcode is "ImageBvhIntersectRay" or "ImageBvh64IntersectRay")) // [local] SHARPEMU_PROFILE_GPU_PASSES: ray-tracing programs
            Console.Error.WriteLine($"[PERF][GPU_SHADER_RT] hash=0x{source.Hash:X16} stage={source.Label} instructions={program.Instructions.Count}");
        if (Environment.GetEnvironmentVariable("SHARPEMU_LOG_GLOBAL_STORES") == "1") // [local] programs that store through global/flat addresses
        {
            var stores = program.Instructions.Where(static i => i.Opcode.StartsWith("GlobalStore", StringComparison.Ordinal) || i.Opcode.StartsWith("FlatStore", StringComparison.Ordinal) || i.Opcode.StartsWith("GlobalAtomic", StringComparison.Ordinal) || i.Opcode.StartsWith("FlatAtomic", StringComparison.Ordinal)).Select(static i => i.Opcode).Distinct().ToArray();
            if (stores.Length != 0) Console.Error.WriteLine($"[GLOBAL_STORES] hash=0x{source.Hash:X16} stage={source.Label} instructions={program.Instructions.Count} ops={string.Join(",", stores)}");
        }

        return program;
    }

    private Gen5ShaderProgram DecodeCore(ShaderSource source, (ulong Hash, uint CodeSize, ulong ContinuationAddressOffset) key)
    {
        using var decodeScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramDecode);
        var recording = _host.ShaderPrewarm is not null ? new RecordingCpuMemory(_context.Memory) : null;
        var context = recording is null ? _context : new CpuContext(recording, _context.TargetGeneration);
        if (Environment.GetEnvironmentVariable("LOCAL_DUMP_SHADER_HASH") is { Length: > 0 } wanted &&
            wanted.Contains($"{source.Hash:X16}", StringComparison.OrdinalIgnoreCase))
        {
            DumpFailedShader(source); // [local]
        }

        Gen5ShaderProgram program;
        var decoded = source.Registered.Frontend is { } frontend
            ? frontend.TryDecode(context, source.Registered, source.Stage, out program, out var error)
            : Gen5ShaderTranslator.TryDecodeProgram(context, source.Address, out program, out error);
        if (!decoded)
        {
            DumpFailedShader(source); // [local]
            var userData = source.Registered.Frontend is null ? "" : $" user_data=[{string.Join(',', source.UserData.Select(word => word.ToString("X8")))}]";
            throw SubmissionScheduler.Fatal($"The shader program cannot be decoded: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={error}.{userData}");
        }

        if (recording is not null)
        {
            var external = source.Registered.Frontend;
            _codeCaptures[key] = new ShaderCodeCapture
            {
                Hash = source.Hash,
                CodeSize = source.CodeSize,
                Address = source.Address,
                Generation = _context.TargetGeneration,
                Fused = external is null && Gen5ShaderTranslator.TryGetFusedProgramParts(
                    _context, source.Address, out var entryHeader, out var continuation, out var continuationHeader)
                    ? new FusedCodeParts(entryHeader, continuation, continuationHeader)
                    : null,
                Ranges = recording.TakeRanges(),
                FrontendDomain = external?.CacheDomain ?? 0,
                FrontendUserData = external is null ? [] : source.Registered.FrontendUserData.ToArray(),
                FrontendContinuationOffset = external is null ? 0 : source.ContinuationAddressOffset,
            };
        }

        return program;
    }

    internal (ShaderProgram Handle, ShaderProgramInfo Program) CompilePrewarmStage(ShaderSource source, StageCompileOptions options,
        ResourceSpecialization specialization, uint pushDataCursor)
    {
        var entry = CreateEntry(source, options);
        var permutation = CompilePermutation(source, options, entry, specialization, pushDataCursor, KeyFor(source, options),
            sourceWasCached: false, snapshot: null!);
        return (permutation.Handle, permutation.Program);
    }

    // Links a program with shader calls before it returns.
    public ShaderProgram GetOrCompile(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor, out ShaderStageResources stage)
    {
        try
        {
            return GetOrCompileCore(source, options, ref pushDataCursor, out stage, deferLinking: false);
        }
        catch (ShaderProgramRejectedException exception)
        {
            throw SubmissionScheduler.Fatal(exception.Message);
        }
    }

    // False with no rejection while the program's link runs on a worker (DeferLinking).
    public bool TryGetProgram(ShaderSource source, StageCompileOptions options, bool strictShaders,
        ref uint pushDataCursor, out ShaderProgram program, out ShaderStageResources stage, out string rejection)
    {
        rejection = string.Empty;
        try
        {
            program = GetOrCompileCore(source, options, ref pushDataCursor, out stage, DeferLinking);
            return true;
        }
        catch (ShaderProgramPendingException)
        {
            program = default;
            stage = default;
            return false;
        }
        catch (ShaderProgramRejectedException exception)
        {
            if (strictShaders)
            {
                throw SubmissionScheduler.Fatal(exception.Message);
            }

            program = default;
            stage = default;
            rejection = exception.Message;
            return false;
        }
    }

    private sealed class ShaderProgramRejectedException(string message) : Exception(message);

    // [local] SHARPEMU_UNREADABLE_IMAGE_TABLE_REJECT (=1 by default, =0 fatal as before): a program whose image table cannot be
    // read or enumerated for this dispatch is rejected like one with incompatible candidates, so the dispatch is skipped
    // and the next one tries again. GTA V's 0xD44E (reflection hit shading) failed this way, fatally, with reason=Other
    // right after the player got into the car outside the prologue bank.
    private static readonly bool UnreadableImageTableRejects =
        Environment.GetEnvironmentVariable("SHARPEMU_UNREADABLE_IMAGE_TABLE_REJECT") != "0";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> UnreadableImageTables = new();

    private static void NoteUnreadableImageTable(ulong hash, string message)
    {
        var count = UnreadableImageTables.AddOrUpdate(hash, 1, static (_, value) => value + 1);
        if ((count & (count - 1)) == 0)
            Console.Error.WriteLine($"[GPU][WARN][UNREADABLE_IMAGE_TABLE] count={count} {message}");
    }

    private sealed class ShaderProgramPendingException() : Exception("The linked program is still being prepared.");

    [ThreadStatic]
    private static ProgramSourceEntry? _lastEntry;

    internal static ProgramSourceEntry? LastEntry => _lastEntry;

    internal bool TryGetProgramForEntry(ProgramSourceEntry entry, ShaderSource source, StageCompileOptions options, bool strictShaders,
        ref uint pushDataCursor, out ShaderProgram program, out ShaderStageResources stage, out string rejection)
    {
        rejection = string.Empty;
        try
        {
            program = CompleteProgram(entry, sourceWasCached: true, key: null, source, options, ref pushDataCursor, out stage, DeferLinking);
            return true;
        }
        catch (ShaderProgramPendingException)
        {
            program = default;
            stage = default;
            return false;
        }
        catch (ShaderProgramRejectedException exception)
        {
            if (strictShaders)
            {
                throw SubmissionScheduler.Fatal(exception.Message);
            }

            program = default;
            stage = default;
            rejection = exception.Message;
            return false;
        }
    }

    // With runtimeFallback, the caller's dispatch runs that ready program of it (see _runtimeFallbacks).
    private ShaderProgram GetOrCompileCore(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor, out ShaderStageResources stage,
        bool deferLinking, ProgramSourceEntry? runtimeFallback = null)
    {
        ProgramKey? key = null;
        ProgramSourceEntry? entry;
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramCacheLookup))
        using (SharpEmu.Libs.VideoOut.FrontProfile.Measure(SharpEmu.Libs.VideoOut.FrontProfile.Phase.ProgramKey))
        {
            entry = FindEntry(source, options, out key);
        }

        Diagnostics.FrontCostProbe.Count(Diagnostics.FrontCostProbe.Counter.Stages); // [local] SHARPEMU_FRONTCOST_PROBE
        Diagnostics.FrontCostProbe.Lap(Diagnostics.FrontCostProbe.Step.StageKey);

        var sourceWasCached = entry is not null;
        if (RenderTrace.Enabled && RenderTrace.Pipeline())
        {
            RenderTrace.Write($"ProgramCache lookup stage={source.Label} hash=0x{source.Hash:X16} cached={entry is not null} permutations={entry?.Permutations.Count ?? 0}");
        }

        if (entry is null)
        {
            // [local] Decoded and planned outside the gate: an entry is a pure function of its key, and a thread that
            // loses the race uses the entry that won. The key is taken before CreateEntry, from the state FindEntry built.
            var createdKey = BuildProgramKey(source);
            var created = CreateEntry(source, options);
            entry = _programs.GetOrAdd(createdKey, created);
            if (ReferenceEquals(entry, created))
            {
                key = createdKey;
                ShaderCacheCounters.CountProgram();
            }
            else
            {
                sourceWasCached = true;
            }
        }

        return CompleteProgram(entry, sourceWasCached, key, source, options, ref pushDataCursor, out stage, deferLinking, runtimeFallback);
    }

    private ProgramSourceEntry? FindEntry(ShaderSource source, StageCompileOptions options, out ProgramKey? key)
    {
        BuildStaticState(source.Stage, options);
        var probe = new ProgramKeyProbe(source.Stage, source.Hash, (uint)source.UserData.Length, source.CodeSize,
            source.ContinuationAddressOffset, CollectionsMarshal.AsSpan(StaticState));
        if (_programs.GetAlternateLookup<ProgramKeyProbe>().TryGetValue(probe, out var found, out var entry))
        {
            key = found;
            return entry;
        }

        key = null;
        return null;
    }

    private ShaderProgram CompleteProgram(ProgramSourceEntry entry, bool sourceWasCached, ProgramKey? key, ShaderSource source, StageCompileOptions options,
        ref uint pushDataCursor, out ShaderStageResources stage, bool deferLinking, ProgramSourceEntry? runtimeFallback = null)
    {
        if (runtimeFallback is null)
        {
            _lastEntry = entry;
        }

        var scratch = _scratch.Value!;
        var inputs = InputsFor(scratch, source, options);
        var hasShaderCalls = entry.HasShaderCalls;
        if (hasShaderCalls && !_compiler.LinkedShaderCallsSupported)
            throw new ShaderProgramRejectedException(
                $"The backend does not support linked shader calls: backend={_compiler.BackendName} stage={source.Label} hash=0x{source.Hash:X16}.");

        // Both are assigned by the materialization that follows; a failed one throws. [local] Shared empty instances, as the
        // materializers before this merge expected non-null outputs.
        var snapshot = EmptySnapshot;
        var specialization = EmptySpecialization;
        var materializations = MaterializationsFor(entry, out var sharedForTables); // [local] SHARPEMU_PROGRAM_GATE(_TABLES)
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
            ResourceMaterializationFailure materializationFailure;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool materialized;
            MaterializationTally.Outcome outcome;
            var frontStarted = SharpEmu.Libs.VideoOut.FrontProfile.Active ? started : 0; // [local]
            var hitsBefore = materializations?.Hits ?? 0;
            var refreshesBefore = materializations?.TableRefreshes ?? 0;
            // [local] SHARPEMU_PROFILE_RESOLVE: this materialization's time and completion waits, by program.
            var resolveStarted = SharpEmu.Libs.VideoOut.ResolveProfile.Enabled ? started : 0;
            var waitsBefore = resolveStarted != 0 ? SharpEmu.Libs.VideoOut.ResolveProfile.CompletionWaits : 0;
            var waitTicksBefore = resolveStarted != 0 ? SharpEmu.Libs.VideoOut.ResolveProfile.CompletionWaitTicks : 0;
            var coldStarted = Diagnostics.FrameMissProbe.Cold ? started : 0; // [local] SHARPEMU_COLDRES_PROBE
            var coldWaited = Diagnostics.FrameMissProbe.ThreadReadbackTicks;
            Diagnostics.DrawCostProbe.Lap(Diagnostics.DrawCostProbe.Step.MaterializePrelude); // [local] SHARPEMU_DRAWCOST_PROBE
            if (sharedForTables)
            {
                SharpEmu.Libs.VideoOut.FrontProfile.Switch(SharpEmu.Libs.VideoOut.FrontProfile.Phase.ProgramGate);
                lock (_gate)
                {
                    if (coldStarted != 0) // [local] SHARPEMU_COLDRES_PROBE
                    {
                        var entered = System.Diagnostics.Stopwatch.GetTimestamp();
                        Diagnostics.FrameMissProbe.AddPhase(RenderPhaseProfile.Phase.ColdProgramGateWait, entered - coldStarted);
                        coldStarted = entered;
                    }

                    SharpEmu.Libs.VideoOut.FrontProfile.Switch(SharpEmu.Libs.VideoOut.FrontProfile.Phase.Materialize);
                    materialized = MaterializeWith(materializations, entry, inputs, ref snapshot, ref specialization, out materializationFailure,
                        captureIndirectImageFailure, out outcome);
                }
            }
            else
            {
                materialized = MaterializeWith(materializations, entry, inputs, ref snapshot, ref specialization, out materializationFailure,
                    captureIndirectImageFailure, out outcome);
            }

            MaterializationTally.Note(materialized ? outcome : MaterializationTally.Outcome.Failed,
                System.Diagnostics.Stopwatch.GetTimestamp() - started, materialized ? snapshot : null);
            if (Diagnostics.DrawCostProbe.Timing) // [local] SHARPEMU_DRAWCOST_PROBE: what the materialization did
                CountMaterialization(outcome);
            // [local] For the probes below, a hit is a cache hit or a fast materialization that kept its last result.
            var hit = outcome is MaterializationTally.Outcome.FastHit or MaterializationTally.Outcome.FastMemoHit || (materializations is not null && materializations.Hits != hitsBefore);
            if (coldStarted != 0) // [local] SHARPEMU_COLDRES_PROBE
            {
                Diagnostics.FrameMissProbe.AddPhase(hit ? RenderPhaseProfile.Phase.ColdMaterializeHit : RenderPhaseProfile.Phase.ColdMaterializeMiss,
                    System.Diagnostics.Stopwatch.GetTimestamp() - coldStarted);
                Diagnostics.FrameMissProbe.AddPhase(hit ? RenderPhaseProfile.Phase.ColdMaterializeHitWait : RenderPhaseProfile.Phase.ColdMaterializeMissWait,
                    Diagnostics.FrameMissProbe.ThreadReadbackTicks - coldWaited);
            }

            if (Diagnostics.FrameMissProbe.Enabled) // [local]
            {
                Diagnostics.FrameMissProbe.CountOnRender(Diagnostics.FrameMissProbe.Counter.RenderMaterializations);
                if (!hit)
                    Diagnostics.FrameMissProbe.CountOnRender(Diagnostics.FrameMissProbe.Counter.RenderMaterializationMisses);
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
                    hit,
                    SharpEmu.Libs.VideoOut.ResolveProfile.CompletionWaits - waitsBefore,
                    SharpEmu.Libs.VideoOut.ResolveProfile.CompletionWaitTicks - waitTicksBefore);
            if (frontStarted != 0)
                SharpEmu.Libs.VideoOut.FrontProfile.NoteMaterialize(
                    hit ? 0 : materializations is { } refreshed && refreshed.TableRefreshes != refreshesBefore ? 1 : 2,
                    System.Diagnostics.Stopwatch.GetTimestamp() - frontStarted);
            if (!materialized)
            {
                var detail = ResourceMaterializer.TakeFailureDetail(); // [local]
                var message = $"The shader resources could not be materialized: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} reason={materializationFailure}" +
                    (detail is null ? "." : $" detail=[{detail}].");
                if (materializationFailure is ResourceMaterializationFailure.IncompatibleImageCandidates or ResourceMaterializationFailure.ImageCapacityExceeded)
                    throw new ShaderProgramRejectedException(message);
                if (UnreadableImageTableRejects && detail?.StartsWith(ResourceMaterializer.ImageTableDetailPrefix, StringComparison.Ordinal) == true) // [local]
                {
                    NoteUnreadableImageTable(source.Hash, message);
                    throw new ShaderProgramRejectedException(message);
                }
                throw SubmissionScheduler.Fatal(message);
            }
        }

        Diagnostics.DrawCostProbe.Lap(Diagnostics.DrawCostProbe.Step.ProgramMaterialize); // [local] SHARPEMU_DRAWCOST_PROBE
        Diagnostics.FrontCostProbe.Lap(Diagnostics.FrontCostProbe.Step.StageMaterialize); // [local] SHARPEMU_FRONTCOST_PROBE
        ulong[] callArguments = [];
        ulong[] callTargets = [];
        (ulong Target, ulong Argument)[]? callRecords = null;
        DeviceAddressRange[]? callRecordRanges = null;
        if (hasShaderCalls)
        {
            ShaderResourcePlan? failedLinkedPlan = null;
            var captureLinkedImages = CompiledShaderDump.ShouldWrite(source.Address, source.Hash);
            var programKey = key ??= KeyFor(source, options);
            lock (_callGate)
            {
                try
                {
                    var caller = entry.Program with { Address = source.Address };
                    // Tables and function code are read like the caller's code: a stale image over reused memory must not hide them.
                    var runtimeRecords = runtimeFallback is not null || (RuntimeCallRecords && !_perRecordCallers.Contains(source.Hash));
                    var library = ShaderCallLibrary.Read(caller, entry.Plan.Memory, snapshot, _readGuestWord, _functions, runtimeRecords);
                    if (runtimeRecords)
                    {
                        if (!_calleeUnions.TryGetValue(programKey, out var union))
                        {
                            var kept = (_callUnionStore ??= ShaderCallUnionStore.Open()).Load(programKey.Stage, programKey.Hash);
                            _keptCallees.Add(programKey, kept);
                            union = kept.ToDictionary(static site => site.Key, static site => new Dictionary<ulong, UnitedCallee>(site.Value));
                            _calleeUnions.Add(programKey, union);
                            if (CalleeTiering)
                            {
                                _calleeTiers.Add(programKey, new ShaderCalleeTiers(kept));
                            }
                        }

                        library = library.Unite(caller.Address, union, MaxUnitedCallees);
                        if (_calleeTiers.TryGetValue(programKey, out var tiers))
                        {
                            if (runtimeFallback is null)
                            {
                                tiers.Observe(library, programKey.Hash, _host);
                                library = library.WithSiteCallees(caller.Address,
                                    tiers.Choose(union, FallbackGeneration(programKey), _host.CallMarkSnapshots));
                            }
                        }
                        else if (runtimeFallback is null && !_runtimeFallbacks.ContainsKey(programKey))
                        {
                            // A caller with no ready program first links the callees an earlier session kept for it, whose pipeline
                            // the linked cache holds, or none, so its dispatches run within seconds instead of being skipped until
                            // the driver has compiled the callees' code.
                            library = library.WithSiteCallees(caller.Address, _keptCallees[programKey]);
                        }
                    }

                    var linkedKey = (programKey, library.LayoutIdentity);
                    var linked = runtimeFallback;
                    if (linked is null && !_linkedPrograms.TryGetValue(linkedKey, out linked))
                    {
                        var waveSize = source.Stage == ShaderStage.Mesh
                            ? options.MeshInfo!.Geometry.WaveSize : options.ComputeInfo?.WaveSize ?? 32u;
                        if (deferLinking && !captureLinkedImages)
                        {
                            try
                            {
                                linked = TakeDeferredLink(linkedKey, caller, library, source, waveSize, runtimeRecords);
                            }
                            catch (ShaderProgramPendingException) when (runtimeRecords && _runtimeFallbacks.TryGetValue(programKey, out var ready))
                            {
                                return GetOrCompileCore(source, options, ref pushDataCursor, out stage, deferLinking, ready);
                            }
                        }
                        else
                        {
                            linked = LinkProgram(caller, library, source.Stage, source.Hash, source.UserDataBase, (uint)source.UserData.Length,
                                waveSize, captureLinkedImages ? plan => failedLinkedPlan = plan : null, runtimeRecords, runtimeRecords && CalleeTiering,
                                source.Registered.Frontend) ??
                                RefuseRuntimeCallRecords(source.Hash);
                            failedLinkedPlan = null;
                        }

                        _linkedPrograms.Add(linkedKey, linked);
                    }

                    entry = linked;
                    if (runtimeFallback is null && linked.RuntimeCallRecords && _calleeTiers.TryGetValue(programKey, out var linkedTiers))
                    {
                        linkedTiers.LinkedGeneration = linked.Generation;
                    }

                    callArguments = library.Arguments;
                    if (linked.RuntimeCallRecords)
                    {
                        (callTargets, callRecords, var unservedCopy) = CurrentTargets(linked, library);
                        if (unservedCopy && runtimeFallback is null && _calleeTiers.TryGetValue(programKey, out var unservedTiers))
                        {
                            unservedTiers.NoteUnservedCopy(_host.CallMarkSnapshots);
                        }
                    }
                    else
                    {
                        callTargets = Gen5ShaderCallLinker.CallTargets(library.Calls);
                    }

                    if (!linked.RuntimeCallRecords && entry.Plan.Graph.CallArgumentRegisters != 0 &&
                        entry.Plan.Graph.CallArgumentRegisters != library.Arguments.Length * 2)
                        throw new InvalidOperationException("The linked plan reads a different number of call arguments than the tables hold.");
                    var linkedInputs = entry.Plan.Graph.CallArgumentRegisters != 0
                        ? inputs.WithCallArguments(source.UserDataBase, library.Arguments) : inputs;
                    // A linked plan holds every record's resources; the cache revalidates their reads instead of walking them again.
                    var linkedMaterialized = (LockFree ? Materializations : _materializations) is { } linkedMaterializations && captureIndirectImageFailure is null // [local] SHARPEMU_PROGRAM_GATE=0: the shared cache
                        ? linkedMaterializations.Materialize(entry.Plan, linkedInputs, _readResidentGuestBytes, ref snapshot, ref specialization, out _)
                        : ResourceMaterializer.Materialize(entry.Plan, linkedInputs, ref snapshot, ref specialization);
                    if (!linkedMaterialized)
                        throw new InvalidOperationException("The linked shader resources could not be materialized.");
                    if (linked.RuntimeCallRecords)
                        (callRecords, callRecordRanges) = CallRecordRanges(entry.Plan, callRecords!, callTargets, linkedInputs, source.UserDataBase);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ResourcePlanException)
                {
                    if (failedLinkedPlan is not null) ShaderImageDescriptorDump.Write(source, failedLinkedPlan, inputs);
                    if (CompiledShaderDump.ShouldWrite(source.Address, source.Hash))
                        ShaderCallDump.Write(CompiledShaderDump.GetBasePath(source.Label, source.Address, source.Hash),
                            entry.Program, snapshot, entry.Plan.Memory, _host.TryReadCleanGuestWord, exception as ShaderFunctionReadException);
                    var readState = exception is ShaderFunctionReadException failedRead ? _host.DescribeCleanRead(failedRead.Address) : "";
                    throw new ShaderProgramRejectedException($"The shader call library cannot be linked: hash=0x{source.Hash:X16} error={exception.Message}" +
                        (readState.Length != 0 ? $" read_state: {readState}" : ""));
                }
            }
        }

        _imageDescriptorTrace.Record(source, entry.Plan, snapshot);
        // [local] An indirect dispatch by threads needs the layout that keeps its limits in ShaderData.
        var needsShaderDataLimits = IndirectLimitsInShaderData && source.Stage == ShaderStage.Compute && options.ComputeInfo is { } computeInfo &&
            IsIndirectThreadDispatch(computeInfo);
        var permutation = Volatile.Read(ref entry.LastPermutation) is { } last && ReferenceEquals(last.Specialization, specialization) &&
            (!needsShaderDataLimits || last.Bindings.LimitsInShaderData) &&
            last.Bindings.PushDataStartDword == PushData.StartFor(pushDataCursor, last.Bindings.ShaderDataDwordCount)
            ? last
            : FindPermutation(entry, specialization, pushDataCursor, needsShaderDataLimits);
        if (permutation is null)
        {
            // A ready program serves while this one is prepared; it is never translated again for another specialization.
            if (runtimeFallback is not null)
                throw new ShaderProgramPendingException();
            try
            {
                permutation = MakePermutation(source, options, entry, specialization, pushDataCursor, key, sourceWasCached, snapshot,
                    deferTranslation: hasShaderCalls && deferLinking, needsShaderDataLimits);
            }
            catch (ShaderProgramPendingException) when (entry.RuntimeCallRecords)
            {
                var ready = ReadyFallback(key!, entry);
                if (ready is null)
                    throw;
                return GetOrCompileCore(source, options, ref pushDataCursor, out stage, deferLinking, ready);
            }
        }
        else
        {
            // [local] SHARPEMU_PROGRAM_GATE_VERIFY: the plain program's hit against the gated, shared-cache lookup (a linked
            // program's materialization has other inputs, so it is not checked).
            if (_verify && !hasShaderCalls)
            {
                VerifyHit(source, options, scratch, inputs, entry, permutation, snapshot, specialization, pushDataCursor, needsShaderDataLimits);
            }

            if (_walkCheck && !hasShaderCalls)
            {
                WalkCheckHit(source, inputs, entry, snapshot, specialization);
            }

            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"ProgramCache hit stage={source.Label} hash=0x{source.Hash:X16} id=0x{permutation.Handle.Id:X16}");
            }
        }

        if (ReferenceEquals(permutation.Specialization, specialization))
        {
            Volatile.Write(ref entry.LastPermutation, permutation);
        }

        Diagnostics.FrontCostProbe.Lap(Diagnostics.FrontCostProbe.Step.StagePermutation); // [local] SHARPEMU_FRONTCOST_PROBE
        stage = CreateStageResources(permutation.Program, snapshot, source, options, callArguments, callTargets, callRecords, callRecordRanges);
        Diagnostics.DrawCostProbe.Lap(Diagnostics.DrawCostProbe.Step.ProgramStage); // [local] SHARPEMU_DRAWCOST_PROBE
        Diagnostics.FrontCostProbe.Lap(Diagnostics.FrontCostProbe.Step.StageResources); // [local] SHARPEMU_FRONTCOST_PROBE
        if (entry.RuntimeCallRecords)
        {
            lock (_callGate)
            {
                if (!RuntimePipelineReady(key!, entry, permutation.Handle, stage, options, runtimeFallback))
                {
                    if (runtimeFallback is not null)
                        throw new ShaderProgramPendingException();
                    if (ReadyFallback(key!, entry) is { } ready)
                        return GetOrCompileCore(source, options, ref pushDataCursor, out stage, deferLinking, ready);
                }
            }
        }

        permutation.Bindings.AdvancePushData(ref pushDataCursor);
        return permutation.Handle;
    }

    // The permutation another thread added meanwhile, else one compiled under _gate; a deferred translation of a compute
    // program with shader calls leaves the program pending until its worker is done.
    private ProgramPermutation MakePermutation(ShaderSource source, StageCompileOptions options, ProgramSourceEntry entry,
        ResourceSpecialization specialization, uint pushDataCursor, ProgramKey? key, bool sourceWasCached, ResourceSnapshot snapshot, bool deferTranslation,
        bool needsShaderDataLimits)
    {
        if (LockFree) SharpEmu.Libs.VideoOut.FrontProfile.Switch(SharpEmu.Libs.VideoOut.FrontProfile.Phase.ProgramGate); // [local]
        lock (_gate)
        {
            if (LockFree) SharpEmu.Libs.VideoOut.FrontProfile.Switch(SharpEmu.Libs.VideoOut.FrontProfile.Phase.Permutation); // [local]
            if (FindPermutation(entry, specialization, pushDataCursor, needsShaderDataLimits) is { } found)
            {
                return found;
            }

            var programKey = key ?? KeyFor(source, options);
            ProgramPermutation permutation;
            if (deferTranslation && source.Stage == ShaderStage.Compute && !CompiledShaderDump.ShouldWrite(source.Address, source.Hash))
            {
                permutation = TakeDeferredPermutation(source, options, entry, specialization, pushDataCursor, programKey, sourceWasCached, snapshot);
            }
            else
            {
                using var coldScope = SharpEmu.Libs.VideoOut.RenderPhaseProfile.MeasureDetail(SharpEmu.Libs.VideoOut.RenderPhaseProfile.Phase.ColdPermutationCompile); // [local] SHARPEMU_COLDRES_PROBE
                SharpEmu.Libs.Diagnostics.FrameMissProbe.CountOnRender(SharpEmu.Libs.Diagnostics.FrameMissProbe.Counter.PermutationCompiles);
                permutation = CompilePermutation(source, options, entry, specialization, pushDataCursor, programKey, sourceWasCached, snapshot);
            }

            entry.AddPermutation(permutation);
            ShaderCacheCounters.CountPermutation();
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"ProgramCache insert stage={source.Label} hash=0x{source.Hash:X16} id=0x{permutation.Handle.Id:X16} permutation={entry.Permutations.Count - 1}");
            }

            return permutation;
        }
    }

    private ProgramKey KeyFor(ShaderSource source, StageCompileOptions options)
    {
        BuildStaticState(source.Stage, options);
        return BuildProgramKey(source);
    }

    private static ProgramPermutation? FindPermutation(ProgramSourceEntry entry, ResourceSpecialization specialization, uint pushDataCursor,
        bool needsShaderDataLimits)
    {
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPermutationLookup))
        using (SharpEmu.Libs.VideoOut.FrontProfile.Measure(SharpEmu.Libs.VideoOut.FrontProfile.Phase.Permutation))
        {
            foreach (var candidate in entry.Permutations)
            {
                if (Matches(candidate, specialization, pushDataCursor, needsShaderDataLimits))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    // Whether the draw can use a permutation: its specialization and push-data start. A tessellation layout and a layout
    // forced to ShaderData ([local] its limits there) have no push start.
    private static bool Matches(ProgramPermutation candidate, ResourceSpecialization specialization, uint pushDataCursor, bool needsShaderDataLimits)
    {
        var layout = candidate.Bindings;
        return (!needsShaderDataLimits || layout.LimitsInShaderData) &&
            layout.PushDataStartDword == (layout.UsesTessellationData || layout.LimitsInShaderData
                ? PushData.NoStart
                : PushData.StartFor(pushDataCursor, layout.ShaderDataDwordCount)) &&
            candidate.Specialization.Equals(specialization);
    }

    private ProgramSourceEntry? ReadyFallback(ProgramKey key, ProgramSourceEntry entry)
    {
        lock (_callGate)
        {
            return _runtimeFallbacks.TryGetValue(key, out var ready) && !ReferenceEquals(ready, entry) ? ready : null;
        }
    }

    // [local] One inputs object per thread, refilled per lookup; an image-candidate trace names the shader, so it gets its own.
    private ResourceRuntimeInputs InputsFor(LookupScratch scratch, ShaderSource source, StageCompileOptions options)
    {
        var computeState = source.Stage == ShaderStage.Compute && options.ComputeInfo is { } compute
            ? (ComputeSelectorState?)new ComputeSelectorState(compute.WaveSize, Math.Max(compute.ThreadsX, 1),
                Math.Max(compute.ThreadsY, 1), Math.Max(compute.ThreadsZ, 1), compute.DispatchThreadDimensions,
                compute.LocalDataShareDwords, compute.ThreadIdCount)
            : null;
        var inputs = scratch.Inputs;
        if (SharpEmu.Libs.Gpu.Images.ImageClearTrace.Enabled)
        {
            return new ResourceRuntimeInputs
            {
                UserData = source.UserData,
                ShaderBase = source.Address,
                ReadMemory = inputs.ReadMemory,
                ReadCleanMemory = inputs.ReadCleanMemory,
                TraceImageCandidate = (descriptorSource, candidateKey, heap, address, words) =>
                    _imageDescriptorTrace.RecordCandidate(source.Hash, descriptorSource, candidateKey, heap, address, words.Span),
                ReadResidentMemory = inputs.ReadResidentMemory,
                ComputeState = computeState,
                SynchronizeRanges = inputs.SynchronizeRanges,
                SynchronizeTableRange = inputs.SynchronizeTableRange,
                CompareTableChunk = inputs.CompareTableChunk,
            };
        }

        inputs.UserData = source.UserData;
        inputs.ShaderBase = source.Address;
        inputs.ComputeState = computeState;
        return inputs;
    }

    // [local] SHARPEMU_DRAWCOST_PROBE: the materialization's outcome and its fast memo's action, among the item's counts.
    private static void CountMaterialization(MaterializationTally.Outcome outcome)
    {
        switch (outcome)
        {
            case MaterializationTally.Outcome.FastHit or MaterializationTally.Outcome.FastMiss or MaterializationTally.Outcome.FastFallback or
                MaterializationTally.Outcome.FastMemoHit:
                Diagnostics.DrawCostProbe.Count(Diagnostics.DrawCostProbe.Counter.MaterializeFast);
                switch (ResourceMaterializer.LastFastMemoAction)
                {
                    case ResourceMaterializer.FastMemoAction.Hit:
                        Diagnostics.DrawCostProbe.Count(Diagnostics.DrawCostProbe.Counter.FastMemoHit);
                        break;
                    case ResourceMaterializer.FastMemoAction.Recorded:
                        Diagnostics.DrawCostProbe.Count(Diagnostics.DrawCostProbe.Counter.FastMemoRecorded);
                        break;
                    case ResourceMaterializer.FastMemoAction.Skipped:
                        Diagnostics.DrawCostProbe.Count(Diagnostics.DrawCostProbe.Counter.FastMemoSkipped);
                        break;
                }

                break;
            case MaterializationTally.Outcome.CacheHit or MaterializationTally.Outcome.CacheRebaseHit or MaterializationTally.Outcome.TableRefresh:
                Diagnostics.DrawCostProbe.Count(Diagnostics.DrawCostProbe.Counter.MaterializeCacheHit);
                break;
            default:
                Diagnostics.DrawCostProbe.Count(Diagnostics.DrawCostProbe.Counter.MaterializeFull);
                break;
        }
    }

    private bool MaterializeWith(ResourceMaterializationCache? materializations, ProgramSourceEntry entry, ResourceRuntimeInputs inputs,
        ref ResourceSnapshot snapshot, ref ResourceSpecialization specialization, out ResourceMaterializationFailure failure,
        Action<IndirectImageFailure>? captureIndirectImageFailure, out MaterializationTally.Outcome outcome)
    {
        if (captureIndirectImageFailure is null && ResourceMaterializer.CanMaterializeFast(entry.Plan))
        {
            var fast = ResourceMaterializer.MaterializeFast(entry.Plan, inputs, ref snapshot, ref specialization, out failure);
            outcome = ResourceMaterializer.LastFastOutcome;
            return fast;
        }

        if (materializations is not null && captureIndirectImageFailure is null)
        {
            var cached = materializations.Materialize(entry.Plan, inputs, _readResidentGuestBytes, ref snapshot, ref specialization, out failure,
                _compareResidentGuestBytes);
            outcome = MaterializationTally.FromCache(materializations.LastOutcome);
            return cached;
        }

        outcome = MaterializationTally.Outcome.Full;
        return ResourceMaterializer.Materialize(entry.Plan, inputs, ref snapshot, ref specialization, out failure, captureIndirectImageFailure);
    }

    // [local] SHARPEMU_PROGRAM_GATE_VERIFY: the hit found without the gate, looked up again under it as before the
    // change (the shared cache, the entry list) without compiling anything.
    private void VerifyHit(ShaderSource source, StageCompileOptions options, LookupScratch scratch, ResourceRuntimeInputs inputs,
        ProgramSourceEntry entry, ProgramPermutation found, ResourceSnapshot snapshot, ResourceSpecialization specialization,
        uint pushDataCursor, bool needsShaderDataLimits)
    {
        string? difference;
        lock (_gate)
        {
            try
            {
                difference = VerifyUnderGate(source, options, scratch, inputs, entry, found, snapshot, specialization, pushDataCursor,
                    needsShaderDataLimits);
            }
            catch (Exception exception)
            {
                ProgramGateVerify.NoteError(source, exception);
                return;
            }
        }

        ProgramGateVerify.Note(source, difference);
    }

    private void WalkCheckHit(ShaderSource source, ResourceRuntimeInputs inputs, ProgramSourceEntry entry, ResourceSnapshot snapshot,
        ResourceSpecialization specialization)
    {
        string? difference;
        lock (_gate)
        {
            try
            {
                var walkSnapshot = EmptySnapshot;
                var walkSpecialization = EmptySpecialization;
                if (!ResourceMaterializer.Materialize(entry.Plan, inputs, ref walkSnapshot, ref walkSpecialization, out var failure, null))
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

    private string? VerifyUnderGate(ShaderSource source, StageCompileOptions options, LookupScratch scratch, ResourceRuntimeInputs inputs,
        ProgramSourceEntry entry, ProgramPermutation found, ResourceSnapshot snapshot, ResourceSpecialization specialization,
        uint pushDataCursor, bool needsShaderDataLimits)
    {
        var staticState = scratch.VerifyState;
        BuildStaticState(source.Stage, options, staticState);
        var probe = new ProgramKeyProbe(source.Stage, source.Hash, (uint)source.UserData.Length, source.CodeSize,
            source.ContinuationAddressOffset, CollectionsMarshal.AsSpan(staticState));
        if (!_programs.GetAlternateLookup<ProgramKeyProbe>().TryGetValue(probe, out var reference))
            return "entry_missing";
        if (!ReferenceEquals(reference, entry))
            return "entry";
        var referenceSnapshot = EmptySnapshot;
        var referenceSpecialization = EmptySpecialization;
        var materialized = _materializations is not null
            ? _materializations.Materialize(entry.Plan, inputs, _readResidentGuestBytes, ref referenceSnapshot, ref referenceSpecialization, out var failure)
            : ResourceMaterializer.Materialize(entry.Plan, inputs, ref referenceSnapshot, ref referenceSpecialization, out failure, null);
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
                var walked = ResourceMaterializer.Materialize(entry.Plan, inputs, ref walkSnapshot, ref walkSpecialization, out _, null);
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
            if (Matches(candidate, referenceSpecialization, pushDataCursor, needsShaderDataLimits))
            {
                referenceFound = candidate;
                break;
            }
        }

        if (referenceFound is null)
            return "permutation_missing";
        // The entry's last permutation can serve a hit that a later one in the list matches too.
        if (!ReferenceEquals(referenceFound, found) && !Matches(found, referenceSpecialization, pushDataCursor, needsShaderDataLimits))
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

    // The key of the entry the static state was just built for.
    private ProgramKey BuildProgramKey(ShaderSource source) =>
        new(source.Stage, source.Hash, (uint)source.UserData.Length, source.CodeSize,
            source.ContinuationAddressOffset, StaticState.ToArray());

    // With runtimeRecords, a program whose callees cannot read their records at run time is refused (null), and its caller
    // links a copy per record from then on (RefuseRuntimeCallRecords).
    private ProgramSourceEntry? LinkProgram(Gen5ShaderProgram caller, ShaderCallLibrary library, ShaderStage stage, ulong hash,
        uint userDataBase, uint userDataCount, uint waveSize, Action<ShaderResourcePlan>? beforeResourceTracking, bool runtimeRecords = false,
        bool callMarks = false, IShaderFrontend? frontend = null)
    {
        Gen5ShaderProgram program;
        ShaderResourcePlan plan;
        try
        {
            program = Gen5ShaderCallLinker.Link(caller, library.Calls, runtimeRecords);
            plan = ShaderResourcePlan.Extract(program, stage, hash, userDataBase, userDataCount,
                beforeResourceTracking: beforeResourceTracking, waveSize: waveSize, callRecords: runtimeRecords, callMarks: callMarks,
                normalizeBufferDescriptor: frontend?.NormalizeBufferDescriptor,
                normalizeImageDescriptor: ImageNormalizer(frontend),
                imageNormalizationVersion: frontend?.ImageNormalizationVersion);
        }
        catch (Exception exception) when (runtimeRecords && exception is InvalidOperationException or ResourcePlanException)
        {
            Console.Error.WriteLine($"[GPU][WARN][LINKED_PROGRAM] Run-time call records refused, the caller links a copy per record: " +
                $"hash=0x{hash:X16} error={exception.Message}");
            return null;
        }

        ulong[] callTargets = runtimeRecords ? Gen5ShaderCallLinker.CallTargets(program) : [];
        return new ProgramSourceEntry { Program = program, Plan = plan, RuntimeCallRecords = runtimeRecords,
            CallTargets = callTargets,
            CallTargetCodes = [.. callTargets.Select(address => library.CodeHashes[address])],
            RuntimeCallees = runtimeRecords ? library.SiteCallees() : null,
            Generation = Interlocked.Increment(ref _linkGenerations),
            HasShaderCalls = program.Instructions.Any(instruction => instruction.Opcode == "SSwappcB64"),
            HasBitwiseExclusiveOr = program.Instructions.Any(instruction => instruction.Opcode.Contains("Xor", StringComparison.Ordinal)) };
    }

    // The program linked for linkedKey once its worker is done; until then the program is pending. A finished link of
    // the program's earlier layout is kept, as the game can return to that table, and a failed one is rejected as a
    // link on the command stream would be.
    // The buffers each record's callee computes a V# for from its argument; the host resolves the rest (PrepareCallRecords).
    // The memory each record's callee reads through its buffers, and the records left: a record whose buffer descriptor
    // describes no buffer is left out, so its call returns at once instead of reading through it.
    private static ((ulong Target, ulong Argument)[] Records, DeviceAddressRange[] Ranges) CallRecordRanges(ShaderResourcePlan plan,
        (ulong Target, ulong Argument)[] records, ulong[] callTargets, ResourceRuntimeInputs inputs, uint userDataBase)
    {
        if (!plan.CallRecordCallees.Any(static callee => callee.BufferWords.Length != 0))
        {
            return (records, []);
        }

        var slotOf = new Dictionary<ulong, int>(callTargets.Length);
        for (var slot = 0; slot < callTargets.Length; slot++)
        {
            slotOf.TryAdd(callTargets[slot], slot);
        }

        var ranges = new List<DeviceAddressRange>();
        var kept = new List<(ulong Target, ulong Argument)>(records.Length);
        foreach (var record in records)
        {
            var first = ranges.Count;
            if (slotOf.TryGetValue(record.Target, out var slot) &&
                !CallRecordPlanner.AddRecordRanges(plan, slot, record.Argument, inputs, userDataBase, ranges))
            {
                ranges.RemoveRange(first, ranges.Count - first);
                if (_invalidRecordReports++ < 8)
                {
                    Console.Error.WriteLine($"[GPU][WARN][LINKED_PROGRAM] A call record's buffer describes no buffer; its call returns at once: " +
                        $"hash=0x{plan.Hash:X16} target=0x{record.Target:X} argument=0x{record.Argument:X}.");
                }

                continue;
            }

            kept.Add(record);
        }

        return (kept.Count == records.Length ? records : [.. kept], [.. ranges]);
    }

    private static int _invalidRecordReports;

    // Whether a runtime-record program's pipeline is ready for this compute dispatch; its compile starts or is polled here, and a
    // ready program becomes the caller's fallback. Without a pipeline host every pipeline counts as ready.
    private bool RuntimePipelineReady(ProgramKey key, ProgramSourceEntry entry, ShaderProgram handle, ShaderStageResources stage,
        StageCompileOptions options, ProgramSourceEntry? runtimeFallback)
    {
        if (TryComputePipeline is not { } tryPipeline || options.ComputeInfo is not { } input)
        {
            return true;
        }

        if (runtimeFallback is not null)
        {
            return tryPipeline(handle, stage, input);
        }

        // Older programs whose compile finished meanwhile serve while this one compiles.
        _runtimeCompiles.TryGetValue(key, out var compiles);
        if (compiles is not null)
        {
            for (var index = compiles.Count - 1; index >= 0; index--)
            {
                var (older, olderHandle, olderStage) = compiles[index];
                if (ReferenceEquals(older, entry))
                {
                    continue;
                }

                if (FallbackGeneration(key) >= older.Generation)
                {
                    compiles.RemoveAt(index);
                }
                else if (tryPipeline(olderHandle, olderStage, input))
                {
                    PromoteRuntimeFallback(key, older);
                    compiles.RemoveAt(index);
                }
            }
        }

        if (tryPipeline(handle, stage, input))
        {
            PromoteRuntimeFallback(key, entry);
            compiles?.RemoveAll(compile => ReferenceEquals(compile.Entry, entry));
            return true;
        }

        if (compiles is null)
        {
            compiles = [];
            _runtimeCompiles.Add(key, compiles);
        }

        if (!compiles.Any(compile => ReferenceEquals(compile.Entry, entry) && compile.Handle.Id == handle.Id))
        {
            compiles.Add((entry, handle, stage));
            if (compiles.Count > 4)
            {
                compiles.RemoveAt(0);
            }
        }

        return false;
    }

    private long FallbackGeneration(ProgramKey key) => _runtimeFallbacks.TryGetValue(key, out var ready) ? ready.Generation : -1;

    // A newer program than the caller's fallback becomes it.
    private void PromoteRuntimeFallback(ProgramKey key, ProgramSourceEntry entry)
    {
        if (FallbackGeneration(key) >= entry.Generation)
        {
            return;
        }

        _runtimeFallbacks[key] = entry;
        if (entry.CallTargets.Length != 0 && entry.RuntimeCallees is { } callees)
        {
            (_callUnionStore ??= ShaderCallUnionStore.Open()).Save(key.Stage, key.Hash, callees);
        }
    }

    // The address each callee slot of a runtime-record program serves at this dispatch, and the records it serves. A slot serves
    // the address it was linked with while that address holds the code linked for it, else an address the tables name with
    // that code at one of the slot's sites (the game loaded the function elsewhere, as it does in another session), else none
    // (ulong.MaxValue matches no call). A record no slot serves returns at once and never runs the code that used to be at
    // its address. UnservedCopy is whether the tables name code the program holds at an address no slot serves.
    private static (ulong[] Targets, (ulong Target, ulong Argument)[] Records, bool UnservedCopy) CurrentTargets(ProgramSourceEntry program,
        ShaderCallLibrary library)
    {
        const ulong NoTarget = ulong.MaxValue;
        var targets = new ulong[program.CallTargets.Length];
        var served = new HashSet<ulong>();
        var unserved = false;
        for (var slot = 0; slot < targets.Length; slot++)
        {
            var address = program.CallTargets[slot];
            var same = slot < program.CallTargetCodes.Length && library.CurrentCodeHashes.TryGetValue(address, out var code) &&
                code.AsSpan().SequenceEqual(program.CallTargetCodes[slot]);
            targets[slot] = same && served.Add(address) ? address : NoTarget;
            unserved |= targets[slot] == NoTarget;
        }

        if (unserved)
        {
            var slotSites = new Dictionary<ulong, HashSet<uint>>();
            foreach (var (pc, callees) in program.RuntimeCallees ?? [])
            {
                foreach (var callee in callees)
                {
                    if (!slotSites.TryGetValue(callee.Target.Address, out var sites))
                    {
                        slotSites.Add(callee.Target.Address, sites = []);
                    }

                    sites.Add(pc);
                }
            }

            var byCode = new Dictionary<string, List<ulong>>();
            foreach (var (address, code) in library.CurrentCodeHashes)
            {
                if (!served.Contains(address))
                {
                    var key = Convert.ToHexString(code);
                    if (!byCode.TryGetValue(key, out var addresses))
                    {
                        byCode.Add(key, addresses = []);
                    }

                    addresses.Add(address);
                }
            }

            for (var slot = 0; slot < targets.Length; slot++)
            {
                if (targets[slot] != NoTarget || slot >= program.CallTargetCodes.Length ||
                    !byCode.TryGetValue(Convert.ToHexString(program.CallTargetCodes[slot]), out var candidates))
                {
                    continue;
                }

                var sites = slotSites.GetValueOrDefault(program.CallTargets[slot]) ?? [];
                var best = NoTarget;
                var bestSites = 0;
                foreach (var candidate in candidates)
                {
                    var shared = served.Contains(candidate) || !library.CurrentSites.TryGetValue(candidate, out var namedAt)
                        ? 0 : namedAt.Count(sites.Contains);
                    if (shared > bestSites)
                    {
                        (best, bestSites) = (candidate, shared);
                    }
                }

                if (best != NoTarget)
                {
                    targets[slot] = best;
                    served.Add(best);
                }
            }
        }

        var codes = program.CallTargetCodes.Select(Convert.ToHexString).ToHashSet();
        var unservedCopy = library.CurrentCodeHashes.Any(named =>
            !served.Contains(named.Key) && codes.Contains(Convert.ToHexString(named.Value)));
        return (targets, [.. library.Records.Where(record => served.Contains(record.Target))], unservedCopy);
    }

    // A caller whose callees cannot read their records at run time skips this dispatch and links a copy per record next.
    private ProgramSourceEntry RefuseRuntimeCallRecords(ulong hash)
    {
        _perRecordCallers.Add(hash);
        throw new ShaderProgramPendingException();
    }

    private ProgramSourceEntry TakeDeferredLink((ProgramKey Key, string Library) linkedKey, Gen5ShaderProgram caller,
        ShaderCallLibrary library, ShaderSource source, uint waveSize, bool runtimeRecords)
    {
        if (_pendingLinks.TryGetValue(source.Hash, out var pending))
        {
            if (!pending.Link.IsCompleted)
            {
                throw new ShaderProgramPendingException();
            }

            _pendingLinks.Remove(source.Hash);
            var linked = pending.Link.GetAwaiter().GetResult() ?? RefuseRuntimeCallRecords(source.Hash);
            if (pending.LinkedKey.Equals(linkedKey))
            {
                return linked;
            }

            _linkedPrograms.TryAdd(pending.LinkedKey, linked);
        }

        var (stage, hash, userDataBase, userDataCount) = (source.Stage, source.Hash, source.UserDataBase, (uint)source.UserData.Length);
        var callMarks = runtimeRecords && CalleeTiering;
        var frontend = source.Registered.Frontend;
        _pendingLinks.Add(source.Hash, (linkedKey, Task.Run(() =>
            LinkProgram(caller, library, stage, hash, userDataBase, userDataCount, waveSize, null, runtimeRecords, callMarks, frontend))));
        throw new ShaderProgramPendingException();
    }

    private static ShaderStageResources CreateStageResources(
        ShaderProgramInfo program, ResourceSnapshot snapshot, ShaderSource source, StageCompileOptions options, ulong[] callArguments,
        ulong[] callTargets, (ulong Target, ulong Argument)[]? callRecords, DeviceAddressRange[]? callRecordRanges)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramResourceAssembly);
        using var frontScope = SharpEmu.Libs.VideoOut.FrontProfile.Measure(SharpEmu.Libs.VideoOut.FrontProfile.Phase.StageResources);
        return new(program, snapshot, source.Address)
        {
            CallArguments = callArguments,
            CallTargets = callTargets,
            CallRecords = callRecords,
            CallRecordRanges = callRecordRanges,
            ThreadLimits = source.Stage == ShaderStage.Compute && options.ComputeInfo is { DispatchThreadDimensions: true } compute
                ? new DispatchThreadLimits(compute.DispatchThreadsX, compute.DispatchThreadsY, compute.DispatchThreadsZ)
                : null,
        };
    }

    private void BuildStaticState(ShaderStage stage, StageCompileOptions options) => BuildStaticState(stage, options, StaticState);

    // [local] Into any list: the verify builds a second copy beside the lookup's.
    private static void BuildStaticState(ShaderStage stage, StageCompileOptions options, List<uint> staticState)
    {
        switch (stage)
        {
            case ShaderStage.Vertex:
            case ShaderStage.TessellationEvaluation:
                StageStaticKey.Build(options.VertexInfo ?? throw new ArgumentException("The vertex lookup has no vertex input info."), options.RequiredVertexOutputCount, staticState);
                break;
            case ShaderStage.Pixel:
                StageStaticKey.Build(options.PixelInfo ?? throw new ArgumentException("The pixel lookup has no pixel input info."), staticState);
                foreach (var output in options.PixelOutputs)
                {
                    staticState.Add(output.GuestSlot);
                    staticState.Add(output.HostLocation);
                    staticState.Add(output.ExportTarget);
                    staticState.Add(output.Index);
                    staticState.Add(output.DualSourceAlpha ? 1u : 0u);
                }
                break;
            case ShaderStage.Mesh:
                StageStaticKey.Build(options.MeshInfo ?? throw new ArgumentException("The mesh lookup has no mesh input info."), options.RequiredVertexOutputCount, staticState);
                staticState.Add(options.MeshOutputLocationMask.HasValue ? 1u : 0u);
                staticState.Add(options.MeshOutputLocationMask.GetValueOrDefault());
                break;
            default:
                StageStaticKey.Build(options.ComputeInfo ?? throw new ArgumentException("The compute lookup has no compute input info."), staticState);
                break;
        }

        if (options.Tessellation is { } tessellation)
        {
            staticState.Add((uint)tessellation.Domain);
            staticState.Add((uint)tessellation.Spacing);
            staticState.Add(tessellation.PointMode ? 1u : 0);
            staticState.Add(tessellation.Clockwise ? 1u : 0);
        }

        if (options.TessellationHull is { } hull)
        {
            staticState.Add(hull.InputControlPoints);
            staticState.Add(hull.OutputControlPoints);
            staticState.Add(hull.PatchesPerGroup);
            staticState.Add(hull.HullEntryPc);
        }
    }

    private ProgramSourceEntry CreateEntry(ShaderSource source, StageCompileOptions options)
    {
        using var planningScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPlanning);
        using var coldScope = SharpEmu.Libs.VideoOut.RenderPhaseProfile.MeasureDetail(SharpEmu.Libs.VideoOut.RenderPhaseProfile.Phase.ColdEntryCreate); // [local] SHARPEMU_COLDRES_PROBE
        SharpEmu.Libs.Diagnostics.FrameMissProbe.CountOnRender(SharpEmu.Libs.Diagnostics.FrameMissProbe.Counter.EntryCreates);
        var program = Decode(source);
        if (options.TessellationHull is not null && source.Registered.Frontend is null)
        {
            program = Gen5TessellationLowering.PrepareMergedHull(program,
                checked(source.UserDataBase + (uint)source.UserData.Length - 2));
        }

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
                // Resource planning must use the guest wave size of shader compilation.
                waveSize: source.Stage switch
                {
                    ShaderStage.Mesh => options.MeshInfo!.Geometry.WaveSize,
                    ShaderStage.Compute => options.ComputeInfo?.WaveSize ?? 64u,
                    _ => 32u,
                }, dynamicUserData: options.VertexInfo?.StartInstanceUserData ?? -1,
                normalizeBufferDescriptor: source.Registered.Frontend?.NormalizeBufferDescriptor,
                normalizeImageDescriptor: ImageNormalizer(source.Registered.Frontend),
                imageNormalizationVersion: source.Registered.Frontend?.ImageNormalizationVersion);
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
            HasShaderCalls = program.Instructions.Any(instruction => instruction.Opcode == "SSwappcB64"),
            ConstantFill = source.Stage == ShaderStage.Compute ? ConstantFillDetector.Detect(program) : null,
            BoundedFill = source.Stage == ShaderStage.Compute ? BoundedFillDetector.Detect(program) : null,
            BoundedCopy = source.Stage == ShaderStage.Compute ? BoundedFillDetector.DetectCopy(program) : null,
            StoresUniformValues = source.Stage == ShaderStage.Compute && GuardedStoreDetector.StoresOnlyUniformValues(program),
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

    // The permutation a worker translated for this program, once it is done; until then the program is pending. A
    // translation for a layout or specialization the program has since left is dropped, and the current one starts.
    private ProgramPermutation TakeDeferredPermutation(ShaderSource source, StageCompileOptions options, ProgramSourceEntry entry,
        ResourceSpecialization specialization, uint pushDataCursor, ProgramKey key, bool sourceWasCached, ResourceSnapshot snapshot)
    {
        if (_pendingTranslations.TryGetValue(source.Hash, out var pending))
        {
            if (!pending.Translation.IsCompleted)
            {
                throw new ShaderProgramPendingException();
            }

            _pendingTranslations.Remove(source.Hash);
            if (ReferenceEquals(pending.Entry, entry) && pending.Specialization.Equals(specialization) &&
                pending.PushDataCursor == pushDataCursor)
            {
                DeferredTranslation translation;
                try
                {
                    translation = pending.Translation.GetAwaiter().GetResult();
                }
                catch (ResourcePlanException exception)
                {
                    throw SubmissionScheduler.Fatal($"The shader binding layout is invalid: stage={source.Label} hash=0x{source.Hash:X16} error={exception.Message}.");
                }

                return CompilePermutation(source, options, entry, specialization, pushDataCursor, key, sourceWasCached, snapshot, translation);
            }
        }

        var (plan, program, compiler) = (entry.Plan, entry.Program, _compiler);
        var (userDataBase, userDataCount) = (source.UserDataBase, (uint)source.UserData.Length);
        var current = options.ComputeInfo!;
        // The fields the request and the layout read, taken now: the dispatch may change its info once it moves on.
        var info = new ComputeInputInfo
        {
            ThreadsX = current.ThreadsX,
            ThreadsY = current.ThreadsY,
            ThreadsZ = current.ThreadsZ,
            DispatchThreadDimensions = current.DispatchThreadDimensions,
            WaveSize = current.WaveSize,
            LocalDataShareDwords = current.LocalDataShareDwords,
            IeeeMode = current.IeeeMode,
            ScratchDwords = current.ScratchDwords,
        };
        var systemRegisters = options.ComputeSystemRegisters;
        var (sharedInt64Atomics, execGuardElision, signedZeroInfNanPreserve, wave64Native, imageInt64Atomics) =
            (_host.SharedInt64AtomicsEnabled, _host.ExecGuardElisionEnabled, _host.ShaderSignedZeroInfNanPreserveFloat32Supported && SignedZeroInfNanAllowed,
                _host.ComputeWave64SubgroupNative, _host.ImageInt64AtomicsEnabled);
        var subgroup32 = _host.ComputeRunsOn32LaneSubgroups(current);
        var tableBlocks = _host.TableBlocks;
        var usesBufferLengths = source.Registered.Frontend?.RequiresExplicitBufferLengths ?? false;
        var tessellationHull = options.TessellationHull;
        var usesTessellationData = options.Tessellation is not null || tessellationHull is not null;
        var (bufferInt64Atomics, float16Conversions) = (_host.BufferInt64AtomicsEnabled, _host.Float16ConversionsEnabled); // [local]
        var indirectThreads = IsIndirectThreadDispatch(current); // [local] the copy below keeps no dispatch counts
        _pendingTranslations[source.Hash] = new(entry, specialization, pushDataCursor, Task.Run(() =>
        {
            var resources = ResourceMaterializer.ApplyTo(plan, specialization);
            var layout = AllocateLayout(program, plan, resources, userDataBase, userDataCount, pushDataCursor, info.DispatchThreadDimensions,
                usesBufferLengths: usesBufferLengths, usesTessellationData: usesTessellationData, tableBlocks: tableBlocks,
                indirectThreads: indirectThreads);
            var request = BuildComputeRequest(plan, resources, layout, info, systemRegisters, sharedInt64Atomics, execGuardElision,
                signedZeroInfNanPreserve, wave64Native, subgroup32, imageInt64Atomics, tessellationHull,
                bufferInt64Atomics: bufferInt64Atomics, float16Conversions: float16Conversions);
            var translated = compiler.TryCompileProgram(request, out var compiled, out var error);
            return new DeferredTranslation(resources, layout, request, translated, compiled, error);
        }));
        throw new ShaderProgramPendingException();
    }

    private ProgramPermutation CompilePermutation(
        ShaderSource source,
        StageCompileOptions options,
        ProgramSourceEntry entry,
        ResourceSpecialization specialization,
        uint pushDataCursor,
        ProgramKey key,
        bool sourceWasCached,
        ResourceSnapshot snapshot,
        DeferredTranslation? deferred = null)
    {
        var program = entry.Program;
        var plan = entry.Plan;
        SpecializedResourceInfo resources;
        BindingLayout layout;
        if (deferred is not null)
        {
            (resources, layout) = (deferred.Resources, deferred.Layout);
        }
        else
        {
            try
            {
                using var layoutScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramLayoutRequest);
                resources = ResourceMaterializer.ApplyTo(plan, specialization);
                layout = AllocateLayout(program, plan, resources, source.UserDataBase, (uint)source.UserData.Length, pushDataCursor,
                    source.Stage == ShaderStage.Compute && options.ComputeInfo!.DispatchThreadDimensions,
                    usesMeshDrawParameters: source.Stage == ShaderStage.Mesh, tableBlocks: _host.TableBlocks && source.Stage != ShaderStage.Mesh,
                    usesBufferLengths: source.Registered.Frontend?.RequiresExplicitBufferLengths ?? false,
                    usesTessellationData: options.Tessellation is not null || options.TessellationHull is not null,
                    indirectThreads: source.Stage == ShaderStage.Compute && IsIndirectThreadDispatch(options.ComputeInfo!));
            }
            catch (ResourcePlanException exception)
            {
                throw SubmissionScheduler.Fatal($"The shader binding layout is invalid: stage={source.Label} hash=0x{source.Hash:X16} error={exception.Message}.");
            }
        }

        var compileStarted = CompileLog.Timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local] SHARPEMU_COMPILE_LOG
        ShaderCompileRequest request;
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramLayoutRequest))
        {
            request = deferred?.Request ?? BuildRequest(source, options, entry, resources, layout);
        }
        if (request.Mesh is { } mesh)
        {
            var limits = _host.MeshLimits;
            var parameterLocations = MeshShaderConfiguration.ParameterLocations(request);
            var parameterCount = parameterLocations.Length;
            var outputLocations = Math.Max(parameterCount + 3, parameterCount == 0 ? 0 : (int)parameterLocations[^1] + 1);
            if ((ulong)outputLocations * 4 > limits.MaxOutputComponents)
                throw new ShaderProgramRejectedException($"The mesh output exceeds the device component limit: " +
                    $"components={outputLocations * 4} limit={limits.MaxOutputComponents}.");
            var outputBytes = mesh.OutputMemoryBytes(parameterCount,
                limits.OutputPerVertexGranularity, limits.OutputPerPrimitiveGranularity);
            if (outputBytes > limits.MaxOutputMemoryBytes || outputBytes > limits.MaxPayloadAndOutputMemoryBytes)
                throw new ShaderProgramRejectedException($"The mesh output exceeds device memory limits: bytes={outputBytes} " +
                    $"output_limit={limits.MaxOutputMemoryBytes} payload_output_limit={limits.MaxPayloadAndOutputMemoryBytes}.");
        }
        var permutationDump = ShaderPermutationDump.WriteInputs(
            source, key, sourceWasCached, _programs.Keys, entry.Permutations,
            specialization, request, pushDataCursor, _nextProgramId + 1);
        IGuestCompiledShader? compiled;
        string error;
        bool translated;
        if (deferred is not null)
        {
            (translated, compiled, error) = (deferred.Translated, deferred.Compiled, deferred.Error);
        }
        else
        {
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramTranslation))
            {
                translated = _compiler.TryCompileProgram(request, out compiled, out error);
            }
        }

        if (!translated || compiled is null)
        {
            ShaderCallDump.Write(permutationDump, program, snapshot, plan.Memory, _host.TryReadCleanGuestWord);
            throw new ShaderProgramRejectedException($"The shader program cannot be compiled: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={error}.");
        }

        ShaderCacheCounters.CountCompile();
        if (RenderPhaseProfile.Enabled && request.Mesh is not null && compiled.PayloadFileExtension == "spv")
        {
            var payload = compiled.Payload;
            var barriers = 0;
            var loops = 0;
            var elections = 0;
            var atomicOrs = 0;
            var loads = 0;
            var stores = 0;
            var instructions = 0;
            var functionInstructions = 0;
            var beforeFirstLoop = 0;
            var insideFunction = false;
            for (var offset = 20; offset < payload.Length;)
            {
                var instruction = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
                var wordCount = (int)(instruction >> 16);
                if (wordCount == 0 || wordCount > (payload.Length - offset) / 4) break;
                instructions++;
                switch ((SharpEmu.ShaderCompiler.Vulkan.SpirvOp)(instruction & 0xffff))
                {
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.Function: insideFunction = true; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.FunctionEnd: insideFunction = false; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.ControlBarrier: barriers++; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.LoopMerge:
                        if (loops == 0) beforeFirstLoop = functionInstructions;
                        loops++;
                        break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.GroupNonUniformElect: elections++; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.AtomicOr: atomicOrs++; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.Load: loads++; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.Store: stores++; break;
                }
                if (insideFunction) functionInstructions++;
                offset += wordCount * 4;
            }
            Console.Error.WriteLine($"[PERF][MESH_COMPILE] hash=0x{source.Hash:X16} bytes={payload.Length} " +
                $"static_barriers={barriers} static_loops={loops} static_elections={elections} static_atomic_or={atomicOrs} " +
                $"static_instructions={instructions} static_loads={loads} static_stores={stores} " +
                $"static_before_first_loop={beforeFirstLoop} " +
                $"bound_format_specialization=enabled buffer_formats={string.Join(',', resources.Info.Buffers.Select(buffer => buffer.DescriptorFormat))}");
        }
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
        if (CompileLog.Timed) // [local]
        {
            var indirect = IndirectSignature(specialization);
            CompileLog.Note("translate", System.Diagnostics.Stopwatch.GetElapsedTime(compileStarted).TotalMilliseconds, source.Hash,
                $"stage={source.Stage} spirv_bytes={compiled.Payload.Length} images={specialization.Images.Count} indirect={indirect}", always: indirect != "-");
        }
        var info = CreateProgramInfo(source, entry, resources, layout, request, compiled.InputLocationMask);
        if (source.Stage == ShaderStage.Compute && options.TessellationHull is null &&
            _host.ShaderPrewarm is { } prewarm &&
            _codeCaptures.TryGetValue((source.Hash, source.CodeSize, source.ContinuationAddressOffset), out var capture))
        {
            prewarm.RecordCompute(capture, new ComputePrewarmRecord
            {
                Hash = source.Hash,
                CodeSize = source.CodeSize,
                Address = capture.Address,
                ContinuationAddressOffset = source.ContinuationAddressOffset,
                UserDataBase = source.UserDataBase,
                UserDataCount = (uint)source.UserData.Length,
                PushDataCursor = pushDataCursor,
                Info = options.ComputeInfo!,
                SystemRegisters = options.ComputeSystemRegisters,
                Specialization = specialization.Clone(),
            });
        }

        if (source.Stage is ShaderStage.Vertex or ShaderStage.Pixel && options.Tessellation is null && !entry.HasShaderCalls &&
            _host.ShaderPrewarm is { } stagePrewarm &&
            _programs.TryGetValue(key, out var canonical) && ReferenceEquals(canonical, entry) &&
            _codeCaptures.TryGetValue((source.Hash, source.CodeSize, source.ContinuationAddressOffset), out var stageCapture))
        {
            stagePrewarm.RecordStage(stageCapture, module, new StagePrewarmRecord
            {
                Stage = source.Stage,
                CodeKey = stageCapture.Key,
                Registered = source.Registered with
                {
                    CodeAddress = stageCapture.Address,
                    ContinuationAddress = source.Registered.IsFused ? stageCapture.Address + source.ContinuationAddressOffset : 0,
                },
                Hash = source.Hash,
                UserDataBase = source.UserDataBase,
                UserDataCount = (uint)source.UserData.Length,
                PushDataCursor = pushDataCursor,
                Options = options.Clone(), // [local] the pipeline cache refills its options for the next lookup
                Specialization = specialization,
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
        var imageInt64Atomics = _host.ImageInt64AtomicsEnabled;
        var signedZeroInfNanPreserve = _host.ShaderSignedZeroInfNanPreserveFloat32Supported && SignedZeroInfNanAllowed; // [local] A/B switch
        switch (source.Stage)
        {
            case ShaderStage.Vertex:
            case ShaderStage.TessellationEvaluation:
            {
                var info = options.VertexInfo!;
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = 32,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    IeeeMode = info.IeeeMode,
                    ScratchDwords = info.ScratchDwords,
                    EnableGraphicsSubgroupOperations = source.Stage != ShaderStage.TessellationEvaluation && enableGraphicsSubgroups,
                    SupportsSharedInt64Atomics = sharedInt64Atomics,
                    SupportsBufferInt64Atomics = bufferInt64Atomics,
                    SupportsImageInt64Atomics = imageInt64Atomics,
                    ShaderSignedZeroInfNanPreserveFloat32Supported = signedZeroInfNanPreserve,
                    RequiredVertexOutputCount = options.RequiredVertexOutputCount,
                    Tessellation = options.Tessellation,
                    VertexInputs = entry.VertexInputs,
                    PositionExportControl = info.PositionExportControl,
                    SupportsClipDistance = _host.ClipDistanceEnabled,
                    SupportsDrawParameters = _host.ShaderDrawParametersSupported,
                    StartInstanceUserData = info.StartInstanceUserData,
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
                    IeeeMode = info.IeeeMode,
                    ScratchDwords = info.ScratchDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    SupportsSharedInt64Atomics = sharedInt64Atomics,
                    SupportsBufferInt64Atomics = bufferInt64Atomics,
                    SupportsImageInt64Atomics = imageInt64Atomics,
                    ShaderSignedZeroInfNanPreserveFloat32Supported = signedZeroInfNanPreserve,
                    PixelOutputs = options.PixelOutputs,
                    PixelDepthExportEnable = info.DepthExportEnable,
                    PixelInputEnable = options.PixelInputEnable,
                    PixelCustomInterpolationMask = info.CustomInterpolationMask,
                    SupportsPerVertexPixelInputs = _host.PerVertexPixelInputsSupported,
                    PixelInputAddress = options.PixelInputAddress,
                    PixelInputCntl = interpolators,
                    MipStatisticsSubgroupReduction = _host.MipStatisticsSubgroupReduction,
                };
            }

            case ShaderStage.Mesh:
            {
                var info = options.MeshInfo!;
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = info.Geometry.WaveSize,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    IeeeMode = info.Geometry.IeeeMode,
                    ScratchDwords = info.Geometry.ScratchDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    SupportsImageInt64Atomics = imageInt64Atomics,
                    RequiredVertexOutputCount = options.RequiredVertexOutputCount,
                    PositionExportControl = info.Geometry.PositionExportControl,
                    MeshOutputLocationMask = options.MeshOutputLocationMask,
                    ClipSpace = new ShaderClipSpaceTransform(
                        info.Geometry.ClipSpace.Enabled,
                        info.Geometry.ClipSpace.ScaleX,
                        info.Geometry.ClipSpace.ScaleY,
                        info.Geometry.ClipSpace.OffsetX,
                        info.Geometry.ClipSpace.OffsetY,
                        info.Geometry.ClipSpace.HalfExtentX,
                        info.Geometry.ClipSpace.HalfExtentY),
                    LocalSizeX = info.Geometry.ThreadsPerGroup,
                    Mesh = new MeshShaderConfiguration(info.Geometry.OutputVertexCapacity, info.Geometry.OutputPrimitiveCapacity,
                        info.Geometry.ProvokingVertex, info.Geometry.InputPrimitiveCountPerWorkgroup, info.Geometry.InputVertexCountPerWorkgroup,
                        info.Geometry.LocalDataShareDwords, info.Geometry.InputTriangleStrip, info.Execution.DeviceSubgroupLaneCount,
                        info.Geometry.InputPointList, info.Geometry.InputLineList),
                };
            }

            default:
                return BuildComputeRequest(entry.Plan, resources, layout, options.ComputeInfo!, options.ComputeSystemRegisters,
                    sharedInt64Atomics, _host.ExecGuardElisionEnabled, signedZeroInfNanPreserve, _host.ComputeWave64SubgroupNative,
                    _host.ComputeRunsOn32LaneSubgroups(options.ComputeInfo!), imageInt64Atomics, options.TessellationHull,
                    bufferInt64Atomics: bufferInt64Atomics, float16Conversions: float16Conversions);
        }
    }

    private static BindingLayout AllocateLayout(Gen5ShaderProgram program, ShaderResourcePlan plan, SpecializedResourceInfo resources,
        uint userDataBase, uint userDataCount, uint pushDataCursor, bool usesDispatchThreadLimits, bool usesMeshDrawParameters = false,
        bool usesBufferLengths = false, bool usesTessellationData = false, bool tableBlocks = false, bool indirectThreads = false) =>
        BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, userDataBase, userDataCount, plan.Graph.ExcludedUserDataRegisters),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            pushDataCursor,
            usesDispatchThreadLimits: usesDispatchThreadLimits,
            usesMeshDrawParameters: usesMeshDrawParameters,
            callArgumentCount: BindingLayout.CountCallArguments(program),
            callTargetCount: BindingLayout.CountCallTargets(program),
            usesBufferLengths: usesBufferLengths,
            usesTessellationData: usesTessellationData, tableBlock: tableBlocks,
            limitsInShaderData: (IndirectLimitsInShaderData && indirectThreads) || LimitsInShaderDataHashes.Contains(plan.Hash)); // [local]

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

    // [local] SHARPEMU_SIGNED_ZERO_INF_NAN=0: no SignedZeroInfNanPreserve even where the device supports it (A/B switch).
    private static readonly bool SignedZeroInfNanAllowed = Environment.GetEnvironmentVariable("SHARPEMU_SIGNED_ZERO_INF_NAN") != "0";

    private static ShaderCompileRequest BuildComputeRequest(ShaderResourcePlan plan, SpecializedResourceInfo resources, BindingLayout layout,
        ComputeInputInfo info, Gen5ComputeSystemRegisters? systemRegisters, bool sharedInt64Atomics, bool execGuardElision,
        bool signedZeroInfNanPreserve, bool hostWave64Supported = false, bool hostComputeSubgroup32 = false,
        bool imageInt64Atomics = false, Gen5TessellationHullInfo? tessellationHull = null, bool bufferInt64Atomics = false,
        bool float16Conversions = false) =>
        new(plan, resources, layout)
        {
            WaveSize = info.WaveSize,
            TessellationHull = tessellationHull,
            CooperativeWave64Workgroup = tessellationHull is not null,
            HostWave64Supported = info.WaveSize == 64 && hostWave64Supported,
            HostComputeSubgroup32 = info.WaveSize == 64 && !hostWave64Supported && hostComputeSubgroup32,
            EnableExecGuardElision = info.WaveSize != 64 || execGuardElision,
            IeeeMode = info.IeeeMode,
            TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
            ScratchDwords = info.ScratchDwords,
            SupportsSharedInt64Atomics = sharedInt64Atomics,
            SupportsBufferInt64Atomics = bufferInt64Atomics,
            SupportsFloat16Conversions = float16Conversions,
            SupportsImageInt64Atomics = imageInt64Atomics,
            ShaderSignedZeroInfNanPreserveFloat32Supported = signedZeroInfNanPreserve,
            ComputeSystemRegisters = systemRegisters,
            LocalDataShareDwords = info.LocalDataShareDwords,
            LocalSizeX = Math.Max(info.ThreadsX, 1),
            LocalSizeY = Math.Max(info.ThreadsY, 1),
            LocalSizeZ = Math.Max(info.ThreadsZ, 1),
        };

    internal static bool TryCompilePrewarm(ComputePrewarmRecord record, ShaderCodeCapture code, IGuestGpuBackend compiler,
        bool sharedInt64Atomics, bool execGuardElision, bool signedZeroInfNanPreserve, out IGuestCompiledShader? compiled,
        out BindingLayout? layout, out string error, bool hostWave64Supported = false, bool hostComputeSubgroup32 = false,
        bool imageInt64Atomics = false, bool tableBlocks = false, Func<ulong, IShaderFrontend?>? frontendOf = null,
        bool bufferInt64Atomics = false, bool float16Conversions = false)
    {
        compiled = null;
        layout = null;
        try
        {
            IShaderFrontend? frontend = null;
            Gen5ShaderProgram program;
            if (code.FrontendDomain != 0)
            {
                frontend = frontendOf?.Invoke(code.FrontendDomain);
                if (frontend is null)
                {
                    error = $"The shader frontend 0x{code.FrontendDomain:X16} is not registered.";
                    return false;
                }

                var shader = new RegisteredShader(code.Address, 0, code.CodeSize, 0, 0, 0, 0, 0, 0)
                {
                    Frontend = frontend,
                    FrontendUserData = code.FrontendUserData,
                };
                if (!frontend.TryDecode(code.CreateContext(), shader, ShaderStage.Compute, out program, out error))
                {
                    return false;
                }
            }
            else if (!Gen5ShaderTranslator.TryDecodeProgram(code.CreateContext(), code.Address, out program, out error))
            {
                return false;
            }

            var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, record.Hash, record.UserDataBase, record.UserDataCount,
                waveSize: record.Info.WaveSize,
                normalizeBufferDescriptor: frontend?.NormalizeBufferDescriptor,
                normalizeImageDescriptor: frontend?.NormalizeImageDescriptor,
                imageNormalizationVersion: frontend?.ImageNormalizationVersion);
            var resources = ResourceMaterializer.ApplyTo(plan, record.Specialization);
            layout = AllocateLayout(program, plan, resources, record.UserDataBase, record.UserDataCount, record.PushDataCursor,
                record.Info.DispatchThreadDimensions, usesBufferLengths: frontend?.RequiresExplicitBufferLengths ?? false,
                tableBlocks: tableBlocks, indirectThreads: IsIndirectThreadDispatch(record.Info));
            var request = BuildComputeRequest(plan, resources, layout, record.Info, record.SystemRegisters,
                sharedInt64Atomics, execGuardElision, signedZeroInfNanPreserve, hostWave64Supported, hostComputeSubgroup32,
                imageInt64Atomics, bufferInt64Atomics: bufferInt64Atomics, float16Conversions: float16Conversions);
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
        ShaderCompileRequest request, uint? inputLocationMask)
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
                ShaderStage.Mesh => ShaderStageKind.Mesh,
                ShaderStage.TessellationEvaluation => ShaderStageKind.TessellationEvaluation,
                _ => ShaderStageKind.Compute,
            },
            Hash = source.Hash,
            Frontend = source.Registered.Frontend,
            TessellationFactorBuffer = request.TessellationHull is { } hull
                ? checked((int)Gen5TessellationLowering.FindFactorBuffer(entry.Plan, hull.HullEntryPc)) : -1,
            UsesTessellationLocalDataRing = request.TessellationHull is not null && entry.Program.TessellationRegisters.LocalDataRing,
            UserDataBase = source.UserDataBase,
            UserDataCount = (uint)source.UserData.Length,
            ParameterExportMask = entry.Program.ParameterExportMask,
            InputLocationMask = inputLocationMask,
            PixelColorExportMasks = entry.Program.PixelColorExportMasks,
            VertexOffsetScalarRegister = entry.EmbeddedFetch?.VertexOffsetScalarRegister ?? ShaderProgramInfo.NoScalarRegister,
            InstanceOffsetScalarRegister = entry.EmbeddedFetch?.InstanceOffsetScalarRegister ?? ShaderProgramInfo.NoScalarRegister,
            UsesDeviceAddresses = info.UsesDeviceAddresses,
            ConstantStoreValue = FindConstantStoreValue(entry.Program),
            HasBitwiseExclusiveOr = entry.HasBitwiseExclusiveOr,
            ConstantFill = entry.ConstantFill,
            BoundedFill = entry.BoundedFill,
            BoundedCopy = entry.BoundedCopy,
            StoresUniformValues = entry.StoresUniformValues,
            Buffers = buffers,
            Images = images,
            SamplerCount = info.Samplers.Count,
            WaveSize = request.WaveSize,
            ScratchDwords = request.ScratchDwords,
            VertexFetchComponents = fetchComponents,
            Resources = resources,
            Bindings = layout,
            CallRecordCallees = request.CallRecordCallees,
        };
    }
}

// Writes each compiled module and its decoded listing when the dump switch is on.
internal static class CompiledShaderDump
{
    internal static bool ShouldWrite(ulong shaderAddress, ulong? shaderHash)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var addressFilter = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV_ADDRESS");
        if (!string.IsNullOrWhiteSpace(addressFilter))
        {
            // [local] A comma-separated list of addresses.
            var matched = false;
            foreach (var filter in addressFilter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var span = filter.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                matched |= ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var filteredAddress) &&
                    shaderAddress == filteredAddress;
            }

            if (!matched)
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
            directory = Path.Combine(SharpEmuPaths.DataRoot, "shader-dumps");
        }

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{shaderAddress:X16}-{hash:X16}.{stage}");
    }

    public static void Write(string stage, ulong shaderAddress, ulong hash, IGuestCompiledShader shader, Gen5ShaderProgram program)
    {
        if (shader.Payload.Length == 0 || !ShouldWrite(shaderAddress, hash)) return;
        var basePath = GetBasePath(stage, shaderAddress, hash);
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

        try
        {
            File.WriteAllBytes($"{basePath}.{shader.PayloadFileExtension}", shader.Payload);
            File.WriteAllLines($"{basePath}.ir.txt", lines);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[SHADER][WARN] Cannot write the compiled shader dump: {exception.Message}");
        }
    }
}
