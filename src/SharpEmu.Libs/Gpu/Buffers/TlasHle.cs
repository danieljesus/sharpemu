// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_TLAS_HLE (=1 by default, =0 off): GTA V's TLAS that the reflection pass 0x8395 traverses, built by the host
// in the game's format instead of by the game's chain of compute programs.
//
// One chain per TLAS and frame, keyed by its work header (a CP-written block in a shared work area):
//   CP: the TLAS header template (256 B), then 0x5F74 on it; the work header and a counter.
//   0x0DA3: per-instance world boxes (28-byte records: 6 floats + 0x80000000 | index) and the scene box (work header).
//   0xB198 (reads the scene box on the CPU: the wait), 0xD5D0 x3 (in the node area), 0xF5EB, 0x994A (the instance
//   records, from the header's +0x28/+0x74), 0x11B4, 0xCA99, 0x5515 x12, 0xF05B, 0x7846/0x35B8 x2 (header +0x8C/+0x90),
//   0x02F1 (the final nodes; waits as well).
// Here 0x0DA3 and 0x994A run; at 0xB198 the host writes the header fields and the tree topology (Morton order of the
// translations of the CPU-written source records), records one pass that fills the boxes from 0x0DA3's records, and
// the rest of the chain is skipped. A chain is intercepted only when its destination is a known TLAS (one 0x8395 read
// through the pointer at its user data 4/5 + 0x40, or one 0x994A filled the instance table of in an earlier frame) and
// 0xB198's user data match the 0x0DA3 of the same work header (records, count, node area = base + 0x100).
//   =1 replace; =2 verify: the game's chain runs; the host builds into a buffer of its own and, after 0x02F1, compares
//   with the game's tree once the batch completes (leaf box per instance, instance set, containment, header words).
//
// Format: header 256 B ("PSR_BVHL"; +0x10 total size; +0x28 instance table = 0x100 + n * 64; +0x50 last node unit;
// +0x74 n; dwords 37-44 the V#s of the TLAS and of the instance table). Box nodes of 128 B from unit 4 (64-byte units):
// 4 child pointers ((unit << 3) | 5 node, (instance << 3) | 0 leaf, 0xFFFFFFFF empty), 4 boxes of 6 floats, 4 dwords of
// 0xFFFFFFFF. The game also packs two-child nodes in one unit, with 0x1E in the unused slots; the host does not.
internal sealed unsafe class TlasHle : IDisposable
{
    public static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TLAS_HLE"), out var mode) ? mode : 0;
    public static bool Enabled => Mode != 0;
    internal static TlasHle? Instance;

    private const ulong Hash0DA3 = 0x0DA37182659C9032;
    private const ulong HashB198 = 0xB198A93EC372A571;
    private const ulong HashD5D0 = 0xD5D0140BDBDE8647;
    private const ulong HashF5EB = 0xF5EBBA997605508B;
    private const ulong Hash994A = 0x994AF59D50BEDCA8;
    private const ulong Hash11B4 = 0x11B464F16874897E;
    private const ulong HashCA99 = 0xCA99120EAF300BFD;
    private const ulong Hash5515 = 0x5515EAC3C1B5EDC8;
    private const ulong HashF05B = 0xF05BBF883D986FC9;
    private const ulong Hash7846 = 0x78461F32DB415FB0;
    private const ulong Hash35B8 = 0x35B85620669D35A4;
    private const ulong Hash02F1 = 0x02F144937456C9DE;
    private const ulong Hash8395 = 0x8395E43F382309DF;
    private const int MaxNodes = 4096;
    private const int SetRing = 256;
    private const int VerifyEvery = 4;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly GuestBufferCache _cache;
    private readonly Dictionary<ulong, ulong> _programByAddress = new();
    private readonly HashSet<ulong> _traversedBases = new();
    private readonly HashSet<ulong> _instanceTableBases = new();
    private readonly Dictionary<ulong, Chain> _chains = new();
    private readonly GpuBuffer _scratch;
    private readonly GpuBuffer? _verifyOutput;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _sets = new DescriptorSet[SetRing];
    private int _nextSet;

    private sealed class Chain
    {
        public required ulong Key;
        public required int Slot;
        public ulong Records, Source, Base, Total;
        public uint Count;
        public bool Active, VerifyThis;
        public int Nodes, Builds;
        public uint[]? Topology;
        public GpuBuffer? HeaderBefore;
        public readonly HashSet<ulong> Blocks = new();
    }

