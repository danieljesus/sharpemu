// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Gpu.Rendering;

// Render-thread-only storage. Exact lengths preserve descriptor counts, and
// clearing on return preserves new-array semantics and releases resource refs.
internal sealed class RenderScratchPool<T>(int maxBytes = 256 * 1024)
{
    // [local] SHARPEMU_SCRATCH_POOL_INDEXED (on by default, =0 off): the buckets of short lengths sit in an array
    // indexed by length instead of the dictionary (five pools rent and return per prepared stage, two lookups each).
    // The same limits apply: at most 128 lengths, four arrays per length and the byte budget.
    internal static readonly bool Indexed = Environment.GetEnvironmentVariable("SHARPEMU_SCRATCH_POOL_INDEXED") != "0";
    private const int IndexedLengths = 64;

    private readonly Dictionary<int, Stack<T[]>> _arrays = new();
    private readonly Stack<T[]>?[] _indexed = new Stack<T[]>?[IndexedLengths];
    private int _bucketCount;
    internal int RetainedBytes { get; private set; }

    internal T[] Rent(int length)
    {
        if (length == 0) return [];
        var bucket = Indexed && length < IndexedLengths ? _indexed[length] : _arrays.GetValueOrDefault(length);
        if (bucket is { Count: not 0 })
        {
            var result = bucket.Pop();
            RetainedBytes -= checked(length * Unsafe.SizeOf<T>());
            return result;
        }
        return new T[length];
    }

    internal void Return(T[] array)
    {
        if (array.Length == 0) return;
        var bytes = (long)array.Length * Unsafe.SizeOf<T>();
        if (bytes > maxBytes - RetainedBytes) return;
        var indexed = Indexed && array.Length < IndexedLengths;
        var bucket = indexed ? _indexed[array.Length] : _arrays.GetValueOrDefault(array.Length);
        if (bucket is null)
        {
            if (_bucketCount == 128) return;
            bucket = new Stack<T[]>(4);
            if (indexed)
                _indexed[array.Length] = bucket;
            else
                _arrays.Add(array.Length, bucket);
            _bucketCount++;
        }
        if (bucket.Count == 4) return;
        Array.Clear(array);
        bucket.Push(array);
        RetainedBytes += (int)bytes;
    }
}
