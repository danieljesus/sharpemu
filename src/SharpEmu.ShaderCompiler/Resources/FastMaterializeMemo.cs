// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.ShaderCompiler.Resources;

// [local] SHARPEMU_FAST_MATERIALIZE_MEMO (=1 on, =2 verify, off by default): a plan the fast path materializes (no cache,
// MaterializeFast) runs its program on every draw and dispatch. A run is a function of the user data words it reads, the
// shader base, the compute state and the guest words it reads, nothing else: the memo remembers, per thread and plan,
// the last MemoEntries runs with the user data words they read (index and value), and every guest read they made, in
// order, with its answer. A later run whose read user data words are equal replays those reads through the same readers,
// in the same order: while every answer is equal the program would have issued the same reads (each read's address is a
// function of the user data and the answers before it), so it would produce the same snapshot and specialization, which
// the memo returns with the draw's own user data. The first different answer ends the replay and the program runs (the
// reads already replayed are read again, as any re-read is). The replay pays the reads but not the program's steps, its
// output assembly or the specialization memo search. A plan with a frontend normalizer is not memoized (the normalizers
// can depend on other state). =2 runs the program on every hit as well and counts the ones that differ
// ([PERF][FAST_MEMO] in the fast report). Settable for tests (per async flow).
// [local] Round 2 (open world, cv1): 22 % hits over all threads, and the misses paid the recording (a wrapped reader per
// read, a stored entry of six arrays per run), so the switch did not pay. A plan whose stored runs hit less than one time
// in MemoGateRatio over MemoGateStores stores is no longer recorded for its next calls (MemoSkipFirst, doubling up to
// MemoSkipMax while it keeps failing): those runs take the plain path at its own cost. An entry's arrays are reused
// when its slot is overwritten. LastFastMemoAction tells the caller what the memo did (SHARPEMU_DRAWCOST_PROBE counts).
public static partial class ResourceMaterializer
{
    public static int FastMemoMode
    {
        get => FastMemoOverride.Value ?? FastMemoDefault;
        set => FastMemoOverride.Value = value;
    }

    private static readonly int FastMemoDefault =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FAST_MATERIALIZE_MEMO"), out var fastMemo) ? fastMemo : 0;
    private static readonly AsyncLocal<int?> FastMemoOverride = new();

    private const int MemoEntries = 4;
    private const int MemoGateStores = 32;
    private const int MemoGateRatio = 4;
    private const int MemoSkipFirst = 256;
    private const int MemoSkipMax = 16384;

    public enum FastMemoAction : byte { None, Hit, Recorded, ReplayMiss, Skipped, Nested, Refused }

    [ThreadStatic] private static FastMemoAction _lastFastMemoAction;

    // [local] What the memo did on this thread's last MaterializeFast (None when it was off or not asked).
    public static FastMemoAction LastFastMemoAction => _lastFastMemoAction;

    private static readonly ThreadCounter _fastMemoSkipped = new();
    private const int MemoMaxReads = 512;
    private const int MemoMaxBytes = 16384;

    private static readonly ThreadCounter _fastMemoHits = new();
    private static readonly ThreadCounter _fastMemoMisses = new();
    private static readonly ThreadCounter _fastMemoReplayMisses = new();
    private static readonly ThreadCounter _fastMemoStored = new();
    private static readonly ThreadCounter _fastMemoRefused = new();
    private static long _fastMemoVerified, _fastMemoMismatches;
    private static int _fastMemoMismatchLogs;

    internal static string TakeFastMemoReport() => FormattableString.Invariant(
        $"fast_memo_hits={_fastMemoHits.Take()} fast_memo_misses={_fastMemoMisses.Take()} fast_memo_replay_misses={_fastMemoReplayMisses.Take()} ") +
        FormattableString.Invariant(
            $"fast_memo_stored={_fastMemoStored.Take()} fast_memo_refused={_fastMemoRefused.Take()} fast_memo_skipped={_fastMemoSkipped.Take()} fast_memo_verified={Interlocked.Exchange(ref _fastMemoVerified, 0)} fast_memo_mismatches={Interlocked.Read(ref _fastMemoMismatches)}");

    internal static long FastMemoMismatches => Interlocked.Read(ref _fastMemoMismatches);

    private static bool Memoizable(ShaderResourcePlan plan) => plan.NormalizeBufferDescriptor is null && plan.NormalizeImageDescriptor is null;

    private enum MemoReadKind : byte { Word, CleanWord, Resident }

