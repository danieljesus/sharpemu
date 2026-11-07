// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.ShaderCompiler.Resources;

// Copies guest bytes only when they are already resident on the CPU side. It never
// synchronizes: a range the GPU may still own, or that a clean read would refuse,
// returns false so the caller falls back to a full materialization.
public delegate bool ResidentGuestBytesReader(ulong address, Span<byte> destination, bool clean);

// [local] SHARPEMU_VALIDATE_IN_PLACE: the reader's bytes compared with expected in place; false when not compared.
public delegate bool ResidentGuestBytesComparer(ulong address, ReadOnlySpan<byte> expected, bool clean, out bool equal);

// [local] SHARPEMU_STORE_TABLE_COMPARE: Equal or Different when the resident reader would read the range and it was compared
// in place; Refused when the resident reader would refuse it (the GPU may own it); Uncompared when it could not be
// compared here (the caller copies it through the resident reader, which decides).
public delegate DeviceStoreTablePlanner.TableChunkCompare ResidentTableChunkComparer(ulong address, ReadOnlySpan<byte> expected);

// Cache hits require current dependency bytes and the current draw's address mapping.
// Failed resident reads fall back to full evaluation without changing GPU ownership.
public sealed partial class ResourceMaterializationCache
{
    // Two generations approximate LRU: a full young generation becomes the old one,
    // and an old entry that is used again is promoted back.
    private readonly int _generationCapacity;
    private Dictionary<ulong, Entry> _young = new();
    private Dictionary<ulong, Entry> _old = new();
    private byte[] _scratch = new byte[256];
    private byte[] _refreshScratch = [];
    private ReadRecorder? _spareRecorder;
    // The user data words each (plan, base, compute state) has read so far. They only index the
    // entries; Entry.Matches is what decides a hit.
    private readonly Dictionary<ulong, ulong[]> _learnedUserData = new();
    private readonly Dictionary<ulong, AddressRebase> _rebases = new();

    private AddressRebase RebaseOf(ulong drawKey, ShaderResourcePlan plan)
    {
        if (!_rebases.TryGetValue(drawKey, out var rebase))
            _rebases.Add(drawKey, rebase = new AddressRebase(plan));
        return rebase;
    }

    // Titles allocate per-draw constants linearly, so a buffer's base address changes on every
    // draw while everything else the draw binds stays the same; keyed on that address, the cache
    // misses nearly every draw. A user data word the plan uses verbatim as a buffer's base dword
    // is a candidate. It is rebased only once misses have shown it repeatedly that two draws
    // differing in nothing but candidate words materialize identically, except for those base
    // dwords, which equal the words: the same descriptors, table, ranges, specialization and
    // recorded guest bytes. One counterexample rejects the word for good. A rebased word leaves
    // the key and the match, and a hit patches the base dwords from the draw's own user data.
    private sealed class AddressRebase
    {
        private const int Confirmations = 3;

        // A word that is a base address. A buffer candidate is the base dword of a buffer descriptor
        // (Buffer >= 0): the hit patches that dword, and reads inside the descriptor's range move.
        // A raw candidate is the low dword of the handle of scalar loads; its reads lie at constant
        // byte offsets [MinOffset, MaxOffset] from the base whose high dword is user data word High
        // (or HighConstant when High < 0).
        private readonly record struct Candidate(int UserData, int Buffer, int High = -1, uint HighConstant = 0,
            long MinOffset = 0, long MaxOffset = 0);

        private const ulong RawAddressMask = 0x0000_FFFF_FFFF_FFFFul;

        private readonly Candidate[] _candidates;
        private readonly ulong[] _candidateMask;
        private ulong[] _rejected = [];
        private readonly Dictionary<int, int> _confirmed = new();
        private const int RecentCapacity = 512;
        private readonly Dictionary<ulong, Entry> _recent = new();

        private ulong StructureOf(Entry entry)
        {
            var hash = new HashCode();
            hash.Add(entry.UserData.Length);
            for (var index = 0; index < entry.UserData.Length; index++)
            {
                if (!IsUsed(entry.UsedUserData, index) || IsUsed(_candidateMask, index))
                    continue;
                hash.Add(index);
                hash.Add(entry.UserData[index]);
            }

            return (uint)hash.ToHashCode();
        }

        private readonly ShaderResourcePlan _plan;

        public AddressRebase(ShaderResourcePlan plan)
        {
            _plan = plan;
            var candidates = new List<Candidate>();

            for (var buffer = 0; buffer < plan.Info.Buffers.Count; buffer++)
            {
                var source = plan.DescriptorSources[(int)plan.Info.Buffers[buffer].Source];
                if (source.Dwords.Length != 0 && UserDataIndex(source.Dwords[0]) is { } index)
                    candidates.Add(new Candidate(index, buffer));
            }

            var values = PlanValues(plan);
            var buffered = candidates.Select(candidate => candidate.UserData).ToHashSet();
            foreach (var raw in RawCandidates(plan, values))
                if (!buffered.Contains(raw.UserData))
                    candidates.Add(raw);

            _candidates = candidates.ToArray();
            _candidateMask = MaskOf(_candidates.Select(candidate => candidate.UserData));
            var memoryBases = MemoryBaseCandidates(plan);
            MemoryBases = memoryBases.Select(candidate => candidate.Buffer).ToArray();
            _memoryState = new int[plan.Info.Buffers.Count];
            _memorySlots = new int[plan.Info.Buffers.Count];
            _memorySlots.AsSpan().Fill(-1);
            foreach (var (buffer, slot) in memoryBases)
                _memorySlots[buffer] = slot;
            _memoryLearnable = memoryBases.Count;
            (TableSlotBuffers, TableSlotOffsets, TableSlotsThroughBases) = TableSlotSources(plan, memoryBases);
            DirectSlots = DirectSlotsOf(plan, TableSlotBuffers);
        }

        // A table slot that is one dword loaded through a user data pointer at constant offsets: its
        // word is that dword. High is the user data index of the pointer's high dword, or -1 for
        // the constant HighConstant.
        public readonly record struct DirectLoad(int Low, int High, ulong HighConstant, long Immediate, uint Offset);

        // Per table slot: its direct load (default for a slot through a memory-based buffer), or
        // null when some other slot is not a direct load.
        public DirectLoad[]? DirectSlots { get; }

        private DirectLoad[]? DirectSlotsOf(ShaderResourcePlan plan, int[] slotBuffers)
        {
            var loads = new DirectLoad[plan.TableReads.Count];
            for (var slot = 0; slot < loads.Length; slot++)
            {
                if (slotBuffers[slot] >= 0)
                    continue;
                var value = plan.TableReads[slot].Value;
                if (value.Kind != ScalarValueKind.ScalarAddressWord || value.Operands.Length < 2 ||
                    value.Operands[1].Kind != ScalarValueKind.Constant || (uint)value.MemoryIndex >= (uint)plan.Memory.Count)
                    return null;
                var handle = value.Operands[0];
                if (handle.Operands.Length < 2 || UserDataIndex(handle.Operands[0]) is not { } low)
                    return null;
                var high = UserDataIndex(handle.Operands[1]);
                if (high is null && handle.Operands[1].Kind != ScalarValueKind.Constant)
                    return null;
                loads[slot] = new DirectLoad(low, high ?? -1, high is null ? handle.Operands[1].Payload : 0,
                    (int)plan.Memory[value.MemoryIndex].Offset, (uint)value.Operands[1].Payload);
            }

            return loads;
        }

        // Per table slot: the memory-based buffer it reads a dword of at a constant byte offset from
        // the base, or -1. TableSlotsThroughBases: some slot reads through such a buffer otherwise.
        public int[] TableSlotBuffers { get; } = [];
        public long[] TableSlotOffsets { get; } = [];
        public bool TableSlotsThroughBases { get; }

        private static (int[] Buffers, long[] Offsets, bool Through) TableSlotSources(ShaderResourcePlan plan,
            List<(int Buffer, int Slot)> memoryBases)
        {
            var buffers = new int[plan.TableReads.Count];
            var offsets = new long[plan.TableReads.Count];
            buffers.AsSpan().Fill(-1);
            if (memoryBases.Count == 0)
                return (buffers, offsets, false);

            int BufferOf(ScalarValue handleWord)
            {
                foreach (var (buffer, slot) in memoryBases)
                {
                    var baseWord = plan.DescriptorSources[(int)plan.Info.Buffers[buffer].Source].Dwords[0];
                    if (ReferenceEquals(handleWord, baseWord) ||
                        (slot >= 0 && handleWord.Kind == ScalarValueKind.ResourceTableWord && (int)handleWord.Payload == slot))
                        return buffer;
                }

                return -1;
            }

            var through = false;
            for (var slot = 0; slot < plan.TableReads.Count; slot++)
            {
                var value = plan.TableReads[slot].Value;
                if (value.Kind == ScalarValueKind.ScalarBufferWord && value.Operands.Length >= 2 &&
                    value.Operands[0].Operands.Length != 0 && value.Operands[1].Kind == ScalarValueKind.Constant &&
                    value.MemoryIndex >= 0 && value.MemoryIndex < plan.Memory.Count &&
                    BufferOf(value.Operands[0].Operands[0]) is var direct and >= 0)
                {
                    buffers[slot] = direct;
                    offsets[slot] = ((long)plan.Memory[value.MemoryIndex].Offset + (long)(uint)value.Operands[1].Payload) & ~3L;
                    continue;
                }

                var visited = new HashSet<ScalarValue>();
                var pending = new Stack<ScalarValue>();
                pending.Push(value);
                while (!through && pending.Count != 0)
                {
                    var current = pending.Pop();
                    if (!visited.Add(current))
                        continue;
                    if (current.Kind == ScalarValueKind.ScalarBufferWord && current.Operands.Length != 0 &&
                        current.Operands[0].Operands.Length != 0 && BufferOf(current.Operands[0].Operands[0]) >= 0)
                        through = true;
                    foreach (var operand in current.Operands)
                        pending.Push(operand);
                }
            }

            return (buffers, offsets, through);
        }

        private readonly int[] _memorySlots;

        // Memory-loaded bases, learned per buffer like the user data words: confirmations count up
        // to MemoryActive; MemoryRejected is final.
        private const int MemoryActive = Confirmations;
        private const int MemoryRejected = -1;
        private readonly int[] _memoryState;
        private int _memoryLearnable;

        public IReadOnlyList<int> MemoryBases { get; }

        public bool HasLearnableMemoryBases => _memoryLearnable != 0;


        public bool IsRejectedMemoryBase(int buffer) => _memoryState[buffer] == MemoryRejected;

        public bool AreActiveMemoryBases(List<(int Buffer, uint Word)> patches)
        {
            foreach (var (buffer, _) in patches)
                if (_memoryState[buffer] < MemoryActive)
                    return false;
            return true;
        }

        // A rebased refresh computed for a stale entry, compared with the full materialization of
        // the same draw: agreement counts towards activating its bases, one disagreement rejects
        // them for good.
        public void ConfirmMemoryBases(List<(int Buffer, uint Word)> patches, bool agrees)
        {
            foreach (var (buffer, _) in patches)
            {
                if (_memoryState[buffer] == MemoryRejected)
                    continue;
                if (!agrees)
                {
                    _memoryState[buffer] = MemoryRejected;
                    _memoryLearnable--;
                }
                else if (_memoryState[buffer] < MemoryActive)
                {
                    _memoryState[buffer]++;
                }
            }
        }

        // The window of buffer reads a patched base covers for this draw.
        public static bool TryMovedWindow(uint[] descriptor, uint baseWord, out ulong start, out ulong size)
        {
            start = size = 0;
            if (descriptor.Length < 3)
                return false;
            start = baseWord | ((ulong)(descriptor[1] & 0xFFFF) << 32);
            var stride = (descriptor[1] >> 16) & 0x3FFF;
            size = stride == 0 ? descriptor[2] : (ulong)descriptor[2] * stride;
            return true;
        }

        // The base dword moves, and so does the table slot that holds the same word.
        public ResourceSnapshot PatchMemoryBases(ResourceSnapshot snapshot, List<(int Buffer, uint Word)> patches)
        {
            var buffers = (uint[][])snapshot.Buffers.Clone();
            uint[]? table = null;
            foreach (var (buffer, word) in patches)
            {
                if (ReferenceEquals(buffers[buffer], snapshot.Buffers[buffer]))
                    buffers[buffer] = (uint[])buffers[buffer].Clone();
                var previous = buffers[buffer][0];
                buffers[buffer][0] = word;
                var slot = _memorySlots[buffer];
                var flat = slot >= 0 && slot < _plan.TableReads.Count ? (int)_plan.TableReads[slot].FlatOffset : -1;
                if (flat >= 0 && flat < snapshot.FlattenedResourceTable.Length && snapshot.FlattenedResourceTable[flat] == previous)
                {
                    table ??= (uint[])snapshot.FlattenedResourceTable.Clone();
                    table[flat] = word;
                }
            }

            return new ResourceSnapshot
            {
                Buffers = buffers,
                Images = snapshot.Images,
                Samplers = snapshot.Samplers,
                FlattenedResourceTable = table ?? snapshot.FlattenedResourceTable,
                UserData = snapshot.UserData,
                DeviceAddressRanges = snapshot.DeviceAddressRanges,
            };
        }

        private int? UserDataIndex(ScalarValue value)
        {
            if (value.Kind != ScalarValueKind.UserData)
                return null;
            var register = value.UserDataRegister;
            return register >= _plan.UserDataBase && register - _plan.UserDataBase < _plan.UserDataCount
                ? (int)(register - _plan.UserDataBase)
                : null;
        }

        // Every scalar load of the plan whose handle takes its low dword verbatim from user data.
        // A word qualifies only when all its loads use constant offsets and one high dword.
        // Every value the plan evaluates, operands included.
        private static HashSet<ScalarValue> PlanValues(ShaderResourcePlan plan)
        {
            var roots = new List<ScalarValue>();
            foreach (var source in plan.DescriptorSources)
                roots.AddRange(source.Dwords);
            roots.AddRange(plan.TableReads.Select(read => read.Value));
            roots.AddRange(plan.DynamicReads);
            foreach (var access in plan.Accesses)
            {
                if (access is null)
                    continue;
                foreach (var value in new[] { access.Handle, access.SamplerHandle, access.Read, access.Offset, access.Active })
                    if (value is not null)
                        roots.Add(value);
            }

            roots.AddRange(plan.Graph.Values);
            var visited = new HashSet<ScalarValue>();
            var pending = new Stack<ScalarValue>(roots);
            while (pending.Count != 0)
            {
                var value = pending.Pop();
                if (!visited.Add(value))
                    continue;
                foreach (var operand in value.Operands)
                    pending.Push(operand);
            }

            return visited;
        }

        // Buffers whose base dword is read from guest memory, directly or through a slot of the
        // flattened table: descriptor tables the title rewrites per frame or per draw point them at
        // freshly allocated constants. The slot (or -1) is where the table holds the same word.
        // Recorded reads inside such a buffer's window move with its base.
        private static List<(int Buffer, int Slot)> MemoryBaseCandidates(ShaderResourcePlan plan)
        {
            var bases = new List<(int Buffer, int Slot)>();
            for (var buffer = 0; buffer < plan.Info.Buffers.Count; buffer++)
            {
                var source = plan.DescriptorSources[(int)plan.Info.Buffers[buffer].Source];
                if (source.Dwords.Length < 2)
                    continue;
                var baseWord = source.Dwords[0];
                var slot = baseWord.Kind == ScalarValueKind.ResourceTableWord ? (int)baseWord.Payload : -1;
                if (slot < 0 && baseWord.Kind is not (ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord))
                    continue;
                bases.Add((buffer, slot));
            }

            return bases;
        }

        private IEnumerable<Candidate> RawCandidates(ShaderResourcePlan plan, HashSet<ScalarValue> values)
        {
            var found = new Dictionary<int, Candidate>();
            var disqualified = new HashSet<int>();
            foreach (var value in values)
            {
                if (value.Kind is not (ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord) ||
                    value.Operands.Length < 2 || value.Operands[0].Operands.Length < 2 ||
                    UserDataIndex(value.Operands[0].Operands[0]) is not { } index)
                    continue;

                var high = value.Operands[0].Operands[1];
                var highIndex = UserDataIndex(high);
                var offset = value.Operands[1];
                if ((highIndex is null && high.Kind != ScalarValueKind.Constant) || offset.Kind != ScalarValueKind.Constant ||
                    value.MemoryIndex >= plan.Memory.Count)
                {
                    disqualified.Add(index);
                    continue;
                }

                var immediate = (long)plan.Memory[value.MemoryIndex].Offset;
                var relative = value.Kind == ScalarValueKind.ScalarBufferWord
                    ? (immediate + (long)(uint)offset.Payload) & ~3L
                    : (immediate & ~3L) + (long)((uint)offset.Payload & ~3u);
                var candidate = new Candidate(index, -1, highIndex ?? -1, highIndex is null ? (uint)high.Payload : 0, relative, relative);
                if (found.TryGetValue(index, out var existing))
                {
                    if (existing.High != candidate.High || existing.HighConstant != candidate.HighConstant)
                    {
                        disqualified.Add(index);
                        continue;
                    }

                    candidate = existing with
                    {
                        MinOffset = Math.Min(existing.MinOffset, relative),
                        MaxOffset = Math.Max(existing.MaxOffset, relative),
                    };
                }

                found[index] = candidate;
            }

            return found.Values.Where(candidate => !disqualified.Contains(candidate.UserData));
        }

        // The window [start, start + size) of reads a candidate's base covers in this entry.
        private bool TryWindow(Entry entry, in Candidate candidate, out ulong start, out ulong size, out ulong addressBase)
        {
            start = size = addressBase = 0;
            if (candidate.Buffer >= 0)
            {
                if (candidate.Buffer >= entry.Snapshot.Buffers.Length)
                    return false;
                var descriptor = entry.Snapshot.Buffers[candidate.Buffer];
                if (descriptor.Length < 3)
                    return false;
                start = addressBase = descriptor[0] | ((ulong)(descriptor[1] & 0xFFFF) << 32);
                var stride = (descriptor[1] >> 16) & 0x3FFF;
                size = stride == 0 ? descriptor[2] : (ulong)descriptor[2] * stride;
                return true;
            }

            // The window starts at the base itself: the read prefetch fetches a pointer's block from
            // its base, ahead of the lowest offset a load uses.
            addressBase = RawBase(entry.UserData, candidate, entry.UserData[candidate.UserData]);
            var lowest = Math.Min(0, candidate.MinOffset);
            var low = (long)addressBase + lowest;
            if (low < 0)
                return false;
            start = (ulong)low;
            size = (ulong)(candidate.MaxOffset - lowest) + sizeof(uint);
            return true;
        }

