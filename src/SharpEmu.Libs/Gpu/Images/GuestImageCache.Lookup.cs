// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    // [local] SHARPEMU_IMAGE_LOOKUP_SLOTS (power of two): GTA V looks up thousands of distinct requests
    // per frame, which collide in few slots. Unset: 256, our measured default (upstream's own constant is now 4096).
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
        public ulong Begin;
        public ulong End;
        public bool ExactFormat;
    }

    // [local] SHARPEMU_IMAGE_LOOKUP_HISTORY=<n> (power of two, default 64): how many lookup events a memo can be checked
    // against before it counts as stale (DarthPotato f8bc6512 measured 64 overflowing in Yotei).
    private static readonly int LookupEventSlots = LookupHistory();

    private static int LookupHistory()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_LOOKUP_HISTORY"), out var slots) || slots < 64)
        {
            return 64;
        }

        return (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Min(slots, 1 << 16));
    }

    private readonly record struct LookupEvent(ulong Generation, ulong Begin, ulong End);

    private readonly LookupEvent[] _lookupEvents = new LookupEvent[LookupEventSlots];
    private int _lookupEventCursor;
    private static readonly ThreadCounter _totalLookupRevalidations = new();

    private LookupMemo[]? _lookups;
    private ulong _lookupGeneration = 1;
    private long _lookupHits;
    private long _lookupMisses;
    private static readonly ThreadCounter _totalLookupHits = new();
    private static readonly ThreadCounter _totalLookupMisses = new();
    private static readonly ThreadCounter _totalGenerationBumps = new();
    private static readonly ThreadCounter _totalBindingHits = new();
    private static readonly ThreadCounter _totalBindingMisses = new();

    public static void CountBindingMemo(bool hit) => (hit ? _totalBindingHits : _totalBindingMisses).Increment();

    public (long Hits, long Misses) LookupMemoCounters => (_lookupHits, _lookupMisses);

    private static readonly long[] _missReasons = new long[4]; // [local]

    public static string TakeLookupReport() =>
        FormattableString.Invariant($"[PERF][IMAGE_LOOKUP] memo_hits={_totalLookupHits.Take()} memo_misses={_totalLookupMisses.Take()} ") +
        FormattableString.Invariant($"generation_bumps={_totalGenerationBumps.Take()} revalidated={_totalLookupRevalidations.Take()} ") +
        FormattableString.Invariant($"binding_hits={_totalBindingHits.Take()} binding_misses={_totalBindingMisses.Take()} ") +
        FormattableString.Invariant($"miss_empty={Interlocked.Exchange(ref _missReasons[0], 0)} miss_other_request={Interlocked.Exchange(ref _missReasons[1], 0)} ") + // [local]
        FormattableString.Invariant($"miss_stale={Interlocked.Exchange(ref _missReasons[2], 0)} miss_gone={Interlocked.Exchange(ref _missReasons[3], 0)} depth_color_copies={TakeDepthColorCopies()}"); // [local]

    private void InvalidateLookups() => InvalidateLookups(0, ulong.MaxValue);

    private void InvalidateLookups(ulong begin, ulong end)
    {
        _lookupGeneration++;
        _lookupEvents[_lookupEventCursor++ & (LookupEventSlots - 1)] = new LookupEvent(_lookupGeneration, begin, end);
        _totalGenerationBumps.Increment();
        if (RangeLookupGeneration)
        {
            StampPageGenerations(begin, end, _lookupGeneration); // [local]
        }
    }

    // [local] An image left every lookup (removed or parked). Upstream's memos check Registered/Parked themselves, so
    // no range event is logged (it would only evict real ones from the ring); the generation still moves for the
    // memos that trust it alone (SHARPEMU_TARGET_MEMO's stamp, RangeGeneration, slice validation).
    private void BumpLookupGenerationForRemoval(ulong begin, ulong end)
    {
        _lookupGeneration++;
        _totalGenerationBumps.Increment();
        if (RangeLookupGeneration)
        {
            StampPageGenerations(begin, end, _lookupGeneration);
        }
    }

    // [local] SHARPEMU_RANGE_LOOKUP_GENERATION: a lookup only sees images owning its pages, so a change
    // stamps the generation on the pages it touches (hashed into buckets) and RangeGeneration tells a remembered
    // lookup whether any page of its range was stamped after it. Off: RangeGeneration is the global
    // LookupGeneration, so any change invalidates. The lookup memo here uses upstream's range events instead.
    private static readonly bool RangeLookupGeneration =
        Environment.GetEnvironmentVariable("SHARPEMU_RANGE_LOOKUP_GENERATION") == "1";
    private const int GenerationBuckets = 4096;
    private readonly ulong[] _pageGenerations = new ulong[GenerationBuckets];

    // [local] begin/end are guest addresses, end exclusive.
    private void StampPageGenerations(ulong begin, ulong end, ulong generation)
    {
        if (end <= begin || !ImagePageOwnerTable.TryGetPageRange(begin, end - begin, out var firstPage, out var lastPageExclusive) ||
            lastPageExclusive - firstPage >= GenerationBuckets)
        {
            Array.Fill(_pageGenerations, generation);
            return;
        }

        for (var page = firstPage; page < lastPageExclusive; page++)
        {
            _pageGenerations[page & (GenerationBuckets - 1)] = generation;
        }
    }

    // [local] The latest generation that can affect a lookup of this range; equal to LookupGeneration when off.
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

    // Changes whenever an image a lookup could find is created, removed or replaced, so a caller
    // that remembers a lookup's result can tell when it may have changed.
    public ulong LookupGeneration => _lookupGeneration;

    // What a remembered lookup still does on reuse: the image counts as used now. False when the
    // image is gone.
    public bool TryReuseAcquired(ResourceSlotIdentifier imageIdentifier, ulong backing)
    {
        using var held = _lock.Hold();
        if (_slots.TryGet(imageIdentifier) is not { Registered: true, Parked: false } image || image.DepthOwner.IsValid ||
            image.Binding.NeedsRebind || image.IsCpuDirty || image.IsBufferModified || image.Backing.Handle.Handle != backing)
        {
            return false;
        }

        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        if (!ImageDescription.IsEmptyRange(image.Description.Data))
        {
            MergeMipTailBlock(image);
        }

        return true;
    }

    public bool TryTouchRemembered(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        if (_slots.TryGet(imageIdentifier) is not { Registered: true, Parked: false } image)
        {
            return false;
        }

        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        return true;
    }

    public bool TryTouchRemembered(ResourceSlotIdentifier imageIdentifier, ref ulong generation, ulong begin, ulong end)
    {
        using var held = _lock.Hold();
        if (!LookupUnchangedSince(generation, begin, end) || _slots.TryGet(imageIdentifier) is not { Registered: true, Parked: false } image)
        {
            return false;
        }

        generation = _lookupGeneration;
        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        return true;
    }

    // [local] SHARPEMU_STAGE_MEMO_TOUCH: the stage memo's freshness check for one remembered texture under a single
    // hold of the lock (it held it twice per texture of every reused stage: TryGetImage, then TryTouchRemembered).
    // False when the image is gone, or (touch) when a lookup of the range could answer differently now or the image is
    // not a registered, unparked one; a stale binding (IsStaleBinding) is reported through image and is not touched,
    // as before.
    public bool TryTouchRememberedImage(ResourceSlotIdentifier imageIdentifier, ref ulong generation, ulong begin, ulong end, bool touch,
        out CachedImage? image)
    {
        using var held = _lock.Hold();
        image = _slots.TryGet(imageIdentifier);
        if (image is null)
        {
            return false;
        }

        if (!touch || IsStaleBinding(image))
        {
            return true;
        }

        if (!LookupUnchangedSince(generation, begin, end) || image is not { Registered: true, Parked: false })
        {
            return false;
        }

        generation = _lookupGeneration;
        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        return true;
    }

    // [local] A bound image that must be found again: unregistered or owned by a depth image (unless it is the empty
    // range) or flagged for a rebind. The recording host's IsStale.
    public static bool IsStaleBinding(CachedImage image) =>
        ((!image.Registered || image.DepthOwner.IsValid) && !ImageDescription.IsEmptyRange(image.Description.Data)) || image.Binding.NeedsRebind;

    public GuestSpan RememberedRange(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var held = _lock.Read();
        return UnionRange(request.Description.Data, _slots.TryGet(imageIdentifier)?.Description.Data ?? request.Description.Data);
    }

    private static GuestSpan UnionRange(GuestSpan left, GuestSpan right)
    {
        var begin = Math.Min(left.Address, right.Address);
        var end = Math.Max(left.End, right.End);
        return new GuestSpan(begin, end - begin);
    }

    private bool LookupUnchangedSince(ulong generation, ulong begin, ulong end)
    {
        if (generation == _lookupGeneration)
        {
            return true;
        }

        if (_lookupGeneration - generation > (ulong)LookupEventSlots)
        {
            return false;
        }

        for (var index = _lookupEventCursor - 1; index >= 0 && index > _lookupEventCursor - 1 - LookupEventSlots; index--)
        {
            ref readonly var lookupEvent = ref _lookupEvents[index & (LookupEventSlots - 1)];
            if (lookupEvent.Generation <= generation)
            {
                break;
            }

            if (lookupEvent.Begin < end && begin < lookupEvent.End)
            {
                return false;
            }
        }

        _totalLookupRevalidations.Increment();
        return true;
    }

    // [local] SHARPEMU_IMAGE_LOOKUP_FULL_HASH: the slot hashes every byte of the request, as SameRequest compares
    // them. The field subset sent requests that differ elsewhere (level count, aspect, swizzle...) to the same slot
    // whatever the table size, where they evicted each other (GTA V: 91 % of the misses).
    private static readonly bool LookupFullHash = Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_LOOKUP_FULL_HASH") == "1"; // opt-in: hits 67 -> 83 % with 4096 slots and range generations, ImageLookup unchanged (rt467)

    private static int LookupSlot(in ImageRequest request, bool exactFormat)
    {
        if (LookupFullHash)
        {
            var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ImageRequest>(in request));
            var full = System.IO.Hashing.XxHash3.HashToUInt64(bytes, exactFormat ? 1 : 0);
            return (int)(full ^ (full >> 32)) & (LookupSlots - 1);
        }

        var hash = HashCode.Combine(request.Description.Data.Address, request.Description.Data.Size, request.Description.PixelFormat,
            request.Description.Extent.Width, request.View.Format, request.View.BaseLevel, request.View.BaseLayer,
            HashCode.Combine(request.Role, exactFormat, request.View.Type, request.Description.Resources));
        return hash & (LookupSlots - 1);
    }

    private static bool SameRequest(in ImageRequest left, in ImageRequest right) =>
        SameDescription(left.Description, right.Description) && left.View == right.View && left.Role == right.Role &&
        left.TraceTextureMetadataAddress == right.TraceTextureMetadataAddress &&
        string.Equals(left.TraceTextureDescriptor, right.TraceTextureDescriptor, StringComparison.Ordinal) &&
        left.TraceMetadataCompress == right.TraceMetadataCompress && left.TraceWriteCompress == right.TraceWriteCompress;

    private static bool SameDescription(in ImageDescription left, in ImageDescription right)
    {
        if (left.Data != right.Data || left.Stencil != right.Stencil ||
            left.HtileClearMask != right.HtileClearMask || left.PixelFormat != right.PixelFormat ||
            left.GuestFormat != right.GuestFormat || left.Type != right.Type ||
            left.Extent.Width != right.Extent.Width || left.Extent.Height != right.Extent.Height ||
            left.Extent.Depth != right.Extent.Depth || left.Resources != right.Resources ||
            left.Pitch != right.Pitch || left.BytesPerBlock != right.BytesPerBlock ||
            left.Samples != right.Samples || left.TileMode != right.TileMode || left.Bgra16 != right.Bgra16)
        {
            return false;
        }

        ref readonly var a = ref left.Metadata;
        ref readonly var b = ref right.Metadata;
        if (a.Range != b.Range || a.Kind != b.Kind || a.Control != b.Control || a.Compression != b.Compression ||
            a.StencilCompressed != b.StencilCompressed || a.NativeColorClear != b.NativeColorClear ||
            a.ColorAlphaOnLeastSignificantBits != b.ColorAlphaOnLeastSignificantBits ||
            a.ColorMetadataBaseLayer != b.ColorMetadataBaseLayer || a.PackedColorClearSupported != b.PackedColorClearSupported ||
            a.PackedColorClear.Uint32_0 != b.PackedColorClear.Uint32_0 ||
            a.PackedColorClear.Uint32_1 != b.PackedColorClear.Uint32_1 ||
            a.PackedColorClear.Uint32_2 != b.PackedColorClear.Uint32_2 ||
            a.PackedColorClear.Uint32_3 != b.PackedColorClear.Uint32_3)
        {
            return false;
        }

        for (var level = 0; level < ImageDescription.MaxLevels; level++)
        {
            ref readonly var first = ref left.MipLayout[level];
            ref readonly var second = ref right.MipLayout[level];
            if (first.Offset != second.Offset || first.Size != second.Size ||
                first.Pitch != second.Pitch || first.Height != second.Height)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryReuseLookup(ref ImageRequest request, bool exactFormat, out ResourceSlotIdentifier result)
    {
        result = ResourceSlotIdentifier.Invalid;
        if (!LookupMemoEnabled || _lookups is not { } lookups || request.Role == ImageRole.DisplaySurface)
        {
            return false;
        }

        ref var memo = ref lookups[LookupSlot(request, exactFormat)];
        if (memo.ExactFormat != exactFormat || !SameRequest(memo.Request, request) || !LookupUnchangedSince(memo.Generation, memo.Begin, memo.End) ||
            _slots.TryGet(memo.Result) is not { Registered: true, Parked: false } image)
        {
            _lookupMisses++;
            _totalLookupMisses.Increment();
            // [local] why: 0 empty slot, 1 another request, 2 stale generation/range, 3 image gone or parked
            var reason = memo.Generation == 0 ? 0 : memo.ExactFormat != exactFormat || !SameRequest(memo.Request, request) ? 1 :
                _slots.TryGet(memo.Result) is not { Registered: true, Parked: false } ? 3 : 2;
            Interlocked.Increment(ref _missReasons[reason]);
            return false;
        }

        memo.Generation = _lookupGeneration;
        _lookupHits++;
        _totalLookupHits.Increment();
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
        var range = UnionRange(original.Description.Data, _slots[result].Description.Data);
        memo.Begin = range.Address;
        memo.End = range.End;
    }
}
