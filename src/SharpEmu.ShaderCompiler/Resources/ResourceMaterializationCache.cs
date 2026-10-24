// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// Copies guest bytes only when they are already resident on the CPU side. It never
// synchronizes: a range the GPU may still own, or that a clean read would refuse,
// returns false so the caller falls back to a full materialization.
public delegate bool ResidentGuestBytesReader(ulong address, Span<byte> destination, bool clean);

// [local] SHARPEMU_VALIDATE_IN_PLACE: the reader's bytes compared with expected in place; false when not compared.
public delegate bool ResidentGuestBytesComparer(ulong address, ReadOnlySpan<byte> expected, bool clean, out bool equal);

// Materialization is a pure function of the plan, the draw's user data, the shader
// base, the compute state and the guest words it reads. This cache records exactly
// those words and reuses the result while every one of them is unchanged, so a draw
// that re-binds the same resources skips the descriptor walk. Any failed read, any
// GPU-owned range and any byte difference make the entry miss; nothing is guessed.
public sealed class ResourceMaterializationCache
{
    // Two generations approximate LRU: a full young generation becomes the old one,
    // and an old entry that is used again is promoted back.
    private readonly int _generationCapacity;
    private Dictionary<ulong, Entry> _young = new();
    private Dictionary<ulong, Entry> _old = new();
    private byte[] _scratch = new byte[256];
    private ReadRecorder? _spareRecorder;

    public ResourceMaterializationCache(int generationCapacity = 16384, bool stampStoreTables = false)
    {
        _generationCapacity = Math.Max(1, generationCapacity);
        _stampStoreTables = stampStoreTables;
    }

    // [local] SHARPEMU_PROGRAM_GATE's per-thread caches: the store-table ranges of a walk come from the thread's scan
    // cache, whose words the recorder does not see when the scan was cached, so an entry of a plan with store tables
    // only serves while the thread's scan cache still holds the scans its walk used (DeviceStoreTablePlanner.UsedScan).
    private readonly bool _stampStoreTables;

    private bool StampsTables(ShaderResourcePlan plan) => _stampStoreTables && plan.DeviceStoreTables.Count != 0;

    private static bool TablesCurrent(Entry entry) => DeviceStoreTablePlanner.ScansCurrent(entry.UsedScans);

    private static long _totalHits;
    private static long _totalMisses;
    private static long _totalUncacheable;
    private const int MaxVariants = 8;

    // SHARPEMU_MATERIALIZATION_CONTENT_KEY=0 keys entries on every user-data word again, as
    // before the key was reduced to the words that reach the outputs.
    internal static readonly bool ContentKeyed = Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZATION_CONTENT_KEY") != "0";
    private static long _totalStale;
    private static long _totalStaleUnreadable;
    private static long _totalRefreshes;
    private static long _totalTableStale; // [local] hits refused because a store-table scan they used was dropped

    [ThreadStatic]
    private static bool _readingTable;

    [ThreadStatic]
    public static ulong CurrentPlanHash; // [local]

