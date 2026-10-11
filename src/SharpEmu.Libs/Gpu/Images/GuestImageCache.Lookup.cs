// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    // [local] SHARPEMU_IMAGE_LOOKUP_SLOTS (power of two): GTA V looks up thousands of distinct requests
    // per frame, which collide in 256 slots.
    private static readonly int LookupSlots =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_LOOKUP_SLOTS"), out var slots) && slots > 0 && (slots & (slots - 1)) == 0
            ? slots
            : 256;

    private static readonly bool LookupMemoEnabled = Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_LOOKUP_MEMO") != "0";

    private struct LookupMemo
    {
        public ImageRequest Request;
        public ImageViewDescription View;
        public ResourceSlotIdentifier Result;
        public ulong Generation;
        public bool ExactFormat;
    }

    private LookupMemo[]? _lookups;
    private ulong _lookupGeneration = 1;
    private long _lookupHits;
    private long _lookupMisses;
    private static long _totalLookupHits;
    private static long _totalLookupMisses;

    public (long Hits, long Misses) LookupMemoCounters => (_lookupHits, _lookupMisses);

    public static string TakeLookupReport() => FormattableString.Invariant(
        $"[PERF][IMAGE_LOOKUP] memo_hits={Interlocked.Exchange(ref _totalLookupHits, 0)} memo_misses={Interlocked.Exchange(ref _totalLookupMisses, 0)}");

    // [local] SHARPEMU_RANGE_LOOKUP_GENERATION: a lookup only sees images owning its pages, so a change
    // stamps the generation on the pages it touches (hashed into buckets) and a remembered lookup stays
    // valid while no page of its range was stamped after it. Off: any change invalidates every lookup.
    private static readonly bool RangeLookupGeneration =
        Environment.GetEnvironmentVariable("SHARPEMU_RANGE_LOOKUP_GENERATION") == "1";
    private const int GenerationBuckets = 4096;
    private readonly ulong[] _pageGenerations = new ulong[GenerationBuckets];

    private void InvalidateLookups(ulong firstPage, ulong lastPageExclusive)
    {
        var generation = ++_lookupGeneration;
        if (lastPageExclusive - firstPage >= GenerationBuckets)
        {
            Array.Fill(_pageGenerations, generation);
            return;
        }

        for (var page = firstPage; page < lastPageExclusive; page++)
        {
            _pageGenerations[page & (GenerationBuckets - 1)] = generation;
        }
    }

    // The latest generation that can affect a lookup of this range; equal to LookupGeneration when off.
    public ulong RangeGeneration(ulong address, ulong size)
    {
        if (!RangeLookupGeneration || !ImagePageOwnerTable.TryGetPageRange(address, size, out var first, out var lastExclusive) ||
            lastExclusive - first >= GenerationBuckets)
        {
            return _lookupGeneration;
        }

        var latest = 0UL;
        for (var page = first; page < lastExclusive; page++)
        {
            latest = Math.Max(latest, _pageGenerations[page & (GenerationBuckets - 1)]);
        }

        return latest;
    }

    // [local, from #1077] Changes whenever an image a lookup could find is created, removed or replaced,
    // so a caller that remembers a lookup's result can tell when it may have changed.
    public ulong LookupGeneration => _lookupGeneration;

    // What a remembered lookup still does on reuse: the image counts as used now. False when it is gone.
    public bool TryTouchRemembered(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        if (_slots.TryGet(imageIdentifier) is not { Registered: true } image)
        {
            return false;
        }

        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        return true;
    }

    private static int LookupSlot(in ImageRequest request, bool exactFormat)
    {
        var hash = HashCode.Combine(request.Description.Data.Address, request.Description.Data.Size, request.Description.PixelFormat,
            request.Description.Extent.Width, request.View.Format, request.View.BaseLevel, request.View.BaseLayer,
            HashCode.Combine(request.Role, exactFormat, request.View.Type, request.Description.Resources));
        return hash & (LookupSlots - 1);
    }

    private static bool SameRequest(in ImageRequest left, in ImageRequest right) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<ImageRequest>(in left)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<ImageRequest>(in right)));

    private bool TryReuseLookup(ref ImageRequest request, bool exactFormat, out ResourceSlotIdentifier result)
    {
        result = ResourceSlotIdentifier.Invalid;
        if (!LookupMemoEnabled || _lookups is not { } lookups || request.Role == ImageRole.DisplaySurface)
        {
            return false;
        }

        ref var memo = ref lookups[LookupSlot(request, exactFormat)];
        if (memo.Generation == 0 || memo.ExactFormat != exactFormat || !SameRequest(memo.Request, request) ||
            RangeGeneration(request.Description.Data.Address, request.Description.Data.Size) > memo.Generation ||
            _slots.TryGet(memo.Result) is not { Registered: true } image)
        {
            _lookupMisses++;
            Interlocked.Increment(ref _totalLookupMisses);
            return false;
        }

        _lookupHits++;
        Interlocked.Increment(ref _totalLookupHits);
        request.View = memo.View;
        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        result = memo.Result;
        return true;
    }

    private void RememberLookup(in ImageRequest original, bool exactFormat, in ImageViewDescription view, ResourceSlotIdentifier result)
    {
        if (!LookupMemoEnabled || original.Role == ImageRole.DisplaySurface)
        {
            return;
        }

        var lookups = _lookups ??= new LookupMemo[LookupSlots];
        ref var memo = ref lookups[LookupSlot(original, exactFormat)];
        memo.Request = original;
        memo.View = view;
        memo.Result = result;
        memo.ExactFormat = exactFormat;
        memo.Generation = _lookupGeneration;
    }
}
