// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// Copies guest bytes only when they are already resident on the CPU side. It never
// synchronizes: a range the GPU may still own, or that a clean read would refuse,
// returns false so the caller falls back to a full materialization.
public delegate bool ResidentGuestBytesReader(ulong address, Span<byte> destination, bool clean);

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

    public ResourceMaterializationCache(int generationCapacity = 16384)
    {
        _generationCapacity = Math.Max(1, generationCapacity);
    }

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
            $"[PERF][RESOURCE_CACHE] hits={hits} misses={misses} stale={stale} stale_unreadable={staleUnreadable} refreshes={refreshes} uncacheable={uncacheable} hit_rate={(total == 0 ? 0 : hits * 100.0 / total):F1}% {RawReadPrefetch.TakeReport()} read_memo_hits={Interlocked.Exchange(ref ReadRecorder.MemoHits, 0)} read_memo_misses={Interlocked.Exchange(ref ReadRecorder.MemoMisses, 0)}{TakeRebaseReport()}");
    }

    public bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure)
    {
        CurrentPlanHash = plan.Hash; // [local] for overlay diagnostics
        var key = KeyOf(plan, inputs);
        var found = TryFind(key, plan, inputs, out var cached);
        Entry? shifted = null; // [local] SHARPEMU_MATERIALIZE_REBASE
        if (found)
        {
            if (StaleLog) _lastStaleReason = "nomatch";
            var unreadable = false;
            Entry? previous = null;
            for (var variant = cached; variant is not null; previous = variant, variant = variant.Next)
            {
                if (!variant.Matches(plan, inputs))
                    continue;
                var validated = Validate(variant, inputs, residentReader, out var variantUnreadable);
                if (validated && StaleLog) _lastStaleReason = "snapshot";
                if (validated && variant.SnapshotFor(inputs, _patches) is { } hit)
                {
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

                unreadable |= variantUnreadable;
            }

            if (cached.UserDataEquals(inputs) && TryRefreshTable(key, cached, plan, inputs, residentReader, out var refreshed))
            {
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
                    if (variant.Matches(plan, inputs))
                        shifted = TryShift(variant, inputs, residentReader);
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

        Misses++;
        Interlocked.Increment(ref _totalMisses);
        var recorder = TakeRecorder();
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
            if (!ResourceMaterializer.Materialize(plan, recording, ref snapshot, ref specialization, out failure))
            {
                if (shifted is not null)
                    NoteRebaseVerdict(plan.Hash, shifted, null, null, null, "walk_failed");
                return false;
            }

            if (recorder.Failed)
            {
                if (shifted is not null)
                    NoteRebaseVerdict(plan.Hash, shifted, snapshot, specialization, null, null);
                Uncacheable++;
                Interlocked.Increment(ref _totalUncacheable);
                return true;
            }

            var built = recorder.Build(plan, inputs, snapshot, specialization);
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
                NoteMiss(plan.Hash, found, recorder.Reads.Count, System.Diagnostics.Stopwatch.GetTimestamp() - missStarted);
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

    private static string? SnapshotDifference(ResourceSnapshot shifted, ResourceSnapshot walked)
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

        var current = new byte[cached.Bytes.Length];
        for (var index = 0; index < cached.RangeAddresses.Length; index++)
        {
            if (!residentReader(cached.RangeAddresses[index], current.AsSpan(cached.RangeOffsets[index], cached.RangeLengths[index]), cached.RangeClean[index]))
                return false;
        }

        var changed = false;
        for (var offset = 0; offset < current.Length; offset += sizeof(uint))
        {
            if (current.AsSpan(offset, sizeof(uint)).SequenceEqual(cached.Bytes.AsSpan(offset, sizeof(uint))))
                continue;
            if (!cached.WordTableOnly[offset / sizeof(uint)])
                return false;
            changed = true;
        }

        if (!changed)
            return false;

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

    private bool Validate(Entry entry, ResourceRuntimeInputs inputs, ResidentGuestBytesReader residentReader, out bool unreadable)
    {
        unreadable = false;
        _patches.Clear();
        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            if (!entry.TryRebase(index, inputs, out var address))
            {
                if (StaleLog) _lastStaleReason = $"rebase:r{index}:base{entry.RangeBase[index]}";
                return false;
            }
            var length = entry.RangeLengths[index];
            if (_scratch.Length < length)
                _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
            var current = _scratch.AsSpan(0, length);
            if (!residentReader(address, current, entry.RangeClean[index]))
            {
                unreadable = true;
                if (StaleLog) _lastStaleReason = $"unreadable:r{index}:clean{entry.RangeClean[index]}";
                return false;
            }

            var cached = entry.Bytes.AsSpan(entry.RangeOffsets[index], length);
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
            for (var offset = 0; offset < length; offset += sizeof(uint))
            {
                var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current[offset..]);
                var was = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cached[offset..]);
                if (word == was)
                    continue;
                var wordIndex = (entry.RangeOffsets[index] + offset) / sizeof(uint);
                if (!entry.WordPatches.TryGetValue(wordIndex, out var targets) || !Entry.PatchAllowed(targets, word, was))
                    return false;
                _patches.Add((wordIndex, word));
            }
        }

        return true;
    }

    private sealed class Entry
    {
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

            Array.Sort(_sortKeys, _sortOrder, 0, count);
            for (var index = 0; index < count; index++)
                _sortScratch[index] = reads[_sortOrder[index]];
            _sortScratch.AsSpan(0, count).CopyTo(reads);
            if (_sortKeys.Length > KeptReads)
            {
                _sortKeys = [];
                _sortOrder = [];
                _sortScratch = [];
            }
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

            for (var offset = 0; offset + sizeof(uint) <= destination.Length; offset += sizeof(uint))
                _reads.Add((address + (ulong)offset,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(destination[offset..]), clean, _inTable,
                    RuntimeValueEvaluator.CurrentReadBase, RuntimeValueEvaluator.CurrentReadNode, 0UL));
            return true;
        }

        public Entry Build(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization)
        {
            SortReadsByAddress();
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
    }
}