    public static bool ReadingTable
    {
        get => _readingTable;
        private set => _readingTable = value;
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Uncacheable { get; private set; }
    public long TableRefreshes { get; private set; }

    // Process-wide counters since the previous call, for the periodic render report.
    public static string TakeReport()
    {
        var hits = Interlocked.Exchange(ref _totalHits, 0);
        var misses = Interlocked.Exchange(ref _totalMisses, 0);
        var uncacheable = Interlocked.Exchange(ref _totalUncacheable, 0);
        var stale = Interlocked.Exchange(ref _totalStale, 0);
        var staleUnreadable = Interlocked.Exchange(ref _totalStaleUnreadable, 0);
        var refreshes = Interlocked.Exchange(ref _totalRefreshes, 0);
        var total = hits + misses;
        return FormattableString.Invariant(
            $"[PERF][RESOURCE_CACHE] hits={hits} misses={misses} stale={stale} stale_unreadable={staleUnreadable} table_stale={Interlocked.Exchange(ref _totalTableStale, 0)} refreshes={refreshes} uncacheable={uncacheable} hit_rate={(total == 0 ? 0 : hits * 100.0 / total):F1}% {RawReadPrefetch.TakeReport()} read_memo_hits={Interlocked.Exchange(ref ReadRecorder.MemoHits, 0)} read_memo_misses={Interlocked.Exchange(ref ReadRecorder.MemoMisses, 0)} runsort_checked={Interlocked.Exchange(ref ReadRecorder.RunSortChecked, 0)} runsort_mismatches={Interlocked.Exchange(ref ReadRecorder.RunSortMismatches, 0)} block_builds={Interlocked.Exchange(ref ReadRecorder.BlockBuilds, 0)} block_fallbacks={Interlocked.Exchange(ref ReadRecorder.BlockFallbacks, 0)} block_checked={Interlocked.Exchange(ref ReadRecorder.BlockChecked, 0)} block_mismatches={Interlocked.Exchange(ref ReadRecorder.BlockMismatches, 0)}{TakeRebaseReport()}{TakeSyncValidateReport()}{TakeInPlaceReport()}");
    }

    public bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure,
        ResidentGuestBytesComparer? residentComparer = null)
    {
        CurrentPlanHash = plan.Hash; // [local] for overlay diagnostics
        var key = KeyOf(plan, inputs);
        var found = TryFind(key, plan, inputs, out var cached);
        var stamps = StampsTables(plan);
        if (found && stamps)
        {
            var current = false;
            for (var variant = cached; variant is not null && !current; variant = variant.Next)
                current = TablesCurrent(variant);
            if (!current)
            {
                Interlocked.Increment(ref _totalTableStale);
                found = false; // every variant predates a table invalidation: walk, and drop them
            }
        }
        Entry? shifted = null; // [local] SHARPEMU_MATERIALIZE_REBASE
        (ResourceSnapshot Snapshot, ResourceSpecialization Specialization)? syncCheck = null; // [local] SHARPEMU_MATERIALIZE_SYNC_VALIDATE=2
        if (found)
        {
            if (StaleLog) _lastStaleReason = "nomatch";
            var unreadable = false;
            Entry? previous = null;
            for (var variant = cached; variant is not null; previous = variant, variant = variant.Next)
            {
                if (!variant.Matches(plan, inputs) || (stamps && !TablesCurrent(variant)))
                    continue;
                var validateStarted = MaterializeSplitProfile.Start(); // [local]
                var validated = Validate(variant, inputs, residentReader, out var variantUnreadable, residentComparer);
                MaterializeSplitProfile.Note(MaterializeSplitProfile.Kind.Validate, validateStarted);
                if (validated && StaleLog) _lastStaleReason = "snapshot";
                var syncValidated = _syncValidated;
                if (validated && variant.SnapshotFor(inputs, _patches) is { } hit)
                {
                    if (syncValidated)
                    {
                        NoteSyncValidate(plan.Hash, hit: true);
                        if (SyncValidateMode == 2)
                        {
                            syncCheck = (hit, variant.Specialization);
                            break;
                        }
                    }

                    if (previous is not null)
                    {
                        previous.Next = variant.Next;
                        variant.Next = cached;
                        Store(key, variant);
                    }

                    Hits++;
                    Interlocked.Increment(ref _totalHits);
                    snapshot = hit;
                    specialization = variant.Specialization;
                    failure = default;
                    return true;
                }

                if (syncValidated)
                    NoteSyncValidate(plan.Hash, hit: false);
                unreadable |= variantUnreadable;
            }

            if (syncCheck is not null)
                goto Walk;

            var refreshStarted = MaterializeSplitProfile.Start(); // [local]
            Entry refreshed = null!;
            var refreshedTable = (!stamps || TablesCurrent(cached)) && cached.UserDataEquals(inputs) && TryRefreshTable(key, cached, plan, inputs, residentReader, out refreshed);
            MaterializeSplitProfile.Note(MaterializeSplitProfile.Kind.Refresh, refreshStarted);
            if (refreshedTable)
            {
                refreshed.UsedScans = cached.UsedScans;
                TableRefreshes++;
                Interlocked.Increment(ref _totalRefreshes);
                snapshot = refreshed.Snapshot;
                specialization = refreshed.Specialization;
                failure = default;
                return true;
            }

            if (RebaseMode != 0)
            {
                for (var variant = cached; variant is not null && shifted is null; variant = variant.Next)
                {
                    if (variant.Matches(plan, inputs) && (!stamps || TablesCurrent(variant)))
                    {
                        var shiftStarted = MaterializeSplitProfile.Start(); // [local]
                        shifted = TryShift(variant, inputs, residentReader);
                        MaterializeSplitProfile.Note(MaterializeSplitProfile.Kind.Shift, shiftStarted);
                        if (shifted is not null)
                            shifted.UsedScans = variant.UsedScans;
                    }
                }

                if (shifted is not null)
                {
                    Interlocked.Increment(ref _rebaseCandidates);
                    var trust = _rebaseTrust.GetValueOrDefault(plan.Hash);
                    if (RebaseMode == 3 || (RebaseMode == 1 && trust >= RebaseConfirmations && (++_rebaseUses & RebaseSampleMask) != 0))
                    {
                        shifted.Next = cached;
                        TrimVariants(shifted);
                        Store(key, shifted);
                        Hits++;
                        Interlocked.Increment(ref _totalHits);
                        Interlocked.Increment(ref _rebaseHits);
                        snapshot = shifted.Snapshot;
                        specialization = shifted.Specialization;
                        failure = default;
                        return true;
                    }

                    if (trust < 0 && RebaseMode == 1)
                        shifted = null;
                }
            }

            Interlocked.Increment(ref _totalStale);
            if (unreadable)
                Interlocked.Increment(ref _totalStaleUnreadable);
            if (StaleLog)
                NoteStale(plan.Hash, cached);
        }

    Walk:
        Misses++;
        Interlocked.Increment(ref _totalMisses);
        var recorder = TakeRecorder();
        recorder.AllowBlocks(); // [local] SHARPEMU_RECORDER_BLOCKS
        var missStarted = MissLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            var recording = new ResourceRuntimeInputs
            {
                UserData = inputs.UserData,
                ShaderBase = inputs.ShaderBase,
                ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
                ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
                ReadResidentMemory = recorder.WrapResident(inputs.ReadResidentMemory),
                ReadsClean = inputs.ReadsClean,
                ComputeState = inputs.ComputeState,
                TablePhase = recorder.TablePhase,
            };
            var walkStarted = MaterializeSplitProfile.Start(); // [local]
            if (stamps)
                DeviceStoreTablePlanner.BeginUsedScans();
            bool walked;
            DeviceStoreTablePlanner.UsedScan[] usedScans;
            try
            {
                walked = ResourceMaterializer.Materialize(plan, recording, ref snapshot, ref specialization, out failure);
            }
            finally
            {
                usedScans = stamps ? DeviceStoreTablePlanner.EndUsedScans() : [];
            }
            MaterializeSplitProfile.Note(recorder.RecordedWords > MaterializeSplitProfile.BigReads ? MaterializeSplitProfile.Kind.BigWalk : MaterializeSplitProfile.Kind.Walk, walkStarted);
            MaterializeSplitProfile.NoteReads(recorder.RecordedWords);
            if (!walked)
            {
                if (shifted is not null)
                    NoteRebaseVerdict(plan.Hash, shifted, null, null, null, "walk_failed");
                return false;
            }

            if (syncCheck is { } check)
                NoteSyncCheck(plan.Hash, check.Snapshot, check.Specialization, snapshot, specialization);
            if (recorder.Failed)
            {
                if (shifted is not null)
                    NoteRebaseVerdict(plan.Hash, shifted, snapshot, specialization, null, null);
                Uncacheable++;
                Interlocked.Increment(ref _totalUncacheable);
                return true;
            }

            var buildStarted = MaterializeSplitProfile.Start(); // [local]
            var bigBuild = recorder.RecordedWords > MaterializeSplitProfile.BigReads;
            var built = recorder.Build(plan, inputs, snapshot, specialization);
            built.UsedScans = usedScans;
            MaterializeSplitProfile.Note(bigBuild ? MaterializeSplitProfile.Kind.BigBuild : MaterializeSplitProfile.Kind.Build, buildStarted);
            if (shifted is not null)
                NoteRebaseVerdict(plan.Hash, shifted, snapshot, specialization, built, null);
            if (found)
            {
                built.Next = cached;
                var depth = 1;
                for (var variant = built; variant.Next is not null; variant = variant.Next)
                {
                    if (++depth >= MaxVariants)
                    {
                        variant.Next = null;
                        break;
                    }
                }
            }

            Store(key, built);
            return true;
        }
        finally
        {
            if (MissLog)
                NoteMiss(plan.Hash, found, recorder.RecordedWords, System.Diagnostics.Stopwatch.GetTimestamp() - missStarted);
            ReturnRecorder(recorder);
        }
    }

    // [local] SHARPEMU_MATERIALIZE_MISS_LOG=1: full walks by plan (time, recorded reads, whether the key was found).
    private static readonly bool MissLog = Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_MISS_LOG") == "1";
    private static readonly Dictionary<ulong, (long Ticks, long Count, long Reads, long Found)> _missByPlan = new();
    private static long _missSamples;
    private static long _missWindowStart = System.Diagnostics.Stopwatch.GetTimestamp();

    private static void NoteMiss(ulong planHash, bool found, int reads, long ticks)
    {
        lock (_missByPlan)
        {
            var entry = _missByPlan.GetValueOrDefault(planHash);
            _missByPlan[planHash] = (entry.Ticks + ticks, entry.Count + 1, entry.Reads + reads, entry.Found + (found ? 1 : 0));
            if ((++_missSamples & 8191) != 0)
                return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var seconds = (now - _missWindowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
            _missWindowStart = now;
            var total = _missByPlan.Values.Sum(value => value.Ticks);
            Console.Error.WriteLine(FormattableString.Invariant($"[MATERIALIZE_MISS] window_s={seconds:F1} misses=8192 total_ms={total * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}"));
            foreach (var (plan, value) in _missByPlan.OrderByDescending(pair => pair.Value.Ticks).Take(12))
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[MATERIALIZE_MISS] plan=0x{plan:X16} n={value.Count} ms={value.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} us_per={value.Ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency / value.Count:F1} reads_per={value.Reads / (double)value.Count:F0} found={value.Found}"));
            _missByPlan.Clear();
        }
    }

    // [local] SHARPEMU_MATERIALIZE_REBASE (=1 by default since 2026-10-13, =0 disables): per-frame rings. A table the draw's user-data pointer reaches moves with
    // that pointer, and the buffer descriptors inside it point into the same ring, so their base dwords moved by the
    // same delta and every read through them moved too. The rule: one non-zero delta D (multiple of 256) from the
    // user-data pointers; every range based on a moved pointer, or read relative to a base address that equals
    // (low dword) a word that moved by D, is read again at its recorded address + D, every other range at its
    // recorded address; each word must be unchanged or exactly old + D (no carry into the high dword). The output is
    // the cached one with the draw's user data, every buffer base dword, table word and device-address range base
    // equal to a moved word's old value taken to its new value; a moved value seen in an image or sampler word, or in a
    // buffer dword other than the base, refuses. Ranges read through an unknown base refuse.
    // =2 verifies every candidate against the full walk (the walk's result is used); =1 uses a plan's candidates
    // once RebaseConfirmations of them agreed with the walk, rejects the plan for good on one disagreement, and
    // keeps verifying one use in RebaseSampleMask + 1; =3 uses every candidate unverified (measurement only).
    internal static readonly int RebaseMode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_REBASE"), out var rebaseMode) ? rebaseMode : 1;
    private static readonly bool SplitByBaseAddress = RebaseMode != 0;
    private static readonly bool RebaseLog = Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_REBASE_LOG") == "1";
    private const ulong ConflictingBaseAddress = ulong.MaxValue;
    private const int RebaseConfirmations = 4;
    private const long RebaseSampleMask = 63;
    private long _rebaseUses;
    private readonly Dictionary<ulong, int> _rebaseTrust = new();
    private static long _rebaseCandidates, _rebaseHits, _rebaseAgreed, _rebaseMismatches, _rebaseUnverifiable, _rebaseRejectedPlans;
    private static readonly Dictionary<string, long> _rebaseRefusals = new();
    private static readonly Dictionary<ulong, (long Agreed, long Mismatched)> _rebaseByPlan = new();
    private static int _rebaseMismatchLogs;
    private static long _rebaseMismatchTotal;

    private static string TakeRebaseReport()
    {
        if (RebaseMode == 0)
            return string.Empty;
        string refusals;
        lock (_rebaseRefusals)
        {
            refusals = string.Join(",", _rebaseRefusals.OrderByDescending(pair => pair.Value).Take(8).Select(pair => $"{pair.Key}:{pair.Value}"));
            _rebaseRefusals.Clear();
        }

        string plans;
        lock (_rebaseByPlan)
        {
            plans = string.Join(",", _rebaseByPlan.OrderByDescending(pair => pair.Value.Agreed + pair.Value.Mismatched).Take(6)
                .Select(pair => $"{pair.Key:X16}:{pair.Value.Agreed}/{pair.Value.Mismatched}"));
            _rebaseByPlan.Clear();
        }

        return FormattableString.Invariant(
            $" rebase_candidates={Interlocked.Exchange(ref _rebaseCandidates, 0)} rebase_hits={Interlocked.Exchange(ref _rebaseHits, 0)} rebase_agreed={Interlocked.Exchange(ref _rebaseAgreed, 0)} rebase_mismatches={Interlocked.Exchange(ref _rebaseMismatches, 0)} rebase_mismatches_total={Interlocked.Read(ref _rebaseMismatchTotal)} rebase_unverifiable={Interlocked.Exchange(ref _rebaseUnverifiable, 0)} rebase_rejected_plans={Interlocked.Read(ref _rebaseRejectedPlans)} rebase_refusals=[{refusals}] rebase_plans=[{plans}]");
    }

    private static void Refuse(string reason)
    {
        if (!RebaseLog)
            return;
        lock (_rebaseRefusals)
            _rebaseRefusals[reason] = _rebaseRefusals.GetValueOrDefault(reason) + 1;
    }

    private static void TrimVariants(Entry head)
    {
        var depth = 1;
        for (var variant = head; variant.Next is not null; variant = variant.Next)
        {
            if (++depth >= MaxVariants)
            {
                variant.Next = null;
                break;
            }
        }
    }

    private Entry? TryShift(Entry entry, ResourceRuntimeInputs inputs, ResidentGuestBytesReader residentReader)
    {
        var count = entry.RangeAddresses.Length;
        if (entry.RangeReadBase is not { } readBases)
        {
            Refuse("no_bases");
            return null;
        }

        var addresses = new ulong[count];
        var userBased = new bool[count];
        var delta = 0L;
        var movedBases = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            if (entry.RangeBase[index] < 0)
                continue;
            if (!entry.TryRebase(index, inputs, out var address))
            {
                Refuse("rebase");
                return null;
            }

            userBased[index] = true;
            addresses[index] = address;
            var moved = (long)(address - entry.RangeAddresses[index]);
            if (moved == 0)
                continue;
            if (delta != 0 && moved != delta)
            {
                Refuse("two_deltas");
                return null;
            }

            delta = moved;
            var register = entry.RangeBase[index] - (int)entry.Plan.UserDataBase;
            movedBases.Add(entry.UserData[register]);
        }

        if (delta == 0)
        {
            Refuse("no_delta");
            return null;
        }

        if ((delta & 0xFF) != 0)
        {
            Refuse("unaligned_delta");
            return null;
        }

        var current = new byte[entry.Bytes.Length];
        var substitutions = new Dictionary<uint, uint>();
        var movedThrough = new bool[count];
        for (var pass = 0; ; pass++)
        {
            if (pass == 4)
            {
                Refuse("unstable");
                return null;
            }

            substitutions.Clear();
            for (var index = 0; index < count; index++)
            {
                var recorded = entry.RangeAddresses[index];
                if (!userBased[index])
                {
                    var readBase = readBases[index];
                    if (readBase == 0 || readBase == ConflictingBaseAddress)
                    {
                        Refuse("unknown_base");
                        return null;
                    }

                    movedThrough[index] = movedBases.Contains((uint)readBase);
                    addresses[index] = movedThrough[index] ? (ulong)((long)recorded + delta) : recorded;
                }

                var length = entry.RangeLengths[index];
                var offset = entry.RangeOffsets[index];
                var span = current.AsSpan(offset, length);
                if (!residentReader(addresses[index], span, entry.RangeClean[index]))
                {
                    Refuse("unreadable");
                    return null;
                }

                for (var word = 0; word < length; word += sizeof(uint))
                {
                    var now = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[word..]);
                    var was = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(offset + word));
                    if (now == was)
                        continue;
                    var expected = (long)was + delta;
                    if (expected < 0 || expected > uint.MaxValue || (uint)expected != now)
                    {
                        Refuse("word_changed");
                        return null;
                    }

                    if (substitutions.TryGetValue(was, out var other) && other != now)
                    {
                        Refuse("ambiguous_word");
                        return null;
                    }

                    substitutions[was] = now;
                }
            }

            var grown = false;
            foreach (var was in substitutions.Keys)
                grown |= movedBases.Add(was);
            if (!grown)
                break;
        }

        var bufferMap = new Dictionary<uint, uint>(substitutions);
        foreach (var was in movedBases)
        {
            var now = (long)was + delta;
            if (now >= 0 && now <= uint.MaxValue)
                bufferMap.TryAdd(was, (uint)now);
        }

        var cachedSnapshot = entry.Snapshot;
        foreach (var image in cachedSnapshot.Images)
            foreach (var word in image)
                if (substitutions.ContainsKey(word))
                {
                    Refuse("image_word");
                    return null;
                }

        foreach (var sampler in cachedSnapshot.Samplers)
            foreach (var word in sampler)
                if (substitutions.ContainsKey(word))
                {
                    Refuse("sampler_word");
                    return null;
                }

        if (entry.SnapshotFor(inputs, _noPatches) is not { } snapshot)
        {
            Refuse("snapshot");
            return null;
        }

        var buffers = (uint[][])snapshot.Buffers.Clone();
        for (var buffer = 0; buffer < buffers.Length; buffer++)
        {
            var original = cachedSnapshot.Buffers[buffer];
            for (var dword = 0; dword < original.Length; dword++)
            {
                if (!substitutions.TryGetValue(original[dword], out var now) || buffers[buffer][dword] != original[dword])
                    continue;
                if (dword != 0)
                {
                    Refuse("buffer_dword");
                    return null;
                }

                if (ReferenceEquals(buffers[buffer], snapshot.Buffers[buffer]))
                    buffers[buffer] = (uint[])buffers[buffer].Clone();
                buffers[buffer][dword] = now;
            }
        }

        var table = snapshot.FlattenedResourceTable;
        for (var slot = 0; slot < table.Length; slot++)
        {
            if (!substitutions.TryGetValue(cachedSnapshot.FlattenedResourceTable[slot], out var now) ||
                table[slot] != cachedSnapshot.FlattenedResourceTable[slot])
                continue;
            if (ReferenceEquals(table, snapshot.FlattenedResourceTable))
                table = (uint[])table.Clone();
            table[slot] = now;
        }

        var ranges = snapshot.DeviceAddressRanges;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (!bufferMap.ContainsKey((uint)range.Base))
                continue;
            if (ReferenceEquals(ranges, snapshot.DeviceAddressRanges))
                ranges = (DeviceAddressRange[])ranges.Clone();
            ranges[index] = range with { Base = (ulong)((long)range.Base + delta) };
        }

        var sorted = true;
        for (var index = 1; index < count && sorted; index++)
            sorted = addresses[index - 1] < addresses[index];
        var shiftedBases = new ulong[count];
        for (var index = 0; index < count; index++)
            shiftedBases[index] = userBased[index] || movedThrough[index] ? (ulong)((long)readBases[index] + delta) : readBases[index];
        var userData = new uint[inputs.UserData.Count];
        for (var index = 0; index < userData.Length; index++)
            userData[index] = inputs.UserData[index];
        return new Entry
        {
            Plan = entry.Plan,
            UserData = userData,
            ShaderBase = entry.ShaderBase,
            ComputeState = entry.ComputeState,
            RangeAddresses = addresses,
            RangeOffsets = entry.RangeOffsets,
            RangeLengths = entry.RangeLengths,
            RangeClean = entry.RangeClean,
            RangeBase = entry.RangeBase,
            RangeReadBase = shiftedBases,
            ExactUserData = entry.ExactUserData,
            WordTableOnly = entry.WordTableOnly,
            WordPatches = entry.WordPatches,
            TableRefreshable = entry.TableRefreshable && sorted,
            Bytes = current,
            Snapshot = new ResourceSnapshot
            {
                Buffers = buffers,
                Images = snapshot.Images,
                Samplers = snapshot.Samplers,
                FlattenedResourceTable = table,
                UserData = snapshot.UserData,
                DeviceAddressRanges = ranges,
            },
            Specialization = entry.Specialization,
        };
    }

    private readonly List<(int Word, uint Value)> _noPatches = new();

    // A rebase candidate against the full walk of the same draw: the outputs, and with a built entry the reads.
    private void NoteRebaseVerdict(ulong planHash, Entry shifted, ResourceSnapshot? snapshot, ResourceSpecialization? specialization, Entry? built, string? unverifiable)
    {
        string? difference = unverifiable is not null ? null : SnapshotDifference(shifted.Snapshot, snapshot!) ??
            (Equals(shifted.Specialization, specialization) ? null : "specialization") ??
            (built is null ? null : ReadsDifference(shifted, built));
        if (unverifiable is not null)
        {
            Interlocked.Increment(ref _rebaseUnverifiable);
            return;
        }

        var trust = _rebaseTrust.GetValueOrDefault(planHash);
        lock (_rebaseByPlan)
        {
            var entry = _rebaseByPlan.GetValueOrDefault(planHash);
            _rebaseByPlan[planHash] = difference is null ? (entry.Agreed + 1, entry.Mismatched) : (entry.Agreed, entry.Mismatched + 1);
        }

        if (difference is null)
        {
            Interlocked.Increment(ref _rebaseAgreed);
            if (trust >= 0)
                _rebaseTrust[planHash] = trust + 1;
            return;
        }

        Interlocked.Increment(ref _rebaseMismatches);
        var total = Interlocked.Increment(ref _rebaseMismatchTotal);
        if (trust >= 0)
        {
            _rebaseTrust[planHash] = -1;
            Interlocked.Increment(ref _rebaseRejectedPlans);
        }

        // Always printed (the first 64, then every 1024th), with or without SHARPEMU_PROFILE_RENDER: grep for it.
        if (Interlocked.Increment(ref _rebaseMismatchLogs) <= 64 || (total & 1023) == 0)
            Console.Error.WriteLine($"[MATERIALIZE_REBASE_MISMATCH] plan=0x{planHash:X16} total={total} {difference}");
    }

    public static string? SnapshotDifference(ResourceSnapshot shifted, ResourceSnapshot walked) // [local] public for SHARPEMU_PROGRAM_GATE_VERIFY
    {
        static string? Jagged(string name, uint[][] left, uint[][] right)
        {
            if (left.Length != right.Length)
                return $"{name}_count {left.Length}!={right.Length}";
            for (var index = 0; index < left.Length; index++)
                if (!left[index].AsSpan().SequenceEqual(right[index]))
                    return $"{name}[{index}] {string.Join(",", left[index].Select(word => word.ToString("X8")))} != {string.Join(",", right[index].Select(word => word.ToString("X8")))}";
            return null;
        }

        if (Jagged("buffer", shifted.Buffers, walked.Buffers) is { } buffers)
            return buffers;
        if (Jagged("image", shifted.Images, walked.Images) is { } images)
            return images;
        if (Jagged("sampler", shifted.Samplers, walked.Samplers) is { } samplers)
            return samplers;
        if (!shifted.FlattenedResourceTable.AsSpan().SequenceEqual(walked.FlattenedResourceTable))
        {
            var first = 0;
            while (first < Math.Min(shifted.FlattenedResourceTable.Length, walked.FlattenedResourceTable.Length) &&
                   shifted.FlattenedResourceTable[first] == walked.FlattenedResourceTable[first])
                first++;
            return $"table len {shifted.FlattenedResourceTable.Length}/{walked.FlattenedResourceTable.Length} first_diff={first}";
        }

        if (!shifted.UserData.AsSpan().SequenceEqual(walked.UserData))
            return "user_data";
        if (!shifted.DeviceAddressRanges.AsSpan().SequenceEqual(walked.DeviceAddressRanges))
            return $"device_ranges {string.Join(",", shifted.DeviceAddressRanges.Select(range => $"{range.Handle}:{range.Base:X}+{range.Size:X}"))} != {string.Join(",", walked.DeviceAddressRanges.Select(range => $"{range.Handle}:{range.Base:X}+{range.Size:X}"))}";
        return null;
    }

    // The same words read, at the same addresses, with the same bytes.
    private static string? ReadsDifference(Entry shifted, Entry built)
    {
        static List<(ulong Address, uint Word)> Words(Entry entry)
        {
            var words = new List<(ulong, uint)>(entry.Bytes.Length / sizeof(uint));
            for (var range = 0; range < entry.RangeAddresses.Length; range++)
                for (var offset = 0; offset < entry.RangeLengths[range]; offset += sizeof(uint))
                    words.Add((entry.RangeAddresses[range] + (ulong)offset,
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(entry.RangeOffsets[range] + offset))));
            words.Sort();
            return words;
        }

        var left = Words(shifted);
        var right = Words(built);
        if (left.Count != right.Count)
            return $"reads_count {left.Count}!={right.Count}";
        for (var index = 0; index < left.Count; index++)
            if (left[index] != right[index])
                return $"read[{index}] 0x{left[index].Address:X}={left[index].Word:X8} != 0x{right[index].Address:X}={right[index].Word:X8}";
        return null;
    }

    // Most stale entries in Demon's Souls differ only in words the shader reads through scalar
    // loads (inline constants rewritten every frame); the descriptors, device ranges and
    // specialization are unchanged. When every changed word was read only while evaluating the
    // flattened table, only the table is evaluated again. It is refused when the plan's
    // specialization reads or extends the table (indirect or candidate tables), when the table
    // now reads a word the entry did not validate, or when a word moved between the two reads.
    private bool TryRefreshTable(ulong key, Entry cached, ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader, out Entry refreshed)
    {
        refreshed = null!;
        if (!cached.TableRefreshable)
            return false;

        // [local] SHARPEMU_VALIDATE_IN_PLACE: the ranges are read into a reused buffer, copied out only when the refresh
        // goes ahead, and the change scan skips equal runs at once (the same verdict, =2 counts any other).
        var fastScan = ValidateInPlaceMode != 0;
        byte[] current;
        if (fastScan)
        {
            if (_refreshScratch.Length < cached.Bytes.Length)
                _refreshScratch = new byte[Math.Max(cached.Bytes.Length, _refreshScratch.Length * 2)];
            current = _refreshScratch;
        }
        else
        {
            current = new byte[cached.Bytes.Length];
        }

        for (var index = 0; index < cached.RangeAddresses.Length; index++)
        {
            if (!residentReader(cached.RangeAddresses[index], current.AsSpan(cached.RangeOffsets[index], cached.RangeLengths[index]), cached.RangeClean[index]))
                return false;
        }

        var currentBytes = current.AsSpan(0, cached.Bytes.Length);
        var changed = fastScan ? TableWordsChangedSkippingEqual(currentBytes, cached) : TableWordsChanged(currentBytes, cached);
        if (ValidateInPlaceMode == 2)
        {
            Interlocked.Increment(ref _refreshScanChecked);
            if (changed != TableWordsChanged(currentBytes, cached))
            {
                Interlocked.Increment(ref _refreshScanMismatches);
                Interlocked.Increment(ref _inPlaceMismatchTotal);
                if (Interlocked.Increment(ref _inPlaceMismatchLogs) <= 20)
                    Console.Error.WriteLine($"[REFRESH_SCAN_MISMATCH] plan=0x{plan.Hash:X16} bytes={currentBytes.Length} fast={changed}");
            }
        }

        if (changed != true)
            return false;
        if (fastScan)
            current = currentBytes.ToArray();

        var recorder = TakeRecorder();
        try
        {
            var recording = new ResourceRuntimeInputs
            {
                UserData = inputs.UserData,
                ShaderBase = inputs.ShaderBase,
                ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
                ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
                ReadResidentMemory = recorder.WrapResident(inputs.ReadResidentMemory),
                ReadsClean = inputs.ReadsClean,
                ComputeState = inputs.ComputeState,
            };
            var cachedTable = cached.Snapshot.FlattenedResourceTable;
            ReadingTable = true;
            bool evaluated;
            uint[] table;
            try
            {
                evaluated = ResourceMaterializer.TryEvaluateTable(plan, recording, out table);
            }
            finally
            {
                ReadingTable = false;
            }

            if (!evaluated || recorder.Failed || table.Length != cachedTable.Length)
                return false;

            foreach (var (address, word, _, _, _, _, _) in recorder.Reads)
            {
                if (!TryFindWord(cached, address, out var offset) ||
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset, sizeof(uint))) != word)
                    return false;
            }

            // The written device-address slots come from reads validated unchanged above.
            foreach (var slot in plan.WrittenRangeSlotByHandle.Values)
                cachedTable.AsSpan((int)slot, ShaderResourcePlan.WrittenRangeDwordCount).CopyTo(table.AsSpan((int)slot));

            var previous = cached.Snapshot;
            refreshed = new Entry
            {
                Plan = cached.Plan,
                UserData = cached.UserData,
                ShaderBase = cached.ShaderBase,
                ComputeState = cached.ComputeState,
                RangeAddresses = cached.RangeAddresses,
                RangeOffsets = cached.RangeOffsets,
                RangeLengths = cached.RangeLengths,
                RangeClean = cached.RangeClean,
                RangeBase = cached.RangeBase,
                RangeReadBase = cached.RangeReadBase,
                ExactUserData = cached.ExactUserData,
                WordTableOnly = cached.WordTableOnly,
                WordPatches = cached.WordPatches,
                TableRefreshable = true,
                Bytes = current,
                Snapshot = new ResourceSnapshot
                {
                    Buffers = previous.Buffers,
                    Images = previous.Images,
                    Samplers = previous.Samplers,
                    FlattenedResourceTable = table,
                    UserData = previous.UserData,
                    DeviceAddressRanges = previous.DeviceAddressRanges,
                },
                Specialization = cached.Specialization,
                Next = cached.Next,
            };
            Store(key, refreshed);
            return true;
        }
        finally
        {
            ReturnRecorder(recorder);
        }
    }

    private byte[] _refreshScratch = [];
    private static long _refreshScanChecked, _refreshScanMismatches;

    // True when only flattened-table words changed (some did), false when none did, null when another word changed.
    private static bool? TableWordsChanged(ReadOnlySpan<byte> current, Entry cached)
    {
        var changed = false;
        for (var offset = 0; offset < current.Length; offset += sizeof(uint))
        {
            if (current.Slice(offset, sizeof(uint)).SequenceEqual(cached.Bytes.AsSpan(offset, sizeof(uint))))
                continue;
            if (!cached.WordTableOnly[offset / sizeof(uint)])
                return null;
            changed = true;
        }

        return changed;
    }

    // [local] TableWordsChanged with every run of equal bytes skipped at once.
    private static bool? TableWordsChangedSkippingEqual(ReadOnlySpan<byte> current, Entry cached)
    {
        var changed = false;
        var bytes = cached.Bytes.AsSpan(0, current.Length);
        var offset = 0;
        while (offset < current.Length)
        {
            offset = NextChangedWord(current, bytes, offset);
            if (offset >= current.Length)
                break;
            if (!cached.WordTableOnly[offset / sizeof(uint)])
                return null;
            changed = true;
            offset += sizeof(uint);
        }

        return changed;
    }

    private ReadRecorder TakeRecorder()
    {
        var recorder = _spareRecorder ?? new ReadRecorder();
        _spareRecorder = null;
        return recorder;
    }

    private void ReturnRecorder(ReadRecorder recorder)
    {
        recorder.Reset();
        _spareRecorder = recorder;
    }

    // The byte offset of a recorded dword in the entry's bytes, found by its address.
    private static bool TryFindWord(Entry entry, ulong address, out int offset)
    {
        offset = 0;
        var addresses = entry.RangeAddresses;
        int low = 0, high = addresses.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (addresses[middle] <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0)
            return false;
        var delta = address - addresses[found];
        if (delta % sizeof(uint) != 0 || delta >= (ulong)entry.RangeLengths[found])
            return false;
        offset = entry.RangeOffsets[found] + (int)delta;
        return true;
    }

    private static ulong KeyOf(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(plan));
        hash.Add(inputs.ShaderBase);
        hash.Add(inputs.ComputeState);
        var userData = inputs.UserData;
        var use = ContentKeyed ? plan.UserDataUse : null;
        hash.Add(userData.Count);
        // A second, independent mix keeps accidental collisions out of the 64-bit key.
        var high = 0x9E3779B9u;
        for (var index = 0; index < userData.Count; index++)
        {
            // A word the plan only reads through contributes nothing: entries based on other
            // pointers are found by the key and told apart by what they read.
            var word = use is null || use.IsValueRegister(index) ? userData[index] : 0u;
            hash.Add(word);
            high = (high ^ word) * 0x01000193u;
        }

        var low = (uint)hash.ToHashCode();
        return ((ulong)high << 32) | low;
    }

    private bool TryFind(ulong key, ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out Entry entry)
    {
        if (_young.TryGetValue(key, out entry!))
            return entry.Matches(plan, inputs);
        if (!_old.TryGetValue(key, out entry!) || !entry.Matches(plan, inputs))
            return false;
        _old.Remove(key);
        Store(key, entry);
        return true;
    }

    private void Store(ulong key, Entry entry)
    {
        if (_young.Count >= _generationCapacity && !_young.ContainsKey(key))
        {
            _old = _young;
            _young = new Dictionary<ulong, Entry>(_generationCapacity);
        }

        _young[key] = entry;
    }

    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    // The words of the entry being validated that this draw reads differently, when every one
    // of them is a patchable read: (word index, the draw's word).
    private readonly List<(int Word, uint Value)> _patches = new();

    // [local] SHARPEMU_MATERIALIZE_STALE_LOG=1: why entries found by their key fail validation, by plan.
    private static readonly bool StaleLog = Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_STALE_LOG") == "1";
    private string _lastStaleReason = "none";
    private static readonly Dictionary<ulong, Dictionary<string, long>> _staleByPlan = new();
    private static long _staleSamples;
    private static readonly Dictionary<ulong, int> _staleLogged = new();

    private void NoteStale(ulong planHash, Entry entry)
    {
        var words = entry.Bytes.Length / sizeof(uint);
        lock (_staleByPlan)
        {
            if (!_staleByPlan.TryGetValue(planHash, out var reasons))
                _staleByPlan[planHash] = reasons = new Dictionary<string, long>();
            reasons[_lastStaleReason] = reasons.GetValueOrDefault(_lastStaleReason) + 1;
            reasons[$"words{words}"] = reasons.GetValueOrDefault($"words{words}") + 1;
            reasons[$"ranges{entry.RangeAddresses.Length}"] = reasons.GetValueOrDefault($"ranges{entry.RangeAddresses.Length}") + 1;
            if ((++_staleSamples & 4095) == 0)
            {
                foreach (var (plan, counts) in _staleByPlan.OrderByDescending(pair => pair.Value.Where(kv => !kv.Key.StartsWith("words") && !kv.Key.StartsWith("ranges")).Sum(kv => kv.Value)).Take(10))
                    Console.Error.WriteLine($"[MATERIALIZE_STALE] plan=0x{plan:X16} " + string.Join(" ", counts.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}={kv.Value}")));
                _staleByPlan.Clear();
            }
        }
    }

    // [local] SHARPEMU_MATERIALIZE_SYNC_VALIDATE (0 off by default): a non-clean range the resident reader refuses
    // because the GPU may still own it is synchronized (the readback the full walk would do for those same words)
    // and compared like any other range. An entry whose every recorded word is unchanged is a hit: the walk it
    // replaces would have read the same words, taken the same branches and produced the cached result. Clean ranges
    // keep refusing (the walk would fail them too). In the same readback the other ranges this plan found GPU-owned
    // before are brought back when their last writer has already completed, so the plan's next draws validate from
    // resident memory; the dirty tracking still decides every later read, so a range written again stays refused.
    // =1 on; =2 also runs the full walk on each hit validated that way, uses its result and counts differences;
    // =3 synchronizes only the range being validated (no other ranges of the plan).
    public static readonly int SyncValidateMode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_SYNC_VALIDATE"), out var syncValidate) ? syncValidate : 0;
    private const int MaxHotRanges = 16;
    private bool _syncValidated;
    private readonly Dictionary<ulong, List<(ulong Address, int Length)>> _hotRanges = new();
    private (ulong Address, int Length)[] _syncRanges = new (ulong, int)[MaxHotRanges + 1];
    private static long _syncValidateReads, _syncValidateFailed, _syncValidateHits, _syncValidateChecked, _syncValidateMismatches, _syncValidateMismatchTotal;
    private static readonly Dictionary<ulong, (long Hits, long Stale)> _syncValidateByPlan = new();

    private bool TrySyncRead(ResourceRuntimeInputs inputs, ulong planHash, ulong address, Span<byte> destination, ResidentGuestBytesReader residentReader)
    {
        if (inputs.SynchronizeRanges is not { } synchronize)
            return false;
        if (!_hotRanges.TryGetValue(planHash, out var hot))
            _hotRanges[planHash] = hot = new List<(ulong, int)>(MaxHotRanges);
        var count = 0;
        _syncRanges[count++] = (address, destination.Length);
        if (SyncValidateMode != 3)
            foreach (var range in hot)
                if (range.Address != address)
                    _syncRanges[count++] = range;
        Interlocked.Increment(ref _syncValidateReads);
        if (!synchronize(_syncRanges.AsSpan(0, count)) || !residentReader(address, destination, false))
        {
            Interlocked.Increment(ref _syncValidateFailed);
            return false;
        }

        if (!hot.Exists(range => range.Address == address))
        {
            if (hot.Count == MaxHotRanges)
                hot.RemoveAt(0);
            hot.Add((address, destination.Length));
        }

        _syncValidated = true;
        return true;
    }

    private static void NoteSyncValidate(ulong planHash, bool hit)
    {
        if (hit)
            Interlocked.Increment(ref _syncValidateHits);
        lock (_syncValidateByPlan)
        {
            var entry = _syncValidateByPlan.GetValueOrDefault(planHash);
            _syncValidateByPlan[planHash] = hit ? (entry.Hits + 1, entry.Stale) : (entry.Hits, entry.Stale + 1);
        }
    }

    private static string TakeSyncValidateReport()
    {
        if (SyncValidateMode == 0)
            return string.Empty;
        string plans;
        lock (_syncValidateByPlan)
        {
            plans = string.Join(",", _syncValidateByPlan.OrderByDescending(pair => pair.Value.Hits + pair.Value.Stale).Take(6)
                .Select(pair => $"{pair.Key:X16}:{pair.Value.Hits}/{pair.Value.Stale}"));
            _syncValidateByPlan.Clear();
        }

        return FormattableString.Invariant(
            $" sync_validate_reads={Interlocked.Exchange(ref _syncValidateReads, 0)} sync_validate_failed={Interlocked.Exchange(ref _syncValidateFailed, 0)} sync_validate_hits={Interlocked.Exchange(ref _syncValidateHits, 0)} sync_validate_checked={Interlocked.Exchange(ref _syncValidateChecked, 0)} sync_validate_mismatches={Interlocked.Exchange(ref _syncValidateMismatches, 0)} sync_validate_mismatches_total={Interlocked.Read(ref _syncValidateMismatchTotal)} sync_validate_plans=[{plans}]");
    }

    private static void NoteSyncCheck(ulong planHash, ResourceSnapshot validated, ResourceSpecialization validatedSpecialization,
        ResourceSnapshot walked, ResourceSpecialization walkedSpecialization)
    {
        Interlocked.Increment(ref _syncValidateChecked);
        var difference = SnapshotDifference(validated, walked) ?? (Equals(validatedSpecialization, walkedSpecialization) ? null : "specialization");
        if (difference is null)
            return;
        Interlocked.Increment(ref _syncValidateMismatches);
        // Always printed (the first 64, then every 1024th): grep for it.
        var total = Interlocked.Increment(ref _syncValidateMismatchTotal);
        if (total <= 64 || (total & 1023) == 0)
            Console.Error.WriteLine($"[MATERIALIZE_SYNC_VALIDATE_MISMATCH] plan=0x{planHash:X16} total={total} {difference}");
    }

    // [local] SHARPEMU_VALIDATE_IN_PLACE (1 by default, =2 verify, =0 off): a recorded range of InPlaceMinBytes or more is compared
    // with the guest bytes where they lie (the reader's own ownership checks first) instead of copied out and compared;
    // an equal range skips the copy, a different one of an entry without word patches fails as the copy would, and
    // anything else takes the copy path unchanged. =2 also takes the copy path after every
    // in-place verdict and counts a different verdict as a race when a second in-place compare agrees with the copy
    // (the memory changed in between), else as a mismatch.
    public static readonly int ValidateInPlaceMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_VALIDATE_IN_PLACE"), out var inPlaceMode) ? inPlaceMode : 1;
    private const int InPlaceMinBytes = 4096;
    private static long _inPlaceCompared, _inPlaceEqual, _inPlaceStale, _inPlaceUnhandled, _inPlaceUnhandledFront, _inPlaceKb, _inPlaceChecked, _inPlaceRaces, _inPlaceMismatches, _inPlaceMismatchTotal;
    private static int _inPlaceMismatchLogs;

    private static string TakeInPlaceReport() => ValidateInPlaceMode == 0 ? string.Empty :
        $" in_place_compared={Interlocked.Exchange(ref _inPlaceCompared, 0)} in_place_equal={Interlocked.Exchange(ref _inPlaceEqual, 0)} in_place_stale={Interlocked.Exchange(ref _inPlaceStale, 0)} in_place_unhandled={Interlocked.Exchange(ref _inPlaceUnhandled, 0)} in_place_unhandled_front={Interlocked.Exchange(ref _inPlaceUnhandledFront, 0)} in_place_kb={Interlocked.Exchange(ref _inPlaceKb, 0)} in_place_checked={Interlocked.Exchange(ref _inPlaceChecked, 0)} in_place_races={Interlocked.Exchange(ref _inPlaceRaces, 0)} in_place_mismatches={Interlocked.Exchange(ref _inPlaceMismatches, 0)} patch_scan_checked={Interlocked.Exchange(ref _patchScanChecked, 0)} patch_scan_mismatches={Interlocked.Exchange(ref _patchScanMismatches, 0)} refresh_scan_checked={Interlocked.Exchange(ref _refreshScanChecked, 0)} refresh_scan_mismatches={Interlocked.Exchange(ref _refreshScanMismatches, 0)} in_place_mismatches_total={Interlocked.Read(ref _inPlaceMismatchTotal)}";

    private static void CheckInPlace(bool equal, ResidentGuestBytesComparer comparer, ulong address, ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> cached, bool readable, bool clean)
    {
        Interlocked.Increment(ref _inPlaceChecked);
        var copyEqual = readable && current.SequenceEqual(cached);
        if (copyEqual == equal)
            return;
        if (comparer(address, cached, clean, out var again) && again == copyEqual)
        {
            Interlocked.Increment(ref _inPlaceRaces);
            return;
        }

        Interlocked.Increment(ref _inPlaceMismatches);
        Interlocked.Increment(ref _inPlaceMismatchTotal);
        if (Interlocked.Increment(ref _inPlaceMismatchLogs) <= 20)
            Console.Error.WriteLine($"[VALIDATE_IN_PLACE_MISMATCH] address=0x{address:X} bytes={cached.Length} in_place={equal} copy={copyEqual} readable={readable} clean={clean}");
    }

    // The patches of a changed range, word by word; false at the first changed word that cannot be patched.
    private static bool CollectPatches(Entry entry, int index, ReadOnlySpan<byte> current, ReadOnlySpan<byte> cached,
        List<(int Word, uint Value)> patches)
    {
        for (var offset = 0; offset < current.Length; offset += sizeof(uint))
        {
            var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current[offset..]);
            var was = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cached[offset..]);
            if (word == was)
                continue;
            var wordIndex = (entry.RangeOffsets[index] + offset) / sizeof(uint);
            if (!entry.WordPatches!.TryGetValue(wordIndex, out var targets) || !Entry.PatchAllowed(targets, word, was))
                return false;
            patches.Add((wordIndex, word));
        }

        return true;
    }

    // [local] The offset of the first word at or after offset (a word offset) that differs between the two spans of the
    // same length, or their length when none does: the equal bytes are skipped vectorized.
    internal static int NextChangedWord(ReadOnlySpan<byte> current, ReadOnlySpan<byte> cached, int offset) =>
        offset + (current[offset..].CommonPrefixLength(cached[offset..]) & ~(sizeof(uint) - 1));

    // [local] CollectPatches with every run of equal bytes skipped at once: the same changed words, in the same order.
    private static bool CollectPatchesSkippingEqual(Entry entry, int index, ReadOnlySpan<byte> current, ReadOnlySpan<byte> cached,
        List<(int Word, uint Value)> patches)
    {
        var offset = 0;
        while (offset < current.Length)
        {
            offset = NextChangedWord(current, cached, offset);
            if (offset >= current.Length)
                break;
            var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current[offset..]);
            var was = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cached[offset..]);
            var wordIndex = (entry.RangeOffsets[index] + offset) / sizeof(uint);
            if (!entry.WordPatches!.TryGetValue(wordIndex, out var targets) || !Entry.PatchAllowed(targets, word, was))
                return false;
            patches.Add((wordIndex, word));
            offset += sizeof(uint);
        }

        return true;
    }

    private static long _patchScanChecked, _patchScanMismatches;
    private readonly List<(int Word, uint Value)> _patchCheck = new();

    private void CheckPatchScan(Entry entry, int index, ReadOnlySpan<byte> current, ReadOnlySpan<byte> cached, bool patched, int patchesBefore)
    {
        Interlocked.Increment(ref _patchScanChecked);
        _patchCheck.Clear();
        var expected = CollectPatches(entry, index, current, cached, _patchCheck);
        var same = expected == patched;
        if (same && expected)
        {
            same = _patches.Count - patchesBefore == _patchCheck.Count;
            for (var item = 0; same && item < _patchCheck.Count; item++)
                same = _patches[patchesBefore + item] == _patchCheck[item];
        }

        if (!same)
        {
            Interlocked.Increment(ref _patchScanMismatches);
            Interlocked.Increment(ref _inPlaceMismatchTotal);
            if (Interlocked.Increment(ref _inPlaceMismatchLogs) <= 20)
                Console.Error.WriteLine($"[VALIDATE_PATCH_SCAN_MISMATCH] plan=0x{entry.Plan.Hash:X16} range={index} bytes={current.Length} fast={patched}/{_patches.Count - patchesBefore} slow={expected}/{_patchCheck.Count}");
        }
    }

    private bool Validate(Entry entry, ResourceRuntimeInputs inputs, ResidentGuestBytesReader residentReader, out bool unreadable,
        ResidentGuestBytesComparer? residentComparer = null)
    {
        unreadable = false;
        _syncValidated = false;
        _patches.Clear();
        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            if (!entry.TryRebase(index, inputs, out var address))
            {
                if (StaleLog) _lastStaleReason = $"rebase:r{index}:base{entry.RangeBase[index]}";
                return false;
            }
            var length = entry.RangeLengths[index];
            var inPlace = false; // [local] SHARPEMU_VALIDATE_IN_PLACE
            var inPlaceEqual = false;
            if (residentComparer is not null && length >= InPlaceMinBytes)
            {
                inPlace = residentComparer(address, entry.Bytes.AsSpan(entry.RangeOffsets[index], length), entry.RangeClean[index], out inPlaceEqual);
                if (!inPlace)
                {
                    Interlocked.Increment(ref _inPlaceUnhandled);
                    if (System.Threading.Thread.CurrentThread.Name == "GPU front")
                        Interlocked.Increment(ref _inPlaceUnhandledFront);
                }
                else
                {
                    Interlocked.Increment(ref _inPlaceCompared);
                    Interlocked.Add(ref _inPlaceKb, length / 1024);
                    if (inPlaceEqual)
                        Interlocked.Increment(ref _inPlaceEqual);
                    if (inPlaceEqual && ValidateInPlaceMode != 2)
                    {
                        MaterializeSplitProfile.NoteValidatedBytes(length);
                        continue;
                    }

                    // A changed range without patchable words fails below whatever the copy holds.
                    if (!inPlaceEqual && entry.WordPatches is null && ValidateInPlaceMode != 2 && !StaleLog)
                    {
                        Interlocked.Increment(ref _inPlaceStale);
                        MaterializeSplitProfile.NoteValidatedBytes(length);
                        return false;
                    }
                }
            }

            if (_scratch.Length < length)
                _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
            var current = _scratch.AsSpan(0, length);
            if (!residentReader(address, current, entry.RangeClean[index]) &&
                !(SyncValidateMode != 0 && !entry.RangeClean[index] && TrySyncRead(inputs, entry.Plan.Hash, address, current, residentReader)))
            {
                if (inPlace && ValidateInPlaceMode == 2)
                    CheckInPlace(inPlaceEqual, residentComparer!, address, current, entry.Bytes.AsSpan(entry.RangeOffsets[index], length), readable: false, entry.RangeClean[index]);
                unreadable = true;
                if (StaleLog) _lastStaleReason = $"unreadable:r{index}:clean{entry.RangeClean[index]}";
                return false;
            }

            var cached = entry.Bytes.AsSpan(entry.RangeOffsets[index], length);
            MaterializeSplitProfile.NoteValidatedBytes(length); // [local]
            if (inPlace && ValidateInPlaceMode == 2)
                CheckInPlace(inPlaceEqual, residentComparer!, address, current, cached, readable: true, entry.RangeClean[index]);
            if (current.SequenceEqual(cached))
                continue;
            if (StaleLog)
            {
                var first = 0;
                var changedWords = 0;
                for (var offset = length - sizeof(uint); offset >= 0; offset -= sizeof(uint))
                    if (!current.Slice(offset, sizeof(uint)).SequenceEqual(cached.Slice(offset, sizeof(uint))))
                    {
                        first = offset;
                        changedWords++;
                    }
                var wordIndex = (entry.RangeOffsets[index] + first) / sizeof(uint);
                lock (_staleByPlan)
                {
                    var logged = _staleLogged.GetValueOrDefault(entry.Plan.Hash);
                    if (logged < 4 && _staleLogged.Count < 64)
                    {
                        _staleLogged[entry.Plan.Hash] = logged + 1;
                        var info = entry.Plan.Info;
                        Console.Error.WriteLine($"[MATERIALIZE_STALE_SAMPLE] plan=0x{entry.Plan.Hash:X16} range={index} address=0x{address:X} recorded=0x{entry.RangeAddresses[index]:X} base={entry.RangeBase[index]} " +
                            $"cached={Convert.ToHexString(cached)} current={Convert.ToHexString(current)} buffers={info.Buffers.Count} images={info.Images.Count} samplers={info.Samplers.Count} sources={entry.Plan.DescriptorSources.Count} " +
                            $"user_data=[{string.Join(",", entry.UserData.Select(word => word.ToString("X8")))}] draw_user_data=[{string.Join(",", Enumerable.Range(0, inputs.UserData.Count).Select(i => inputs.UserData[i].ToString("X8")))}]");
                    }
                }
                _lastStaleReason = $"word:r{index}/{entry.RangeAddresses.Length}:len{length}:dw{first / sizeof(uint)}:changed{changedWords}:table{entry.WordTableOnly[wordIndex]}:patchable{entry.WordPatches?.ContainsKey(wordIndex) == true}:base{entry.RangeBase[index]}";
            }
            if (entry.WordPatches is null)
                return false;
            if (ValidateInPlaceMode == 0)
            {
                if (!CollectPatches(entry, index, current, cached, _patches))
                    return false;
                continue;
            }

            var patchesBefore = _patches.Count; // [local] SHARPEMU_VALIDATE_IN_PLACE: equal runs skipped vectorized
            var patched = CollectPatchesSkippingEqual(entry, index, current, cached, _patches);
            if (ValidateInPlaceMode == 2)
                CheckPatchScan(entry, index, current, cached, patched, patchesBefore);
            if (!patched)
                return false;
        }

        return true;
    }

    private sealed class Entry
    {
        // [local] The store-table scans the walk used, for a cache that stamps store tables.
        public DeviceStoreTablePlanner.UsedScan[] UsedScans = [];
        public required ShaderResourcePlan Plan { get; init; }
        public required uint[] UserData { get; init; }
        public required ulong ShaderBase { get; init; }
        public required ComputeSelectorState? ComputeState { get; init; }
        public required ulong[] RangeAddresses { get; init; }
        public required int[] RangeOffsets { get; init; }
        public required int[] RangeLengths { get; init; }
        public required bool[] RangeClean { get; init; }
        // Per range: the user-data register pair its address was based on, AbsoluteBase when the
        // address does not move with the user data, UnknownBase when the reader could not tell.
        public required int[] RangeBase { get; init; }
        // [local] Per range: the base address its reads were relative to (0 unknown), with SHARPEMU_MATERIALIZE_REBASE.
        public ulong[]? RangeReadBase { get; init; }
        // A range read through an unknown base keeps the entry to draws with the same user data.
        public required bool ExactUserData { get; init; }
        // Per recorded dword: read only while the flattened table was evaluated.
        public required bool[] WordTableOnly { get; init; }
        // By recorded dword: where the plan copies it verbatim (UserDataUseAnalysis.PatchableReads).
        public required Dictionary<int, UserDataUseAnalysis.PatchTarget[]>? WordPatches { get; init; }
        // The table has the plan's plain layout, so no specialization read or extended it.
        public required bool TableRefreshable { get; init; }
        public required byte[] Bytes { get; init; }
        public required ResourceSnapshot Snapshot { get; set; }
        public required ResourceSpecialization Specialization { get; init; }
        public Entry? Next { get; set; }

        public bool Matches(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
        {
            if (!ReferenceEquals(Plan, plan) || ShaderBase != inputs.ShaderBase ||
                !Nullable.Equals(ComputeState, inputs.ComputeState) || UserData.Length != inputs.UserData.Count)
                return false;
            if (ExactUserData || !ContentKeyed)
                return UserDataEquals(inputs);
            var use = plan.UserDataUse;
            for (var index = 0; index < UserData.Length; index++)
                if (use.IsValueRegister(index) && UserData[index] != inputs.UserData[index])
                    return false;
            return true;
        }

        public bool UserDataEquals(ResourceRuntimeInputs inputs)
        {
            if (UserData.Length != inputs.UserData.Count)
                return false;
            for (var index = 0; index < UserData.Length; index++)
                if (UserData[index] != inputs.UserData[index])
                    return false;
            return true;
        }

        // The address of a recorded range for this draw: the recorded one, or the recorded one
        // moved by the difference between the draw's and the entry's base pointer. Only the 48
        // address bits may differ; the stride and record fields of a buffer handle live in the
        // same words and change the address formula.
        public bool TryRebase(int range, ResourceRuntimeInputs inputs, out ulong address)
        {
            address = RangeAddresses[range];
            var register = RangeBase[range];
            if (register < 0)
                return register == UserDataUseAnalysis.AbsoluteBase || UserDataEquals(inputs);
            var index = register - (int)Plan.UserDataBase;
            if (index < 0 || index + 1 >= UserData.Length)
                return false;
            var recorded = (UserData[index] | ((ulong)UserData[index + 1] << 32));
            var current = (inputs.UserData[index] | ((ulong)inputs.UserData[index + 1] << 32));
            if (recorded == current)
                return true;
            if (((recorded ^ current) & ~AddressMask) != 0)
                return false;
            var recordedBase = recorded & AddressMask;
            var currentBase = current & AddressMask;
            if (recordedBase == 0 || currentBase == 0)
                return false;
            var moved = address + currentBase - recordedBase;
            if (moved > AddressMask)
                return false;
            address = moved;
            return true;
        }

        // A buffer dword may take another value only where the base address and record count
        // live: dword 0, dword 2 and the low 16 bits of dword 1. The specialization was derived
        // from the stride and format bits of the cached words, which must stay.
        private static bool BufferDwordMayChange(int dword, uint word, uint cached) =>
            dword is 0 or 2 || (dword == 1 && ((word ^ cached) >> 16) == 0);

        public static bool PatchAllowed(UserDataUseAnalysis.PatchTarget[] targets, uint word, uint cached)
        {
            foreach (var target in targets)
            {
                if (!target.Table && !BufferDwordMayChange(target.Dword, word, cached))
                    return false;
            }

            return true;
        }

        // The cached snapshot, with the draw's own user data when it differs from the entry's:
        // the words the plan reads through are not part of the key, and the host still binds
        // every user-data word the shader reads directly. Buffer dwords computed from user data
        // are evaluated again for this draw, and the words the draw reads differently go to the
        // places they are copied to. Null when a buffer dword changed beyond its base address.
        public ResourceSnapshot? SnapshotFor(ResourceRuntimeInputs inputs, List<(int Word, uint Value)> patches)
        {
            if (patches.Count == 0 && UserDataEquals(inputs))
                return Snapshot;
            var buffers = Snapshot.Buffers;
            var table = Snapshot.FlattenedResourceTable;
            void SetBuffer(int buffer, int dword, uint word)
            {
                if (ReferenceEquals(buffers, Snapshot.Buffers))
                    buffers = [.. Snapshot.Buffers];
                if (ReferenceEquals(buffers[buffer], Snapshot.Buffers[buffer]))
                    buffers[buffer] = [.. Snapshot.Buffers[buffer]];
                buffers[buffer][dword] = word;
            }

            var use = Plan.UserDataUse;
            foreach (var dword in use.RebasableDwords)
            {
                if ((uint)dword.Buffer >= (uint)buffers.Length || (uint)dword.Dword >= (uint)buffers[dword.Buffer].Length ||
                    !use.TryEvaluatePure(dword.Value, inputs.UserData, out var evaluated))
                    return null;
                var word = (uint)evaluated;
                var cached = buffers[dword.Buffer][dword.Dword];
                if (word == cached)
                    continue;
                if (!BufferDwordMayChange(dword.Dword, word, cached))
                    return null;
                SetBuffer(dword.Buffer, dword.Dword, word);
            }

            foreach (var (wordIndex, word) in patches)
            {
                foreach (var target in WordPatches![wordIndex])
                {
                    if (target.Table)
                    {
                        if ((uint)target.Index >= (uint)table.Length)
                            return null;
                        if (ReferenceEquals(table, Snapshot.FlattenedResourceTable))
                            table = [.. Snapshot.FlattenedResourceTable];
                        table[target.Index] = word;
                    }
                    else
                    {
                        if ((uint)target.Index >= (uint)buffers.Length || (uint)target.Dword >= (uint)buffers[target.Index].Length)
                            return null;
                        SetBuffer(target.Index, target.Dword, word);
                    }
                }
            }

            var sameUserData = UserDataEquals(inputs);
            var snapshot = new ResourceSnapshot
            {
                Buffers = buffers,
                Images = Snapshot.Images,
                Samplers = Snapshot.Samplers,
                FlattenedResourceTable = table,
                UserData = sameUserData ? Snapshot.UserData : inputs.UserData as uint[] ?? [.. inputs.UserData],
                DeviceAddressRanges = Snapshot.DeviceAddressRanges,
            };
            if (sameUserData)
            {
                // The entry's own draw now reads these words: keep them, so the next one is a plain hit.
                foreach (var (wordIndex, word) in patches)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(wordIndex * sizeof(uint)), word);
                Snapshot = snapshot;
            }

            return snapshot;
        }
    }

    private sealed class ReadRecorder
    {
        private readonly List<(ulong Address, uint Word, bool Clean, bool Table, int Base, int Node, ulong BaseAddress)> _reads = new();
        private bool _inTable;
        private GuestWordReader? _reader;
        private GuestWordReader? _cleanReader;
        private ResidentGuestBytesReader? _residentReader;
        private readonly GuestWordReader _recordRead;
        private readonly GuestWordReader _recordCleanRead;
        private readonly ResidentGuestBytesReader _recordResidentRead;

        public ReadRecorder()
        {
            _recordRead = (ulong address, out uint word) => Read(_reader!, address, out word, clean: false);
            _recordCleanRead = (ulong address, out uint word) => Read(_cleanReader!, address, out word, clean: true);
            _recordResidentRead = (ulong address, Span<byte> destination, bool clean) => ReadResident(address, destination, clean);
            TablePhase = inTable =>
            {
                _inTable = inTable;
                ReadingTable = inTable;
            };
        }

        public void Reset()
        {
            _reads.Clear();
            _blocks.Clear();
            _blockWords = 0;
            _blockBytesUsed = 0;
            _blocksAllowed = false;
            if (_blockBytes.Length > KeptReads * sizeof(uint))
                _blockBytes = [];
            if (_memo.Count != 0)
                _memo.Clear();
            // Retain ordinary descriptor walks without keeping unusually large
            // tables alive for the lifetime of the renderer.
            if (_reads.Capacity > KeptReads)
                _reads.Capacity = 0;
            _reader = null;
            _cleanReader = null;
            _residentReader = null;
            _inTable = false;
            Failed = false;
        }

        // [local] SHARPEMU_RECORDER_KEEP: the read-list capacity a returned recorder keeps, in entries of 40 bytes
        // (default 1 << 20; 0 restores the old 16384). Camera moves record walks of more than 16384 reads, so the list
        // was dropped and grown again from empty on every such miss: ~160 MB/s of List growth on the front in pan,
        // 43 % of all managed allocation, much of it on the large-object heap.
        private static readonly int KeptReads = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RECORDER_KEEP"), out var kept)
            ? kept <= 0 ? 16384 : kept
            : 1 << 20;

        // [local] SHARPEMU_RECORDER_KEYSORT (on by default; =0 disables): the recorded reads are sorted through a ulong key
        // array and an index array instead of a Comparison over the 40-byte tuples (a delegate call and two tuple
        // copies per comparison): 3.4 ms per frame of the front in camera moves. Array.Sort(keys, items) runs the same
        // introsort with the same comparisons, so the resulting order, ties included, is the one the old sort gave.
        private static readonly bool KeySort = Environment.GetEnvironmentVariable("SHARPEMU_RECORDER_KEYSORT") != "0";
        private ulong[] _sortKeys = [];
        private int[] _sortOrder = [];
        private (ulong Address, uint Word, bool Clean, bool Table, int Base, int Node, ulong BaseAddress)[] _sortScratch = [];

        private void SortReadsByAddress()
        {
            var count = _reads.Count;
            if (!KeySort || count < 2)
            {
                _reads.Sort((left, right) => left.Address.CompareTo(right.Address));
                return;
            }

            if (_sortKeys.Length < count)
            {
                var size = Math.Max(count, _sortKeys.Length * 2);
                _sortKeys = new ulong[size];
                _sortOrder = new int[size];
                _sortScratch = new (ulong, uint, bool, bool, int, int, ulong)[size];
            }

            var reads = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_reads);
            for (var index = 0; index < count; index++)
            {
                _sortKeys[index] = reads[index].Address;
                _sortOrder[index] = index;
            }

            if (RunSort != 0 && count >= RunSortMinReads && RunMergeSort(count))
            {
                if (RunSort == 2)
                {
                    Array.Sort(_sortKeys, _sortOrder, 0, count);
                    CompareRunOrder(reads, count);
                }
                else
                {
                    _runIdxA.AsSpan(0, count).CopyTo(_sortOrder);
                }
            }
            else
            {
                Array.Sort(_sortKeys, _sortOrder, 0, count);
            }

            for (var index = 0; index < count; index++)
                _sortScratch[index] = reads[_sortOrder[index]];
            _sortScratch.AsSpan(0, count).CopyTo(reads);
            if (_sortKeys.Length > KeptReads)
            {
                _sortKeys = [];
                _sortOrder = [];
                _sortScratch = [];
            }

            if (_runKeysA.Length > KeptReads)
            {
                _runKeysA = [];
                _runIdxA = [];
                _runKeysB = [];
                _runIdxB = [];
            }
        }

        // [local] SHARPEMU_RECORDER_RUNSORT (1 by default, =0 turns it off): a walk that recorded at least RunSortMinReads words, with
        // at most MaxLongRuns runs of LongRun or more ascending addresses (a store table read in one pass in camera moves:
        // ~520 k words; the introsort of those was 3.5 ms per frame of the front), is ordered by merging: the words outside
        // the long runs are sorted, the long runs are taken as they are, and the parts are merged. Words of one address
        // keep the order they were read in (the introsort leaves them in any order), so the entry Build makes can differ
        // only where one walk read an address twice and the first read differs in value, clean, table, base or node.
        // =2 orders both ways, keeps the introsort's order and counts such addresses (runsort_mismatches).
        private static readonly int RunSort = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RECORDER_RUNSORT"), out var runSort) ? runSort : 1;
        private const int RunSortMinReads = 65536;
        private const int LongRun = 4096;
        private const int MaxLongRuns = 8;
        internal static long RunSortMismatches, RunSortChecked;
        private ulong[] _runKeysA = [], _runKeysB = [];
        private int[] _runIdxA = [], _runIdxB = [];
        private readonly List<(int Start, int Length)> _segments = new();
        private readonly List<(int Start, int Length)> _mergedSegments = new();

        // The record order sorted by (address, record index) into _runIdxA; false when the reads have no long run, or
        // too many, and the introsort should run instead. _sortKeys holds the addresses in record order.
        private bool RunMergeSort(int count)
        {
            var keys = _sortKeys;
            if (_runKeysA.Length < count)
            {
                var size = Math.Max(count, _runKeysA.Length * 2);
                _runKeysA = new ulong[size];
                _runIdxA = new int[size];
                _runKeysB = new ulong[size];
                _runIdxB = new int[size];
            }

            _segments.Clear();
            var rest = 0;
            var start = 0;
            for (var index = 1; index <= count; index++)
            {
                if (index != count && keys[index] >= keys[index - 1])
                    continue;
                var length = index - start;
                if (length >= LongRun)
                {
                    if (_segments.Count == MaxLongRuns)
                        return false;
                    _segments.Add((start, length));
                }
                else
                {
                    for (var position = start; position < index; position++)
                    {
                        _runKeysA[rest] = keys[position];
                        _runIdxA[rest++] = position;
                    }
                }

                start = index;
            }

            if (_segments.Count == 0)
                return false;
            Array.Sort(_runKeysA, _runIdxA, 0, rest);
            for (var group = 0; group < rest;)
            {
                var end = group + 1;
                while (end < rest && _runKeysA[end] == _runKeysA[group])
                    end++;
                if (end - group > 1)
                    Array.Sort(_runIdxA, group, end - group);
                group = end;
            }

            // The long runs follow the sorted rest in buffer A; record positions are already ascending inside each.
            var offset = rest;
            for (var run = 0; run < _segments.Count; run++)
            {
                var (runStart, runLength) = _segments[run];
                Array.Copy(keys, runStart, _runKeysA, offset, runLength);
                for (var position = 0; position < runLength; position++)
                    _runIdxA[offset + position] = runStart + position;
                _segments[run] = (offset, runLength);
                offset += runLength;
            }

            if (rest != 0)
                _segments.Insert(0, (0, rest));
            while (_segments.Count > 1)
            {
                _mergedSegments.Clear();
                for (var pair = 0; pair < _segments.Count; pair += 2)
                {
                    var (leftStart, leftLength) = _segments[pair];
                    if (pair + 1 == _segments.Count)
                    {
                        Array.Copy(_runKeysA, leftStart, _runKeysB, leftStart, leftLength);
                        Array.Copy(_runIdxA, leftStart, _runIdxB, leftStart, leftLength);
                        _mergedSegments.Add((leftStart, leftLength));
                        continue;
                    }

                    var (rightStart, rightLength) = _segments[pair + 1];
                    MergeRuns(leftStart, leftLength, rightStart, rightLength);
                    _mergedSegments.Add((leftStart, leftLength + rightLength));
                }

                (_runKeysA, _runKeysB) = (_runKeysB, _runKeysA);
                (_runIdxA, _runIdxB) = (_runIdxB, _runIdxA);
                _segments.Clear();
                _segments.AddRange(_mergedSegments);
            }

            return true;
        }

        // Two adjacent ordered parts of buffer A into buffer B at the left part's start, by (address, record index).
        private void MergeRuns(int leftStart, int leftLength, int rightStart, int rightLength)
        {
            var left = leftStart;
            var right = rightStart;
            var leftEnd = leftStart + leftLength;
            var rightEnd = rightStart + rightLength;
            var target = leftStart;
            while (left < leftEnd && right < rightEnd)
            {
                var takeLeft = _runKeysA[left] < _runKeysA[right] ||
                    (_runKeysA[left] == _runKeysA[right] && _runIdxA[left] < _runIdxA[right]);
                var from = takeLeft ? left++ : right++;
                _runKeysB[target] = _runKeysA[from];
                _runIdxB[target++] = _runIdxA[from];
            }

            for (; left < leftEnd; left++, target++)
            {
                _runKeysB[target] = _runKeysA[left];
                _runIdxB[target] = _runIdxA[left];
            }

            for (; right < rightEnd; right++, target++)
            {
                _runKeysB[target] = _runKeysA[right];
                _runIdxB[target] = _runIdxA[right];
            }
        }

        // SHARPEMU_RECORDER_RUNSORT=2: the first record of each address, which is what Build keeps, under both orders.
        private void CompareRunOrder(Span<(ulong Address, uint Word, bool Clean, bool Table, int Base, int Node, ulong BaseAddress)> reads, int count)
        {
            var mismatches = 0L;
            var groups = 0L;
            for (var index = 0; index < count; index++)
            {
                var introsort = reads[_sortOrder[index]];
                var merged = reads[_runIdxA[index]];
                if (introsort.Address != merged.Address)
                {
                    mismatches++;
                    continue;
                }

                if (index != 0 && reads[_sortOrder[index - 1]].Address == introsort.Address)
                    continue;
                groups++;
                if (introsort != merged)
                    mismatches++;
            }

            Interlocked.Add(ref RunSortMismatches, mismatches);
            Interlocked.Add(ref RunSortChecked, groups);
        }

        public bool Failed { get; private set; }

        public List<(ulong Address, uint Word, bool Clean, bool Table, int Base, int Node, ulong BaseAddress)> Reads => _reads;

        public Action<bool> TablePhase { get; }

        public GuestWordReader? Wrap(GuestWordReader? inner, bool clean)
        {
            if (inner is null)
                return null;
            if (clean)
            {
                _cleanReader = inner;
                return _recordCleanRead;
            }
            _reader = inner;
            return _recordRead;
        }

        // [local] SHARPEMU_MATERIALIZE_READ_MEMO=1: a word read again during the same walk returns the value the
        // first read returned (the walk reads descriptor words several times); every read is still recorded.
        internal static readonly bool ReadMemo = Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_READ_MEMO") == "1";
        private readonly Dictionary<ulong, uint> _memo = new();
        internal static long MemoHits, MemoMisses;

        private bool Read(GuestWordReader inner, ulong address, out uint word, bool clean)
        {
            var key = (address << 1) | (clean ? 1UL : 0UL);
            if (ReadMemo && _memo.TryGetValue(key, out word))
            {
                MemoHits++;
            }
            else
            {
                if (!inner(address, out word))
                {
                    Failed = true;
                    return false;
                }

                if (ReadMemo)
                {
                    MemoMisses++;
                    _memo[key] = word;
                }
            }

            _reads.Add((address, word, clean, _inTable, RuntimeValueEvaluator.CurrentReadBase, RuntimeValueEvaluator.CurrentReadNode, RuntimeValueEvaluator.CurrentReadBaseAddress));
            return true;
        }

        public ResidentGuestBytesReader? WrapResident(ResidentGuestBytesReader? inner)
        {
            if (inner is null)
                return null;
            _residentReader = inner;
            return _recordResidentRead;
        }

        private bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            if (!_residentReader!(address, destination, clean))
                return false;

            if (BlockMode != 0 && _blocksAllowed && destination.Length >= BlockMinBytes)
            {
                var words = destination.Length / sizeof(uint);
                var length = words * sizeof(uint);
                if (_blockBytes.Length < _blockBytesUsed + length)
                    Array.Resize(ref _blockBytes, Math.Max(_blockBytesUsed + length, _blockBytes.Length * 2));
                destination[..length].CopyTo(_blockBytes.AsSpan(_blockBytesUsed));
                _blocks.Add((address, _blockBytesUsed, words, clean, _inTable, RuntimeValueEvaluator.CurrentReadBase, RuntimeValueEvaluator.CurrentReadNode));
                _blockBytesUsed += length;
                _blockWords += words;
                return true;
            }

            for (var offset = 0; offset + sizeof(uint) <= destination.Length; offset += sizeof(uint))
                _reads.Add((address + (ulong)offset,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(destination[offset..]), clean, _inTable,
                    RuntimeValueEvaluator.CurrentReadBase, RuntimeValueEvaluator.CurrentReadNode, 0UL));
            return true;
        }

        // [local] SHARPEMU_RECORDER_BLOCKS (1 by default, =0 turns it off): on a miss, a resident read of BlockMinBytes or more (a store
        // table read in 64 KB chunks: ~520 k words per scan in camera moves) is kept as one block of bytes instead of a
        // 40-byte record per word, and Build lays each block into the entry at once: the words of a block are one
        // ascending run with the same clean, table, base and node, so the per-word steps of Build reduce to one range
        // decision, a fill and a copy. Blocks that overlap each other or a word read are expanded into word records
        // first and Build runs as before. =2 builds the entry both ways, keeps the per-word one and counts the entries
        // whose fields differ (block_mismatches).
        private static readonly int BlockMode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RECORDER_BLOCKS"), out var blockMode) ? blockMode : 1;
        private const int BlockMinBytes = 4096;
        internal static long BlockBuilds, BlockFallbacks, BlockMismatches, BlockChecked;
        private readonly List<(ulong Address, int Offset, int Words, bool Clean, bool Table, int Base, int Node)> _blocks = new();
        private byte[] _blockBytes = [];
        private int _blockBytesUsed;
        private int _blockWords;
        private bool _blocksAllowed;

        public void AllowBlocks() => _blocksAllowed = true;

        public int RecordedWords => _reads.Count + _blockWords;

        public Entry Build(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization)
        {
            if (_blocks.Count == 0)
                return BuildWords(plan, inputs, snapshot, specialization);
            var blocked = BuildBlocks(plan, inputs, snapshot, specialization);
            if (blocked is null)
                Interlocked.Increment(ref BlockFallbacks);
            else
                Interlocked.Increment(ref BlockBuilds);
            if (blocked is not null && BlockMode != 2)
                return blocked;
            ExpandBlocks();
            var words = BuildWords(plan, inputs, snapshot, specialization);
            if (blocked is not null)
            {
                Interlocked.Increment(ref BlockChecked);
                if (!SameEntry(words, blocked))
                    Interlocked.Increment(ref BlockMismatches);
            }

            return words;
        }

        private void ExpandBlocks()
        {
            foreach (var (address, offset, words, clean, table, readBase, node) in _blocks)
            {
                for (var word = 0; word < words; word++)
                    _reads.Add((address + (ulong)word * sizeof(uint),
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_blockBytes.AsSpan(offset + word * sizeof(uint))),
                        clean, table, readBase, node, 0UL));
            }

            _blocks.Clear();
            _blockWords = 0;
        }

        private static bool SameEntry(Entry left, Entry right)
        {
            if (!left.RangeAddresses.AsSpan().SequenceEqual(right.RangeAddresses) || !left.RangeOffsets.AsSpan().SequenceEqual(right.RangeOffsets) ||
                !left.RangeLengths.AsSpan().SequenceEqual(right.RangeLengths) || !left.RangeClean.AsSpan().SequenceEqual(right.RangeClean) ||
                !left.RangeBase.AsSpan().SequenceEqual(right.RangeBase) || left.ExactUserData != right.ExactUserData ||
                !left.WordTableOnly.AsSpan().SequenceEqual(right.WordTableOnly) || left.TableRefreshable != right.TableRefreshable ||
                !left.Bytes.AsSpan().SequenceEqual(right.Bytes) || !left.UserData.AsSpan().SequenceEqual(right.UserData) ||
                left.ShaderBase != right.ShaderBase || !Nullable.Equals(left.ComputeState, right.ComputeState))
                return false;
            if ((left.RangeReadBase is null) != (right.RangeReadBase is null) ||
                (left.RangeReadBase is not null && !left.RangeReadBase.AsSpan().SequenceEqual(right.RangeReadBase)))
                return false;
            if ((left.WordPatches is null) != (right.WordPatches is null))
                return false;
            if (left.WordPatches is not null)
            {
                if (left.WordPatches.Count != right.WordPatches!.Count)
                    return false;
                foreach (var (word, targets) in left.WordPatches)
                {
                    if (!right.WordPatches.TryGetValue(word, out var other) || !ReferenceEquals(targets, other))
                        return false;
                }
            }

            return true;
        }

        // The entry with the blocks laid in whole; null when a block overlaps another block or a word record.
        private Entry? BuildBlocks(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization)
        {
            var sortStarted = MaterializeSplitProfile.Start();
            var bigSort = RecordedWords > MaterializeSplitProfile.BigReads;
            SortReadsByAddress();
            MaterializeSplitProfile.Note(bigSort ? MaterializeSplitProfile.Kind.BigSort : MaterializeSplitProfile.Kind.Sort, sortStarted);
            _blocks.Sort(static (left, right) => left.Address.CompareTo(right.Address));
            var reads = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_reads);
            ulong previousEnd = 0;
            foreach (var block in _blocks)
            {
                if (block.Words == 0)
                    continue;
                var start = block.Address;
                var stop = block.Address + (ulong)block.Words * sizeof(uint);
                if (start < previousEnd)
                    return null;
                previousEnd = stop;
                // The first word record at or after the block's start must lie past its end.
                int low = 0, high = reads.Length;
                while (low < high)
                {
                    var middle = (low + high) >>> 1;
                    if (reads[middle].Address < start)
                        low = middle + 1;
                    else
                        high = middle;
                }

                if (low < reads.Length && reads[low].Address < stop)
                    return null;
            }

            // Count unique words and contiguous ranges, as BuildWords does, a block at a time.
            var rangeCount = 0;
            var wordCount = 0;
            ulong end = 0;
            var previous = ulong.MaxValue;
            var previousBase = UserDataUseAnalysis.UnknownBase;
            var previousBaseAddress = 0UL;
            var exactUserData = false;
            var readIndex = 0;
            var blockIndex = 0;
            while (readIndex < reads.Length || blockIndex < _blocks.Count)
            {
                if (blockIndex < _blocks.Count && (readIndex == reads.Length || _blocks[blockIndex].Address < reads[readIndex].Address))
                {
                    var block = _blocks[blockIndex++];
                    if (block.Words == 0)
                        continue;
                    exactUserData |= block.Base == UserDataUseAnalysis.UnknownBase;
                    if (wordCount == 0 || block.Address != end || block.Base != previousBase ||
                        (SplitByBaseAddress && previousBaseAddress != 0)) rangeCount++;
                    wordCount += block.Words;
                    previous = block.Address + (ulong)(block.Words - 1) * sizeof(uint);
                    previousBase = block.Base;
                    previousBaseAddress = 0;
                    end = block.Address + (ulong)block.Words * sizeof(uint);
                    continue;
                }

                var read = reads[readIndex++];
                exactUserData |= read.Base == UserDataUseAnalysis.UnknownBase;
                if (read.Address == previous) continue;
                if (wordCount == 0 || read.Address != end || read.Base != previousBase ||
                    (SplitByBaseAddress && read.BaseAddress != previousBaseAddress)) rangeCount++;
                wordCount++;
                previous = read.Address;
                previousBase = read.Base;
                previousBaseAddress = read.BaseAddress;
                end = read.Address + sizeof(uint);
            }

            var addresses = new ulong[rangeCount];
            var offsets = new int[rangeCount];
            var lengths = new int[rangeCount];
            var clean = new bool[rangeCount];
            var bases = new int[rangeCount];
            var readBases = SplitByBaseAddress ? new ulong[rangeCount] : null;
            var bytes = new byte[wordCount * sizeof(uint)];
            var tableOnly = new bool[wordCount];
            var patchableReads = plan.UserDataUse.PatchableReads;
            Dictionary<int, UserDataUseAnalysis.PatchTarget[]>? patches = null;
            var rangeIndex = -1;
            var wordIndex = -1;
            end = 0;
            previous = ulong.MaxValue;
            previousBase = UserDataUseAnalysis.UnknownBase;
            previousBaseAddress = 0;
            readIndex = 0;
            blockIndex = 0;
            while (readIndex < reads.Length || blockIndex < _blocks.Count)
            {
                if (blockIndex < _blocks.Count && (readIndex == reads.Length || _blocks[blockIndex].Address < reads[readIndex].Address))
                {
                    var block = _blocks[blockIndex++];
                    if (block.Words == 0)
                        continue;
                    var first = wordIndex + 1;
                    if (block.Node != RuntimeValueEvaluator.NoReadNode && patchableReads.TryGetValue(block.Node, out var blockTargets))
                    {
                        patches ??= new Dictionary<int, UserDataUseAnalysis.PatchTarget[]>();
                        for (var word = 0; word < block.Words; word++)
                            patches[first + word] = blockTargets;
                    }

                    if (rangeIndex < 0 || block.Address != end || block.Base != previousBase ||
                        (readBases is not null && previousBaseAddress != 0))
                    {
                        rangeIndex++;
                        addresses[rangeIndex] = block.Address;
                        offsets[rangeIndex] = first * sizeof(uint);
                        bases[rangeIndex] = block.Base;
                        if (readBases is not null)
                            readBases[rangeIndex] = 0;
                    }

                    previousBase = block.Base;
                    previousBaseAddress = 0;
                    lengths[rangeIndex] += block.Words * sizeof(uint);
                    clean[rangeIndex] |= block.Clean;
                    tableOnly.AsSpan(first, block.Words).Fill(block.Table);
                    _blockBytes.AsSpan(block.Offset, block.Words * sizeof(uint)).CopyTo(bytes.AsSpan(first * sizeof(uint)));
                    wordIndex += block.Words;
                    previous = block.Address + (ulong)(block.Words - 1) * sizeof(uint);
                    end = block.Address + (ulong)block.Words * sizeof(uint);
                    continue;
                }

                var (address, wordValue, wordClean, table, readBase, node, baseAddress) = reads[readIndex++];
                if (address == previous)
                {
                    clean[rangeIndex] |= wordClean;
                    if (readBases is not null && readBases[rangeIndex] != baseAddress)
                        readBases[rangeIndex] = ConflictingBaseAddress;
                    tableOnly[wordIndex] &= table;
                    if (bases[rangeIndex] != readBase)
                        bases[rangeIndex] = UserDataUseAnalysis.UnknownBase;
                    if (patches is not null && patches.TryGetValue(wordIndex, out var existing) &&
                        (node == RuntimeValueEvaluator.NoReadNode || !patchableReads.TryGetValue(node, out var again) || !ReferenceEquals(again, existing)))
                        patches.Remove(wordIndex);
                    continue;
                }

                wordIndex++;
                if (node != RuntimeValueEvaluator.NoReadNode && patchableReads.TryGetValue(node, out var targets))
                    (patches ??= new Dictionary<int, UserDataUseAnalysis.PatchTarget[]>())[wordIndex] = targets;
                if (rangeIndex < 0 || address != end || readBase != previousBase ||
                    (readBases is not null && baseAddress != previousBaseAddress))
                {
                    rangeIndex++;
                    addresses[rangeIndex] = address;
                    offsets[rangeIndex] = wordIndex * sizeof(uint);
                    bases[rangeIndex] = readBase;
                    if (readBases is not null)
                        readBases[rangeIndex] = baseAddress;
                }

                previousBase = readBase;
                previousBaseAddress = baseAddress;
                lengths[rangeIndex] += sizeof(uint);
                clean[rangeIndex] |= wordClean;
                tableOnly[wordIndex] = table;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(wordIndex * sizeof(uint)), wordValue);
                previous = address;
                end = address + sizeof(uint);
            }

            return NewEntry(plan, inputs, snapshot, specialization, addresses, offsets, lengths, clean, bases, readBases, exactUserData,
                tableOnly, patches, bytes);
        }

        private static Entry NewEntry(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization, ulong[] addresses, int[] offsets, int[] lengths, bool[] clean, int[] bases,
            ulong[]? readBases, bool exactUserData, bool[] tableOnly, Dictionary<int, UserDataUseAnalysis.PatchTarget[]>? patches,
            byte[] bytes)
        {
            var userData = new uint[inputs.UserData.Count];
            for (var index = 0; index < userData.Length; index++)
                userData[index] = inputs.UserData[index];
            return new Entry
            {
                Plan = plan,
                UserData = userData,
                ShaderBase = inputs.ShaderBase,
                ComputeState = inputs.ComputeState,
                RangeAddresses = addresses,
                RangeOffsets = offsets,
                RangeLengths = lengths,
                RangeClean = clean,
                RangeBase = bases,
                RangeReadBase = readBases,
                ExactUserData = exactUserData,
                WordTableOnly = tableOnly,
                WordPatches = patches is { Count: > 0 } ? patches : null,
                TableRefreshable = snapshot.FlattenedResourceTable.Length ==
                    plan.TableReads.Count + plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount,
                Bytes = bytes,
                Snapshot = snapshot,
                Specialization = specialization,
            };
        }

        private Entry BuildWords(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization)
        {
            var sortStarted = MaterializeSplitProfile.Start(); // [local]
            var bigSort = _reads.Count > MaterializeSplitProfile.BigReads;
            SortReadsByAddress();
            MaterializeSplitProfile.Note(bigSort ? MaterializeSplitProfile.Kind.BigSort : MaterializeSplitProfile.Kind.Sort, sortStarted);
            // Count unique words and contiguous ranges first, then fill the
            // retained arrays directly. Temporary lists previously duplicated
            // every recorded byte and range on each cache miss.
            var rangeCount = 0;
            var wordCount = 0;
            ulong end = 0;
            var previous = ulong.MaxValue;
            var previousBase = UserDataUseAnalysis.UnknownBase;
            var previousBaseAddress = 0UL;
            var exactUserData = false;
            foreach (var read in _reads)
            {
                exactUserData |= read.Base == UserDataUseAnalysis.UnknownBase;
                if (read.Address == previous) continue;
                if (wordCount == 0 || read.Address != end || read.Base != previousBase ||
                    (SplitByBaseAddress && read.BaseAddress != previousBaseAddress)) rangeCount++;
                wordCount++;
                previous = read.Address;
                previousBase = read.Base;
                previousBaseAddress = read.BaseAddress;
                end = read.Address + sizeof(uint);
            }

            var addresses = new ulong[rangeCount];
            var offsets = new int[rangeCount];
            var lengths = new int[rangeCount];
            var clean = new bool[rangeCount];
            var bases = new int[rangeCount];
            var readBases = SplitByBaseAddress ? new ulong[rangeCount] : null;
            var bytes = new byte[wordCount * sizeof(uint)];
            var tableOnly = new bool[wordCount];
            var patchableReads = plan.UserDataUse.PatchableReads;
            Dictionary<int, UserDataUseAnalysis.PatchTarget[]>? patches = null;
            var rangeIndex = -1;
            var wordIndex = -1;
            end = 0;
            previous = ulong.MaxValue;
            previousBase = UserDataUseAnalysis.UnknownBase;
            previousBaseAddress = 0;
            foreach (var (address, word, wordClean, table, readBase, node, baseAddress) in _reads)
            {
                if (address == previous)
                {
                    clean[rangeIndex] |= wordClean;
                    if (readBases is not null && readBases[rangeIndex] != baseAddress)
                        readBases[rangeIndex] = ConflictingBaseAddress;
                    tableOnly[wordIndex] &= table;
                    // The same word read through two bases cannot follow either: keep it in place.
                    if (bases[rangeIndex] != readBase)
                        bases[rangeIndex] = UserDataUseAnalysis.UnknownBase;
                    // A word read by two nodes is fixed unless both are the same patchable read.
                    if (patches is not null && patches.TryGetValue(wordIndex, out var existing) &&
                        (node == RuntimeValueEvaluator.NoReadNode || !patchableReads.TryGetValue(node, out var again) || !ReferenceEquals(again, existing)))
                        patches.Remove(wordIndex);
                    continue;
                }

                wordIndex++;
                if (node != RuntimeValueEvaluator.NoReadNode && patchableReads.TryGetValue(node, out var targets))
                    (patches ??= new Dictionary<int, UserDataUseAnalysis.PatchTarget[]>())[wordIndex] = targets;
                if (rangeIndex < 0 || address != end || readBase != previousBase ||
                    (readBases is not null && baseAddress != previousBaseAddress))
                {
                    rangeIndex++;
                    addresses[rangeIndex] = address;
                    offsets[rangeIndex] = wordIndex * sizeof(uint);
                    bases[rangeIndex] = readBase;
                    if (readBases is not null)
                        readBases[rangeIndex] = baseAddress;
                }

                previousBase = readBase;
                previousBaseAddress = baseAddress;
                lengths[rangeIndex] += sizeof(uint);
                clean[rangeIndex] |= wordClean;
                tableOnly[wordIndex] = table;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(wordIndex * sizeof(uint)), word);
                previous = address;
                end = address + sizeof(uint);
            }

            return NewEntry(plan, inputs, snapshot, specialization, addresses, offsets, lengths, clean, bases, readBases, exactUserData,
                tableOnly, patches, bytes);
        }
    }
}