    private readonly record struct MemoRead(MemoReadKind Kind, ulong Address, int Offset, int Length, bool Clean, bool Ok, uint Word);

    // Overwritten in place when its slot comes round again (the arrays grow, never shrink; the counts say how much is used).
    private sealed class MemoEntry
    {
        public int UserDataCount;
        public int[] UsedIndices = [];
        public uint[] UsedValues = [];
        public int UsedCount;
        public ulong ShaderBase;
        public ComputeSelectorState? ComputeState;
        public bool ReadsClean;
        public MemoRead[] Reads = [];
        public int ReadCount;
        public byte[] Bytes = [];
        public ResourceSnapshot Snapshot = null!;
        public ResourceSpecialization Specialization = null!;
    }

    private sealed class MemoSet
    {
        public readonly MemoEntry?[] Entries = new MemoEntry?[MemoEntries];
        public int Next;
        // The gate: stores and hits since the last verdict, and the calls left to skip.
        public int Stores;
        public int Hits;
        public int Skip;
        public int SkipPeriod = MemoSkipFirst;
    }

    [ThreadStatic] private static Dictionary<ShaderResourcePlan, MemoSet>? _memoSets;
    [ThreadStatic] private static MemoRecorder? _memoRecorder;
    [ThreadStatic] private static byte[]? _memoReplayBytes;

    // Records the user data words and guest reads of one program run; taken out of its slot while in use (a guest read can
    // materialize another draw on this thread).
    private sealed class MemoRecorder : IReadOnlyList<uint>
    {
        private readonly GuestWordReader _readWord;
        private readonly GuestWordReader _readCleanWord;
        private readonly ResidentGuestBytesReader _readResident;
        private ResourceRuntimeInputs? _inner;
        private ResourceRuntimeInputs? _wrapped;
        public IReadOnlyList<uint> UserData = [];
        public ulong[] Used = new ulong[4];
        public readonly List<MemoRead> Reads = new();
        public byte[] Bytes = new byte[256];
        public int ByteCount;
        public bool Overflowed;

        public MemoRecorder()
        {
            _readWord = ReadWord;
            _readCleanWord = ReadCleanWord;
            _readResident = ReadResident;
        }

        // The inputs the run sees: the caller's, with this recorder in front of the user data and the readers. The caller's
        // inputs object is refilled per lookup (one per thread), so the wrapper is rebuilt only when another one comes.
        public ResourceRuntimeInputs Wrap(ResourceRuntimeInputs inputs)
        {
            if (!ReferenceEquals(_inner, inputs) || _wrapped is null)
            {
                _inner = inputs;
                _wrapped = new ResourceRuntimeInputs
                {
                    ReadMemory = inputs.ReadMemory is null ? null : _readWord,
                    ReadCleanMemory = inputs.ReadCleanMemory is null ? null : _readCleanWord,
                    TraceImageCandidate = inputs.TraceImageCandidate,
                    TablePhase = inputs.TablePhase,
                    ReadResidentMemory = inputs.ReadResidentMemory is null ? null : _readResident,
                    CompareTableChunk = inputs.CompareTableChunk,
                    ReadsClean = inputs.ReadsClean,
                    SynchronizeRanges = inputs.SynchronizeRanges,
                    SynchronizeTableRange = inputs.SynchronizeTableRange,
                };
            }

            UserData = inputs.UserData;
            Array.Clear(Used);
            Reads.Clear();
            ByteCount = 0;
            Overflowed = false;
            _wrapped.UserData = this;
            _wrapped.ShaderBase = inputs.ShaderBase;
            _wrapped.ComputeState = inputs.ComputeState;
            return _wrapped;
        }

        public void Release()
        {
            UserData = [];
            if (_wrapped is not null)
                _wrapped.UserData = [];
        }

        public uint this[int index]
        {
            get
            {
                var value = UserData[index];
                if ((index >> 6) >= Used.Length)
                    Array.Resize(ref Used, (index >> 6) + 1);
                Used[index >> 6] |= 1UL << (index & 63);
                return value;
            }
        }

        public int Count => UserData.Count;

        public IEnumerator<uint> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private bool ReadWord(ulong address, out uint word)
        {
            var ok = _inner!.ReadMemory!(address, out word);
            Note(new MemoRead(MemoReadKind.Word, address, 0, 0, false, ok, ok ? word : 0));
            return ok;
        }

        private bool ReadCleanWord(ulong address, out uint word)
        {
            var ok = _inner!.ReadCleanMemory!(address, out word);
            Note(new MemoRead(MemoReadKind.CleanWord, address, 0, 0, false, ok, ok ? word : 0));
            return ok;
        }