    private static long _pointerReads, _pointerMisses, _gameBadPointers;
    private static int _traversedBasesCount;
    private static long _compareErrors, _sourceGpuDirty;
    private static int _compareStage;
    private static string _oursSample = string.Empty;
    private static long _builds, _skipped, _fallbacks, _verifies, _leafMismatches, _instanceSetDiff, _containment, _gameNodes, _ourNodes, _headerFieldDiff;
    private static readonly Dictionary<string, long> _fallbackReasons = new();
    private static readonly Dictionary<ulong, long> _skippedByHash = new();
    private static readonly Dictionary<ulong, long> _unmatched = new();
    private static readonly Dictionary<string, long> _chainsSeen = new();
    private static readonly SortedDictionary<int, long> _chainWrittenDwords = new();
    private static readonly SortedDictionary<int, long> _headerDiffDwords = new();
    private static string _headerSample = string.Empty;

    public TlasHle(GpuDeviceInfo device, SubmissionScheduler scheduler, GuestBufferCache cache)
    {
        _device = device;
        _scheduler = scheduler;
        _cache = cache;
        var vk = device.Vk;
        _scratch = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, (ulong)MaxNodes * 24);
        if (Mode == 2)
            _verifyOutput = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, (ulong)MaxNodes * 128);

        var bindings = stackalloc DescriptorSetLayoutBinding[4];
        for (uint index = 0; index < 4; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = index,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = bindings };
        Require(vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out _setLayout), "descriptor set layout");
        var setLayout = _setLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        Require(vk.CreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out _pipelineLayout), "pipeline layout");
        var spirv = TlasHleShaders.CreateTlasBoxes();
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Require(vk.CreateShaderModule(device.Device, &moduleInfo, null, out module), "shader module");
        }

        ReadOnlySpan<byte> entryPoint = "main\0"u8;
        fixed (byte* entry = entryPoint)
        {
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Layout = _pipelineLayout,
                Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = entry },
            };
            Require(vk.CreateComputePipelines(device.Device, default, 1, &pipelineInfo, null, out _pipeline), "pipeline");
        }

        vk.DestroyShaderModule(device.Device, module, null);
        var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 4 * SetRing };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = SetRing, PoolSizeCount = 1, PPoolSizes = &poolSize };
        Require(vk.CreateDescriptorPool(device.Device, &poolInfo, null, out _pool), "descriptor pool");
        var layouts = stackalloc DescriptorSetLayout[SetRing];
        for (var index = 0; index < SetRing; index++)
            layouts[index] = _setLayout;
        var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = SetRing, PSetLayouts = layouts };
        fixed (DescriptorSet* sets = _sets)
            Require(vk.AllocateDescriptorSets(device.Device, &allocateInfo, sets), "descriptor sets");
        Console.Error.WriteLine($"[TLAS_HLE] mode={Mode}");
    }

    private static void Require(Result result, string what)
    {
        if (result != Result.Success)
            throw SubmissionScheduler.Fatal($"TLAS HLE: {what} failed with {result}.");
    }

    private static bool IsChainHash(ulong hash) =>
        hash is Hash0DA3 or HashB198 or HashD5D0 or HashF5EB or Hash994A or Hash11B4 or HashCA99 or Hash5515 or HashF05B or Hash7846 or Hash35B8 or Hash02F1 or Hash8395;

    // The program at a shader address, once prepared (its hash is known only then).
    public void NoteProgram(ulong shaderAddress, ulong hash)
    {
        if (ConsumerProbe)
            _anyProgram[shaderAddress] = hash;
        if (IsChainHash(hash))
            _programByAddress[shaderAddress] = hash;
        else
            _programByAddress.Remove(shaderAddress);
    }

    private static ulong Pointer(ReadOnlySpan<uint> userData, int index) =>
        index + 1 < userData.Length ? userData[index] | ((ulong)(userData[index + 1] & 0xFFFF) << 32) : 0;

    // At a dispatch, before its program is prepared (so before any table read): Skip when the dispatch is to be left
    // out; Recorded when the host recorded work of its own (the caller rebinds its state).
    public (bool Skip, bool Recorded) BeforeDispatch(ulong shaderAddress, ReadOnlySpan<uint> userData, Action endRendering)
    {
        if (ConsumerProbe && _builtBases.Count != 0)
            ProbeConsumers(shaderAddress, userData);
        if (!_programByAddress.TryGetValue(shaderAddress, out var hash))
            return (false, false);
        switch (hash)
        {
            case Hash8395:
            {
                var constants = Pointer(userData, 4);
                Span<byte> pointer = stackalloc byte[8];
                if (constants != 0 && _cache.TryReadGuestClean(constants + 0x40, pointer))
                {
                    _pointerReads++;
                    _traversedBases.Add(BinaryPrimitives.ReadUInt64LittleEndian(pointer) & 0xFFFF_FFFF_FFFFUL);
                    _traversedBasesCount = _traversedBases.Count;
                }
                else
                {
                    _pointerMisses++;
                }
                return (false, false);
            }

            case Hash0DA3:
            {
                // V#(records, stride 28, n), V#(source, stride 96, n) x2, the work header, 0x40.
                if (userData.Length < 14)
                    return (false, false);
                var chain = GetChain(Pointer(userData, 12));
                chain.Records = Pointer(userData, 0);
                chain.Count = userData[2];
                chain.Source = Pointer(userData, 4);
                chain.Active = false;
                return (false, false);
            }

            case HashB198:
                return AtB198(userData, endRendering);

            case Hash02F1:
            {
                // The TLAS base, the work header, the node area, ...
                if (!_chains.TryGetValue(Pointer(userData, 2), out var chain))
                    return (false, false);
                // The work blocks 0xF05B fills for it (lists and the shared table), for the next frames' matching.
                for (var index = 6; index <= 10; index += 2)
                {
                    var block = Pointer(userData, index);
                    if (block != 0)
                        chain.Blocks.Add(block);
                }

                if (!chain.Active || Pointer(userData, 0) != chain.Base)
                    return (false, false);
                chain.Active = false;
                CountSkip(hash);
                if (Mode == 2)
                {
                    GuestBufferCache.PostDispatchHook = () => Verify(chain);
                    return (false, false);
                }

                return (true, false);
            }

            case Hash994A:
                // The work header, the TLAS it fills the instance table of: a TLAS base for the next frames' builds.
                if (_chains.ContainsKey(Pointer(userData, 0)) && Pointer(userData, 2) != 0)
                    _instanceTableBases.Add(Pointer(userData, 2));
                return (false, false);

            default:
            {
                Chain? owner = null;
                var active = false;
                foreach (var chain in _chains.Values)
                {
                    if (!chain.Active)
                        continue;
                    active = true;
                    if (BelongsTo(chain, userData))
                    {
                        owner = chain;
                        break;
                    }
                }

                if (owner == null)
                {
                    if (active)
                        _unmatched[hash] = _unmatched.GetValueOrDefault(hash) + 1;
                    return (false, false);
                }

                CountSkip(hash);
                return (Mode == 1, false);
            }
        }
    }

    // [local] SHARPEMU_TLAS_HLE_CONSUMERS=1: which programs reach a TLAS some chain built, through a user-data pointer
    // or a qword in the first 512 bytes behind one (sampled), by the TLAS's instance count.
    private static readonly bool ConsumerProbe = Environment.GetEnvironmentVariable("SHARPEMU_TLAS_HLE_CONSUMERS") == "1";
    private readonly Dictionary<ulong, ulong> _anyProgram = new();
    private readonly Dictionary<ulong, uint> _builtBases = new();
    private readonly Dictionary<ulong, int> _probeTicks = new();
    private static readonly Dictionary<string, long> _consumers = new();

    // A CPU read (program 0: the guest's own; else a table read for that program's preparation) of a built TLAS: the
    // header's +0x8C/+0x90 apart, as nothing else writes them.
    public void NoteCpuRead(ulong address, ulong size, ulong program)
    {
        if (!ConsumerProbe || _builtBases.Count == 0)
            return;
        lock (_cpuReads)
        {
            foreach (var (tlas, count) in _builtBases)
            {
                var total = 0x100UL + count * 192UL;
                if (address >= tlas + total || address + size <= tlas)
                    continue;
                var part = address < tlas + 0x100
                    ? (address < tlas + 0x94 && address + size > tlas + 0x8C ? "hdr8C" : "hdr")
                    : address < tlas + 0x100 + count * 64UL ? "nodes" : "instances";
                var key = $"{(program == 0 ? "cpu" : $"{program >> 48:X4}")}/{part}/n{count}";
                _cpuReads[key] = _cpuReads.GetValueOrDefault(key) + 1;
            }
        }
    }

    private static readonly Dictionary<string, long> _cpuReads = new();

    private void ProbeConsumers(ulong shaderAddress, ReadOnlySpan<uint> userData)
    {
        if (!_anyProgram.TryGetValue(shaderAddress, out var hash) || (IsChainHash(hash) && hash != Hash8395))
            return;
        var tick = _probeTicks.GetValueOrDefault(shaderAddress);
        _probeTicks[shaderAddress] = tick + 1;
        var deep = tick % 16 == 0;
        Span<byte> block = stackalloc byte[0x200];
        for (var index = 0; index + 1 < userData.Length; index += 2)
        {
            var pointer = Pointer(userData, index);
            if (pointer == 0)
                continue;
            if (_builtBases.TryGetValue(pointer, out var count))
            {
                Note(hash, $"ud{index}", count);
                continue;
            }

            if (!deep || !_cache.TryReadGuestUnchecked(pointer, block))
                continue;
            for (var offset = 0; offset < block.Length; offset += 8)
            {
                var value = BinaryPrimitives.ReadUInt64LittleEndian(block[offset..]) & 0xFFFF_FFFF_FFFFUL;
                if (value != 0 && _builtBases.TryGetValue(value, out count))
                    Note(hash, $"ud{index}+{offset:X}", count);
            }
        }

        static void Note(ulong hash, string where, uint count)
        {
            var key = $"{hash:X16}/{where}/n{count}";
            _consumers[key] = _consumers.GetValueOrDefault(key) + 1;
        }
    }

    private static void CountSkip(ulong hash)
    {
        _skipped++;
        _skippedByHash[hash] = _skippedByHash.GetValueOrDefault(hash) + 1;
    }

    // A chain dispatch by its user data: the work header (some passes set its low bit), the block just below it
    // (0x7846/0x35B8 counters), or the TLAS itself (0xD5D0 in the node area, 0x35B8 on the header).
    private static bool BelongsTo(Chain chain, ReadOnlySpan<uint> userData)
    {
        for (var index = 0; index + 1 < userData.Length; index++)
        {
            var pointer = Pointer(userData, index);
            if ((pointer & ~1UL) == chain.Key || (pointer >= chain.Key - 0x100 && pointer < chain.Key) || chain.Blocks.Contains(pointer) ||
                (pointer >= chain.Base && pointer < chain.Base + chain.Total))
                return true;
        }

        return false;
    }

    private (bool Skip, bool Recorded) AtB198(ReadOnlySpan<uint> userData, Action endRendering)
    {
        // V#(records, stride 28, n), V#(node area, stride 16, n), the work header, 0x40.
        if (userData.Length < 10)
            return (false, false);
        var chain = GetChain(Pointer(userData, 8));
        chain.Active = false;
        var tlas = Pointer(userData, 4) - 0x100;
        var traversed = _traversedBases.Contains(tlas);
        var known = traversed || _instanceTableBases.Contains(tlas);
        var signature = chain.Records != 0 && Pointer(userData, 0) == chain.Records && userData[2] == chain.Count && userData[6] == chain.Count;
        if (signature && ConsumerProbe)
            lock (_cpuReads) _builtBases[tlas] = chain.Count;
        var seen = $"n{chain.Count}/{(traversed ? "traversed" : known ? "994a" : "unknown")}/{(signature ? "sig" : "nosig")}";
        _chainsSeen[seen] = _chainsSeen.GetValueOrDefault(seen) + 1;
        if (!known || !signature)
            return (false, false);
        chain.Base = tlas;
        chain.Total = 0x100UL + chain.Count * 64UL + chain.Count * 128UL;
        chain.VerifyThis = Mode == 2 && chain.Builds++ % VerifyEvery == 0;
        if (Mode == 2 && !chain.VerifyThis)
            return (false, false);
        endRendering();
        if (!TryBuild(chain, out var reason))
        {
            Fallback(reason);
            return (false, true);
        }

        chain.Active = true;
        CountSkip(HashB198);
        return (Mode == 1, true);
    }

    private Chain GetChain(ulong work)
    {
        if (!_chains.TryGetValue(work, out var chain))
            _chains[work] = chain = new Chain { Key = work, Slot = _chains.Count % VerifySlots };
        return chain;
    }

    private static void Fallback(string reason)
    {
        _fallbacks++;
        _fallbackReasons[reason] = _fallbackReasons.GetValueOrDefault(reason) + 1;
    }

    // A 4-wide tree over the instances in Morton order of their translations, breadth first (children after their
    // parent): a range of more than four splits into chunks of the largest power of four below it, so leaves pack.
    internal static (List<uint> Entries, int Nodes) BuildTopology(ReadOnlySpan<(float X, float Y, float Z)> positions)
    {
        var count = positions.Length;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var (x, y, z) in positions)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
        }

        static ulong Quantize(float value, float min, float max) =>
            max > min ? (ulong)Math.Clamp((value - min) / (max - min) * 1023f, 0f, 1023f) : 0;
        static ulong Spread(ulong value)
        {
            value &= 0x3FF;
            value = (value | (value << 16)) & 0x30000FF;
            value = (value | (value << 8)) & 0x300F00F;
            value = (value | (value << 4)) & 0x30C30C3;
            value = (value | (value << 2)) & 0x9249249;
            return value;
        }

        var keys = new (ulong Code, int Index)[count];
        for (var index = 0; index < count; index++)
        {
            var (x, y, z) = positions[index];
            keys[index] = ((Spread(Quantize(x, minX, maxX)) << 2) | (Spread(Quantize(y, minY, maxY)) << 1) | Spread(Quantize(z, minZ, maxZ)), index);
        }

        Array.Sort(keys, (left, right) => left.Code != right.Code ? left.Code.CompareTo(right.Code) : left.Index.CompareTo(right.Index));
        var entries = new List<uint>();
        var ranges = new List<(int Start, int Length)> { (0, count) };
        for (var node = 0; node < ranges.Count; node++)
        {
            var (start, length) = ranges[node];
            var chunk = 1;
            while (chunk * 4 < length)
                chunk *= 4;
            var offset = start;
            for (var slot = 0; slot < 4; slot++)
            {
                var part = Math.Min(chunk, start + length - offset);
                if (part <= 0)
                {
                    entries.Add(0xFFFFFFFF);
                    continue;
                }

                if (part == 1)
                {
                    entries.Add(0x80000000u | (uint)keys[offset].Index);
                }
                else
                {
                    entries.Add((uint)ranges.Count);
                    ranges.Add((offset, part));
                }

                offset += part;
            }
        }

        return (entries, ranges.Count);
    }

    // The node words: child pointers, boxes (empty slots +inf/-inf; the box pass fills the others), the trailer.
    internal static uint[] NodeWords(List<uint> entries, int nodes)
    {
        var words = new uint[nodes * 32];
        for (var node = 0; node < nodes; node++)
        {
            for (var child = 0; child < 4; child++)
            {
                var entry = entries[node * 4 + child];
                words[node * 32 + child] = entry == 0xFFFFFFFF ? 0xFFFFFFFF
                    : (entry & 0x80000000) != 0 ? (entry & 0x7FFFFFFF) << 3
                    : ((4 + 2 * entry) << 3) | 5;
                for (var component = 0; component < 6; component++)
                    words[node * 32 + 4 + child * 6 + component] = entry == 0xFFFFFFFF
                        ? BitConverter.SingleToUInt32Bits(component < 3 ? float.PositiveInfinity : float.NegativeInfinity)
                        : 0;
            }

            for (var tail = 28; tail < 32; tail++)
                words[node * 32 + tail] = 0xFFFFFFFF;
        }

        return words;
    }

    // The header words the build owns, as (dword index, value).
    internal static (int Dword, uint Value)[] HeaderWords(ulong tlas, uint count, int nodes)
    {
        var instanceOffset = 0x100UL + count * 64UL;
        var total = instanceOffset + count * 128UL;
        var instances = tlas + instanceOffset;
        return
        [
            (0x10 / 4, (uint)total),
            (0x28 / 4, (uint)instanceOffset),
            (0x50 / 4, (uint)(4 + 2 * nodes - 1)),
            (0x74 / 4, count),
            (37, (uint)tlas), (38, (uint)((tlas >> 32) & 0xFFFF) | 0x00010000u), (39, (uint)total), (40, 0x30005004u),
            (41, (uint)instances), (42, (uint)((instances >> 32) & 0xFFFF) | 0x00800000u), (43, count), (44, 0x00016204u),
        ];
    }

    private const int VerifySlots = 4;
    private static readonly bool PoisonBuildCounters = Environment.GetEnvironmentVariable("SHARPEMU_TLAS_HLE_POISON") == "1";

    private static ulong VerifySlotOffset(Chain chain) => (ulong)chain.Slot * (MaxNodes / VerifySlots) * 128UL;

    private bool TryBuild(Chain chain, out string reason)
    {
        reason = string.Empty;
        var count = (int)chain.Count;
        if (count < 2 || count > MaxNodes)
        {
            reason = "count";
            return false;
        }

        // The translations only order the leaves: the boxes come from 0x0DA3's records on the GPU, so a stale or
        // unreadable source costs tree quality, never correctness. No wait for the GPU here.
        var source = new byte[count * 96];
        if (!_cache.TryReadGuestClean(chain.Source, source))
        {
            _sourceGpuDirty++;
            if (!_cache.TryReadGuestUnchecked(chain.Source, source))
                source.AsSpan().Clear();
        }

        var positions = new (float X, float Y, float Z)[count];
        for (var index = 0; index < count; index++)
        {
            var record = MemoryMarshal.Cast<byte, float>(source.AsSpan(index * 96, 96));
            var (x, y, z) = (record[17], record[18], record[19]);
            positions[index] = float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) ? (x, y, z) : (0, 0, 0);
        }

        var (entries, nodes) = BuildTopology(positions);
        if (nodes > (Mode == 2 ? MaxNodes / VerifySlots : MaxNodes) || (ulong)nodes * 128 > (ulong)count * 64)
        {
            reason = "node_area";
            return false;
        }

        chain.Nodes = nodes;
        chain.Topology = [.. entries];
        var nodeWords = NodeWords(entries, nodes);
        GpuBuffer output;
        ulong outputOffset;
        if (Mode == 2)
        {
            // The header as the CP and 0x5F74 left it, before the game's chain.
            var (game, gameOffset) = _cache.ObtainBuffer(chain.Base, 0x100, isWritten: false);
            chain.HeaderBefore?.Dispose();
            chain.HeaderBefore = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 0x100);
            chain.HeaderBefore.CopyFrom(_scheduler.Current, game, gameOffset, 0, 0x100, destinationAfter: AccessFlags.HostReadBit);
            // A slot per chain: the three chains of a frame build before any of them is compared.
            output = _verifyOutput!;
            outputOffset = VerifySlotOffset(chain);
            var staging = _cache.UploadStream(MemoryMarshal.AsBytes<uint>(nodeWords), 4);
            output.CopyFrom(_scheduler.Current, staging.Buffer, staging.Offset, outputOffset, (ulong)nodeWords.Length * 4, AccessFlags.HostWriteBit);
        }
        else
        {
            (output, outputOffset) = _cache.ObtainBuffer(chain.Base, chain.Total, isWritten: true);
            // Only the fields the build owns; the rest stays as the CP template and 0x5F74 left it.
            Span<byte> word = stackalloc byte[4];
            foreach (var (dword, value) in HeaderWords(chain.Base, chain.Count, nodes))
            {
                if (dword == 0x50 / 4)
                    continue; // the template's capacity bound (n / 2 nodes rounded up) covers the host's smaller tree
                BinaryPrimitives.WriteUInt32LittleEndian(word, value);
                _cache.WriteHostMemory(chain.Base + (ulong)dword * 4, word);
                if (TableWordMemo.Enabled)
                    TableWordMemo.StoreHostWrite(chain.Base + (ulong)dword * 4, value);
            }

            if (PoisonBuildCounters)
            {
                // [local] SHARPEMU_TLAS_HLE_POISON=1: +0x8C/+0x90 (0x35B8's, left at the template's 0) set to garbage,
                // to show by the image that nothing outside the skipped chain reads them.
                BinaryPrimitives.WriteUInt32LittleEndian(word, 0xDEADBEEF);
                _cache.WriteHostMemory(chain.Base + 0x8C, word);
                _cache.WriteHostMemory(chain.Base + 0x90, word);
            }

            _cache.WriteHostMemory(chain.Base + 0x100, MemoryMarshal.AsBytes<uint>(nodeWords));
            outputOffset += 0x100;
        }

        var (records, recordsOffset) = _cache.ObtainBuffer(chain.Records, (ulong)count * 28, isWritten: false);
        var topology = new uint[4 + entries.Count];
        topology[0] = (uint)nodes;
        topology[1] = (uint)(recordsOffset / 4);
        topology[2] = (uint)(outputOffset / 4);
        topology[3] = 7;
        entries.CopyTo(topology, 4);
        var (topologyBuffer, topologyOffset) = _cache.UploadStream(MemoryMarshal.AsBytes<uint>(topology), _device.MinStorageBufferOffsetAlignment);
        RecordBoxPass(records, topologyBuffer, topologyOffset, (ulong)topology.Length * 4, output);
        _builds++;
        return true;
    }

    private void RecordBoxPass(GpuBuffer records, GpuBuffer topology, ulong topologyOffset, ulong topologySize, GpuBuffer output)
    {
        var vk = _device.Vk;
        var set = _sets[_nextSet++ % SetRing];
        var infos = stackalloc DescriptorBufferInfo[4];
        infos[0] = new DescriptorBufferInfo(records.Handle, 0, records.Size);
        infos[1] = new DescriptorBufferInfo(topology.Handle, topologyOffset, topologySize);
        infos[2] = new DescriptorBufferInfo(output.Handle, 0, output.Size);
        infos[3] = new DescriptorBufferInfo(_scratch.Handle, 0, _scratch.Size);
        var writes = stackalloc WriteDescriptorSet[4];
        for (uint index = 0; index < 4; index++)
            writes[index] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = index, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + index };
        vk.UpdateDescriptorSets(_device.Device, 4, writes, 0, null);

        _scheduler.EndRendering();
        var command = new CommandBuffer(_scheduler.Current.Handle);
        var before = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit,
        };
        VulkanSynchronization.PipelineBarrier(vk, command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &before, 0, null, 0, null);
        VkRecord.CmdBindPipeline(vk, command, PipelineBindPoint.Compute, _pipeline);
        VkRecord.CmdBindDescriptorSets(vk, command, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        VkRecord.CmdDispatch(vk, command, 1, 1, 1);
        var after = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.ShaderWriteBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        VulkanSynchronization.PipelineBarrier(vk, command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit, 0, 1, &after, 0, null, 0, null);
    }

    // =2: after the game's 0x02F1, its TLAS (header, nodes) and ours, compared once the batch completes.
    private void Verify(Chain chain)
    {
        if (chain.Topology is not { } topology || _verifyOutput is null || chain.HeaderBefore is not { } before)
            return;
        chain.HeaderBefore = null;
        var count = (int)chain.Count;
        var gameBytes = 0x100UL + (ulong)count * 64;
        var ourBytes = (ulong)chain.Nodes * 128;
        var download = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, gameBytes + ourBytes);
        var (game, gameOffset) = _cache.ObtainBuffer(chain.Base, gameBytes, isWritten: false);
        _scheduler.EndRendering();
        download.CopyFrom(_scheduler.Current, game, gameOffset, 0, gameBytes);
        download.CopyFrom(_scheduler.Current, _verifyOutput, VerifySlotOffset(chain), gameBytes, ourBytes, destinationAfter: AccessFlags.HostReadBit);
        var nodes = chain.Nodes;
        var tlas = chain.Base;
        _scheduler.QueueCompletionAction(() =>
        {
            download.Invalidate(0, download.Size);
            before.Invalidate(0, before.Size);
            Compare(download.Mapped[..(int)gameBytes], download.Mapped.Slice((int)gameBytes, (int)ourBytes), before.Mapped[..0x100], tlas, count, nodes, topology);
            download.Dispose();
            before.Dispose();
        });
    }

    private static readonly object ReportLock = new();

    private static void Compare(ReadOnlySpan<byte> game, ReadOnlySpan<byte> ours, ReadOnlySpan<byte> headerBefore, ulong tlas, int count, int nodes, uint[] topology)
    {
        lock (ReportLock)
        {
            try
            {
                CompareLocked(game, ours, headerBefore, tlas, count, nodes, topology);
            }
            catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                if (_compareErrors++ == 0)
                    Console.Error.WriteLine($"[TLAS_HLE] compare failed at stage {_compareStage}: {exception}");
            }
        }
    }

    private static void CompareLocked(ReadOnlySpan<byte> game, ReadOnlySpan<byte> ours, ReadOnlySpan<byte> headerBefore, ulong tlas, int count, int nodes, uint[] topology)
    {
        _verifies++;
        _compareStage = 1;
        var gameWords = MemoryMarshal.Cast<byte, uint>(game);
        var ourWords = MemoryMarshal.Cast<byte, uint>(ours);
        var ourFloats = MemoryMarshal.Cast<byte, float>(ours);
        var beforeWords = MemoryMarshal.Cast<byte, uint>(headerBefore);

        // Header: the dwords the game's chain wrote, and the ones the host would write that differ (+0x50 aside: it
        // follows the tree).
        for (var dword = 0; dword < 64; dword++)
            if (gameWords[dword] != beforeWords[dword])
                _chainWrittenDwords[dword] = _chainWrittenDwords.GetValueOrDefault(dword) + 1;
        foreach (var (dword, value) in HeaderWords(tlas, (uint)count, nodes))
        {
            if (dword != 0x50 / 4 && gameWords[dword] != value)
            {
                _headerFieldDiff++;
                _headerDiffDwords[dword] = _headerDiffDwords.GetValueOrDefault(dword) + 1;
            }
        }

        if (_headerSample.Length == 0)
        {
            var sample = new List<string>();
            for (var dword = 0; dword < 64; dword++)
                if (gameWords[dword] != 0 || beforeWords[dword] != 0)
                    sample.Add($"{dword}/{beforeWords[dword]:X}>{gameWords[dword]:X}");
            _headerSample = string.Join(' ', sample);
        }

        _compareStage = 2;
        // The game's tree from its root: the box slot of each instance leaf.
        var gameLeaves = new Dictionary<int, int>();
        var stack = new Stack<uint>();
        stack.Push(0x25);
        var visited = 0;
        while (stack.Count != 0 && visited++ < 4 * MaxNodes)
        {
            var unit = (long)(stack.Pop() >> 3);
            if (unit < 4 || (unit + 2) * 16 > gameWords.Length)
            {
                _gameBadPointers++;
                continue;
            }
            _gameNodes++;
            for (var child = 0; child < 4; child++)
            {
                var pointer = gameWords[(int)unit * 16 + child];
                if (pointer == 0xFFFFFFFF || (pointer & 7) == 6)
                    continue;
                if ((pointer & 7) == 5)
                    stack.Push(pointer);
                else if ((pointer & 7) == 0)
                    gameLeaves[(int)(pointer >> 3)] = (int)unit * 16 + 4 + child * 6;
            }
        }

        _compareStage = 3;
        if (_oursSample.Length == 0)
        {
            var sample = new List<string> { $"bytes={ours.Length} game_bytes={game.Length} nodes={nodes} topo={topology.Length}" };
            for (var index = 0; index < 28 && index < ourWords.Length; index++)
                sample.Add(index < 4 ? $"{ourWords[index]:X}" : $"{ourFloats[index]:0.##}");
            _oursSample = string.Join(' ', sample);
        }
        _ourNodes += nodes;
        var ourLeaves = new HashSet<int>();
        for (var node = 0; node < nodes; node++)
        {
            for (var child = 0; child < 4; child++)
            {
                var entry = topology[node * 4 + child];
                if (entry == 0xFFFFFFFF)
                    continue;
                var slot = node * 32 + 4 + child * 6;
                if ((entry & 0x80000000) != 0)
                {
                    var index = (int)(entry & 0x7FFFFFFF);
                    ourLeaves.Add(index);
                    if (!gameLeaves.TryGetValue(index, out var gameSlot))
                        continue;
                    for (var component = 0; component < 6; component++)
                    {
                        if (ourWords[slot + component] != gameWords[gameSlot + component])
                        {
                            _leafMismatches++;
                            break;
                        }
                    }
                }
                else
                {
                    // The child node's own boxes inside the one written for it here.
                    for (var grandChild = 0; grandChild < 4; grandChild++)
                    {
                        if (topology[entry * 4 + grandChild] == 0xFFFFFFFF)
                            continue;
                        var inner = (int)entry * 32 + 4 + grandChild * 6;
                        for (var component = 0; component < 6; component++)
                        {
                            var value = ourFloats[inner + component];
                            var bound = ourFloats[slot + component];
                            if (component < 3 ? value < bound : value > bound)
                            {
                                _containment++;
                                break;
                            }
                        }
                    }
                }
            }
        }

        _compareStage = 4;
        foreach (var index in gameLeaves.Keys)
            if (!ourLeaves.Contains(index))
                _instanceSetDiff++;
        foreach (var index in ourLeaves)
            if (!gameLeaves.ContainsKey(index))
                _instanceSetDiff++;
    }

    private static string CpuReads()
    {
        lock (_cpuReads)
        {
            var text = string.Join(",", _cpuReads.Select(pair => $"{pair.Key}/{pair.Value}"));
            _cpuReads.Clear();
            return text;
        }
    }

    public static string? TakeReport()
    {
        if (!Enabled)
            return null;
        lock (ReportLock)
            return TakeReportLocked();
    }

    private static string TakeReportLocked()
    {
        static string Hashes(Dictionary<ulong, long> counts) => string.Join(",", counts.Select(pair => $"{pair.Key >> 48:X4}/{pair.Value}"));
        var line = $"[PERF][TLAS_HLE] mode={Mode} tlas_pointer_reads={_pointerReads}/{_pointerMisses} bases={_traversedBasesCount} game_bad_pointers={_gameBadPointers} compare_errors={_compareErrors} source_gpu_dirty={_sourceGpuDirty} builds={_builds} skipped={_skipped} (mode 2: would skip) [{Hashes(_skippedByHash)}] unmatched=[{Hashes(_unmatched)}] " +
            $"fallbacks={_fallbacks} [{string.Join(",", _fallbackReasons.Select(pair => $"{pair.Key}/{pair.Value}"))}] " +
            $"chains=[{string.Join(",", _chainsSeen.Select(pair => $"{pair.Key}/{pair.Value}"))}] verifies={_verifies} " +
            $"leaf_mismatches={_leafMismatches} instance_set_diff={_instanceSetDiff} containment={_containment} game_nodes={_gameNodes} our_nodes={_ourNodes} " +
            $"header_field_diff={_headerFieldDiff} [{string.Join(",", _headerDiffDwords.Select(pair => $"{pair.Key}/{pair.Value}"))}] " +
            $"chain_written=[{string.Join(",", _chainWrittenDwords.Select(pair => $"{pair.Key}/{pair.Value}"))}] header=[{_headerSample}] ours=[{_oursSample}] consumers=[{string.Join(",", _consumers.Select(pair => $"{pair.Key}/{pair.Value}"))}] cpu_reads=[{CpuReads()}]";
        _pointerReads = _pointerMisses = _gameBadPointers = _sourceGpuDirty = 0;
        _builds = _skipped = _fallbacks = _verifies = _leafMismatches = _instanceSetDiff = _containment = _gameNodes = _ourNodes = _headerFieldDiff = 0;
        _fallbackReasons.Clear();
        _consumers.Clear();
        _skippedByHash.Clear();
        _unmatched.Clear();
        _chainsSeen.Clear();
        _chainWrittenDwords.Clear();
        _headerDiffDwords.Clear();
        _headerSample = string.Empty;
        return line;
    }

    public void Dispose()
    {
        var vk = _device.Vk;
        vk.DestroyPipeline(_device.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_device.Device, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_device.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_device.Device, _setLayout, null);
        _scratch.Dispose();
        _verifyOutput?.Dispose();
    }
}
