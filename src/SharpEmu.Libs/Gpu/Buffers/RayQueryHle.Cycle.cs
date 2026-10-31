// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_BLAS_CYCLE (see Gen5SpirvTranslator.BlasCycle.cs): the counters of each 0x8395 dispatch
// ([GPU][BLAS_CYCLE] when a walk revisited, reached the guard or was long; a total every 1024 dispatches), and the
// graph of every blob such a walk was in, read back and walked from its root ([GPU][BLAS_GRAPH]): reachable box nodes
// and pair records (a tree walk takes at most their sum), back edges (cycles), cross edges (a box with two parents),
// children out of the blob. The first BLAS_CYCLE_DUMPS checked blobs go to %TEMP%/gta/blascycle.
internal sealed partial class RayQueryHle
{
    private const uint CycleFirstWord = 84;
    private const int CycleWords = 10;
    private const uint DefaultRoot = 0x25;
    private const int CycleDumps = 6;
    private static bool CycleOnly => DeviceAddressPaging.BlasCycleMode != 0 && !DeviceAddressPaging.RqMapProbe && DeviceAddressPaging.RayQueryMode == 0;
    private static readonly string[] CycleNames =
        ["checked", "revisits", "revisit_guarded", "revisit_finished", "stopped", "long", "guarded", "max_steps", "tlas_guarded", "tlas_max"];

    private readonly long[] _cycleTotals = new long[CycleWords];
    private long _cycleDispatches, _cycleNotableDispatches, _cycleLogs, _cycleSamples;
    private int _graphDumps;
    private readonly List<Blob> _graphWanted = new();

    private void NoteBlasCycle(ReadOnlySpan<uint> counters, long frame)
    {
        static float F(uint value) => BitConverter.UInt32BitsToSingle(value);
        if (DeviceAddressPaging.BlasCycleMode == 0)
            return;
        var words = counters.Slice((int)CycleFirstWord, 23).ToArray();
        _cycleDispatches++;
        for (var index = 0; index < CycleWords; index++)
        {
            if (index is 7 or 9)
                _cycleTotals[index] = Math.Max(_cycleTotals[index], words[index]);
            else
                _cycleTotals[index] += words[index];
        }

        var notable = words[1] != 0 || words[4] != 0 || words[5] != 0 || words[6] != 0 || words[8] != 0;
        if (notable)
        {
            _cycleNotableDispatches++;
            if (_cycleLogs++ < 48 || _cycleNotableDispatches % 64 == 0)
                Console.Error.WriteLine($"[GPU][BLAS_CYCLE] frame={frame} mode={DeviceAddressPaging.BlasCycleMode} {string.Join(" ", CycleNames.Select((name, index) => $"{name}={words[index]}"))} notable_dispatches={_cycleNotableDispatches}");
            if (words[10] != 0 && _cycleSamples++ < 16)
                Console.Error.WriteLine($"[GPU][BLAS_CYCLE_SAMPLE] frame={frame} blas=0x{(ulong)words[11] << 8:X} steps={words[12]} node=0x{words[13]:X} revisit_step={words[14]} mark=0x{words[15]:X} origin=({F(words[16])},{F(words[17])},{F(words[18])}) dir=({F(words[19])},{F(words[20])},{F(words[21])}) tmax={F(words[22])}");
        }

        if (_cycleDispatches % 1024 == 0)
            Console.Error.WriteLine($"[GPU][BLAS_CYCLE] total frame={frame} dispatches={_cycleDispatches} notable_dispatches={_cycleNotableDispatches} {string.Join(" ", CycleNames.Select((name, index) => $"{name}={_cycleTotals[index]}"))}");
    }

    // Called from the header's completion action: the read is recorded at the next 0x8395 dispatch.
    private void RequestGraphCheck(Blob blob)
    {
        lock (_lock)
        {
            if (blob.GraphChecks < 3 && !_graphWanted.Contains(blob))
                _graphWanted.Add(blob);
        }
    }

