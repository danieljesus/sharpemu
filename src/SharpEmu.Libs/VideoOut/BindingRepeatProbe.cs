// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_BINDING_REPEAT_LOG=1: how many PrepareBindings inputs (program, shader base and every
// descriptor, user data and device address word) repeat the previous frame or an earlier call of the same
// frame. Sizes a cross-frame memo of prepared bindings.
internal static class BindingRepeatProbe
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_BINDING_REPEAT_LOG") == "1";

    private static readonly object Gate = new();
    private static HashSet<ulong> _current = new();
    private static HashSet<ulong> _previous = new();
    private static long _calls;
    private static long _repeatedPrevious;
    private static long _repeatedSameFrame;
    private static long _programRepeated;
    private static long _frames;
    private static readonly HashSet<ulong> CurrentPrograms = new();
    private static HashSet<ulong> _previousPrograms = new();

    public static void Record(ulong programHash, in ShaderStageResources stage)
    {
        var hash = new XxHash64();
        Span<ulong> head = [programHash, stage.ShaderBase];
        hash.Append(MemoryMarshal.AsBytes(head));
        var snapshot = stage.Resources;
        foreach (var words in snapshot.Images) hash.Append(MemoryMarshal.AsBytes(words.AsSpan()));
        foreach (var words in snapshot.Samplers) hash.Append(MemoryMarshal.AsBytes(words.AsSpan()));
        foreach (var words in snapshot.Buffers) hash.Append(MemoryMarshal.AsBytes(words.AsSpan()));
        hash.Append(MemoryMarshal.AsBytes(snapshot.UserData.AsSpan()));
        hash.Append(MemoryMarshal.AsBytes(snapshot.DeviceAddressRanges.AsSpan()));
        var key = hash.GetCurrentHashAsUInt64();
        var images = new XxHash64();
        images.Append(MemoryMarshal.AsBytes(head));
        foreach (var words in snapshot.Images) images.Append(MemoryMarshal.AsBytes(words.AsSpan()));
        foreach (var words in snapshot.Samplers) images.Append(MemoryMarshal.AsBytes(words.AsSpan()));
        var buffers = new XxHash64();
        foreach (var words in snapshot.Buffers) buffers.Append(MemoryMarshal.AsBytes(words.AsSpan()));
        var user = new XxHash64();
        user.Append(MemoryMarshal.AsBytes(snapshot.UserData.AsSpan()));
        lock (Gate)
        {
            Count(key, programHash);
            Part(0, images.GetCurrentHashAsUInt64());
            Part(1, buffers.GetCurrentHashAsUInt64() ^ programHash);
            Part(2, user.GetCurrentHashAsUInt64() ^ programHash);
        }
    }

    private static readonly HashSet<ulong>[] PartCurrent = [new(), new(), new()];
    private static readonly HashSet<ulong>[] PartPrevious = [new(), new(), new()];
    private static readonly long[] PartRepeated = new long[3];

    // Parts: 0 images and samplers, 1 buffer descriptors, 2 user data; repeat = seen this frame or the previous one.
    private static void Part(int part, ulong key)
    {
        if (!PartCurrent[part].Add(key) || PartPrevious[part].Contains(key))
        {
            PartRepeated[part]++;
        }
    }

    // Image binding memo outcomes: 0 empty, 1 other words, 2 stale generation, 3 hit.
    private static readonly long[] MemoOutcomes = new long[4];

    public static void CountMemo(int outcome)
    {
        lock (Gate)
        {
            MemoOutcomes[outcome]++;
        }
    }

    private static void Count(ulong key, ulong programHash)
    {
        _calls++;
        if (!_current.Add(key))
        {
            _repeatedSameFrame++;
        }
        else if (_previous.Contains(key))
        {
            _repeatedPrevious++;
        }

        CurrentPrograms.Add(programHash);
        if (_previousPrograms.Contains(programHash))
        {
            _programRepeated++;
        }
    }

    public static void EndFrame()
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            EndFrameLocked();
        }
    }

    private static void EndFrameLocked()
    {
        (_previous, _current) = (_current, _previous);
        for (var part = 0; part < 3; part++)
        {
            PartPrevious[part].Clear();
            PartPrevious[part].UnionWith(PartCurrent[part]);
            PartCurrent[part].Clear();
        }
        _current.Clear();
        _previousPrograms = new HashSet<ulong>(CurrentPrograms);
        CurrentPrograms.Clear();
        if (++_frames % 120 != 0 || _calls == 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[BINDING_REPEAT] frames=120 calls_per_frame={_calls / 120.0:F0} repeat_previous_frame={100.0 * _repeatedPrevious / _calls:F1}% " +
            $"repeat_same_frame={100.0 * _repeatedSameFrame / _calls:F1}% new={100.0 * (_calls - _repeatedPrevious - _repeatedSameFrame) / _calls:F1}% " +
            $"program_seen_previous_frame={100.0 * _programRepeated / _calls:F1}% images_repeat={100.0 * PartRepeated[0] / _calls:F1}% " +
            $"buffers_repeat={100.0 * PartRepeated[1] / _calls:F1}% user_data_repeat={100.0 * PartRepeated[2] / _calls:F1}%");
        Array.Clear(PartRepeated);
        var memoTotal = Math.Max(1, MemoOutcomes.Sum());
        Console.Error.WriteLine(
            $"[BINDING_REPEAT] image_memo empty={100.0 * MemoOutcomes[0] / memoTotal:F1}% other_words={100.0 * MemoOutcomes[1] / memoTotal:F1}% " +
            $"stale_generation={100.0 * MemoOutcomes[2] / memoTotal:F1}% hit={100.0 * MemoOutcomes[3] / memoTotal:F1}%");
        Array.Clear(MemoOutcomes);
        _calls = _repeatedPrevious = _repeatedSameFrame = _programRepeated = 0;
    }
}