        private bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            var ok = _inner!.ReadResidentMemory!(address, destination, clean);
            var offset = ByteCount;
            if (ok)
            {
                if (ByteCount + destination.Length > MemoMaxBytes)
                {
                    Overflowed = true;
                }
                else
                {
                    if (Bytes.Length < ByteCount + destination.Length)
                        Array.Resize(ref Bytes, Math.Max(Bytes.Length * 2, ByteCount + destination.Length));
                    destination.CopyTo(Bytes.AsSpan(ByteCount));
                    ByteCount += destination.Length;
                }
            }

            Note(new MemoRead(MemoReadKind.Resident, address, offset, destination.Length, clean, ok, 0));
            return ok;
        }

        private void Note(MemoRead read)
        {
            if (Reads.Count >= MemoMaxReads)
            {
                Overflowed = true;
                return;
            }

            Reads.Add(read);
        }
    }

    private static bool SameUsedUserData(MemoEntry entry, ResourceRuntimeInputs inputs)
    {
        var userData = inputs.UserData;
        if (userData.Count != entry.UserDataCount || entry.ShaderBase != inputs.ShaderBase || entry.ReadsClean != inputs.ReadsClean ||
            !Nullable.Equals(entry.ComputeState, inputs.ComputeState))
            return false;
        if (userData is uint[] words)
        {
            for (var index = 0; index < entry.UsedCount; index++)
                if (words[entry.UsedIndices[index]] != entry.UsedValues[index])
                    return false;
            return true;
        }

        for (var index = 0; index < entry.UsedCount; index++)
            if (userData[entry.UsedIndices[index]] != entry.UsedValues[index])
                return false;
        return true;
    }

    // Replays the entry's reads through the caller's readers, in order; false at the first different answer.
    private static bool Replay(MemoEntry entry, ResourceRuntimeInputs inputs)
    {
        for (var readIndex = 0; readIndex < entry.ReadCount; readIndex++)
        {
            var read = entry.Reads[readIndex];
            switch (read.Kind)
            {
                case MemoReadKind.Word:
                case MemoReadKind.CleanWord:
                {
                    var reader = read.Kind == MemoReadKind.Word ? inputs.ReadMemory : inputs.ReadCleanMemory;
                    if (reader is null)
                        return false;
                    var ok = reader(read.Address, out var word);
                    if (ok != read.Ok || (ok && word != read.Word))
                        return false;
                    break;
                }
                default:
                {
                    if (inputs.ReadResidentMemory is not { } resident)
                        return false;
                    var bytes = _memoReplayBytes is { } scratch && scratch.Length >= read.Length
                        ? scratch : _memoReplayBytes = new byte[Math.Max(read.Length, 256)];
                    _memoReplayBytes = null; // a nested materialization takes its own
                    try
                    {
                        var span = bytes.AsSpan(0, read.Length);
                        var ok = resident(read.Address, span, read.Clean);
                        if (ok != read.Ok || (ok && !span.SequenceEqual(entry.Bytes.AsSpan(read.Offset, read.Length))))
                            return false;
                    }
                    finally
                    {
                        _memoReplayBytes = bytes;
                    }

                    break;
                }
            }
        }

        return true;
    }

    // The memoized result for this draw: the entry's snapshot with the draw's own user data and origin.
    private static ResourceSnapshot SnapshotFor(MemoEntry entry, ShaderResourcePlan plan, ResourceRuntimeInputs inputs) => new()
    {
        Buffers = entry.Snapshot.Buffers,
        Images = entry.Snapshot.Images,
        Samplers = entry.Snapshot.Samplers,
        FlattenedResourceTable = entry.Snapshot.FlattenedResourceTable,
        UserData = inputs.UserData is uint[] array ? array : inputs.UserData.ToArray(),
        DeviceAddressRanges = entry.Snapshot.DeviceAddressRanges,
        MipStatistics = entry.Snapshot.MipStatistics,
        Origin = new FastOrigin(plan, inputs),
    };

    private static bool TryFastMemoHit(MemoSet? set, ResourceRuntimeInputs inputs, out MemoEntry? hit)
    {
        hit = null;
        if (set is null)
        {
            _fastMemoMisses.Increment();
            return false;
        }

        var replays = 0;
        for (var offset = 0; offset < MemoEntries; offset++)
        {
            // Newest first.
            var entry = set.Entries[(set.Next - 1 - offset + MemoEntries * 2) % MemoEntries];
            if (entry is null || !SameUsedUserData(entry, inputs))
                continue;
            if (Replay(entry, inputs))
            {
                hit = entry;
                set.Hits++;
                _fastMemoHits.Increment();
                return true;
            }

            // Each replay reads again what the program will read; one more is the bound.
            if (++replays == 2)
                break;
        }

        if (replays != 0)
            _fastMemoReplayMisses.Increment();
        else
            _fastMemoMisses.Increment();
        return false;
    }

    private static MemoSet? MemoSetOf(ShaderResourcePlan plan) =>
        _memoSets is { } sets && sets.TryGetValue(plan, out var set) ? set : null;

    private static MemoSet CreateMemoSet(ShaderResourcePlan plan)
    {
        var sets = _memoSets ??= new Dictionary<ShaderResourcePlan, MemoSet>(ReferenceEqualityComparer.Instance);
        if (sets.Count >= 4096)
            sets.Clear();
        var set = new MemoSet();
        sets.Add(plan, set);
        return set;
    }

    private static void StoreFastMemo(MemoSet set, MemoRecorder recorder, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
        ResourceSpecialization specialization)
    {
        if (recorder.Overflowed)
        {
            _fastMemoRefused.Increment();
            _lastFastMemoAction = FastMemoAction.Refused;
            return;
        }

        var entry = set.Entries[set.Next] ??= new MemoEntry();
        var userData = inputs.UserData;
        var used = 0;
        for (var index = 0; index < userData.Count; index++)
            if ((index >> 6) < recorder.Used.Length && (recorder.Used[index >> 6] & (1UL << (index & 63))) != 0)
                used++;
        if (entry.UsedIndices.Length < used)
        {
            entry.UsedIndices = new int[used];
            entry.UsedValues = new uint[used];
        }

        var cursor = 0;
        for (var index = 0; index < userData.Count; index++)
        {
            if ((index >> 6) >= recorder.Used.Length || (recorder.Used[index >> 6] & (1UL << (index & 63))) == 0)
                continue;
            entry.UsedIndices[cursor] = index;
            entry.UsedValues[cursor++] = userData[index];
        }

        entry.UsedCount = used;
        entry.UserDataCount = userData.Count;
        entry.ShaderBase = inputs.ShaderBase;
        entry.ComputeState = inputs.ComputeState;
        entry.ReadsClean = inputs.ReadsClean;
        if (entry.Reads.Length < recorder.Reads.Count)
            entry.Reads = new MemoRead[Math.Max(recorder.Reads.Count, 8)];
        recorder.Reads.CopyTo(entry.Reads);
        entry.ReadCount = recorder.Reads.Count;
        if (entry.Bytes.Length < recorder.ByteCount)
            entry.Bytes = new byte[recorder.ByteCount];
        recorder.Bytes.AsSpan(0, recorder.ByteCount).CopyTo(entry.Bytes);
        entry.Snapshot = snapshot;
        entry.Specialization = specialization;
        set.Next = (set.Next + 1) % MemoEntries;
        _fastMemoStored.Increment();
        _lastFastMemoAction = FastMemoAction.Recorded;
        if (++set.Stores < MemoGateStores)
            return;
        // The verdict: a plan whose runs do not repeat stops paying the recording for a while.
        if (set.Hits * MemoGateRatio < set.Stores)
        {
            set.Skip = set.SkipPeriod;
            set.SkipPeriod = Math.Min(set.SkipPeriod * 2, MemoSkipMax);
        }
        else
        {
            set.SkipPeriod = MemoSkipFirst;
        }

        set.Stores = set.Hits = 0;
    }

    // MaterializeFast's program path with the memo in front: a hit returns the remembered result, a miss runs the program
    // with its reads recorded and remembers the result.
    private static bool MaterializeFastMemoized(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, MaterializationProgram.Output output,
        ref ResourceSnapshot snapshot, ref ResourceSpecialization specialization, out ResourceMaterializationFailure failure, out bool ran)
    {
        ran = true;
        failure = ResourceMaterializationFailure.Other;
        var mode = FastMemoMode;
        var set = MemoSetOf(plan);
        if (set is { Skip: > 0 })
        {
            // The gate: this plan's runs did not repeat; the plain path, no recording.
            set.Skip--;
            _fastMemoSkipped.Increment();
            if (!EvaluateInto(plan, inputs, output))
            {
                ran = false;
                return false;
            }

            NoteProgramRun();
            _lastFastMemoAction = FastMemoAction.Skipped;
            return MaterializeFromOutput(plan, inputs, output, ref snapshot, ref specialization, out failure);
        }

        _lastFastMemoAction = FastMemoAction.ReplayMiss;
        if (TryFastMemoHit(set, inputs, out var hit))
        {
            _lastFastMemoAction = FastMemoAction.Hit;
            if (mode != 2)
            {
                snapshot = SnapshotFor(hit!, plan, inputs);
                specialization = hit!.Specialization;
                _lastFastOutcome = MaterializationTally.Outcome.FastMemoHit;
                failure = ResourceMaterializationFailure.None;
                return true;
            }

            // =2: the program's own answer, compared with the memo's.
            var checkedSnapshot = snapshot;
            var checkedSpecialization = specialization;
            if (!EvaluateInto(plan, inputs, output))
            {
                ran = false;
                return false;
            }

            NoteProgramRun();

            var result = MaterializeFromOutput(plan, inputs, output, ref checkedSnapshot, ref checkedSpecialization, out failure);
            if (result)
                NoteFastMemoVerify(plan, SnapshotFor(hit!, plan, inputs), hit!.Specialization, checkedSnapshot, checkedSpecialization);
            snapshot = checkedSnapshot;
            specialization = checkedSpecialization;
            return result;
        }

        var recorder = _memoRecorder;
        _memoRecorder = null;
        if (recorder is null && _memoRecorderInUse)
        {
            // A nested materialization: no recording.
            if (!EvaluateInto(plan, inputs, output))
            {
                ran = false;
                return false;
            }

            NoteProgramRun();
            _lastFastMemoAction = FastMemoAction.Nested;
            return MaterializeFromOutput(plan, inputs, output, ref snapshot, ref specialization, out failure);
        }

        recorder ??= new MemoRecorder();
        _memoRecorderInUse = true;
        try
        {
            var recording = recorder.Wrap(inputs);
            if (!EvaluateInto(plan, recording, output))
            {
                ran = false;
                return false;
            }

            NoteProgramRun();

            if (!MaterializeFromOutput(plan, inputs, output, ref snapshot, ref specialization, out failure))
                return false;
            StoreFastMemo(MemoSetOf(plan) ?? CreateMemoSet(plan), recorder, inputs, snapshot, specialization);
            return true;
        }
        finally
        {
            recorder.Release();
            _memoRecorderInUse = false;
            _memoRecorder = recorder;
        }
    }

    [ThreadStatic] private static bool _memoRecorderInUse;

    // The counters and outcome MaterializeFast notes before MaterializeFromOutput.
    private static void NoteProgramRun()
    {
        _programRuns.Increment();
        _fastMaterializations.Increment();
        _lastFastOutcome = MaterializationTally.Outcome.FastMiss;
    }

    private static void NoteFastMemoVerify(ShaderResourcePlan plan, ResourceSnapshot memo, ResourceSpecialization memoSpecialization,
        ResourceSnapshot fresh, ResourceSpecialization freshSpecialization)
    {
        Interlocked.Increment(ref _fastMemoVerified);
        var difference = !memoSpecialization.Equals(freshSpecialization) ? "specialization"
            : !SameWordArrays(memo.Buffers, fresh.Buffers) ? "buffers"
            : !SameWordArrays(memo.Images, fresh.Images) ? "images"
            : !SameWordArrays(memo.Samplers, fresh.Samplers) ? "samplers"
            : !memo.FlattenedResourceTable.AsSpan().SequenceEqual(fresh.FlattenedResourceTable) ? "table"
            : !memo.DeviceAddressRanges.AsSpan().SequenceEqual(fresh.DeviceAddressRanges) ? "ranges"
            : !memo.UserData.AsSpan().SequenceEqual(fresh.UserData) ? "user_data"
            : !(memo.MipStatistics ?? []).AsSpan().SequenceEqual(fresh.MipStatistics ?? []) ? "mip_statistics"
            : null;
        if (difference is null)
            return;
        Interlocked.Increment(ref _fastMemoMismatches);
        if (Interlocked.Increment(ref _fastMemoMismatchLogs) <= 40)
            Console.Error.WriteLine($"[FAST_MEMO_MISMATCH] plan=0x{plan.Hash:X16} part={difference}");
    }

    private static bool SameWordArrays(uint[][] left, uint[][] right)
    {
        if (left.Length != right.Length)
            return false;
        for (var index = 0; index < left.Length; index++)
            if (!left[index].AsSpan().SequenceEqual(right[index]))
                return false;
        return true;
    }
}