    private void RequestGraphChecks(long frame)
    {
        if (DeviceAddressPaging.BlasCycleMode == 0)
            return;
        Blob[] wanted;
        lock (_lock)
        {
            // A blob seen again by a notable walk is checked again, up to three times, 256 dispatches apart.
            foreach (var blob in _blobs.Values)
            {
                if (blob.GraphChecks is > 0 and < 3 && frame - blob.LastFrame < 64 && frame - blob.LastGraphFrame >= 256 && !_graphWanted.Contains(blob))
                    _graphWanted.Add(blob);
            }

            wanted = _graphWanted.Where(blob => blob.Size != 0).ToArray();
            _graphWanted.RemoveAll(blob => blob.Size != 0);
        }

        foreach (var blob in wanted)
        {
            blob.GraphChecks++;
            blob.LastGraphFrame = frame;
            Read(blob.Base, blob.Size, bytes =>
            {
                var graph = AnalyzeGraph(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes), DefaultRoot);
                string writers;
                long maxSteps;
                lock (_lock)
                {
                    writers = string.Join(",", blob.Writers.OrderByDescending(pair => pair.Value.Frames).Take(8)
                        .Select(pair => $"{pair.Key >> 48:X4}:{pair.Value.Writes}/{pair.Value.Frames}f@{pair.Value.LastFrame}"));
                    maxSteps = blob.MaxSteps;
                }

                Console.Error.WriteLine($"[GPU][BLAS_GRAPH] frame={frame} base=0x{blob.Base:X} size=0x{blob.Size:X} check={blob.GraphChecks} lanes={blob.Lanes} max_steps={maxSteps} {graph} writers=[{writers}]");
                if (_graphDumps < CycleDumps)
                {
                    _graphDumps++;
                    try
                    {
                        var directory = Path.Combine(Path.GetTempPath(), "gta", "blascycle");
                        Directory.CreateDirectory(directory);
                        File.WriteAllBytes(Path.Combine(directory, $"blob_{blob.Base:X}_{frame}.bin"), bytes.ToArray());
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine($"[GPU][BLAS_GRAPH] dump failed: {error.Message}");
                    }
                }
            });
        }
    }

    internal readonly record struct BlasGraph(
        int Units, int Boxes, int Pairs, int BackEdges, int CrossEdges, int OutOfBlob, int PairOutOfBlob, int PairRevisits,
        int BadChildren, string Cycle)
    {
        // A walk of a tree enters each reachable box and pair record at most once.
        public int TreeBound => Boxes + Pairs;

        public override string ToString() =>
            $"units={Units} boxes={Boxes} pairs={Pairs} tree_bound={TreeBound} back_edges={BackEdges} cross_edges={CrossEdges} oob={OutOfBlob} pair_oob={PairOutOfBlob} pair_revisits={PairRevisits} bad_children={BadChildren} cycle=[{Cycle}]";
    }

    // Depth-first from the root over box nodes (64-byte units; child words 0-3; types 4 and 5 box, 0-3 a pair list at
    // record c >> 3 of 8 bytes that ends at the record with bit 31 of its second word, 0xFFFFFFFF none).
    internal static BlasGraph AnalyzeGraph(ReadOnlySpan<uint> words, uint root)
    {
        var units = words.Length / 16;
        var records = words.Length / 2;
        var color = new byte[units];
        var next = new byte[units];
        var pairSeen = new bool[records];
        var path = new List<int>();
        int boxes = 0, pairs = 0, back = 0, cross = 0, oob = 0, pairOob = 0, pairRevisits = 0, bad = 0;
        var cycle = string.Empty;

        void Enter(uint child)
        {
            var target = (int)(child >> 3);
            if (target >= units)
            {
                oob++;
                return;
            }

            if (color[target] == 1)
            {
                if (back++ == 0)
                {
                    var start = path.LastIndexOf(target);
                    cycle = string.Join(">", path.Skip(Math.Max(start, path.Count - 12)).Select(unit => $"0x{unit:X}")) + $">0x{target:X}";
                }

                return;
            }

            if (color[target] == 2)
            {
                cross++;
                return;
            }

            color[target] = 1;
            boxes++;
            path.Add(target);
        }

        if ((root & 7) is 4 or 5)
            Enter(root);
        while (path.Count != 0)
        {
            var unit = path[^1];
            if (next[unit] == 4)
            {
                color[unit] = 2;
                path.RemoveAt(path.Count - 1);
                continue;
            }

            var child = words[unit * 16 + next[unit]++];
            if (child == uint.MaxValue)
                continue;
            switch (child & 7)
            {
                case 4 or 5:
                    Enter(child);
                    break;
                case <= 3:
                    for (var record = (long)(child >> 3); ; record++)
                    {
                        if (record * 2 + 1 >= words.Length)
                        {
                            pairOob++;
                            break;
                        }

                        if (pairSeen[record])
                        {
                            pairRevisits++;
                        }
                        else
                        {
                            pairSeen[record] = true;
                            pairs++;
                        }

                        if ((words[(int)(record * 2) + 1] & 0x8000_0000) != 0)
                            break;
                    }

                    break;
                case 6:
                    break;
                default:
                    bad++;
                    break;
            }
        }

        return new BlasGraph(units, boxes, pairs, back, cross, oob, pairOob, pairRevisits, bad, cycle);
    }
}