        private static ulong RawBase(IReadOnlyList<uint> userData, in Candidate candidate, uint low)
        {
            var high = candidate.High >= 0 ? userData[candidate.High] : candidate.HighConstant;
            return (((ulong)high << 32) | low) & RawAddressMask & ~3UL;
        }

        // The rebased words; replaced, never mutated, so a caller can compare references.
        public ulong[] Active { get; private set; } = [];

        public bool IsLearnable(int index) => IsUsed(_candidateMask, index) && !IsUsed(_rejected, index) && !IsUsed(Active, index);

        private enum LearnOutcome { NoPrevious, Shape, NonCandidateWord, Identical, RejectedWord, Unpredicted, BasesDisagree, PredictionRejected, PredictionConfirmed, Rejected, Confirmed, Activated, Count }

        private static readonly ThreadCounter[] _learnOutcomes = Enumerable.Range(0, (int)LearnOutcome.Count).Select(_ => new ThreadCounter()).ToArray();

        private static void Note(LearnOutcome outcome) => _learnOutcomes[(int)outcome].Increment();

        public static string TakeLearnReport()
        {
            var parts = new List<string>();
            for (var index = 0; index < (int)LearnOutcome.Count; index++)
            {
                var count = _learnOutcomes[index].Take();
                if (count != 0)
                    parts.Add(FormattableString.Invariant($"{(LearnOutcome)index}:{count}"));
            }

            return " learn=" + string.Join(",", parts);
        }

        // Every rebased base dword of the entry equals the word it came from.
        public bool AppliesTo(Entry entry)
        {
            if (Active.Length == 0)
                return true;
            foreach (var (userData, buffer, _, _, _, _) in _candidates)
            {
                if (buffer >= 0 && IsUsed(Active, userData) &&
                    (buffer >= entry.Snapshot.Buffers.Length || entry.Snapshot.Buffers[buffer].Length == 0 ||
                     userData >= entry.UserData.Length || entry.Snapshot.Buffers[buffer][0] != entry.UserData[userData]))
                    return false;
            }

            return true;
        }

        public ResourceSnapshot Apply(ResourceSnapshot snapshot, IReadOnlyList<uint> userData)
        {
            if (Active.Length == 0)
                return snapshot;
            uint[][]? buffers = null;
            foreach (var (index, buffer, _, _, _, _) in _candidates)
            {
                if (buffer < 0 || !IsUsed(Active, index) || snapshot.Buffers[buffer][0] == userData[index])
                    continue;
                buffers ??= (uint[][])snapshot.Buffers.Clone();
                if (ReferenceEquals(buffers[buffer], snapshot.Buffers[buffer]))
                    buffers[buffer] = (uint[])buffers[buffer].Clone();
                buffers[buffer][0] = userData[index];
            }

            return buffers is null
                ? snapshot
                : new ResourceSnapshot
                {
                    Buffers = buffers,
                    Images = snapshot.Images,
                    Samplers = snapshot.Samplers,
                    FlattenedResourceTable = snapshot.FlattenedResourceTable,
                    UserData = snapshot.UserData,
                    DeviceAddressRanges = snapshot.DeviceAddressRanges,
                    Origin = snapshot.Origin,
                };
        }

        // Compares a fresh materialization with the latest one that agrees on every word but the
        // candidates: the same object drawn again, usually a frame earlier, with new constants.
        // Comparing with the plan's previous draw instead finds another object's textures in
        // the way and never concludes.
        public void Learn(Entry built,
            Func<Entry, ulong[], (ResourceSnapshot Snapshot, List<(int Buffer, uint Word)>? Patches)?>? predict = null)
        {
            if (_candidates.Length == 0)
                return;
            var structure = StructureOf(built);
            _recent.TryGetValue(structure, out var previous);
            if (_recent.Count >= RecentCapacity)
                _recent.Clear();
            _recent[structure] = built;
            if (previous is null || previous.UserData.Length != built.UserData.Length ||
                previous.Normalization != built.Normalization ||
                !previous.UsedUserData.AsSpan().SequenceEqual(built.UsedUserData))
            {
                Note(previous is null ? LearnOutcome.NoPrevious : LearnOutcome.Shape);
                return;
            }

            var differing = new List<int>();
            for (var index = 0; index < built.UserData.Length; index++)
            {
                if (!IsUsed(built.UsedUserData, index) || previous.UserData[index] == built.UserData[index])
                    continue;
                if (!IsUsed(_candidateMask, index))
                {
                    Note(LearnOutcome.NonCandidateWord);
                    return;
                }

                differing.Add(index);
            }

            // A word already rejected explains any mismatch; blaming the others with it would reject
            // sound bases that merely change together with it.
            if (differing.Count == 0 || differing.Any(index => IsUsed(_rejected, index)))
            {
                Note(differing.Count == 0 ? LearnOutcome.Identical : LearnOutcome.RejectedWord);
                return;
            }

            // Reads must land where the previous draw's did, moved with a rebased buffer when they
            // fall inside its window. Guest bytes that changed between the two draws make the
            // comparison say nothing about the words.
            var reads = CompareMovedReads(this, previous, built, differing);
            if (reads == ReadComparison.Inconclusive)
            {
                // Changed guest bytes hide whether the words are bases. Predict this draw from the
                // previous one as a hit with the words rebased would, moved memory-loaded bases
                // included, and judge the words by the prediction instead.
                if (predict?.Invoke(previous, Or(Active, MaskOf(differing))) is not { } prediction)
                {
                    Note(LearnOutcome.Unpredicted);
                    return;
                }

                var agrees = Difference(prediction.Snapshot, previous.Specialization, built.Snapshot, built.Specialization) is null;
                if (prediction.Patches is { } patches)
                {
                    // A wrong prediction that moved bases blames those bases, not the words.
                    ConfirmMemoryBases(patches, agrees);
                    if (!agrees)
                    {
                        Note(LearnOutcome.BasesDisagree);
                        return;
                    }
                }
                else if (!agrees)
                {
                    _rejected = Or(_rejected, MaskOf(differing));
                    if (Overlaps(Active, _rejected))
                        Active = AndNot(Active, _rejected);
                    Note(LearnOutcome.PredictionRejected);
                    return;
                }

                Note(LearnOutcome.PredictionConfirmed);
                Confirm(differing);
                return;
            }

            if (reads == ReadComparison.Mismatch ||
                !SameExceptBases(previous, built, differing, compareTable: reads == ReadComparison.SameBytes))
            {
                _rejected = Or(_rejected, MaskOf(differing));
                if (Overlaps(Active, _rejected))
                    Active = AndNot(Active, _rejected);
                Note(LearnOutcome.Rejected);
                return;
            }

            Note(LearnOutcome.Confirmed);
            Confirm(differing);
        }

        private void Confirm(List<int> differing)
        {
            var activated = new List<int>();
            foreach (var index in differing)
            {
                var count = _confirmed.GetValueOrDefault(index) + 1;
                _confirmed[index] = count;
                if (count >= Confirmations && !IsUsed(_rejected, index) && !IsUsed(Active, index))
                    activated.Add(index);
            }

            if (activated.Count != 0)
            {
                Note(LearnOutcome.Activated);
                Active = Or(Active, MaskOf(activated));
            }
        }

        // Runs a prediction as though the words in probe were rebased already.
        public T WithActive<T>(ulong[] probe, Func<T> run)
        {
            var saved = Active;
            Active = probe;
            try
            {
                return run();
            }
            finally
            {
                Active = saved;
            }
        }

