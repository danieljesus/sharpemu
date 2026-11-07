// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// [local] SHARPEMU_FAST_OUTPUT_REUSE (=1 on, off by default): a fast materialization that finds its specialization memo
// (FastHit, ~84 % of them in the open world) copies the program's output into new arrays for every draw and dispatch:
// one per buffer descriptor, the array of them, the flattened table and the device-address ranges (about 0.5 KB a
// dispatch of the ~3 KB the DRAWCOST probe counts). Consecutive runs of a plan often produce some of the same words (the
// same table, the same buffers, only a moved constant base). The thread now keeps, per plan, the arrays of its last
// FastHit snapshot and hands out the same array object for a part whose words are equal, a new one otherwise. Exact:
// the words are equal, and a published snapshot's arrays are never written (the cache already shares a snapshot's
// arrays between hits; AddressRebase.Apply and the word patches clone before they change one).
public static partial class ResourceMaterializer
{
    public static int FastOutputReuseMode
    {
        get => FastOutputReuseOverride.Value ?? FastOutputReuseDefault;
        set => FastOutputReuseOverride.Value = value;
    }

    private static readonly int FastOutputReuseDefault =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FAST_OUTPUT_REUSE"), out var outputReuse) ? outputReuse : 0;
    private static readonly AsyncLocal<int?> FastOutputReuseOverride = new();

    private sealed class LastOutput
    {
        public uint[][] Buffers = [];
        public uint[] Table = [];
        public DeviceAddressRange[] Ranges = [];
    }

    [ThreadStatic] private static Dictionary<ShaderResourcePlan, LastOutput>? _lastOutputs;

    // The FastHit arrays for this output: a cleared buffer descriptor (reserved bits set) reads as zeros, as in the copy.
    private static void ReuseOutputArrays(ShaderResourcePlan plan, MaterializationProgram.Output output, int bufferCount,
        out uint[][] buffers, out uint[] table, out DeviceAddressRange[] ranges)
    {
        var outputs = _lastOutputs ??= new Dictionary<ShaderResourcePlan, LastOutput>(ReferenceEqualityComparer.Instance);
        if (!outputs.TryGetValue(plan, out var last))
        {
            if (outputs.Count >= 4096)
                outputs.Clear();
            outputs.Add(plan, last = new LastOutput());
        }

        var sameOuter = last.Buffers.Length == bufferCount;
        uint[][]? fresh = null;
        for (var index = 0; index < bufferCount; index++)
        {
            var words = output.Source(index);
            var cleared = (words[3] >> 30) != 0;
            var previous = sameOuter ? last.Buffers[index] : null;
            if (previous is not null && previous.Length == words.Length &&
                (cleared ? !previous.AsSpan().ContainsAnyExcept(0u) : previous.AsSpan().SequenceEqual(words)))
            {
                if (fresh is not null)
                    fresh[index] = previous;
                continue;
            }

            if (fresh is null)
            {
                fresh = new uint[bufferCount][];
                for (var kept = 0; kept < index; kept++)
                    fresh[kept] = last.Buffers[kept];
            }

            fresh[index] = cleared ? new uint[words.Length] : words.ToArray();
        }

        buffers = fresh ?? (last.Buffers.Length == bufferCount ? last.Buffers : []);
        table = last.Table.AsSpan().SequenceEqual(output.TableWords) ? last.Table : output.TableWords.ToArray();
        ranges = last.Ranges.AsSpan().SequenceEqual(output.RangeValues) ? last.Ranges : output.RangeValues.ToArray();
        last.Buffers = buffers;
        last.Table = table;
        last.Ranges = ranges;
    }
}
