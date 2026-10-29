// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// [local] SHARPEMU_SRT_READ_PROBE=1 (diagnostic): what a synchronizing guest read of a materialization is for. The raw
// read in progress names its plan and graph node; Describe lists every plan output that reaches the node and how
// (the node itself is the output word, or the value flows through a pointer or an index of another read).
public static class SrtReadProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_SRT_READ_PROBE") == "1";

    [ThreadStatic]
    internal static ShaderResourcePlan? CurrentPlan;

    private static readonly Dictionary<(ulong Plan, int Node), string> _described = new();

    public static ulong CurrentPlanHash => CurrentPlan?.Hash ?? 0;

    public static int CurrentNode => RuntimeValueEvaluator.CurrentReadNode;

    public static ulong CurrentBaseAddress => RuntimeValueEvaluator.CurrentReadBaseAddress;

    public static int CurrentBaseRegister => RuntimeValueEvaluator.CurrentReadBase;

    public static string Describe()
    {
        var plan = CurrentPlan;
        var node = RuntimeValueEvaluator.CurrentReadNode;
        if (plan is null || node == RuntimeValueEvaluator.NoReadNode)
            return "plan=none";
        lock (_described)
        {
            if (_described.TryGetValue((plan.Hash, node), out var cached))
                return cached;
            var text = DescribeUncached(plan, node);
            _described[(plan.Hash, node)] = text;
            return text;
        }
    }

    private static string DescribeUncached(ShaderResourcePlan plan, int node)
    {
        var uses = new List<string>();
        ScalarValue? self = null;
        for (var source = 0; source < plan.DescriptorSources.Count; source++)
        {
            var words = plan.DescriptorSources[source].Dwords;
            for (var word = 0; word < words.Length; word++)
            {
                var path = Reach(words[word], node, ref self);
                if (path is not null)
                    uses.Add($"{SourceRole(plan, (uint)source)}.w{word}:{path}");
            }
        }

        for (var index = 0; index < plan.TableReads.Count; index++)
        {
            var path = Reach(plan.TableReads[index].Value, node, ref self);
            if (path is not null)
                uses.Add($"flat{plan.TableReads[index].FlatOffset}:{path}");
        }

        foreach (var range in plan.DeviceAddressRanges)
        {
            var low = Reach(range.BaseLow, node, ref self);
            var high = Reach(range.BaseHigh, node, ref self);
            if (low is not null || high is not null)
                uses.Add($"bda{range.Handle}{(range.Written ? "W" : "R")}{(range.Bounded ? $"[{range.FirstByte}..{range.EndByte})" : "[unb]")}.{(low is not null ? "lo:" + low : "hi:" + high)}");
        }

        for (var index = 0; index < plan.DeviceStoreTables.Count; index++)
        {
            var path = Reach(plan.DeviceStoreTables[index], node, ref self);
            if (path is not null)
                uses.Add($"storetable{index}:{path}");
        }

        for (var index = 0; index < plan.ResourceBranches.Count; index++)
        {
            if (plan.ResourceBranches[index].Condition is { } condition && Reach(condition, node, ref self) is { } path)
                uses.Add($"branch{index}:{path}");
        }

        for (var index = 0; index < plan.BufferCandidateTables.Count; index++)
        {
            var table = plan.BufferCandidateTables[index];
            if (Reach(table.SrtHandle, node, ref self) is { } handle)
                uses.Add($"candidates{index}.handle:{handle}");
            if (table.Limit is { } limit && Reach(limit, node, ref self) is { } bound)
                uses.Add($"candidates{index}.limit:{bound}");
        }

        // Direct users in the original graph (before flattened reads were replaced by table words): a raw read whose
        // handle (ptr) or offset (idx) takes this value is why the planner flattened it with the handles.
        var users = new List<string>();
        foreach (var value in plan.Graph.Values)
        {
            for (var index = 0; index < value.Operands.Length; index++)
            {
                var operand = value.Operands[index];
                var direct = operand.Id == node;
                if (!direct && operand.Kind is ScalarValueKind.AddressHandle or ScalarValueKind.BufferHandle)
                    direct = operand.Operands.Any(part => part.Id == node);
                if (!direct)
                    continue;
                var raw = value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord;
                users.Add(raw ? $"{(index == 0 ? "ptr" : "idx")}>mem{value.MemoryIndex}" : value.Kind == ScalarValueKind.Operation ? value.Operation.ToString() : value.Kind.ToString());
            }
        }

        var memory = self is not null && self.MemoryIndex < plan.Memory.Count ? plan.Memory[self.MemoryIndex] : null;
        var origin = self is null ? "?" : $"{self.Kind} mem{self.MemoryIndex} pc=0x{memory?.Pc:X} op={memory?.Opcode} imm=0x{memory?.Offset:X} base={DescribeBase(self)}";
        return $"node={node} {origin} uses=[{string.Join(" ", uses.Take(16))}{(uses.Count > 16 ? $" +{uses.Count - 16}" : "")}] users=[{string.Join(" ", users.Distinct().Take(12))}] device={ResourceTableReadPlanner.IsDeviceDataReadHash(plan.Hash)}";
    }

    private static string DescribeBase(ScalarValue read)
    {
        if (read.Operands.Length == 0)
            return "-";
        var handle = read.Operands[0];
        var text = handle.Operands.Length >= 2 ? $"{Short(handle.Operands[0])}:{Short(handle.Operands[1])}" : Short(handle);
        return read.Operands.Length > 1 ? $"{text}+{Short(read.Operands[1])}" : text;
    }

    private static string Short(ScalarValue value)
    {
        var text = value.ToString();
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    private static string SourceRole(ShaderResourcePlan plan, uint source)
    {
        var info = plan.Info;
        for (var index = 0; index < info.Buffers.Count; index++)
            if (info.Buffers[index].Source == source)
                return $"buf{index}{(info.Buffers[index].Written ? "W" : "R")}{(info.Buffers[index].Formatted ? "f" : "")}";
        for (var index = 0; index < info.Images.Count; index++)
            if (info.Images[index].Source == source)
                return $"img{index}";
        for (var index = 0; index < info.Samplers.Count; index++)
            if (info.Samplers[index].Source == source)
                return $"smp{index}";
        return $"src{source}";
    }

    // "self" when root is the node, "ptr" when the node feeds the handle (base) of a read on the way, "idx" when it feeds a
    // read's offset, "op" when it reaches root only through arithmetic; null when root does not depend on it.
    private static string? Reach(ScalarValue root, int node, ref ScalarValue? self)
    {
        var visited = new HashSet<ScalarValue>(ReferenceEqualityComparer.Instance);
        return Walk(root, node, visited, ref self);
    }

    private static string? Walk(ScalarValue value, int node, HashSet<ScalarValue> visited, ref ScalarValue? self)
    {
        if (value.Id == node && value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord)
        {
            self = value;
            return "self";
        }

        if (!visited.Add(value))
            return null;
        string? best = null;
        var raw = value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord;
        for (var index = 0; index < value.Operands.Length; index++)
        {
            var below = Walk(value.Operands[index], node, visited, ref self);
            if (below is null)
                continue;
            var step = raw ? (index == 0 ? "ptr" : "idx") : "op";
            var path = below == "self" ? step : $"{step}>{below}";
            if (best is null || path.Length < best.Length)
                best = path;
        }

        return best;
    }
}