        // The flattened table is left out when only table-only bytes changed: it holds the words read
        // from memory, whose addresses the read comparison already checked.
        private bool SameExceptBases(Entry previous, Entry built, List<int> differing, bool compareTable)
        {
            if (!previous.Specialization.Equals(built.Specialization))
                return false;

            var left = previous.Snapshot;
            var right = built.Snapshot;
            if (!SameWords(left.Images, right.Images) || !SameWords(left.Samplers, right.Samplers) ||
                (compareTable
                    ? !left.FlattenedResourceTable.AsSpan().SequenceEqual(right.FlattenedResourceTable)
                    : left.FlattenedResourceTable.Length != right.FlattenedResourceTable.Length) ||
                !left.DeviceAddressRanges.AsSpan().SequenceEqual(right.DeviceAddressRanges) ||
                left.Buffers.Length != right.Buffers.Length)
                return false;

            for (var buffer = 0; buffer < left.Buffers.Length; buffer++)
            {
                var before = left.Buffers[buffer];
                var after = right.Buffers[buffer];
                if (before.Length != after.Length)
                    return false;
                for (var dword = 0; dword < before.Length; dword++)
                {
                    if (dword == 0 && BaseWord(buffer, differing) is { } index)
                    {
                        if (before[0] != previous.UserData[index] || after[0] != built.UserData[index])
                            return false;
                    }
                    else if (before[dword] != after[dword])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // Every read of the built draw is the previous draw's read, moved by the rebase when it lies
        // in a moved buffer's window; bytesDiffer tells whether the guest bytes read there changed.
        private enum ReadComparison
        {
            // A read moved where the words cannot explain it while every byte read alike matched.
            Mismatch,
            // Guest bytes changed where the descriptors depend on them, or a changed pointer in
            // guest memory redirected later reads: the pair says nothing about the words.
            Inconclusive,
            SameBytes,
            // Only words the flattened table alone reads changed: per-draw constants.
            TableBytesDiffer,
        }

        private static ReadComparison CompareMovedReads(AddressRebase rebase, Entry previous, Entry built, List<int> differing)
        {
            var matched = previous.RangeAddresses.Length == built.RangeAddresses.Length;
            var bytesDiffer = false;
            var descriptorBytesDiffer = false;
            var common = Math.Min(previous.RangeAddresses.Length, built.RangeAddresses.Length);
            for (var range = 0; range < common; range++)
            {
                var length = previous.RangeLengths[range];
                if (rebase.Move(previous, built.UserData, previous.RangeAddresses[range], length, differing) != built.RangeAddresses[range] ||
                    length != built.RangeLengths[range] || previous.RangeClean[range] != built.RangeClean[range])
                {
                    matched = false;
                    continue;
                }

                for (var offset = 0; offset < length; offset += sizeof(uint))
                {
                    var before = previous.RangeOffsets[range] + offset;
                    var after = built.RangeOffsets[range] + offset;
                    if (previous.Bytes.AsSpan(before, sizeof(uint)).SequenceEqual(built.Bytes.AsSpan(after, sizeof(uint))))
                        continue;
                    bytesDiffer = true;
                    descriptorBytesDiffer |= !previous.WordTableOnly[before / sizeof(uint)] || !built.WordTableOnly[after / sizeof(uint)];
                }
            }

            if (!matched)
                return bytesDiffer ? ReadComparison.Inconclusive : ReadComparison.Mismatch;
            if (descriptorBytesDiffer || (bytesDiffer && !(previous.TableRefreshable && built.TableRefreshable)))
                return ReadComparison.Inconclusive;
            return bytesDiffer ? ReadComparison.TableBytesDiffer : ReadComparison.SameBytes;
        }

        // Where a read the entry recorded lies for a draw with this user data: a read inside the
        // window of a candidate whose base word moved moves with it. Only the words in moving count
        // (the differing words, or the rebased ones on a hit).
        private ulong Move(Entry entry, IReadOnlyList<uint> userData, ulong address, int length, IReadOnlyCollection<int>? moving)
        {
            foreach (var candidate in _candidates)
            {
                var index = candidate.UserData;
                if (moving is null ? !IsUsed(Active, index) : !moving.Contains(index))
                    continue;
                if (userData[index] == entry.UserData[index] || !TryWindow(entry, candidate, out var start, out var size, out var addressBase))
                    continue;
                // Only a range wholly inside the window moves: a range that merely overlaps it can hold
                // another block the title allocated next to it, which must not move.
                if (address < start || address - start + (ulong)length > size)
                    continue;
                var movedBase = candidate.Buffer >= 0
                    ? (addressBase & ~0xFFFF_FFFFUL) | userData[index]
                    : RawBase(entry.UserData, candidate, userData[index]);
                return address - addressBase + movedBase;
            }

            return address;
        }

        // A hit reads the entry's recorded ranges where they lie for this draw.
        public ulong MoveRead(Entry entry, IReadOnlyList<uint> userData, ulong address, int length) =>
            Active.Length == 0 ? address : Move(entry, userData, address, length, null);

        // True when a rebased base of the entry differs from this draw's word.
        public bool Moves(Entry entry, IReadOnlyList<uint> userData)
        {
            foreach (var candidate in _candidates)
                if (IsUsed(Active, candidate.UserData) && entry.UserData[candidate.UserData] != userData[candidate.UserData])
                    return true;
            return false;
        }

        private int? BaseWord(int buffer, List<int> differing)
        {
            foreach (var candidate in _candidates)
                if (candidate.Buffer == buffer && differing.Contains(candidate.UserData))
                    return candidate.UserData;
            return null;
        }

        private static bool SameWords(uint[][] left, uint[][] right)
        {
            if (left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
                if (!left[index].AsSpan().SequenceEqual(right[index]))
                    return false;
            return true;
        }

        private static ulong[] MaskOf(IEnumerable<int> indices)
        {
            var mask = Array.Empty<ulong>();
            foreach (var index in indices)
            {
                if (mask.Length <= index >> 6)
                    Array.Resize(ref mask, (index >> 6) + 1);
                mask[index >> 6] |= 1UL << (index & 63);
            }

            return mask;
        }

        private static ulong[] Or(ulong[] left, ulong[] right)
        {
            var result = new ulong[Math.Max(left.Length, right.Length)];
            for (var index = 0; index < result.Length; index++)
                result[index] = (index < left.Length ? left[index] : 0) | (index < right.Length ? right[index] : 0);
            return result;
        }

        private static ulong[] AndNot(ulong[] left, ulong[] right)
        {
            var result = (ulong[])left.Clone();
            for (var index = 0; index < result.Length && index < right.Length; index++)
                result[index] &= ~right[index];
            return result.All(word => word == 0) ? [] : result;
        }

        private static bool Overlaps(ulong[] left, ulong[] right)
        {
            for (var index = 0; index < left.Length && index < right.Length; index++)
                if ((left[index] & right[index]) != 0)
                    return true;
            return false;
        }
    }

    private const int VolatileStaleLookups = 3;
    private const int MinVolatilePeriod = 32;
    private const int MaxVolatilePeriod = 1024;
    private const int SightingSlots = 1 << 15;

    private readonly ulong[]? _sightings;

    // [local] stampStoreTables/currentStoreTables: SHARPEMU_PROGRAM_GATE_TABLES (StoreTableMode 1 and 3 of ShaderProgramCache).
    public ResourceMaterializationCache(int generationCapacity = 16384, bool limitChurn = false, bool stampStoreTables = false,
        bool currentStoreTables = false)
    {
        _generationCapacity = Math.Max(1, generationCapacity);
        _sightings = limitChurn ? new ulong[SightingSlots] : null;
        _currentStoreTables = currentStoreTables && DeviceStoreTablePlanner.ExactMode == 0;
        _stampStoreTables = stampStoreTables || _currentStoreTables;
    }

    // [local] SHARPEMU_MATERIALIZE_POH=1: the primitive arrays an entry keeps (the recorded words and ranges, the user
    // data and the snapshot's descriptor words, table and device ranges) are copied to the pinned object heap when the
    // entry is stored, unless they already live in generation 2. GTA V panning (2026-11-02): an entry lives for
    // thousands of draws, so every one stored since the previous collection survives it: ~6 MB/s of entries and their
    // arrays were the bulk of what each gen0 promoted (~10 MB, ~1 ms per MB) and gen1 copied again into gen2. Pinned
    // arrays are never marked or copied by gen0/gen1 collections. Same values, other heap: nothing to verify.
    // Measured (with the 192 MB gen0 floor the CLI then sets): gen0 pauses 11.3 -> 7.9 ms, gen1 28 -> 16 ms, total pause
    // 291-390 -> 216 ms per 30 s (~0.2 ms/frame), but the pinned allocations cost more: materialization +0.46 ms/frame
    // on the render thread and +1.65 ms/frame on the front (hits +0.8, misses +0.8). Panning A/B -0.09 FPS (5 of 6
    // pairs lower), static -0.03, misses not fewer. Left off.
    internal static readonly bool DefaultRetainPinned = Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_POH") == "1";

    public bool RetainPinned { get; init; } = DefaultRetainPinned;

    private static T[] Pinned<T>(T[] array) where T : unmanaged
    {
        if (array.Length == 0 || GC.GetGeneration(array) >= GC.MaxGeneration)
            return array;
        var pinned = GC.AllocateUninitializedArray<T>(array.Length, pinned: true);
        array.AsSpan().CopyTo(pinned);
        return pinned;
    }

    private static T[]? PinnedOrNull<T>(T[]? array) where T : unmanaged => array is null ? null : Pinned(array);

    private static uint[][] PinnedJagged(uint[][] arrays)
    {
        uint[][]? copy = null;
        for (var index = 0; index < arrays.Length; index++)
        {
            var pinned = Pinned(arrays[index]);
            if (!ReferenceEquals(pinned, arrays[index]))
                (copy ??= (uint[][])arrays.Clone())[index] = pinned;
        }

        return copy ?? arrays;
    }

    private static ResourceSnapshot RetainSnapshot(ResourceSnapshot snapshot)
    {
        var buffers = PinnedJagged(snapshot.Buffers);
        var images = PinnedJagged(snapshot.Images);
        var samplers = PinnedJagged(snapshot.Samplers);
        var table = Pinned(snapshot.FlattenedResourceTable);
        var userData = Pinned(snapshot.UserData);
        var ranges = Pinned(snapshot.DeviceAddressRanges);
        if (ReferenceEquals(buffers, snapshot.Buffers) && ReferenceEquals(images, snapshot.Images) &&
            ReferenceEquals(samplers, snapshot.Samplers) && ReferenceEquals(table, snapshot.FlattenedResourceTable) &&
            ReferenceEquals(userData, snapshot.UserData) && ReferenceEquals(ranges, snapshot.DeviceAddressRanges))
            return snapshot;
        return new ResourceSnapshot
        {
            Buffers = buffers,
            Images = images,
            Samplers = samplers,
            FlattenedResourceTable = table,
            UserData = userData,
            DeviceAddressRanges = ranges,
            MipStatistics = snapshot.MipStatistics,
            Origin = snapshot.Origin,
        };
    }

    private static void Retain(Entry entry)
    {
        entry.Retained = true;
        entry.UserData = Pinned(entry.UserData);
        entry.RangeAddresses = Pinned(entry.RangeAddresses);
        entry.RangeOffsets = Pinned(entry.RangeOffsets);
        entry.RangeLengths = Pinned(entry.RangeLengths);
        entry.RangeClean = Pinned(entry.RangeClean);
        entry.RangeBase = PinnedOrNull(entry.RangeBase);
        entry.RangeReadBase = PinnedOrNull(entry.RangeReadBase);
        entry.WordTableOnly = Pinned(entry.WordTableOnly);
        entry.Bytes = Pinned(entry.Bytes);
        entry.Snapshot = RetainSnapshot(entry.Snapshot);
    }

    // [local] SHARPEMU_PROGRAM_GATE_TABLES=3: a walk records none of the store-table scan's words, and every hit, refresh
    // or shift of a plan with store tables evaluates its tables again from the thread's scan cache (CurrentTables): the
    // V# words were validated, a cached scan is a list lookup, and a dropped one is rescanned, as a fresh walk would.
    // The ranges the snapshot carries are then always a fresh walk's, without walking the rest of the plan again.
    private readonly bool _currentStoreTables;
    private readonly List<DeviceAddressRange> _tableRanges = new();
    private static long _totalTablesSame, _totalTablesEqual, _totalTablesChanged, _totalTablesFailed;

    private bool CurrentTables(Entry entry, ResourceRuntimeInputs inputs, ref ResourceSnapshot snapshot)
    {
        var used = entry.UsedScans;
        var ranges = snapshot.DeviceAddressRanges;
        var tableCount = 0;
        foreach (var scan in used)
            tableCount += scan.Ranges.Count;
        var prefix = ranges.Length - tableCount;
        var current = _tableRanges;
        current.Clear();
        DeviceStoreTablePlanner.BeginUsedScans();
        bool same;
        DeviceStoreTablePlanner.UsedScan[]? rescans;
        try
        {
            DeviceStoreTablePlanner.Evaluate(entry.Plan, inputs, current);
        }
        finally
        {
            same = DeviceStoreTablePlanner.EndUsedScansSame(used, out rescans);
        }

        if (prefix < 0)
        {
            Interlocked.Increment(ref _totalTablesFailed);
            return false;
        }

        if (same)
        {
            Interlocked.Increment(ref _totalTablesSame);
            return true;
        }

        // The scans moved (a drop and a rescan, or another table): the snapshot holds while their ranges are the same.
        var equal = current.Count == tableCount;
        for (var index = 0; equal && index < tableCount; index++)
            equal = SameRange(current[index], ranges[prefix + index]);
        var owned = ReferenceEquals(snapshot, entry.Snapshot);
        if (equal)
        {
            Interlocked.Increment(ref _totalTablesEqual);
            if (owned)
                entry.UsedScans = rescans!;
            return true;
        }

        Interlocked.Increment(ref _totalTablesChanged);
        var next = new DeviceAddressRange[prefix + current.Count];
        Array.Copy(ranges, next, prefix);
        current.CopyTo(next, prefix);
        snapshot = new ResourceSnapshot
        {
            Buffers = snapshot.Buffers,
            Images = snapshot.Images,
            Samplers = snapshot.Samplers,
            FlattenedResourceTable = snapshot.FlattenedResourceTable,
            UserData = snapshot.UserData,
            DeviceAddressRanges = next,
            MipStatistics = snapshot.MipStatistics,
            Origin = snapshot.Origin,
        };
        if (owned)
        {
            if (entry.Retained) // [local] SHARPEMU_MATERIALIZE_POH
                snapshot = RetainSnapshot(snapshot);
            entry.Snapshot = snapshot;
            entry.UsedScans = rescans!;
        }

        return true;
    }

    // A range's handle is a label the scan cache replays from the scan that filled it.
    private static bool SameRange(DeviceAddressRange left, DeviceAddressRange right) =>
        left.Base == right.Base && left.Size == right.Size && left.Planned == right.Planned && left.Written == right.Written &&
        left.Conservative == right.Conservative;

    // [local] SHARPEMU_PROGRAM_GATE's per-thread caches: the store-table ranges of a walk come from the thread's scan
    // cache, whose words the recorder does not see when the scan was cached, so an entry of a plan with store tables
    // only serves while the thread's scan cache still holds the scans its walk used (DeviceStoreTablePlanner.UsedScan).
    private readonly bool _stampStoreTables;

    private bool StampsTables(ShaderResourcePlan plan) => _stampStoreTables && plan.DeviceStoreTables.Count != 0;

    private static bool TablesCurrent(Entry entry) => DeviceStoreTablePlanner.ScansCurrent(entry.UsedScans);

    private static long _totalTableStale; // [local] hits refused because a store-table scan they used was dropped

    // [local] SHARPEMU_MATERIALIZATION_CONTENT_KEY (on by default; =0 off): the plan's static user-data analysis
    // (UserDataUseAnalysis) narrows what tells entries apart. A register the plan only reads through (a table or
    // constant pointer) or only builds buffer dwords from leaves the key and the match: a hit moves the entry's ranges
    // based on it by the pointer's delta (Entry.TryRebase) and evaluates those buffer dwords again for the draw
    // (Entry.SnapshotFor). With =0 the key and the match are upstream's alone: every user-data word the walks read
    // (learned per draw key) less the words AddressRebase has confirmed as bases. Before the merge with upstream's
    // learned key, =0 compared every user-data word; the learned set is the same comparison less the words no walk
    // of the plan reads.
    private static readonly bool DefaultContentKeyed = Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZATION_CONTENT_KEY") != "0";

    // For tests (they run in parallel): this async flow's value, null for the environment's.
    internal static readonly AsyncLocal<bool?> ContentKeyedOverride = new();

    internal static bool ContentKeyed => ContentKeyedOverride.Value ?? DefaultContentKeyed;

    // [local] Word patches (Entry.WordPatches, UserDataUseAnalysis.PatchableReads): a recorded word the plan copies
    // verbatim into table slots and buffer base dwords may read differently; a hit takes the draw's word in those
    // places. Always on (no environment switch); tests turn it off to observe upstream's table refresh instead.
    internal static readonly AsyncLocal<bool?> WordPatchesOverride = new();

    private static bool WordPatchesEnabled => WordPatchesOverride.Value ?? true;

    [ThreadStatic]
    public static ulong CurrentPlanHash; // [local] for overlay diagnostics

    private static readonly ThreadCounter _totalHits = new();
    private static readonly ThreadCounter _totalMisses = new();
    private static readonly ThreadCounter _totalUncacheable = new();
    private const int MaxVariants = 8;
    private static readonly ThreadCounter _totalStale = new();
    private static readonly ThreadCounter _totalStaleUnreadable = new();
    private static readonly ThreadCounter _totalRefreshes = new();
    private static readonly ThreadCounter _totalNotFound = new();
    private static readonly ThreadCounter[] _refreshRefusals = Enumerable.Range(0, 6).Select(_ => new ThreadCounter()).ToArray();
    private static readonly string[] RefreshRefusalNames = ["not_refreshable", "unreadable", "descriptor_words", "unchanged", "table_eval", "rebased"];
    private int _lastRefreshRefusal;

    private enum Diagnostic
    {
        KeyAbsent, KeyMismatch, OldPromotion, GenerationRotation, GenerationEntriesDropped,
        Variant1, Variant2, Variant3, Variant4, Variant5, Variant6, Variant7, Variant8, BytesChanged, Unreadable,
        RefreshLayout, RefreshUnreadable, RefreshDescriptorChanged, RefreshUnchanged,
        RefreshEvaluation, RefreshReadChanged, RefreshDescriptorSkipped, RefreshOlderVariant, RejectionPrecheck, Count,
    }
    private static readonly bool DiagnosticsEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
    private static readonly long[] DiagnosticCounts = new long[(int)Diagnostic.Count];

    private static void CountDiagnostic(Diagnostic diagnostic, int count = 1)
    {
        if (DiagnosticsEnabled) Interlocked.Add(ref DiagnosticCounts[(int)diagnostic], count);
    }

    private static bool RejectRefresh(Diagnostic reason)
    {
        CountDiagnostic(reason);
        return false;
    }
    private static readonly ThreadCounter _totalFirstSightings = new();
    private static readonly ThreadCounter _totalVolatile = new();
    private static readonly ThreadCounter _totalChurnLearnable = new();
    private static readonly ThreadCounter _totalChurnUnlearnable = new();
    private static readonly ThreadCounter _totalChurnMulti = new();
    private const int DrawHistoryCapacity = 65536;
    private readonly Dictionary<ulong, uint[]> _drawHistories = new();

    private void NoteChurn(ulong drawKey, IReadOnlyList<uint> userData, ulong[]? learned, AddressRebase rebase)
    {
        if (!_drawHistories.TryGetValue(drawKey, out var previous) || previous.Length != userData.Count)
        {
            if (_drawHistories.Count >= DrawHistoryCapacity)
                _drawHistories.Clear();
            var copy = new uint[userData.Count];
            for (var index = 0; index < copy.Length; index++)
                copy[index] = userData[index];
            _drawHistories[drawKey] = copy;
            return;
        }

        var differing = 0;
        var changed = -1;
        for (var index = 0; index < previous.Length; index++)
        {
            if (previous[index] == userData[index])
                continue;
            previous[index] = userData[index];
            if (learned is not null && !IsUsed(learned, index))
                continue;
            differing++;
            changed = index;
        }

        if (differing != 1)
        {
            if (differing > 1)
                _totalChurnMulti.Increment();
            return;
        }

        if (rebase.IsLearnable(changed))
            _totalChurnLearnable.Increment();
        else
            _totalChurnUnlearnable.Increment();
    }

    private static string TakeChurnReport() =>
        FormattableString.Invariant($" churn_learnable={_totalChurnLearnable.Take()} churn_unlearnable={_totalChurnUnlearnable.Take()} churn_multi={_totalChurnMulti.Take()}") +
        AddressRebase.TakeLearnReport();

    private static readonly ThreadCounter _totalRevalidated = new();
    private static readonly ThreadCounter _totalRevalidateStale = new();
    private static readonly ThreadCounter _totalRevalidateUnlinked = new();


    [ThreadStatic]
    private static bool _readingTable;

    public static bool ReadingTable
    {
        get => _readingTable;
        private set => _readingTable = value;
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Uncacheable { get; private set; }
    public long TableRefreshes { get; private set; }
    public long Unrecorded { get; private set; }

    // Process-wide counters since the previous call, for the periodic render report.
    public static string TakeReport()
    {
        var hits = _totalHits.Take();
        var misses = _totalMisses.Take();
        var uncacheable = _totalUncacheable.Take();
        var stale = _totalStale.Take();
        var staleUnreadable = _totalStaleUnreadable.Take();
        var refreshes = _totalRefreshes.Take();
        var notFound = _totalNotFound.Take();
        var firstSightings = _totalFirstSightings.Take();
        var volatileLookups = _totalVolatile.Take();
        var refusals = string.Join(",", RefreshRefusalNames.Select((name, index) => FormattableString.Invariant($"{name}:{_refreshRefusals[index].Take()}")));
        var total = hits + misses;
        var diagnostics = DiagnosticsEnabled
            ? " " + string.Join(" ", Enumerable.Range(0, (int)Diagnostic.Count).Select(index =>
                $"{(Diagnostic)index}={Interlocked.Exchange(ref DiagnosticCounts[index], 0)}"))
            : string.Empty;
        return FormattableString.Invariant(
            $"[PERF][RESOURCE_CACHE] hits={hits} misses={misses} not_found={notFound} stale={stale} stale_unreadable={staleUnreadable} refreshes={refreshes} refused={refusals} uncacheable={uncacheable} hit_rate={(total == 0 ? 0 : hits * 100.0 / total):F1}% {RawReadPrefetch.TakeReport()}{diagnostics}") +
            FormattableString.Invariant($" revalidated={_totalRevalidated.Take()} revalidate_stale={_totalRevalidateStale.Take()} revalidate_unlinked={_totalRevalidateUnlinked.Take()} stats_masked_hits={_totalStatisticsMaskedHits.Take()}") +
            TakeChurnReport() +
            // [local] store tables, recorder, SHARPEMU_MATERIALIZE_REBASE, SYNC_VALIDATE and VALIDATE_IN_PLACE counters
            FormattableString.Invariant($" table_stale={Interlocked.Exchange(ref _totalTableStale, 0)} tables_same={Interlocked.Exchange(ref _totalTablesSame, 0)} tables_equal={Interlocked.Exchange(ref _totalTablesEqual, 0)} tables_changed={Interlocked.Exchange(ref _totalTablesChanged, 0)} tables_failed={Interlocked.Exchange(ref _totalTablesFailed, 0)} read_memo_hits={Interlocked.Exchange(ref ReadRecorder.MemoHits, 0)} read_memo_misses={Interlocked.Exchange(ref ReadRecorder.MemoMisses, 0)} runsort_checked={Interlocked.Exchange(ref ReadRecorder.RunSortChecked, 0)} runsort_mismatches={Interlocked.Exchange(ref ReadRecorder.RunSortMismatches, 0)} block_builds={Interlocked.Exchange(ref ReadRecorder.BlockBuilds, 0)} block_fallbacks={Interlocked.Exchange(ref ReadRecorder.BlockFallbacks, 0)} block_checked={Interlocked.Exchange(ref ReadRecorder.BlockChecked, 0)} block_mismatches={Interlocked.Exchange(ref ReadRecorder.BlockMismatches, 0)}") +
            TakeRebaseReport() + TakeSyncValidateReport() + TakeInPlaceReport() +
            TakeStaleDifferenceReport() + TakeMissReport() + TakeDetailReport();
    }

    private static readonly bool StaleDiffEnabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _staleDifferences = new(StringComparer.Ordinal);

    private static void NoteStaleDifference(Entry stale, Entry built)
    {
        var parts = new SortedSet<string>(StringComparer.Ordinal);
        if (!stale.Specialization.Equals(built.Specialization))
            parts.Add("spec");
        DifferingWords("buf", stale.Snapshot.Buffers, built.Snapshot.Buffers, parts);
        DifferingWords("img", stale.Snapshot.Images, built.Snapshot.Images, parts);
        DifferingWords("smp", stale.Snapshot.Samplers, built.Snapshot.Samplers, parts);
        if (!stale.Snapshot.FlattenedResourceTable.AsSpan().SequenceEqual(built.Snapshot.FlattenedResourceTable))
            parts.Add("table");
        if (!stale.Snapshot.DeviceAddressRanges.AsSpan().SequenceEqual(built.Snapshot.DeviceAddressRanges))
            parts.Add("ranges");
        if (stale.Bytes.Length != built.Bytes.Length || !stale.RangeAddresses.AsSpan().SequenceEqual(built.RangeAddresses))
            parts.Add("reads_moved");
        var signature = parts.Count == 0 ? "none" : string.Join("+", parts);
        _staleDifferences.AddOrUpdate(signature, 1, static (_, count) => count + 1);
    }

    private static void DifferingWords(string name, uint[][] left, uint[][] right, SortedSet<string> parts)
    {
        if (left.Length != right.Length)
        {
            parts.Add($"{name}.count");
            return;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index].Length != right[index].Length)
            {
                parts.Add($"{name}.length");
                continue;
            }

            for (var word = 0; word < left[index].Length; word++)
                if (left[index][word] != right[index][word])
                    parts.Add($"{name}.w{word}");
        }
    }

    private static string TakeStaleDifferenceReport()
    {
        if (_staleDifferences.IsEmpty)
            return string.Empty;
        var ranked = _staleDifferences.ToArray().OrderByDescending(pair => pair.Value).Take(10).ToArray();
        _staleDifferences.Clear();
        return Environment.NewLine + string.Join(Environment.NewLine,
            ranked.Select(pair => FormattableString.Invariant($"[PERF][RESOURCE_STALE_DIFF] n={pair.Value} changed={pair.Key}")));
    }

    // The first part a cached result differs from a fresh one in, or null when they are the same.
    private static string? Difference(ResourceSnapshot cached, ResourceSpecialization cachedSpecialization,
        ResourceSnapshot fresh, ResourceSpecialization freshSpecialization)
    {
        static string? Words(string name, uint[][] left, uint[][] right)
        {
            if (left.Length != right.Length)
                return $"{name}.count {left.Length}!={right.Length}";
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index].Length != right[index].Length)
                    return $"{name}[{index}].length";
                for (var word = 0; word < left[index].Length; word++)
                    if (left[index][word] != right[index][word])
                        return $"{name}[{index}][{word}] cached=0x{left[index][word]:X} fresh=0x{right[index][word]:X}";
            }

            return null;
        }

        if (!cachedSpecialization.Equals(freshSpecialization))
            return "specialization";
        if (Words("buffer", cached.Buffers, fresh.Buffers) is { } buffers)
            return buffers;
        if (Words("image", cached.Images, fresh.Images) is { } images)
            return images;
        if (Words("sampler", cached.Samplers, fresh.Samplers) is { } samplers)
            return samplers;
        if (cached.FlattenedResourceTable.Length != fresh.FlattenedResourceTable.Length)
            return "table.length";
        for (var slot = 0; slot < cached.FlattenedResourceTable.Length; slot++)
            if (cached.FlattenedResourceTable[slot] != fresh.FlattenedResourceTable[slot])
                return $"table[{slot}] cached=0x{cached.FlattenedResourceTable[slot]:X} fresh=0x{fresh.FlattenedResourceTable[slot]:X}";
        if (!cached.DeviceAddressRanges.AsSpan().SequenceEqual(fresh.DeviceAddressRanges))
            return "ranges";
        if (!cached.UserData.AsSpan().SequenceEqual(fresh.UserData))
            return "user_data";
        return null;
    }

    public enum MaterializeOutcome { Failed, Hit, RebaseHit, TableRefresh, Volatile, BecameVolatile, FirstSighting, Miss, Uncacheable, Count }

