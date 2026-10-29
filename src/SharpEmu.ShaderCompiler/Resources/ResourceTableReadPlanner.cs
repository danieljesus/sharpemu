// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// Collects the scalar reads reachable from descriptor handles. Reads with an immediate
// offset receive compact flattened-table slots; dynamic offsets stay explicit.
public sealed class ResourceTableReadPlanner
{
    private readonly ScalarValueGraph _graph;
    private readonly ShaderStage _stage;
    private readonly ulong _hash;
    private static readonly HashSet<ulong> DeviceTableHashes = ParseHashes(System.Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_TABLE_HASHES")); // [local] ',' or '/'
    private readonly List<ResourceTableRead> _reads = [];
    private readonly List<ScalarValue> _dynamicReads = [];
    private readonly List<(ScalarValue Read, uint Slot)> _patches = [];
    private readonly Dictionary<int, uint> _flattenedSlotByMemoryIndex = [];
    private readonly List<ScalarValue> _visiting = [];
    private readonly HashSet<ScalarValue> _visited = [];
    private readonly Dictionary<ScalarValue, ScalarValue> _replacements = [];

    private ResourceTableReadPlanner(ScalarValueGraph graph, ShaderStage stage, ulong hash)
    {
        _graph = graph;
        _stage = stage;
        _hash = hash;
    }

    public sealed record Result(
        IReadOnlyList<ResourceTableRead> Reads,
        IReadOnlyList<ScalarValue> DynamicReads,
        IReadOnlyDictionary<ScalarValue, ScalarValue> Replacements,
        IReadOnlyDictionary<int, uint> FlattenedSlotByMemoryIndex);

    public static bool IsDeviceTableHash(ulong hash) => DeviceTableHashes.Contains(hash); // [local]

    // [local] SHARPEMU_DEVICE_DATA_READS=<hashes> ('/' or ',' separated; empty or 0 off): in the listed shaders an
    // immediate-offset S_LOAD whose result is only shader data (no descriptor, handle or other read depends on it, and
    // no other dword of the same load was flattened for one) is not flattened into the host table: the shader performs
    // it through the device-address page table, as the hardware does, so its word is read when the dispatch runs and the
    // host never reads it while recording. Only the loads' own address operands are still planned.
    private static readonly HashSet<ulong> DeviceDataReadHashes = ParseHashes(System.Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_DATA_READS"));

    internal static HashSet<ulong> ParseHashes(string? value) =>
        value is null or "0" ? [] : new(value.Split(['/', ','], System.StringSplitOptions.RemoveEmptyEntries).Select(h => System.Convert.ToUInt64(h, 16)));

    public static bool IsDeviceDataReadHash(ulong hash) => DeviceDataReadHashes.Contains(hash);

    private static long _deviceDataReads;

    public static long DeviceDataReads => System.Threading.Interlocked.Read(ref _deviceDataReads);

    public static Result Plan(ScalarValueGraph graph, ShaderStage stage, ulong hash) =>
        new ResourceTableReadPlanner(graph, stage, hash).Run();

    private Result Run()
    {
        foreach (var value in _graph.Values)
        {
            if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord &&
                value.MemoryIndex < _graph.Memory.Count)
            {
                var kind = _graph.Memory[value.MemoryIndex].Kind;
                var crosswired = (value.Kind == ScalarValueKind.ScalarAddressWord && kind == MemoryResourceKind.ScalarBuffer) ||
                    (value.Kind == ScalarValueKind.ScalarBufferWord && kind == MemoryResourceKind.ScalarAddress);
                if (crosswired)
                {
                    Fail(_graph.Memory[value.MemoryIndex].Pc, $"{value.Kind} has incompatible scalar memory metadata");
                }
            }
        }

        foreach (var access in _graph.Accesses)
        {
            if (access?.Handle is { } handle)
            {
                foreach (var dword in handle.Operands)
                {
                    Collect(dword, 0);
                }
            }

            if (access?.SamplerHandle is { } sampler)
            {
                foreach (var dword in sampler.Operands)
                {
                    Collect(dword, 0);
                }
            }
        }

        var validatorReadCount = _reads.Count;
        var deviceData = DeviceDataReadHashes.Contains(_hash); // [local] SHARPEMU_DEVICE_DATA_READS
        HashSet<int>? handleIndices = null;
        if (deviceData)
        {
            handleIndices = [];
            foreach (var (patched, _) in _patches)
                handleIndices.Add(patched.MemoryIndex);
        }

        foreach (var access in _graph.Accesses)
        {
            if (access?.Read is { Kind: ScalarValueKind.ScalarAddressWord } read &&
                IsRawRead(read) && read.Operands[1].IsConstant &&
                new RuntimeValueValidator(_graph, _graph.UserDataBase, _graph.UserDataCount, validatorReadCount).Validate(read))
            {
                if (handleIndices is not null && !handleIndices.Contains(read.MemoryIndex))
                {
                    foreach (var operand in read.Operands[0].Operands)
                        Collect(operand, _graph.Memory[read.MemoryIndex].Pc);
                    System.Threading.Interlocked.Increment(ref _deviceDataReads);
                    continue;
                }

                Collect(read, _graph.Memory[read.MemoryIndex].Pc);
            }
        }

        PatchReads();
        return new Result(_reads, _dynamicReads, _replacements, _flattenedSlotByMemoryIndex);
    }

    private void Fail(uint pc, string message) =>
        throw new ResourcePlanException(
            $"shader resource-table planning failed: hash=0x{_hash:X16} stage={_stage} pc=0x{pc:X8} {message}");

    private bool IsRawRead(ScalarValue value) =>
        new RuntimeValueValidator(_graph, _graph.UserDataBase, _graph.UserDataCount, 0).IsRawRead(value);

    private void Collect(ScalarValue value, uint usePc)
    {
        if (value.IsConstant)
        {
            return;
        }

        var cycle = _visiting.IndexOf(value);
        if (cycle >= 0)
        {
            if (_visiting.Skip(cycle).Any(pending => pending.Kind == ScalarValueKind.Phi))
            {
                return;
            }

            Fail(usePc, $"cyclic planning value {value.Kind} without a phi");
        }

        if (!_visited.Add(value))
        {
            return;
        }

        _visiting.Add(value);
        foreach (var operand in value.Operands)
        {
            Collect(operand, usePc);
        }

        _visiting.RemoveAt(_visiting.Count - 1);
        if (!IsRawRead(value) || DeviceTableHashes.Contains(_hash)) // [local] experiment: listed shaders read their tables on the device
        {
            return;
        }

        var offset = value.Operands[1];
        if (!offset.IsConstant)
        {
            if (!_dynamicReads.Contains(value))
            {
                _dynamicReads.Add(value);
            }

            return;
        }

        // A read whose address depends on values the host cannot know (a loop over data the
        // shader loaded itself) cannot be flattened; the shader performs it on the device.
        if (!new RuntimeValueValidator(_graph, _graph.UserDataBase, _graph.UserDataCount, 0).Validate(value))
        {
            return;
        }

        for (var slot = 0; slot < _reads.Count; slot++)
        {
            if (_graph.Equivalent(value, _reads[slot].Value))
            {
                _patches.Add((value, (uint)slot));
                return;
            }
        }

        var newSlot = (uint)_reads.Count;
        _reads.Add(new ResourceTableRead(value, newSlot));
        _patches.Add((value, newSlot));
    }

    // Equivalent reads share one host read, but each instruction must load its destination
    // from that slot instead of reading guest memory again on the device.
    private void PatchReads()
    {
        foreach (var (read, slot) in _patches)
        {
            _replacements[read] = _graph.ResourceTableWord(slot);
            if (read.MemoryIndex < _graph.Memory.Count)
            {
                _graph.Memory[read.MemoryIndex].PlanningOnly = true;
                _flattenedSlotByMemoryIndex.Add(read.MemoryIndex, slot);
            }
        }
    }
}