    private static readonly bool OutcomeTimingEnabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
    private const int OutcomeSides = 3;
    private static readonly string[] OutcomeSideNames = ["render", "stream", "pool"];
    private static readonly ThreadCounter[] _outcomeCounts = Enumerable.Range(0, OutcomeSides * (int)MaterializeOutcome.Count).Select(_ => new ThreadCounter()).ToArray();
    private static readonly ThreadCounter[] _outcomeTicks = Enumerable.Range(0, OutcomeSides * (int)MaterializeOutcome.Count).Select(_ => new ThreadCounter()).ToArray();
    private static readonly ThreadCounter[] _outcomeLookupTicks = Enumerable.Range(0, OutcomeSides).Select(_ => new ThreadCounter()).ToArray();
    private static readonly ThreadCounter[] _outcomeValidatedVariants = Enumerable.Range(0, OutcomeSides).Select(_ => new ThreadCounter()).ToArray();
    [ThreadStatic]
    private static int _outcomeSide;
    private MaterializeOutcome _outcome;
    private long _lookupTicks;
    private long _validatedVariants;

    private static int OutcomeSide()
    {
        if (_outcomeSide == 0)
        {
            var name = Thread.CurrentThread.Name;
            _outcomeSide = name == "SharpEmu command stream" ? 2 : name is not null && name.StartsWith("SharpEmu resolve worker", StringComparison.Ordinal) ? 3 : 1;
        }
        return _outcomeSide - 1;
    }

    public bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure,
        ResidentGuestBytesComparer? residentComparer = null) // [local] SHARPEMU_VALIDATE_IN_PLACE
    {
        CurrentPlanHash = plan.Hash; // [local] for overlay diagnostics
        _outcome = MaterializeOutcome.Failed;
        var timing = new MaterializationTiming(MissDiagnosticsEnabled);
        try
        {
            if (!OutcomeTimingEnabled)
            {
                return MaterializeCore(plan, inputs, residentReader, ref snapshot, ref specialization, out failure, ref timing, residentComparer);
            }

            _outcome = MaterializeOutcome.Failed;
            _validatedVariants = 0;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = MaterializeCore(plan, inputs, residentReader, ref snapshot, ref specialization, out failure, ref timing, residentComparer);
            var ended = System.Diagnostics.Stopwatch.GetTimestamp();
            var outcome = result ? _outcome : MaterializeOutcome.Failed;
            var side = OutcomeSide();
            var slot = side * (int)MaterializeOutcome.Count + (int)outcome;
            _outcomeCounts[slot].Increment();
            _outcomeTicks[slot].Add(ended - started);
            _outcomeLookupTicks[side].Add(_lookupTicks - started);
            _outcomeValidatedVariants[side].Add(_validatedVariants);
            return result;
        }
        finally
        {
            timing.Complete(plan);
        }
    }

    private static long NormalizationOf(ShaderResourcePlan plan) => plan.ImageNormalizationVersion?.Invoke() ?? 0;
    public MaterializeOutcome LastOutcome => _outcome;

    public static string TakeOutcomeReport()
    {
        static string Ms(long ticks) => FormattableString.Invariant($"{ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}");
        var lines = new List<string>();
        for (var side = 0; side < OutcomeSides; side++)
        {
            var parts = new List<string>();
            for (var index = 0; index < (int)MaterializeOutcome.Count; index++)
            {
                var count = _outcomeCounts[side * (int)MaterializeOutcome.Count + index].Take();
                var ticks = _outcomeTicks[side * (int)MaterializeOutcome.Count + index].Take();
                if (count != 0)
                    parts.Add(FormattableString.Invariant($"{(MaterializeOutcome)index}={count}/{Ms(ticks)}ms"));
            }

            lines.Add("[PERF][RESOURCE_OUTCOMES] thread=" + OutcomeSideNames[side] + " " +
                FormattableString.Invariant($"lookup_ms={Ms(_outcomeLookupTicks[side].Take())} ") +
                FormattableString.Invariant($"validated_variants={_outcomeValidatedVariants[side].Take()} ") +
                string.Join(" ", parts));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private bool MaterializeCore(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure,
        ref MaterializationTiming timing,
        ResidentGuestBytesComparer? residentComparer = null)
    {
        // A frontend image conversion can depend on GPU attachments or detile guest
        // texels. Unchanged descriptor bytes do not prove that its result is current.
        if (plan.NormalizeImageDescriptor is not null && plan.Info.Images.Count != 0 && plan.ImageNormalizationVersion is null)
        {
            Uncacheable++;
            _totalUncacheable.Increment();
            return ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, out failure);
        }
        var normalization = NormalizationOf(plan);
        var drawKey = DrawKeyOf(plan, inputs);
        _learnedUserData.TryGetValue(drawKey, out var learned);
        var rebase = RebaseOf(drawKey, plan);
        var use = ContentKeyed ? plan.UserDataUse : null; // [local] SHARPEMU_MATERIALIZATION_CONTENT_KEY
        var key = KeyOf(drawKey, inputs.UserData, learned, rebase.Active, use);
        var found = TryFind(key, plan, inputs, rebase.Active, out var cached);
        _lookupTicks = OutcomeTimingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        // [local] SHARPEMU_PROGRAM_GATE_TABLES: a stamped plan's entries serve only while the scans their walk used are held.
        var stamps = StampsTables(plan);
        var current = stamps && _currentStoreTables; // [local] the tables are evaluated again instead of stamped
        if (found && stamps && !current)
        {
            var scansHeld = false;
            for (var variant = cached; variant is not null && !scansHeld; variant = variant.Next)
                scansHeld = TablesCurrent(variant);
            if (!scansHeld)
            {
                Interlocked.Increment(ref _totalTableStale);
                found = false; // every variant predates a table invalidation: walk, and drop them
            }
        }

        Entry? stale = null;
        (List<(int Buffer, uint Word)> Patches, ResourceSnapshot Snapshot, Entry From)? learning = null;
        Entry? shifted = null; // [local] SHARPEMU_MATERIALIZE_REBASE
        (ResourceSnapshot Snapshot, ResourceSpecialization Specialization)? syncCheck = null; // [local] SHARPEMU_MATERIALIZE_SYNC_VALIDATE=2
        if (found)
        {
            if (cached.VolatileLookups > 0)
            {
                _outcome = MaterializeOutcome.Volatile;
                cached.VolatileLookups--;
                _totalVolatile.Increment();
                return MaterializeUnrecorded(plan, inputs, ref snapshot, ref specialization, out failure);
            }

            if (StaleLog) _lastStaleReason = "nomatch";
            var unreadable = false;
            Entry? previous = null;
            var variantIndex = 0;
            var refreshCandidates = 0;
            var rejection = default(RejectionSample);
            for (var variant = cached; variant is not null; previous = variant, variant = variant.Next)
            {
                CountDiagnostic((Diagnostic)((int)Diagnostic.Variant1 + Math.Min(variantIndex++, MaxVariants - 1)));
                if (!variant.Matches(plan, inputs, rebase.Active) || !rebase.AppliesTo(variant) ||
                    (stamps && !current && !TablesCurrent(variant)))
                    continue;
                stale ??= variant;
                if (rebase.Active.Length == 0 && !rebase.HasLearnableMemoryBases && rejection.Rejects(variant))
                {
                    CountDiagnostic(Diagnostic.RejectionPrecheck);
                    CountDiagnostic(Diagnostic.RefreshDescriptorSkipped);
                    continue;
                }
                var validateStarted = MaterializeSplitProfile.Start(); // [local]
                var validated = Validate(variant, residentReader, rebase, inputs.UserData,
                    out var variantUnreadable, out var descriptorChanged, out rejection, inputs, residentComparer, WordPatchesEnabled);
                MaterializeSplitProfile.Note(MaterializeSplitProfile.Kind.Validate, validateStarted);
                _validatedVariants++;
                if (validated && StaleLog) _lastStaleReason = "snapshot";
                if (validated && _statisticsDiffer && !KeepsImageGroups(variant))
                {
                    validated = false;
                    descriptorChanged = true;
                }

                // [local] The hit's snapshot: the draw's user data, word patches and buffer dwords evaluated again
                // (SnapshotFor), then upstream's rebased base dwords; null when a buffer dword changed beyond its base.
                var syncValidated = _syncValidated;
                ResourceSnapshot? hit = null;
                if (validated)
                {
                    hit = HitSnapshot(variant, inputs, rebase, _patches);
                    if (hit is null)
                        descriptorChanged = true;
                    else if (current && !CurrentTables(variant, inputs, ref hit))
                        hit = null;
                }

                if (hit is not null)
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

                    _outcome = MaterializeOutcome.Hit;
                    variant.StaleLookups = 0;
                    variant.VolatilePeriod = 0;
                    Hits++;
                    _totalHits.Increment();
                    snapshot = hit;
                    if (_statisticsDiffer)
                    {
                        snapshot = WithMipStatistics(snapshot, MipStatisticsOf(variant));
                        _totalStatisticsMaskedHits.Increment();
                    }

                    specialization = variant.Specialization;
                    failure = default;
                    return true;
                }

                if (syncValidated)
                    NoteSyncValidate(plan.Hash, hit: false);
                unreadable |= variantUnreadable;
                // A rejected candidate can fall back to a full walk, but cannot accept stale bytes.
                if (!variantUnreadable && !descriptorChanged && variant.TableRefreshable)
                    refreshCandidates |= 1 << (variantIndex - 1);
                else if (descriptorChanged)
                    CountDiagnostic(Diagnostic.RefreshDescriptorSkipped);
            }

            if (syncCheck is not null)
                goto Walk;

            timing.ChangePhase(MaterializationPhase.TableRefresh);
            var refreshStarted = MaterializeSplitProfile.Start(); // [local]
            // Moved bases first: a changed base word is a descriptor word, which a table refresh
            // would read in full only to refuse.
            ResourceSnapshot rebased = null!;
            List<(int Buffer, uint Word)> patches = null!;
            // Each variant is tried: a key's variants are often the same draw with different
            // descriptors (materials), and only the one this draw binds can be rebased.
            Entry? refreshedFrom = null;
            _lastRefreshRefusal = 0;
            Entry? beforeRefreshed = null;
            if (stale is not null && rebase.HasLearnableMemoryBases)
            {
                for (Entry? previousVariant = null, variant = cached; variant is not null; previousVariant = variant, variant = variant.Next)
                {
                    // [local] Only a variant read at its recorded addresses for this draw: one whose pointers moved
                    // (SHARPEMU_MATERIALIZATION_CONTENT_KEY) would be read where the pointers no longer point.
                    if (!variant.MatchesUsed(plan, inputs, rebase.Active) || !rebase.AppliesTo(variant) ||
                        (stamps && !current && !TablesCurrent(variant)))
                        continue;
                    if (TryRebaseRefresh(variant, plan, inputs, residentReader, rebase, out rebased, out patches))
                    {
                        refreshedFrom = variant;
                        beforeRefreshed = previousVariant;
                        break;
                    }
                }
            }

            if (refreshedFrom is not null)
            {
                if (rebase.AreActiveMemoryBases(patches) && (!current || CurrentTables(refreshedFrom, inputs, ref rebased)))
                {
                    MaterializeSplitProfile.Note(MaterializeSplitProfile.Kind.Refresh, refreshStarted);
                    if (beforeRefreshed is not null)
                    {
                        beforeRefreshed.Next = refreshedFrom.Next;
                        refreshedFrom.Next = cached;
                        Store(key, refreshedFrom);
                    }

                    _outcome = MaterializeOutcome.RebaseHit;
                    Hits++;
                    _totalHits.Increment();
                    snapshot = rebased;
                    specialization = refreshedFrom.Specialization;
                    failure = default;
                    return true;
                }

                learning = (patches, rebased, refreshedFrom);
            }
            if (refreshedFrom is null)
            {
                previous = null;
                variantIndex = 0;
                for (var variant = cached; variant is not null; previous = variant, variant = variant.Next, variantIndex++)
                {
                    var fixedAddresses = rebase.Active.Length == 0 && !rebase.HasLearnableMemoryBases;
                    // [local] As above: a variant whose pointers moved is not refreshed (SHARPEMU_MATERIALIZATION_CONTENT_KEY).
                    if (!variant.MatchesUsed(plan, inputs, rebase.Active) || !rebase.AppliesTo(variant) ||
                        (stamps && !current && !TablesCurrent(variant)) ||
                        (fixedAddresses ? (refreshCandidates & (1 << variantIndex)) == 0 : !ReferenceEquals(variant, cached)) ||
                        !TryRefreshTable(key, variant, plan, inputs, residentReader, rebase, out var refreshed))
                        continue;

                    refreshed.UsedScans = variant.UsedScans; // [local]
                    var refreshedSnapshot = HitSnapshot(refreshed, inputs, rebase, _noPatches);
                    if (refreshedSnapshot is null || (current && !CurrentTables(refreshed, inputs, ref refreshedSnapshot)))
                        continue;
                    if (previous is not null)
                    {
                        previous.Next = variant.Next;
                        refreshed.Next = cached;
                        CountDiagnostic(Diagnostic.RefreshOlderVariant);
                    }
                    Store(key, refreshed);
                    MaterializeSplitProfile.Note(MaterializeSplitProfile.Kind.Refresh, refreshStarted);
                    _outcome = MaterializeOutcome.TableRefresh;
                    TableRefreshes++;
                    _totalRefreshes.Increment();
                    snapshot = refreshedSnapshot;
                    specialization = refreshed.Specialization;
                    failure = default;
                    return true;
                }

            }
            MaterializeSplitProfile.Note(MaterializeSplitProfile.Kind.Refresh, refreshStarted);

            // [local] SHARPEMU_MATERIALIZE_REBASE: a stale entry moved as a whole by one ring delta (TryShift). Tried after
            // upstream's AddressRebase and only on variants that rebase leaves where they are, so the two never move the
            // same entry.
            var rebaseMode = RebaseMode;
            if (rebaseMode != 0 && refreshedFrom is null)
            {
                for (var variant = cached; variant is not null && shifted is null; variant = variant.Next)
                {
                    if (!variant.Matches(plan, inputs, rebase.Active) || rebase.Moves(variant, inputs.UserData) ||
                        (stamps && !current && !TablesCurrent(variant)))
                        continue;
                    var shiftStarted = MaterializeSplitProfile.Start(); // [local]
                    shifted = TryShift(variant, inputs, residentReader);
                    MaterializeSplitProfile.Note(MaterializeSplitProfile.Kind.Shift, shiftStarted);
                    if (shifted is not null)
                    {
                        shifted.UsedScans = variant.UsedScans;
                        var shiftedSnapshot = shifted.Snapshot; // [local] owned: CurrentTables updates the entry
                        if (current && !CurrentTables(shifted, inputs, ref shiftedSnapshot))
                            shifted = null;
                    }
                }

                if (shifted is not null)
                {
                    Interlocked.Increment(ref _rebaseCandidates);
                    var trust = _rebaseTrust.GetValueOrDefault(plan.Hash);
                    var sampled = rebaseMode is 1 or 4;
                    if (rebaseMode == 3 || (sampled && trust >= RebaseConfirmations && (++_rebaseUses & RebaseSampleMask) != 0))
                    {
                        shifted.Next = cached;
                        TrimVariants(shifted);
                        Store(key, shifted);
                        _outcome = MaterializeOutcome.RebaseHit;
                        Hits++;
                        _totalHits.Increment();
                        Interlocked.Increment(ref _rebaseHits);
                        RebaseHits++;
                        NoteWatched(plan.Hash, hit: true);
                        snapshot = shifted.Snapshot;
                        specialization = shifted.Specialization;
                        failure = default;
                        return true;
                    }

                    if (trust < 0 && sampled)
                        shifted = null;
                }
            }

            _totalStale.Increment();
            _refreshRefusals[refreshedFrom is not null ? 5 : _lastRefreshRefusal].Increment();
            if (unreadable)
                _totalStaleUnreadable.Increment();
            if (StaleLog)
                NoteStale(plan.Hash, cached);
            // [local] A shift candidate still to be verified is walked and compared, not skipped as volatile.
            if (_sightings is not null && shifted is null && ++cached.StaleLookups >= VolatileStaleLookups)
            {
                _outcome = MaterializeOutcome.BecameVolatile;
                cached.VolatilePeriod = Math.Clamp(cached.VolatilePeriod * 2, MinVolatilePeriod, MaxVolatilePeriod);
                cached.VolatileLookups = cached.VolatilePeriod;
                cached.StaleLookups = VolatileStaleLookups - 2;
                cached.Next = null;
                _totalVolatile.Increment();
                return MaterializeUnrecorded(plan, inputs, ref snapshot, ref specialization, out failure);
            }
        }
        else if (_sightings is { } sightings)
        {
            ref var sighting = ref sightings[key & (SightingSlots - 1)];
            NoteChurn(drawKey, inputs.UserData, learned, rebase);
            if (sighting != key)
            {
                _outcome = MaterializeOutcome.FirstSighting;
                sighting = key;
                _totalFirstSightings.Increment();
                return MaterializeUnrecorded(plan, inputs, ref snapshot, ref specialization, out failure);
            }

            sighting = 0;
        }
        else
        {
            _totalNotFound.Increment();
        }

    Walk:
        timing.ChangePhase(MaterializationPhase.Materialization);
        _outcome = MaterializeOutcome.Miss;
        Misses++;
        RecordMiss(plan, key, found);
        _totalMisses.Increment();
        var recorder = TakeRecorder();
        recorder.AllowBlocks(); // [local] SHARPEMU_RECORDER_BLOCKS
        var missStarted = MissLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            var recording = new ResourceRuntimeInputs
            {
                UserData = recorder.WrapUserData(inputs.UserData),
                ShaderBase = inputs.ShaderBase,
                ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
                ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
                ReadResidentMemory = recorder.WrapResident(inputs.ReadResidentMemory),
                SynchronizeTableRange = inputs.SynchronizeTableRange, // [local] SHARPEMU_STORE_TABLE_SYNC
                // [local] SHARPEMU_STORE_TABLE_COMPARE: only when the walk's table scans bypass the recorder (GATE_TABLES=3).
                CompareTableChunk = stamps && current ? inputs.CompareTableChunk : null,
                ReadsClean = inputs.ReadsClean,
                ComputeState = inputs.ComputeState,
                TablePhase = recorder.TablePhase,
            };
            var walkStarted = MaterializeSplitProfile.Start(); // [local]
            if (stamps)
                DeviceStoreTablePlanner.BeginUsedScans(unrecorded: current);
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
                _outcome = MaterializeOutcome.Uncacheable;
                Uncacheable++;
                _totalUncacheable.Increment();
                return true;
            }

            timing.ChangePhase(MaterializationPhase.CacheStore);
            var buildStarted = MaterializeSplitProfile.Start(); // [local]
            var bigBuild = recorder.RecordedWords > MaterializeSplitProfile.BigReads;
            var built = recorder.Build(plan, inputs, snapshot, specialization, rebase.MemoryBases, normalization);
            built.UsedScans = usedScans;
            MaterializeSplitProfile.Note(bigBuild ? MaterializeSplitProfile.Kind.BigBuild : MaterializeSplitProfile.Kind.Build, buildStarted);
            if (shifted is not null)
                NoteRebaseVerdict(plan.Hash, shifted, snapshot, specialization, built, null);
            if (StaleDiffEnabled && stale is not null && learning is null)
                NoteStaleDifference(stale, built);
            if (learning is { } pending)
            {
                var agrees = Difference(pending.Snapshot, pending.From.Specialization, built.Snapshot, built.Specialization) is null;
                rebase.ConfirmMemoryBases(pending.Patches, agrees);
            }

            var activeBefore = rebase.Active;
            Learn(rebase, built, plan, inputs, residentReader);
            var widened = Union(learned, built.UsedUserData);
            if (!ReferenceEquals(widened, learned) || !ReferenceEquals(activeBefore, rebase.Active))
            {
                _learnedUserData[drawKey] = widened;
                key = KeyOf(drawKey, inputs.UserData, widened, rebase.Active, use);
                found = false;
            }

            if (found)
            {
                built.StaleLookups = cached.StaleLookups;
                built.VolatilePeriod = cached.VolatilePeriod;
                built.Next = cached;
                var depth = 1;
                for (var variant = built; variant.Next is not null; variant = variant.Next)
                {
                    if (depth++ >= MaxVariants)
                    {
                        variant.Next = null;
                        break;
                    }
                }
            }

            Store(key, built);
            if (built.Retained) // [local] SHARPEMU_MATERIALIZE_POH: the draw gets the entry's snapshot, as a hit would
                snapshot = built.Snapshot;
            return true;
        }
        finally
        {
            if (MissLog)
                NoteMiss(plan.Hash, found, recorder.RecordedWords, System.Diagnostics.Stopwatch.GetTimestamp() - missStarted);
            ReturnRecorder(recorder);
        }
    }

    // [local] A validated variant's snapshot for this draw: Entry.SnapshotFor (the draw's user data, the words it read
    // differently in the places they are copied to, buffer dwords built from user data evaluated again), then
    // upstream's AddressRebase base dwords. Null when a buffer dword changed beyond what may change.
    private static ResourceSnapshot? HitSnapshot(Entry entry, ResourceRuntimeInputs inputs, AddressRebase rebase,
        List<(int Word, uint Value)> patches) =>
        entry.SnapshotFor(inputs, patches) is { } snapshot ? rebase.Apply(snapshot, inputs.UserData) : null;

    private readonly List<(int Word, uint Value)> _noPatches = new();

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

    // Refresh only table words. Descriptor bytes and the reevaluated reads must pass fresh checks.
    private bool TryRefreshTable(ulong key, Entry cached, ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader, AddressRebase rebase, out Entry refreshed)
    {
        refreshed = null!;
        _lastRefreshRefusal = 0;
        if (!cached.TableRefreshable)
            return RejectRefresh(Diagnostic.RefreshLayout);

        var moves = rebase.Moves(cached, inputs.UserData);
        if (!moves && _refreshScratch.Length < cached.Bytes.Length)
            _refreshScratch = new byte[cached.Bytes.Length];
        var current = moves ? new byte[cached.Bytes.Length] : _refreshScratch;
        var currentLength = cached.Bytes.Length;
        var moved = cached.RangeAddresses;
        if (rebase.Moves(cached, inputs.UserData))
        {
            moved = new ulong[cached.RangeAddresses.Length];
            for (var index = 0; index < moved.Length; index++)
                moved[index] = rebase.MoveRead(cached, inputs.UserData, cached.RangeAddresses[index], cached.RangeLengths[index]);
        }

        // [local] SHARPEMU_VALIDATE_IN_PLACE: the change scan skips equal runs at once (the same verdict; =2 also runs the
        // word-by-word scan and counts a different verdict).
        var fastScan = ValidateInPlaceMode != 0;
        var changed = false;
        for (var index = 0; index < cached.RangeAddresses.Length; index++)
        {
            if (!ReadWithProfile(residentReader, moved[index], current.AsSpan(cached.RangeOffsets[index], cached.RangeLengths[index]), cached.RangeClean[index], CacheDetail.RefreshRead))
            {
                _lastRefreshRefusal = 1;
                return false;
            }
            using var comparisonProfile = MeasureDetail(CacheDetail.RefreshCompare);
            var start = cached.RangeOffsets[index];
            var end = start + cached.RangeLengths[index];
            bool? rangeChanged;
            if (fastScan)
            {
                rangeChanged = RangeTableWordsChangedSkippingEqual(current, cached, start, end);
                if (ValidateInPlaceMode == 2)
                {
                    Interlocked.Increment(ref _refreshScanChecked);
                    if (rangeChanged != RangeTableWordsChanged(current, cached, start, end))
                    {
                        Interlocked.Increment(ref _refreshScanMismatches);
                        Interlocked.Increment(ref _inPlaceMismatchTotal);
                        if (Interlocked.Increment(ref _inPlaceMismatchLogs) <= 20)
                            Console.Error.WriteLine($"[REFRESH_SCAN_MISMATCH] plan=0x{plan.Hash:X16} range={index} bytes={end - start} fast={rangeChanged}");
                    }
                }
            }
            else
            {
                rangeChanged = RangeTableWordsChanged(current, cached, start, end);
            }

            if (rangeChanged is null)
            {
                _lastRefreshRefusal = 2;
                return RejectRefresh(Diagnostic.RefreshDescriptorChanged);
            }

            changed |= rangeChanged.Value;
        }

        if (!changed)
        {
            _lastRefreshRefusal = 3;
            return false;
        }

        _lastRefreshRefusal = 4;

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
                SynchronizeTableRange = inputs.SynchronizeTableRange, // [local] SHARPEMU_STORE_TABLE_SYNC
                ReadsClean = inputs.ReadsClean,
                ComputeState = inputs.ComputeState,
            };
            var cachedTable = cached.Snapshot.FlattenedResourceTable;
            ReadingTable = true;
            bool evaluated;
            uint[] table;
            try
            {
                using var evaluationProfile = MeasureDetail(CacheDetail.RefreshEvaluate);
                evaluated = ResourceMaterializer.TryEvaluateTable(plan, recording, out table);
            }
            finally
            {
                ReadingTable = false;
            }

            if (!evaluated || recorder.Failed || table.Length != cachedTable.Length)
                return RejectRefresh(Diagnostic.RefreshEvaluation);

            using (MeasureDetail(CacheDetail.RefreshVerify))
            foreach (var (address, word, _, _) in recorder.Reads)
            {
                var found = ReferenceEquals(moved, cached.RangeAddresses)
                    ? TryFindWord(cached, address, out var offset)
                    : TryFindMovedWord(cached, moved, address, out offset);
                if (!found || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset, sizeof(uint))) != word)
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
                UsedUserData = cached.UsedUserData,
                ShaderBase = cached.ShaderBase,
                ComputeState = cached.ComputeState,
                RangeAddresses = cached.RangeAddresses,
                RangeOffsets = cached.RangeOffsets,
                RangeLengths = cached.RangeLengths,
                RangeClean = cached.RangeClean,
                RangeBase = cached.RangeBase, // [local]
                RangeReadBase = cached.RangeReadBase, // [local]
                ExactUserData = cached.ExactUserData, // [local]
                WordPatches = cached.WordPatches, // [local]
                WordTableOnly = cached.WordTableOnly,
                BaseWordBuffers = cached.BaseWordBuffers,
                ImageStatisticsWords = cached.ImageStatisticsWords,
                TableRefreshable = true,
                Normalization = cached.Normalization,
                // Published entries must not share mutable scratch storage.
                Bytes = current.AsSpan(0, currentLength).ToArray(),
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
            return true;
        }
        finally
        {
            ReturnRecorder(recorder);
        }
    }

    private static long _refreshScanChecked, _refreshScanMismatches;

    // [local] Of one recorded range [start, end) of the entry's bytes: true when only flattened-table words changed (some
    // did), false when none did, null when another word changed.
    private static bool? RangeTableWordsChanged(ReadOnlySpan<byte> current, Entry cached, int start, int end)
    {
        var changed = false;
        for (var offset = start; offset < end; offset += sizeof(uint))
        {
            if (current.Slice(offset, sizeof(uint)).SequenceEqual(cached.Bytes.AsSpan(offset, sizeof(uint))))
                continue;
            if (!cached.WordTableOnly[offset / sizeof(uint)])
                return null;
            changed = true;
        }

        return changed;
    }

    // [local] RangeTableWordsChanged with every run of equal bytes skipped at once.
    private static bool? RangeTableWordsChangedSkippingEqual(ReadOnlySpan<byte> current, Entry cached, int start, int end)
    {
        var changed = false;
        var now = current[start..end];
        var was = cached.Bytes.AsSpan(start, end - start);
        var offset = 0;
        while (offset < now.Length)
        {
            offset = NextChangedWord(now, was, offset);
            if (offset >= now.Length)
                break;
            if (!cached.WordTableOnly[(start + offset) / sizeof(uint)])
                return null;
            changed = true;
            offset += sizeof(uint);
        }

        return changed;
    }

    private bool MaterializeUnrecorded(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization, out ResourceMaterializationFailure failure)
    {
        Unrecorded++;
        return ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, out failure);
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

    // The byte offset of a dword read at address when the entry's ranges lie at moved addresses.
    private static bool TryFindMovedWord(Entry entry, ulong[] moved, ulong address, out int offset)
    {
        // moved may be a reused buffer longer than the entry's ranges.
        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            if (address < moved[index])
                continue;
            var delta = address - moved[index];
            if (delta % sizeof(uint) == 0 && delta < (ulong)entry.RangeLengths[index])
            {
                offset = entry.RangeOffsets[index] + (int)delta;
                return true;
            }
        }

        offset = 0;
        return false;
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

    private static ulong DrawKeyOf(ShaderResourcePlan plan, ResourceRuntimeInputs inputs) =>
        DrawKeyOf(plan, inputs.ShaderBase, inputs.ComputeState, inputs.UserData.Count);

    private static ulong DrawKeyOf(ShaderResourcePlan plan, ulong shaderBase, ComputeSelectorState? computeState, int userDataCount)
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(plan));
        hash.Add(shaderBase);
        hash.Add(computeState);
        hash.Add(userDataCount);
        return (ulong)(uint)hash.ToHashCode() | ((ulong)(uint)RuntimeHelpers.GetHashCode(plan) << 32);
    }

    public bool Revalidate(ResourceSnapshot snapshot, ResidentGuestBytesReader residentReader)
    {
        if (snapshot.Origin is not Entry entry || entry.VolatileLookups > 0)
        {
            _totalRevalidateUnlinked.Increment();
            return false;
        }

        var rebase = RebaseOf(DrawKeyOf(entry.Plan, entry.ShaderBase, entry.ComputeState, entry.UserData.Length), entry.Plan);
        // [local] A word read differently fails here even where a hit would patch it: the held snapshot has the old
        // word (the overload takes no patches). A stamped plan also needs its store-table scans still held.
        if (!Validate(entry, residentReader, rebase, snapshot.UserData, out _) ||
            (StampsTables(entry.Plan) && !_currentStoreTables && !TablesCurrent(entry)))
        {
            _totalRevalidateStale.Increment();
            return false;
        }

        _totalRevalidated.Increment();
        return true;
    }

    // Only the words the draw's shader has been seen to read take part, less the rebased ones: the
    // key is an index, and Entry.Matches decides whether an entry is reusable.
    // [local] With use (SHARPEMU_MATERIALIZATION_CONTENT_KEY), a word the plan only reads through or builds buffer
    // dwords from contributes nothing either: entries based on other pointers are found by the key and told apart by
    // what they read.
    private static ulong KeyOf(ulong drawKey, IReadOnlyList<uint> userData, ulong[]? learned, ulong[]? rebased = null,
        UserDataUseAnalysis? use = null)
    {
        var low = (uint)drawKey;
        var high = (uint)(drawKey >> 32) ^ 0x9E3779B9u;
        if (learned is not null)
        {
            for (var index = 0; index < userData.Count; index++)
            {
                if (!IsUsed(learned, index) || (rebased is not null && IsUsed(rebased, index)) ||
                    (use is not null && !use.IsValueRegister(index)))
                    continue;
                var word = userData[index];
                low = (low ^ word) * 0x01000193u;
                high = ((high + word) * 0x85EBCA6Bu) ^ (high >> 13);
            }
        }

        return ((ulong)high << 32) | low;
    }

    private static bool IsUsed(ulong[] mask, int index) =>
        (uint)(index >> 6) < (uint)mask.Length && (mask[index >> 6] & (1UL << (index & 63))) != 0;

    // The same array when nothing new was learned, so the caller can tell.
    private static ulong[] Union(ulong[]? learned, ulong[] used)
    {
        if (learned is null)
            return used;
        var changed = learned.Length < used.Length;
        for (var index = 0; !changed && index < used.Length; index++)
            changed = (used[index] & ~learned[index]) != 0;
        if (!changed)
            return learned;
        var merged = new ulong[Math.Max(learned.Length, used.Length)];
        for (var index = 0; index < merged.Length; index++)
            merged[index] = (index < learned.Length ? learned[index] : 0) | (index < used.Length ? used[index] : 0);
        return merged;
    }

    // A cached snapshot is shared by every draw that hits it, so it keeps the user data of the draw
    // that built it; a draw whose unread words differ gets its own copy that carries its own.
    private static ResourceSnapshot WithCurrentUserData(ResourceSnapshot cached, IReadOnlyList<uint> userData)
    {
        var words = cached.UserData;
        var same = words.Length == userData.Count;
        for (var index = 0; same && index < words.Length; index++)
            same = words[index] == userData[index];
        if (same)
            return cached;
        var current = new uint[userData.Count];
        for (var index = 0; index < current.Length; index++)
            current[index] = userData[index];
        return new ResourceSnapshot
        {
            Buffers = cached.Buffers,
            Images = cached.Images,
            Samplers = cached.Samplers,
            FlattenedResourceTable = cached.FlattenedResourceTable,
            UserData = current,
            DeviceAddressRanges = cached.DeviceAddressRanges,
            Origin = cached.Origin,
        };
    }

    private bool TryFind(ulong key, ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ulong[] rebased, out Entry entry)
    {
        if (_young.TryGetValue(key, out entry!))
        {
            var matches = entry.Matches(plan, inputs, rebased);
            if (!matches) CountDiagnostic(Diagnostic.KeyMismatch);
            return matches;
        }
        if (!_old.TryGetValue(key, out entry!))
        {
            CountDiagnostic(Diagnostic.KeyAbsent);
            return false;
        }
        if (!entry.Matches(plan, inputs, rebased))
        {
            CountDiagnostic(Diagnostic.KeyMismatch);
            return false;
        }
        CountDiagnostic(Diagnostic.OldPromotion);
        _old.Remove(key);
        Store(key, entry);
        return true;
    }

    private void Store(ulong key, Entry entry)
    {
        if (RetainPinned && !entry.Retained) // [local] SHARPEMU_MATERIALIZE_POH
            Retain(entry);
        entry.Snapshot.Origin = entry;
        if (_young.Count >= _generationCapacity && !_young.ContainsKey(key))
        {
            CountDiagnostic(Diagnostic.GenerationRotation);
            CountDiagnostic(Diagnostic.GenerationEntriesDropped, _old.Count);
            RecordEvictions(_old.Keys);
            _old = _young;
            _young = new Dictionary<ulong, Entry>(_generationCapacity);
        }

        _young[key] = entry;
    }

    // What a hit on the previous entry would give this draw with the words in probe rebased: its
    // reads moved and its bytes unchanged, or its memory-loaded bases moved as well. Null when
    // neither path applies, which says nothing about the words.
    private (ResourceSnapshot Snapshot, List<(int Buffer, uint Word)>? Patches)? Predict(Entry previous, ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs, ResidentGuestBytesReader residentReader, AddressRebase rebase, ulong[] probe) =>
        rebase.WithActive<(ResourceSnapshot, List<(int Buffer, uint Word)>?)?>(probe, () =>
        {
            // [local] MatchesUsed: the prediction reads the entry where upstream's rebase alone puts it.
            if (!previous.MatchesUsed(plan, inputs, probe) || !rebase.AppliesTo(previous))
                return null;
            if (Validate(previous, residentReader, rebase, inputs.UserData, out _))
                return HitSnapshot(previous, inputs, rebase, _noPatches) is { } predicted ? (predicted, null) : null;
            if (rebase.HasLearnableMemoryBases &&
                TryRebaseRefresh(previous, plan, inputs, residentReader, rebase, out var snapshot, out var patches))
                return (snapshot, patches);
            return null;
        });

    // Its own method: a lambda in MaterializeCore that captured the parameters made every call,
    // hits included, allocate the closure on entry.
    private void Learn(AddressRebase rebase, Entry built, ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader) =>
        rebase.Learn(built, (previous, probe) => Predict(previous, plan, inputs, residentReader, rebase, probe));

    // The table for this draw without evaluating it: a slot that reads a memory-based buffer at a
    // constant offset takes the word now at that offset, which the moved window has just read; the
    // other slots read nothing that changed, so they keep the entry's words, except a base slot,
    // which the patch has already moved. current holds this draw's words in the entry's layout.
    // The table is patched in place: the caller passes a copy it owns.
    private static bool TryCopyTable(Entry entry, ShaderResourcePlan plan, AddressRebase rebase, byte[] current, uint[] table)
    {
        var sources = entry.TableCopySources ??= TableCopySourcesOf(entry, plan, rebase, table.Length);
        if (sources.Length == 0)
            return false;
        for (var slot = 0; slot < sources.Length; slot++)
        {
            var offset = sources[slot];
            if (offset >= 0)
                table[(int)plan.TableReads[slot].FlatOffset] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset));
        }

        return true;
    }

    // A table whose slots are all direct loads or reads through memory-based buffers.
    private static bool CanCopyDirect(AddressRebase rebase)
    {
        if (rebase.DirectSlots is not null)
            return true;
        return false;
    }


    // Every slot takes the dword it loads for this draw. A direct slot's address is evaluated from
    // the draw's own user data, as the table evaluation would, and its dword is taken from the range
    // this draw read around it; a slot through a memory-based buffer copies as in TryCopyTable.
    // Nothing is written unless every slot is placed.
    private static bool TryCopyDirectTable(Entry entry, ShaderResourcePlan plan, AddressRebase rebase, byte[] current,
        ulong[] moved, IReadOnlyList<uint> userData, uint[] table)
    {
        var through = entry.TableCopySources ??= TableCopySourcesOf(entry, plan, rebase, table.Length);
        var direct = rebase.DirectSlots!;
        if (through.Length != direct.Length)
            return Unplaced();
        Span<int> sources = direct.Length <= 256 ? stackalloc int[direct.Length] : new int[direct.Length];
        for (var slot = 0; slot < direct.Length; slot++)
        {
            if (plan.TableReads[slot].FlatOffset >= table.Length)
                return Unplaced();
            if (rebase.TableSlotBuffers[slot] >= 0)
            {
                if (through[slot] < 0)
                    return Unplaced();
                sources[slot] = through[slot];
                continue;
            }

            var load = direct[slot];
            if (load.Low >= userData.Count || load.High >= userData.Count)
                return Unplaced();
            var high = load.High >= 0 ? userData[load.High] : load.HighConstant;
            var baseAddress = ((high << 32) | userData[load.Low]) & 0x0000_FFFF_FFFF_FFFFul;
            if (RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarAddressWord, baseAddress, high, 0,
                    load.Immediate, load.Offset, out var address) != RuntimeValueEvaluator.RawAddress.Read ||
                !TryFindMovedWord(entry, moved, address, out sources[slot]))
                return Unplaced();
        }

        for (var slot = 0; slot < sources.Length; slot++)
            table[(int)plan.TableReads[slot].FlatOffset] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(sources[slot]));
        return true;

        static bool Unplaced()
        {
            return false;
        }
    }

    // Per table slot: the byte offset in the entry's words of the word the slot copies, or -1 for a
    // slot that keeps its word. Empty when some slot cannot be placed.
    private static int[] TableCopySourcesOf(Entry entry, ShaderResourcePlan plan, AddressRebase rebase, int tableLength)
    {
        var sources = new int[plan.TableReads.Count];
        for (var slot = 0; slot < sources.Length; slot++)
        {
            sources[slot] = -1;
            var buffer = rebase.TableSlotBuffers[slot];
            if (buffer < 0)
                continue;
            var descriptor = entry.Snapshot.Buffers[buffer];
            if (!AddressRebase.TryMovedWindow(descriptor, descriptor[0], out var start, out var size) ||
                rebase.TableSlotOffsets[slot] < 0 || (ulong)rebase.TableSlotOffsets[slot] + sizeof(uint) > size ||
                !TryFindWord(entry, start + (ulong)rebase.TableSlotOffsets[slot], out var offset) ||
                plan.TableReads[slot].FlatOffset >= tableLength)
                return [];
            sources[slot] = offset;
        }

        return sources;
    }

    // Where each memory-loaded base word of the entry lies: its range and byte offset in it.
    private static (int Range, int Offset)[] BaseWordProbesOf(Entry entry)
    {
        var probes = new List<(int Range, int Offset)>();
        for (var range = 0; range < entry.RangeAddresses.Length; range++)
        {
            for (var offset = 0; offset < entry.RangeLengths[range]; offset += sizeof(uint))
            {
                if (entry.BaseWordBuffers![(entry.RangeOffsets[range] + offset) / sizeof(uint)] >= 0)
                    probes.Add((range, offset));
            }
        }

        return probes.ToArray();
    }

    private byte[] _rebaseCurrent = [];
    private ulong[] _rebaseMoved = [];
    private readonly List<(int Buffer, uint Word)> _rebaseBases = new();

    // Per recorded range: the memory-based buffer whose window wholly holds it, or NoBase. Such a
    // range is read only once the buffer's base is known: it moves with the base.
    private static int[] RangeWindowsOf(Entry entry, AddressRebase rebase)
    {
        var windows = new int[entry.RangeAddresses.Length];
        windows.AsSpan().Fill(NoBase);
        for (var index = 0; index < windows.Length; index++)
        {
            foreach (var buffer in rebase.MemoryBases)
            {
                var descriptor = entry.Snapshot.Buffers[buffer];
                if (descriptor.Length >= 3 && AddressRebase.TryMovedWindow(descriptor, descriptor[0], out var start, out var size) &&
                    entry.RangeAddresses[index] >= start && entry.RangeAddresses[index] - start + (ulong)entry.RangeLengths[index] <= size)
                {
                    windows[index] = buffer;
                    break;
                }
            }
        }

        return windows;
    }

    // A stale entry whose only changed words are memory-loaded buffer bases, and constants only the
    // table reads: the bases are patched into a copy of the snapshot and the table is evaluated
    // again for this draw, which reads the constants where the moved bases now point. Every read of
    // that evaluation is a word the entry validated unchanged, or lies in the window a patched base
    // now covers. The descriptors and specialization are the entry's; learning checks that.
    private bool TryRebaseRefresh(Entry entry, ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader, AddressRebase rebase, out ResourceSnapshot snapshot,
        out List<(int Buffer, uint Word)> patches)
    {
        snapshot = null!;
        patches = null!;
        if (entry.BaseWordBuffers is not { } map)
            return false;
        var userData = inputs.UserData;
        // The words read here are compared and copied, never kept, so the buffers are reused.
        if (_rebaseCurrent.Length < entry.Bytes.Length)
            _rebaseCurrent = new byte[Math.Max(entry.Bytes.Length, _rebaseCurrent.Length * 2)];
        if (_rebaseMoved.Length < entry.RangeAddresses.Length)
            _rebaseMoved = new ulong[Math.Max(entry.RangeAddresses.Length, _rebaseMoved.Length * 2)];
        var current = _rebaseCurrent;
        var moved = _rebaseMoved;
        var windowed = entry.RangeWindows ??= RangeWindowsOf(entry, rebase);
        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            if (windowed[index] != NoBase)
                continue;
            moved[index] = rebase.MoveRead(entry, userData, entry.RangeAddresses[index], entry.RangeLengths[index]);
            if (!residentReader(moved[index], current.AsSpan(entry.RangeOffsets[index], entry.RangeLengths[index]), entry.RangeClean[index]))
                return false;
        }

        // The new base of each buffer whose base word changed, from the ranges read so far. A base
        // word inside a moving window would need its own base first; such an entry is not rebased.
        var rangeCount = entry.RangeAddresses.Length;
        var bases = _rebaseBases;
        bases.Clear();
        foreach (var (range, rangeOffset) in entry.BaseWordProbes ??= BaseWordProbesOf(entry))
        {
            if (windowed[range] != NoBase)
                return false;
            var offset = entry.RangeOffsets[range] + rangeOffset;
            var now = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset));
            if (now != System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(offset)))
                bases.Add((map[offset / sizeof(uint)], now));
        }

        for (var index = 0; index < rangeCount; index++)
        {
            var buffer = windowed[index];
            if (buffer == NoBase)
                continue;
            var oldBase = entry.Snapshot.Buffers[buffer][0];
            var newBase = oldBase;
            foreach (var (changed, word) in bases)
                if (changed == buffer)
                    newBase = word;
            moved[index] = entry.RangeAddresses[index] - oldBase + newBase;
            if (!residentReader(moved[index], current.AsSpan(entry.RangeOffsets[index], entry.RangeLengths[index]), entry.RangeClean[index]))
                return false;
        }

        List<(int Buffer, uint Word)>? found = null;
        // A changed constant outside the moved windows is read by a slot the copy cannot place.
        var constantsOutsideWindows = false;
        for (var index = 0; index < rangeCount; index++)
        {
            // Most ranges did not change at all; only a changed one is looked at word by word.
            if (current.AsSpan(entry.RangeOffsets[index], entry.RangeLengths[index])
                .SequenceEqual(entry.Bytes.AsSpan(entry.RangeOffsets[index], entry.RangeLengths[index])))
                continue;
            for (var offset = entry.RangeOffsets[index]; offset < entry.RangeOffsets[index] + entry.RangeLengths[index]; offset += sizeof(uint))
            {
                var word = offset / sizeof(uint);
                var now = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset));
                if (now == System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(offset)))
                    continue;
                var buffer = map[word];
                if (buffer >= 0)
                {
                    if (rebase.IsRejectedMemoryBase(buffer))
                        return false;
                    (found ??= new()).Add((buffer, now));
                }
                else if (!entry.WordTableOnly[word])
                    return false;
                else
                    constantsOutsideWindows |= windowed[index] == NoBase;
            }
        }

        if (found is null)
            return false;
        var patched = rebase.PatchMemoryBases(rebase.Apply(WithCurrentUserData(entry.Snapshot, userData), userData), found);
        // A changed constant outside the windows is still copied when every slot is a direct load:
        // each slot's word is then the dword at its own address, which this draw has just read.
        var copyDirect = constantsOutsideWindows && CanCopyDirect(rebase);
        if (plan.TableReads.Count != 0 && (!constantsOutsideWindows || copyDirect) && !rebase.TableSlotsThroughBases)
        {
            // The patch cloned the table when it moved a base slot; otherwise it is the entry's.
            var table = ReferenceEquals(patched.FlattenedResourceTable, entry.Snapshot.FlattenedResourceTable)
                ? (uint[])patched.FlattenedResourceTable.Clone()
                : patched.FlattenedResourceTable;
            // The copy writes nothing unless every slot can be placed.
            if (copyDirect
                    ? TryCopyDirectTable(entry, plan, rebase, current, moved, userData, table)
                    : TryCopyTable(entry, plan, rebase, current, table))
            {
                snapshot = new ResourceSnapshot
                {
                    Buffers = patched.Buffers,
                    Images = patched.Images,
                    Samplers = patched.Samplers,
                    FlattenedResourceTable = table,
                    UserData = patched.UserData,
                    DeviceAddressRanges = patched.DeviceAddressRanges,
                };
                patches = found;
                return true;
            }
        }

        if (plan.TableReads.Count != 0)
        {
            if (!entry.TableRefreshable)
                return false;
            var windows = new List<(ulong Start, ulong Size)>(found.Count);
            foreach (var (buffer, baseWord) in found)
                if (AddressRebase.TryMovedWindow(patched.Buffers[buffer], baseWord, out var start, out var size))
                    windows.Add((start, size));

            var recorder = TakeRecorder();
            try
            {
                var recording = new ResourceRuntimeInputs
                {
                    UserData = userData,
                    ShaderBase = inputs.ShaderBase,
                    ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
                    ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
                    ReadResidentMemory = recorder.WrapResident(inputs.ReadResidentMemory),
                    ReadsClean = inputs.ReadsClean,
                    ComputeState = inputs.ComputeState,
                };
                var cachedTable = entry.Snapshot.FlattenedResourceTable;
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

                foreach (var (address, word, _, _) in recorder.Reads)
                {
                    var inWindow = false;
                    foreach (var (start, size) in windows)
                        inWindow |= address >= start && address - start < size;
                    if (inWindow)
                        continue;
                    if (!TryFindMovedWord(entry, moved, address, out var offset) ||
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset, sizeof(uint))) != word)
                        return false;
                }

                // The written device-address slots come from reads validated unchanged above.
                foreach (var slot in plan.WrittenRangeSlotByHandle.Values)
                    cachedTable.AsSpan((int)slot, ShaderResourcePlan.WrittenRangeDwordCount).CopyTo(table.AsSpan((int)slot));
                patched = new ResourceSnapshot
                {
                    Buffers = patched.Buffers,
                    Images = patched.Images,
                    Samplers = patched.Samplers,
                    FlattenedResourceTable = table,
                    UserData = patched.UserData,
                    DeviceAddressRanges = patched.DeviceAddressRanges,
                };
            }
            finally
            {
                ReturnRecorder(recorder);
            }
        }

        snapshot = patched;
        patches = found;
        return true;
    }

    private readonly record struct RejectionSample(int Range, ulong Address, int Length, bool Clean, int WordOffset, uint Word)
    {
        public bool Rejects(Entry entry)
        {
            if (Length == 0 || Range >= entry.RangeAddresses.Length || entry.RangeAddresses[Range] != Address ||
                entry.RangeLengths[Range] != Length || entry.RangeClean[Range] != Clean)
                return false;
            var offset = entry.RangeOffsets[Range] + WordOffset;
            // This sample can reject a variant. It cannot prove that a variant is current.
            return !entry.WordTableOnly[offset / sizeof(uint)] &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(offset, sizeof(uint))) != Word;
        }
    }

    // No word patches: what the entry recorded must still read the same (Revalidate and the learning's predictions).
    private bool Validate(Entry entry, ResidentGuestBytesReader residentReader, AddressRebase rebase,
        IReadOnlyList<uint> userData, out bool unreadable) =>
        Validate(entry, residentReader, rebase, userData, out unreadable, out _, out _);

    private bool _statisticsDiffer;
    private int _validation;
    private uint[] _currentStatistics = [];
    private int[] _statisticsCapturedAt = [];
    private static readonly ThreadCounter _totalStatisticsMaskedHits = new();

    // [local] Where a recorded range lies for a draw with this user data. Two movers: ours (Entry.TryRebase, a range based
    // on a user-data pointer pair the plan only reads through moves by the pair's delta) and upstream's AddressRebase (a
    // range in the window of a confirmed base word moves with it). A range only one of them moves goes there; one both
    // move must land at the same address, else the entry is not reused.
    private static bool TryRangeAddress(Entry entry, int range, IReadOnlyList<uint> userData, AddressRebase rebase, out ulong address)
    {
        var recorded = entry.RangeAddresses[range];
        var upstream = rebase.MoveRead(entry, userData, recorded, entry.RangeLengths[range]);
        if (!entry.TryRebase(range, userData, out address))
            return false;
        if (address == recorded)
        {
            address = upstream;
            return true;
        }

        return upstream == recorded || upstream == address;
    }

    private bool Validate(Entry entry, ResidentGuestBytesReader residentReader, AddressRebase rebase, IReadOnlyList<uint> userData,
        out bool unreadable, out bool descriptorChanged, out RejectionSample rejection, ResourceRuntimeInputs? inputs = null,
        ResidentGuestBytesComparer? residentComparer = null, bool allowPatches = false)
    {
        unreadable = false;
        descriptorChanged = false;
        rejection = default;
        _statisticsDiffer = false;
        _validation++;
        _syncValidated = false; // [local] SHARPEMU_MATERIALIZE_SYNC_VALIDATE
        _patches.Clear(); // [local] word patches
        var patches = allowPatches && entry.WordPatches is not null;
        // A memory-loaded base usually moved when anything did: its few bytes are checked before
        // ranges that can span kilobytes. The full comparison below still decides a match.
        if (entry.BaseWordBuffers is not null)
        {
            Span<byte> word = stackalloc byte[sizeof(uint)];
            foreach (var (range, offset) in entry.BaseWordProbes ??= BaseWordProbesOf(entry))
            {
                // [local] A patchable base word may read differently; the full comparison decides it.
                if (patches && entry.WordPatches!.ContainsKey((entry.RangeOffsets[range] + offset) / sizeof(uint)))
                    continue;
                if (!TryRangeAddress(entry, range, userData, rebase, out var rangeAddress))
                    return false;
                var address = rangeAddress + (ulong)offset;
                if (residentReader(address, word, entry.RangeClean[range]) &&
                    !word.SequenceEqual(entry.Bytes.AsSpan(entry.RangeOffsets[range] + offset, sizeof(uint))))
                    return false;
            }
        }

        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            var length = entry.RangeLengths[index];
            if (!TryRangeAddress(entry, index, userData, rebase, out var address))
            {
                if (StaleLog) _lastStaleReason = $"rebase:r{index}:base{entry.RangeBase?[index]}";
                return false;
            }

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

                    // A changed range without patchable words fails below whatever the copy holds (unless only mip
                    // statistics bits may differ, which the copy decides).
                    if (!inPlaceEqual && !patches && entry.ImageStatisticsWords is null && ValidateInPlaceMode != 2 && !StaleLog)
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
            if (!ReadWithProfile(residentReader, address, current, entry.RangeClean[index], CacheDetail.ValidateRead) &&
                !(SyncValidateMode != 0 && !entry.RangeClean[index] && inputs is not null &&
                  TrySyncRead(inputs, entry.Plan.Hash, address, current, residentReader)))
            {
                if (inPlace && ValidateInPlaceMode == 2)
                    CheckInPlace(inPlaceEqual, residentComparer!, address, current, entry.Bytes.AsSpan(entry.RangeOffsets[index], length), readable: false, entry.RangeClean[index]);
                unreadable = true;
                if (StaleLog) _lastStaleReason = $"unreadable:r{index}:clean{entry.RangeClean[index]}";
                CountDiagnostic(Diagnostic.Unreadable);
                return false;
            }

            using var comparisonProfile = MeasureDetail(CacheDetail.ValidateCompare);
            var cached = entry.Bytes.AsSpan(entry.RangeOffsets[index], length);
            MaterializeSplitProfile.NoteValidatedBytes(length); // [local]
            if (inPlace && ValidateInPlaceMode == 2)
                CheckInPlace(inPlaceEqual, residentComparer!, address, current, cached, readable: true, entry.RangeClean[index]);
            if (current.SequenceEqual(cached) || OnlyMipStatisticsDiffer(entry, index, current))
                continue;
            if (StaleLog)
                NoteStaleSample(entry, index, address, current, cached, userData);
            if (patches)
            {
                // [local] Every changed word a patchable read: the hit takes the draw's words where they are copied.
                bool patched;
                if (ValidateInPlaceMode == 0)
                {
                    patched = CollectPatches(entry, index, current, cached, _patches);
                }
                else
                {
                    var patchesBefore = _patches.Count; // [local] SHARPEMU_VALIDATE_IN_PLACE: equal runs skipped vectorized
                    patched = CollectPatchesSkippingEqual(entry, index, current, cached, _patches);
                    if (ValidateInPlaceMode == 2)
                        CheckPatchScan(entry, index, current, cached, patched, patchesBefore);
                }

                if (patched)
                    continue;
            }

            RecordChangedWords(entry, index, current);
            CountDiagnostic(Diagnostic.BytesChanged);
            for (var offset = 0; offset < length; offset += sizeof(uint))
            {
                var word = (entry.RangeOffsets[index] + offset) / sizeof(uint);
                var now = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current[offset..]);
                var was = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cached[offset..]);
                if (!entry.WordTableOnly[word] && ((now ^ was) & ~MipStatisticsMask(entry, word, out _)) != 0)
                {
                    descriptorChanged = true;
                    // [local] The sample names the recorded address, so only a range read there can give one; a
                    // patchable word proves nothing about another variant.
                    if (address == entry.RangeAddresses[index] && !(patches && entry.WordPatches!.ContainsKey(word)))
                        rejection = new RejectionSample(index, entry.RangeAddresses[index], length, entry.RangeClean[index],
                            offset, now);
                    break;
                }
            }
            return false;
        }

        return true;
    }

    private bool OnlyMipStatisticsDiffer(Entry entry, int range, ReadOnlySpan<byte> current)
    {
        if (entry.ImageStatisticsWords is not { } probes)
            return false;
        var recorded = entry.Bytes.AsSpan(entry.RangeOffsets[range], current.Length);
        var firstWord = entry.RangeOffsets[range] / sizeof(uint);
        for (var offset = 0; offset < current.Length; offset += sizeof(uint))
        {
            var now = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current[offset..]);
            var was = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(recorded[offset..]);
            if (now == was)
                continue;
            var mask = MipStatisticsMask(entry, firstWord + offset / sizeof(uint), out var slot);
            if (slot < 0 || ((now ^ was) & ~mask) != 0)
                return false;
            if (_currentStatistics.Length < probes.Length * 2)
            {
                _currentStatistics = new uint[probes.Length * 2];
                _statisticsCapturedAt = new int[probes.Length * 2];
            }

            _currentStatistics[slot] = now;
            _statisticsCapturedAt[slot] = _validation;
            _statisticsDiffer = true;
        }

        return true;
    }

    private static uint MipStatisticsMask(Entry entry, int word, out int slot)
    {
        slot = -1;
        if (entry.ImageStatisticsWords is not { } probes)
            return 0;
        for (var image = 0; image < probes.Length; image++)
        {
            var probe = probes[image];
            if (probe < 0)
                continue;
            if (probe == word)
            {
                slot = image * 2;
                return ResourceSnapshot.MipStatisticsEnableWordMask;
            }

            if (probe + 1 == word)
            {
                slot = image * 2 + 1;
                return ResourceSnapshot.MipStatisticsSlotWordMask;
            }
        }

        return 0;
    }

    private uint[] MipStatisticsOf(Entry entry)
    {
        var probes = entry.ImageStatisticsWords!;
        var statistics = new uint[probes.Length];
        for (var image = 0; image < probes.Length; image++)
        {
            var probe = probes[image];
            if (probe < 0)
            {
                statistics[image] = ResourceSnapshot.NoMipStatistics;
                continue;
            }

            var word5 = _statisticsCapturedAt.Length > image * 2 && _statisticsCapturedAt[image * 2] == _validation
                ? _currentStatistics[image * 2]
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(probe * sizeof(uint)));
            var word6 = _statisticsCapturedAt.Length > image * 2 + 1 && _statisticsCapturedAt[image * 2 + 1] == _validation
                ? _currentStatistics[image * 2 + 1]
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan((probe + 1) * sizeof(uint)));
            statistics[image] = ResourceSnapshot.MipStatisticsOf(word5, word6);
        }

        return statistics;
    }

    private bool KeepsImageGroups(Entry entry)
    {
        var probes = entry.ImageStatisticsWords!;
        var images = entry.Snapshot.Images;
        var current = new uint[images.Length][];
        for (var image = 0; image < images.Length; image++)
        {
            var words = images[image];
            if (image >= probes.Length || probes[image] < 0 || words.Length < 7)
            {
                current[image] = words;
                continue;
            }

            var patched = (uint[])words.Clone();
            if (_statisticsCapturedAt.Length > image * 2 && _statisticsCapturedAt[image * 2] == _validation)
            {
                patched[5] = (patched[5] & ~ResourceSnapshot.MipStatisticsEnableWordMask) |
                    (_currentStatistics[image * 2] & ResourceSnapshot.MipStatisticsEnableWordMask);
            }

            if (_statisticsCapturedAt.Length > image * 2 + 1 && _statisticsCapturedAt[image * 2 + 1] == _validation)
            {
                patched[6] = (patched[6] & ~ResourceSnapshot.MipStatisticsSlotWordMask) |
                    (_currentStatistics[image * 2 + 1] & ResourceSnapshot.MipStatisticsSlotWordMask);
            }

            current[image] = patched;
        }

        return DescriptorBindingAliases.Group(current, entry.Specialization.Images).SequenceEqual(entry.Specialization.ImageDescriptorGroups);
    }

    private static ResourceSnapshot WithMipStatistics(ResourceSnapshot snapshot, uint[] statistics) => new()
    {
        Buffers = snapshot.Buffers,
        Images = snapshot.Images,
        Samplers = snapshot.Samplers,
        FlattenedResourceTable = snapshot.FlattenedResourceTable,
        UserData = snapshot.UserData,
        DeviceAddressRanges = snapshot.DeviceAddressRanges,
        MipStatistics = statistics,
        Origin = snapshot.Origin,
    };

    private static int[]? MapImageStatisticsWords(ResourceSnapshot snapshot, byte[] bytes)
    {
        int[]? map = null;
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
        for (var image = 0; image < snapshot.Images.Length; image++)
        {
            var descriptor = snapshot.Images[image];
            if (descriptor.Length < 8 || (descriptor[0] == 0 && descriptor[1] == 0))
                continue;
            var match = -1;
            for (var word = 0; word + 8 <= words.Length; word++)
            {
                if (words[word] != descriptor[0] || !words.Slice(word, 8).SequenceEqual(descriptor.AsSpan(0, 8)))
                    continue;
                if (match >= 0)
                {
                    match = -1;
                    break;
                }

                match = word;
            }

            if (match < 0)
                continue;
            if (map is null)
            {
                map = new int[snapshot.Images.Length];
                map.AsSpan().Fill(-1);
            }

            map[image] = match + 5;
        }

        return map;
    }

    private sealed class Entry
    {
        public required ShaderResourcePlan Plan { get; init; }
        // [local] The arrays have setters for SHARPEMU_MATERIALIZE_POH (Retain moves them to the pinned object heap).
        public required uint[] UserData { get; set; }
        // The user data words the evaluator read, one bit per word.
        public required ulong[] UsedUserData { get; init; }
        public required ulong ShaderBase { get; init; }
        public required ComputeSelectorState? ComputeState { get; init; }
        public required ulong[] RangeAddresses { get; set; }
        public required int[] RangeOffsets { get; set; }
        public required int[] RangeLengths { get; set; }
        public required bool[] RangeClean { get; set; }
        // Per recorded dword: read only while the flattened table was evaluated.
        public required bool[] WordTableOnly { get; set; }
        // The table has the plan's plain layout, so no specialization read or extended it.
        public required bool TableRefreshable { get; init; }
        public required byte[] Bytes { get; set; }
        public required ResourceSnapshot Snapshot { get; set; }
        public required ResourceSpecialization Specialization { get; init; }
        public long Normalization { get; init; }
        // Per recorded dword: the buffer whose memory-loaded base dword it is, or a negative value.
        public int[]? BaseWordBuffers { get; init; }
        public int[]? ImageStatisticsWords { get; init; }
        // Filled on the first rebased refresh of the entry; see RangeWindowsOf and TableCopySourcesOf.
        public int[]? RangeWindows { get; set; }
        public int[]? TableCopySources { get; set; }
        public (int Range, int Offset)[]? BaseWordProbes { get; set; }
        public Entry? Next { get; set; }
        public int StaleLookups { get; set; }
        public int VolatileLookups { get; set; }
        public int VolatilePeriod { get; set; }

        // [local] Per range: the user-data register pair its address was based on, AbsoluteBase when the address does
        // not move with the user data, UnknownBase when the reader could not tell (null: every range absolute).
        public int[]? RangeBase { get; set; }
        // [local] Per range: the base address its reads were relative to (0 unknown), with SHARPEMU_MATERIALIZE_REBASE.
        public ulong[]? RangeReadBase { get; set; }
        // [local] A range read through an unknown base: every user-data word the walk read is compared, the words
        // the plan only reads through included.
        public bool ExactUserData { get; init; }
        // [local] By recorded dword: where the plan copies it verbatim (UserDataUseAnalysis.PatchableReads).
        public Dictionary<int, UserDataUseAnalysis.PatchTarget[]>? WordPatches { get; init; }
        // [local] The store-table scans the walk used, for a cache that stamps store tables.
        public DeviceStoreTablePlanner.UsedScan[] UsedScans = [];
        // [local] SHARPEMU_MATERIALIZE_POH: the arrays were moved to the pinned object heap when the entry was stored.
        public bool Retained;

        // A rebased word is an address the hit patches in, so it does not distinguish entries.
        // [local] With SHARPEMU_MATERIALIZATION_CONTENT_KEY, neither does a word the plan only reads through or builds
        // buffer dwords from (UserDataUseAnalysis.IsValueRegister), unless the entry read through an unknown base.
        public bool Matches(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ulong[] rebased) =>
            Matches(plan, inputs, rebased, ExactUserData || !ContentKeyed ? null : plan.UserDataUse);

        // [local] Upstream's match: every word the walk read, less the rebased ones. The entry's ranges then lie where
        // upstream's AddressRebase alone puts them.
        public bool MatchesUsed(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ulong[] rebased) =>
            Matches(plan, inputs, rebased, null);

        private bool Matches(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ulong[] rebased, UserDataUseAnalysis? use)
        {
            if (!ReferenceEquals(Plan, plan) || ShaderBase != inputs.ShaderBase ||
                !Nullable.Equals(ComputeState, inputs.ComputeState) || UserData.Length != inputs.UserData.Count ||
                Normalization != NormalizationOf(plan))
                return false;
            for (var index = 0; index < UserData.Length; index++)
                if (IsUsed(UsedUserData, index) && !IsUsed(rebased, index) && (use is null || use.IsValueRegister(index)) &&
                    UserData[index] != inputs.UserData[index])
                    return false;
            return true;
        }

        public bool UserDataEquals(IReadOnlyList<uint> userData)
        {
            if (UserData.Length != userData.Count)
                return false;
            for (var index = 0; index < UserData.Length; index++)
                if (UserData[index] != userData[index])
                    return false;
            return true;
        }

        // [local] The address of a recorded range for this draw: the recorded one, or the recorded one moved by the
        // difference between the draw's and the entry's base pointer. Only the 48 address bits may differ; the stride
        // and record fields of a buffer handle live in the same words and change the address formula. A range of
        // unknown base stays where it was: Matches compared every word its walk read (ExactUserData).
        public bool TryRebase(int range, IReadOnlyList<uint> userData, out ulong address)
        {
            address = RangeAddresses[range];
            if (RangeBase is not { } bases)
                return true;
            var register = bases[range];
            if (register < 0)
                return true;
            var index = register - (int)Plan.UserDataBase;
            if (index < 0 || index + 1 >= UserData.Length || index + 1 >= userData.Count)
                return false;
            var recorded = (UserData[index] | ((ulong)UserData[index + 1] << 32));
            var current = (userData[index] | ((ulong)userData[index + 1] << 32));
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

        // [local] The cached snapshot, with the draw's own user data when it differs from the entry's (upstream's
        // WithCurrentUserData: a copy, never the caller's list). With SHARPEMU_MATERIALIZATION_CONTENT_KEY, buffer
        // dwords computed from user data are evaluated again for this draw; the words the draw reads differently go to
        // the places they are copied to. Null when a buffer dword changed beyond its base address.
        public ResourceSnapshot? SnapshotFor(ResourceRuntimeInputs inputs, List<(int Word, uint Value)> patches)
        {
            var sameUserData = UserDataEquals(inputs.UserData);
            if (patches.Count == 0 && sameUserData)
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

            if (!sameUserData && ContentKeyed)
            {
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

            uint[] userData;
            if (sameUserData)
            {
                userData = Snapshot.UserData;
            }
            else
            {
                userData = new uint[inputs.UserData.Count];
                for (var index = 0; index < userData.Length; index++)
                    userData[index] = inputs.UserData[index];
            }

            var snapshot = new ResourceSnapshot
            {
                Buffers = buffers,
                Images = Snapshot.Images,
                Samplers = Snapshot.Samplers,
                FlattenedResourceTable = table,
                UserData = userData,
                DeviceAddressRanges = Snapshot.DeviceAddressRanges,
                Origin = Snapshot.Origin,
            };
            if (sameUserData)
            {
                // The entry's own draw now reads these words: keep them, so the next one is a plain hit.
                foreach (var (wordIndex, word) in patches)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(wordIndex * sizeof(uint)), word);
                Snapshot = Retained ? RetainSnapshot(snapshot) : snapshot; // [local] SHARPEMU_MATERIALIZE_POH
                Snapshot.Origin = this;
                return Snapshot;
            }

            return snapshot;
        }
    }

    // A user data list that notes which words are read by index. Copying it out (the snapshot's
    // own copy) goes through the enumerator and is not a read of the evaluator.
    private sealed class UserDataRecorder : IReadOnlyList<uint>
    {
        private IReadOnlyList<uint> _inner = [];
        private ulong[] _used = [];

        public void Begin(IReadOnlyList<uint> inner)
        {
            _inner = inner;
            _used = new ulong[(inner.Count + 63) >> 6];
        }

        // The recorded set; the next Begin allocates a fresh one, so the entry keeps this array.
        public ulong[] TakeUsed() => _used;

        public int Count => _inner.Count;

        public uint this[int index]
        {
            get
            {
                _used[index >> 6] |= 1UL << (index & 63);
                return _inner[index];
            }
        }

        public IEnumerator<uint> GetEnumerator() => _inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ReadRecorder
    {
        private readonly List<(ulong Address, uint Word, bool Clean, bool Table)> _reads = new();
        // [local] Beside each read, in the same order: the user-data register its base came from, the graph node of the raw
        // read and the base address it was relative to (RuntimeValueEvaluator.CurrentRead*), for Entry.RangeBase,
        // Entry.WordPatches and Entry.RangeReadBase.
        private readonly List<(int Base, int Node, ulong BaseAddress)> _sources = new();
        private ulong[] _sortKeys = [];
        private int[] _sortOrder = [];
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
            _sources.Clear();
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
            {
                _reads.Capacity = 0;
                _sources.Capacity = 0;
            }
            if (_sortKeys.Length > KeptReads)
            {
                _sortKeys = [];
                _sortOrder = [];
            }
            if (_runKeysA.Length > KeptReads)
            {
                _runKeysA = [];
                _runIdxA = [];
                _runKeysB = [];
                _runIdxB = [];
            }
            _reader = null;
            _cleanReader = null;
            _residentReader = null;
            _inTable = false;
            Failed = false;
        }

        // [local] SHARPEMU_RECORDER_KEEP: the read-list capacity a returned recorder keeps, in entries (default 1 << 20;
        // 0 restores upstream's 16384). Camera moves record walks of more than 16384 reads, so the list was dropped and
        // grown again from empty on every such miss: ~160 MB/s of List growth on the front in pan, 43 % of all managed
        // allocation, much of it on the large-object heap.
        private static readonly int KeptReads = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RECORDER_KEEP"), out var kept)
            ? kept <= 0 ? 16384 : kept
            : 1 << 20;

        // [local] SHARPEMU_RECORDER_KEYSORT (on by default; =0 disables): reads out of address order are ordered through a
        // ulong key array and an index array instead of a Comparison over the records (a delegate call per comparison):
        // 3.4 ms per frame of the front in camera moves. Reads already in address order are not sorted (upstream).
        private static readonly bool KeySort = Environment.GetEnvironmentVariable("SHARPEMU_RECORDER_KEYSORT") != "0";

        // The record order by address: null when the reads already are in address order, else an index array.
        private int[]? OrderReads()
        {
            // Reads mostly arrive in address order; sorting a sorted list was ~5% of a draw-heavy render thread.
            var reads = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_reads);
            var count = reads.Length;
            var ordered = true;
            for (var index = 1; ordered && index < count; index++)
                ordered = reads[index - 1].Address <= reads[index].Address;
            if (ordered)
                return null;

            if (_sortKeys.Length < count)
            {
                var size = Math.Max(count, _sortKeys.Length * 2);
                _sortKeys = new ulong[size];
                _sortOrder = new int[size];
            }

            for (var index = 0; index < count; index++)
            {
                _sortKeys[index] = reads[index].Address;
                _sortOrder[index] = index;
            }

            if (!KeySort)
            {
                var records = _reads;
                Array.Sort(_sortOrder, 0, count, Comparer<int>.Create((left, right) => records[left].Address.CompareTo(records[right].Address)));
                return _sortOrder;
            }

            if (RunSort != 0 && count >= RunSortMinReads && RunMergeSort(count))
            {
                if (RunSort != 2)
                    return _runIdxA;
                Array.Sort(_sortKeys, _sortOrder, 0, count);
                CompareRunOrder(count);
                return _sortOrder;
            }

            Array.Sort(_sortKeys, _sortOrder, 0, count);
            return _sortOrder;
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
        private void CompareRunOrder(int count)
        {
            var reads = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_reads);
            var sources = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sources);
            var mismatches = 0L;
            var groups = 0L;
            for (var index = 0; index < count; index++)
            {
                var introsort = _sortOrder[index];
                var merged = _runIdxA[index];
                if (reads[introsort].Address != reads[merged].Address)
                {
                    mismatches++;
                    continue;
                }

                if (index != 0 && reads[_sortOrder[index - 1]].Address == reads[introsort].Address)
                    continue;
                groups++;
                if (reads[introsort] != reads[merged] || sources[introsort] != sources[merged])
                    mismatches++;
            }

            Interlocked.Add(ref RunSortMismatches, mismatches);
            Interlocked.Add(ref RunSortChecked, groups);
        }

        public bool Failed { get; private set; }

        private readonly UserDataRecorder _userData = new();

        public IReadOnlyList<uint> WrapUserData(IReadOnlyList<uint> inner)
        {
            _userData.Begin(inner);
            return _userData;
        }

        public List<(ulong Address, uint Word, bool Clean, bool Table)> Reads => _reads;

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
            if (DeviceStoreTablePlanner.ScanUnrecorded) // [local] SHARPEMU_PROGRAM_GATE_TABLES=3
                return inner(address, out word);
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

            using (ResourceMaterializationProfile.MeasureSampled(ResourceMaterializationProfile.Phase.ReadRecording))
            {
                _reads.Add((address, word, clean, _inTable));
                _sources.Add((RuntimeValueEvaluator.CurrentReadBase, RuntimeValueEvaluator.CurrentReadNode, RuntimeValueEvaluator.CurrentReadBaseAddress));
            }
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
            if (DeviceStoreTablePlanner.ScanUnrecorded) // [local] SHARPEMU_PROGRAM_GATE_TABLES=3
                return true;

            var readBase = RuntimeValueEvaluator.CurrentReadBase;
            var node = RuntimeValueEvaluator.CurrentReadNode;
            if (BlockMode != 0 && _blocksAllowed && destination.Length >= BlockMinBytes) // [local] SHARPEMU_RECORDER_BLOCKS
            {
                var blockWords = destination.Length / sizeof(uint);
                var length = blockWords * sizeof(uint);
                if (_blockBytes.Length < _blockBytesUsed + length)
                    Array.Resize(ref _blockBytes, Math.Max(_blockBytesUsed + length, _blockBytes.Length * 2));
                destination[..length].CopyTo(_blockBytes.AsSpan(_blockBytesUsed));
                _blocks.Add((address, _blockBytesUsed, blockWords, clean, _inTable, readBase, node));
                _blockBytesUsed += length;
                _blockWords += blockWords;
                return true;
            }

            using var profile = ResourceMaterializationProfile.MeasureSampled(ResourceMaterializationProfile.Phase.ReadRecording);
            var wordCount = destination.Length / sizeof(uint);
            if (wordCount >= 8)
            {
                var first = _reads.Count;
                System.Runtime.InteropServices.CollectionsMarshal.SetCount(_reads, checked(first + wordCount));
                System.Runtime.InteropServices.CollectionsMarshal.SetCount(_sources, checked(first + wordCount));
                var recorded = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_reads).Slice(first, wordCount);
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sources).Slice(first, wordCount).Fill((readBase, node, 0UL));
                for (var index = 0; index < wordCount; index++)
                    recorded[index] = (address + (ulong)index * sizeof(uint),
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(destination[(index * sizeof(uint))..]), clean, _inTable);
                return true;
            }
            for (var offset = 0; offset + sizeof(uint) <= destination.Length; offset += sizeof(uint))
            {
                _reads.Add((address + (ulong)offset,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(destination[offset..]), clean, _inTable));
                _sources.Add((readBase, node, 0UL));
            }
            return true;
        }

        // [local] SHARPEMU_RECORDER_BLOCKS (1 by default, =0 turns it off): on a miss, a resident read of BlockMinBytes or more (a store
        // table read in 64 KB chunks: ~520 k words per scan in camera moves) is kept as one block of bytes instead of a
        // record per word, and Build lays each block into the entry at once: the words of a block are one
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
            ResourceSpecialization specialization, IReadOnlyList<int> memoryBases, long normalization)
        {
            if (_blocks.Count == 0)
                return BuildWords(plan, inputs, snapshot, specialization, memoryBases, normalization);
            var blocked = BuildBlocks(plan, inputs, snapshot, specialization, memoryBases, normalization);
            if (blocked is null)
                Interlocked.Increment(ref BlockFallbacks);
            else
                Interlocked.Increment(ref BlockBuilds);
            if (blocked is not null && BlockMode != 2)
                return blocked;
            ExpandBlocks();
            var words = BuildWords(plan, inputs, snapshot, specialization, memoryBases, normalization);
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
                {
                    _reads.Add((address + (ulong)word * sizeof(uint),
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_blockBytes.AsSpan(offset + word * sizeof(uint))),
                        clean, table));
                    _sources.Add((readBase, node, 0UL));
                }
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
            if ((left.BaseWordBuffers is null) != (right.BaseWordBuffers is null) ||
                (left.BaseWordBuffers is not null && !left.BaseWordBuffers.AsSpan().SequenceEqual(right.BaseWordBuffers)) ||
                (left.ImageStatisticsWords is null) != (right.ImageStatisticsWords is null) ||
                (left.ImageStatisticsWords is not null && !left.ImageStatisticsWords.AsSpan().SequenceEqual(right.ImageStatisticsWords)))
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

        // [local] The entry with the blocks laid in whole; null when a block overlaps another block or a word record.
        private Entry? BuildBlocks(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization, IReadOnlyList<int> memoryBases, long normalization)
        {
            var sortStarted = MaterializeSplitProfile.Start();
            var bigSort = RecordedWords > MaterializeSplitProfile.BigReads;
            var order = OrderReads();
            MaterializeSplitProfile.Note(bigSort ? MaterializeSplitProfile.Kind.BigSort : MaterializeSplitProfile.Kind.Sort, sortStarted);
            _blocks.Sort(static (left, right) => left.Address.CompareTo(right.Address));
            var reads = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_reads);
            var sources = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sources);
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
                    if (reads[order is null ? middle : order[middle]].Address < start)
                        low = middle + 1;
                    else
                        high = middle;
                }

                if (low < reads.Length && reads[order is null ? low : order[low]].Address < stop)
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
                var record = readIndex < reads.Length ? (order is null ? readIndex : order[readIndex]) : -1;
                if (blockIndex < _blocks.Count && (record < 0 || _blocks[blockIndex].Address < reads[record].Address))
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

                readIndex++;
                var read = reads[record];
                var source = sources[record];
                exactUserData |= source.Base == UserDataUseAnalysis.UnknownBase;
                if (read.Address == previous) continue;
                if (wordCount == 0 || read.Address != end || source.Base != previousBase ||
                    (SplitByBaseAddress && source.BaseAddress != previousBaseAddress)) rangeCount++;
                wordCount++;
                previous = read.Address;
                previousBase = source.Base;
                previousBaseAddress = source.BaseAddress;
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
                var record = readIndex < reads.Length ? (order is null ? readIndex : order[readIndex]) : -1;
                if (blockIndex < _blocks.Count && (record < 0 || _blocks[blockIndex].Address < reads[record].Address))
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

                readIndex++;
                var (address, wordValue, wordClean, table) = reads[record];
                var (readBase, node, baseAddress) = sources[record];
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
                tableOnly, patches, bytes, memoryBases, normalization);
        }

        private Entry NewEntry(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization, ulong[] addresses, int[] offsets, int[] lengths, bool[] clean, int[] bases,
            ulong[]? readBases, bool exactUserData, bool[] tableOnly, Dictionary<int, UserDataUseAnalysis.PatchTarget[]>? patches,
            byte[] bytes, IReadOnlyList<int> memoryBases, long normalization)
        {
            var userData = new uint[inputs.UserData.Count];
            for (var index = 0; index < userData.Length; index++)
                userData[index] = inputs.UserData[index];
            return new Entry
            {
                BaseWordBuffers = memoryBases.Count == 0 ? null : MapBaseWords(snapshot, memoryBases, bytes),
                ImageStatisticsWords = MapImageStatisticsWords(snapshot, bytes),
                Plan = plan,
                UserData = userData,
                UsedUserData = _userData.TakeUsed(),
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
                Normalization = normalization,
            };
        }

        private Entry BuildWords(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization, IReadOnlyList<int> memoryBases, long normalization)
        {
            var sortStarted = MaterializeSplitProfile.Start(); // [local]
            var bigSort = _reads.Count > MaterializeSplitProfile.BigReads;
            var order = OrderReads();
            MaterializeSplitProfile.Note(bigSort ? MaterializeSplitProfile.Kind.BigSort : MaterializeSplitProfile.Kind.Sort, sortStarted);
            var reads = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_reads);
            var sources = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sources);
            // Count unique words and contiguous ranges first, then fill the
            // retained arrays directly. Temporary lists previously duplicated
            // every recorded byte and range on each cache miss.
            // [local] A range also ends where the base register (or, with SHARPEMU_MATERIALIZE_REBASE, the base address)
            // of its reads changes, so each range moves with one base (Entry.TryRebase, TryShift).
            var rangeCount = 0;
            var wordCount = 0;
            ulong end = 0;
            var previous = ulong.MaxValue;
            var previousBase = UserDataUseAnalysis.UnknownBase;
            var previousBaseAddress = 0UL;
            var exactUserData = false;
            for (var index = 0; index < reads.Length; index++)
            {
                var record = order is null ? index : order[index];
                var address = reads[record].Address;
                var source = sources[record];
                exactUserData |= source.Base == UserDataUseAnalysis.UnknownBase;
                if (address == previous) continue;
                if (wordCount == 0 || address != end || source.Base != previousBase ||
                    (SplitByBaseAddress && source.BaseAddress != previousBaseAddress)) rangeCount++;
                wordCount++;
                previous = address;
                previousBase = source.Base;
                previousBaseAddress = source.BaseAddress;
                end = address + sizeof(uint);
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
            for (var index = 0; index < reads.Length; index++)
            {
                var record = order is null ? index : order[index];
                var (address, word, wordClean, table) = reads[record];
                var (readBase, node, baseAddress) = sources[record];
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
                tableOnly, patches, bytes, memoryBases, normalization);
        }

        // The recorded word each memory-loaded base dword came from: the only recorded word holding
        // that value. A word two bases claim names neither. Learning checks the guess.
        private static int[]? MapBaseWords(ResourceSnapshot snapshot, IReadOnlyList<int> memoryBases, byte[] bytes)
        {
            int[]? map = null;
            var wordCount = bytes.Length / sizeof(uint);
            foreach (var buffer in memoryBases)
            {
                if (buffer >= snapshot.Buffers.Length || snapshot.Buffers[buffer].Length < 2)
                    continue;
                var value = snapshot.Buffers[buffer][0];
                var match = -1;
                for (var word = 0; word < wordCount; word++)
                {
                    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(word * sizeof(uint))) != value)
                        continue;
                    if (match >= 0)
                    {
                        match = -1;
                        break;
                    }
                    match = word;
                }

                if (match < 0)
                    continue;
                if (map is null)
                {
                    map = new int[wordCount];
                    map.AsSpan().Fill(NoBase);
                }

                map[match] = map[match] == NoBase ? buffer : SharedBase;
            }

            return map;
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
    // Since 2026-11-03 a recorded word that read the same, and whose value is a moved base (a moved word's old value
    // or a moved user-data pointer), refuses: output words are taken to their new values by value, so such a word
    // copied into a buffer, table slot or range base would move too while the walk keeps it (coincident_word).
    // =4 and =5 are =1 and =2 without that check (the rule before 2026-11-03, for A/B).
    internal static int RebaseMode => RebaseModeOverrideValue.Value ?? DefaultRebaseMode;

    private static readonly int DefaultRebaseMode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_REBASE"), out var rebaseMode) ? rebaseMode : 1;
    private static readonly System.Threading.AsyncLocal<int?> RebaseModeOverrideValue = new();

    // For tests: this async flow's mode, null for the environment's. Only between non-zero modes: the recorder
    // splits ranges by base address when the environment's mode is non-zero (SplitByBaseAddress, read per word).
    internal static int? RebaseModeOverride
    {
        get => RebaseModeOverrideValue.Value;
        set => RebaseModeOverrideValue.Value = value;
    }

    private static readonly bool SplitByBaseAddress = DefaultRebaseMode != 0;
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

    // Rebased hits of this cache (the static counters are process-wide).
    public long RebaseHits { get; private set; }

    // SHARPEMU_MATERIALIZE_REBASE_WATCH=hash[/hash...]: cumulative counts of these plans in the report.
    private static readonly HashSet<ulong> RebaseWatch = (Environment.GetEnvironmentVariable("SHARPEMU_MATERIALIZE_REBASE_WATCH") ?? "")
        .Split(['/', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => ulong.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
            System.Globalization.NumberStyles.HexNumber, null, out var hash) ? hash : 0)
        .Where(hash => hash != 0).ToHashSet();
    private static readonly Dictionary<ulong, (long Tries, long Refused, long Hits, long Agreed, long Mismatched)> _rebaseWatched = new();

    private static void NoteWatched(ulong planHash, bool tried = false, bool refused = false, bool hit = false, bool agreed = false, bool mismatched = false)
    {
        if (RebaseWatch.Count == 0 || !RebaseWatch.Contains(planHash))
            return;
        lock (_rebaseWatched)
        {
            var entry = _rebaseWatched.GetValueOrDefault(planHash);
            _rebaseWatched[planHash] = (entry.Tries + (tried ? 1 : 0), entry.Refused + (refused ? 1 : 0), entry.Hits + (hit ? 1 : 0),
                entry.Agreed + (agreed ? 1 : 0), entry.Mismatched + (mismatched ? 1 : 0));
        }
    }

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

        var watched = string.Empty;
        if (RebaseWatch.Count != 0)
        {
            lock (_rebaseWatched)
                watched = " rebase_watch=[" + string.Join(",", _rebaseWatched.Select(pair => FormattableString.Invariant(
                    $"{pair.Key:X16}:tries={pair.Value.Tries}/refused={pair.Value.Refused}/hits={pair.Value.Hits}/agreed={pair.Value.Agreed}/mismatched={pair.Value.Mismatched}"))) + "]";
        }

        return FormattableString.Invariant(
            $" rebase_candidates={Interlocked.Exchange(ref _rebaseCandidates, 0)} rebase_hits={Interlocked.Exchange(ref _rebaseHits, 0)} rebase_agreed={Interlocked.Exchange(ref _rebaseAgreed, 0)} rebase_mismatches={Interlocked.Exchange(ref _rebaseMismatches, 0)} rebase_mismatches_total={Interlocked.Read(ref _rebaseMismatchTotal)} rebase_unverifiable={Interlocked.Exchange(ref _rebaseUnverifiable, 0)} rebase_rejected_plans={Interlocked.Read(ref _rebaseRejectedPlans)} rebase_refusals=[{refusals}] rebase_plans=[{plans}]{watched}");
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
        var shifted = TryShift(entry, inputs, residentReader, exact: RebaseMode is not (4 or 5));
        NoteWatched(entry.Plan.Hash, tried: true, refused: shifted is null);
        return shifted;
    }

    private Entry? TryShift(Entry entry, ResourceRuntimeInputs inputs, ResidentGuestBytesReader residentReader, bool exact)
    {
        var count = entry.RangeAddresses.Length;
        if (entry.RangeReadBase is not { } readBases || entry.RangeBase is not { } rangeBases)
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
            if (rangeBases[index] < 0)
                continue;
            if (!entry.TryRebase(index, inputs.UserData, out var address))
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
            var register = rangeBases[index] - (int)entry.Plan.UserDataBase;
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

        // A word that read the same but holds a moved base: what it reached in the output would be moved by value.
        if (exact)
        {
            for (var offset = 0; offset < current.Length; offset += sizeof(uint))
            {
                var now = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset));
                if (now == System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry.Bytes.AsSpan(offset)) && movedBases.Contains(now))
                {
                    Refuse("coincident_word");
                    return null;
                }
            }
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
            UsedUserData = entry.UsedUserData,
            Normalization = entry.Normalization,
            BaseWordBuffers = entry.BaseWordBuffers,
            ImageStatisticsWords = entry.ImageStatisticsWords,
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

        NoteWatched(planHash, agreed: difference is null, mismatched: difference is not null);

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

        // Always printed (the first 64, then every 1024th, and a plan's first), with or without SHARPEMU_PROFILE_RENDER: grep for it.
        if (Interlocked.Increment(ref _rebaseMismatchLogs) <= 64 || (total & 1023) == 0 || trust >= 0)
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

    // [local] SHARPEMU_MATERIALIZE_STALE_LOG=1: a sample of the first changed words of a range, and the reason Validate failed.
    private void NoteStaleSample(Entry entry, int index, ulong address, ReadOnlySpan<byte> current, ReadOnlySpan<byte> cached,
        IReadOnlyList<uint> userData)
    {
        var length = current.Length;
        var first = 0;
        var changedWords = 0;
        for (var offset = length - sizeof(uint); offset >= 0; offset -= sizeof(uint))
            if (!current.Slice(offset, sizeof(uint)).SequenceEqual(cached.Slice(offset, sizeof(uint))))
            {
                first = offset;
                changedWords++;
            }
        var wordIndex = (entry.RangeOffsets[index] + first) / sizeof(uint);
        var rangeBase = entry.RangeBase?[index] ?? UserDataUseAnalysis.AbsoluteBase;
        lock (_staleByPlan)
        {
            var logged = _staleLogged.GetValueOrDefault(entry.Plan.Hash);
            if (logged < 4 && _staleLogged.Count < 64)
            {
                _staleLogged[entry.Plan.Hash] = logged + 1;
                var info = entry.Plan.Info;
                Console.Error.WriteLine($"[MATERIALIZE_STALE_SAMPLE] plan=0x{entry.Plan.Hash:X16} range={index} address=0x{address:X} recorded=0x{entry.RangeAddresses[index]:X} base={rangeBase} " +
                    $"cached={Convert.ToHexString(cached)} current={Convert.ToHexString(current)} buffers={info.Buffers.Count} images={info.Images.Count} samplers={info.Samplers.Count} sources={entry.Plan.DescriptorSources.Count} " +
                    $"user_data=[{string.Join(",", entry.UserData.Select(word => word.ToString("X8")))}] draw_user_data=[{string.Join(",", Enumerable.Range(0, userData.Count).Select(i => userData[i].ToString("X8")))}]");
            }
        }
        _lastStaleReason = $"word:r{index}/{entry.RangeAddresses.Length}:len{length}:dw{first / sizeof(uint)}:changed{changedWords}:table{entry.WordTableOnly[wordIndex]}:patchable{entry.WordPatches?.ContainsKey(wordIndex) == true}:base{rangeBase}";
    }

    private const int NoBase = -1;
    private const int SharedBase = -2;
}
