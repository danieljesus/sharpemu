// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Surface metadata (HTile, DCC, CMask, FMask) keyed by its guest address.
public sealed partial class GuestImageCache
{
    private void MaterializeDccClear(
        ResourceSlotIdentifier imageIdentifier,
        in ImageRequest request,
        uint metadataBaseLayer)
    {
        _dccClearCounts[(int)DccClearCount.Calls]++;
        if (request.Description.Metadata.Kind != MetadataKind.Dcc)
        {
            return;
        }

        _dccClearCounts[(int)DccClearCount.Dcc]++;
        if (!DccClearTimed)
        {
            MaterializeDccClearCore(imageIdentifier, request, metadataBaseLayer);
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var cleared = _dccClearCounts[(int)DccClearCount.Cleared];
        MaterializeDccClearCore(imageIdentifier, request, metadataBaseLayer);
        var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        _dccClearCounts[cleared == _dccClearCounts[(int)DccClearCount.Cleared] ? (int)DccClearCount.NoClearTicks : (int)DccClearCount.ClearTicks] += ticks;
    }

    // [local] SHARPEMU_DCC_CLEAR_TIMING_GATE (on by default, =0 off): the two timestamps per DCC lookup feed only the
    // [PERF][DCC_CLEAR] times, printed with the render profile; without it they are not taken.
    private static readonly bool DccClearTimed =
        Environment.GetEnvironmentVariable("SHARPEMU_DCC_CLEAR_TIMING_GATE") == "0" || SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled;

    // [local] SHARPEMU_DCC_CLEAR_LEAN (on by default): the slices' keys are read first, and only when one decodes to a
    // clear does the lookup rent the scan buffer and run the scan-and-clear loop, which reads the keys again. Lookups
    // whose keys are not clears (about 98 % of the DCC ones in GTA V) skip the buffer rent and the per-slice setup.
    // 0 always runs the scan-and-clear loop.
    private static readonly bool DccClearLean = Environment.GetEnvironmentVariable("SHARPEMU_DCC_CLEAR_LEAN") != "0";

    private unsafe void MaterializeDccClearCore(
        ResourceSlotIdentifier imageIdentifier,
        in ImageRequest request,
        uint metadataBaseLayer)
    {
        ref readonly var description = ref request.Description;
        var range = description.Metadata.Range;
        using (var held = _lock.Hold())
        {
            var image = _slots.TryGet(imageIdentifier);
            if (image == null)
            {
                return;
            }

            image.Description.Metadata = description.Metadata;
            if (range.Size == 0 || description.Resources.Levels != 1 ||
                image.Description.Resources.Levels != 1)
            {
                return;
            }

            _surfaceMetadata.Remove(range.Address);
        }

        _dccClearCounts[(int)DccClearCount.Eligible]++;
        var layers = description.TransferLayers;
        const ulong metadataBlockSize = 0x1000;
        if (!ImageDescription.IsValidRange(range) ||
            range.Address % metadataBlockSize != 0 ||
            layers == 0 ||
            range.Size % layers != 0 ||
            (range.Size / layers) % metadataBlockSize != 0)
        {
            throw SubmissionScheduler.Fatal(
                $"DCC slices must contain aligned 4 KiB blocks: address=0x{range.Address:X16} size=0x{range.Size:X} layers={layers}.");
        }

        var view = request.View;
        var volume = description.IsVolume && view.Type == ImageViewType.Type3D;
        var first = volume ? 0u : metadataBaseLayer;
        var imageFirst = volume ? 0u : view.BaseLayer;
        var count = volume ? description.Extent.Depth : view.LayerCount;
        if (first >= layers || count == 0 || count > layers - first)
        {
            throw SubmissionScheduler.Fatal(
                $"The DCC view exceeds its metadata slices: first={first} count={count} layers={layers}.");
        }

        var sliceSize = range.Size / layers;
        var synchronizedAddress = range.Address + sliceSize * first;
        var synchronizedSize = sliceSize * count;

        if (_bufferCache.HasGpuDirtyBytes(synchronizedAddress, synchronizedSize))
        {
            using var readbackScope = SharpEmu.Libs.VideoOut.RenderPhaseProfile.MeasureDetail(SharpEmu.Libs.VideoOut.RenderPhaseProfile.Phase.ImageLookupDccReadback); // [local]
            _dccClearCounts[(int)DccClearCount.Readbacks]++;
            _bufferCache.ReadMemory(synchronizedAddress, synchronizedSize, isWrite: false);
        }

        if (DccClearLean && !AnyDccKeyDecodesToClear(request, range.Address, sliceSize, first, count))
        {
            _dccClearCounts[(int)DccClearCount.Slices] += count;
            return;
        }

        const int metadataScanChunkSize = 64 * 1024;
        var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(
            checked((int)Math.Min(sliceSize, (ulong)metadataScanChunkSize)));
        try
        {
            Span<byte> firstByte = stackalloc byte[1];
            for (uint slice = 0; slice < count; slice++)
            {
                var address = range.Address + sliceSize * (first + slice);
                // [local] The key and, when it decodes, the scan read the slice in place through the backing alias.
                var inPlace = TryScanInPlace(address, sliceSize, scan: false, 0, out var aliasCode);
                if (inPlace is null && !_backing.TryReadBacking(address, firstByte))
                {
                    throw SubmissionScheduler.Fatal(
                        $"Could not read DCC metadata key: address=0x{address:X16}.");
                }

                var code = inPlace is null ? firstByte[0] : aliasCode;
                _dccClearCounts[(int)DccClearCount.Slices]++;
                if (!PackedClearValue.TryDecodeDccColor(
                        request.View.Format,
                        code * 0x01010101u,
                        description.Metadata,
                        allowClearToRegister: request.Role == ImageRole.ColorTarget,
                        out var clear))
                {
                    continue;
                }

                _dccClearCounts[(int)DccClearCount.KeyClear]++;
                _dccClearCounts[(int)DccClearCount.ScannedBytes] += (long)sliceSize;
                var scanned = inPlace is null ? null : TryScanInPlace(address, sliceSize, scan: true, code, out _);
                var uniform = scanned ?? true;
                for (ulong position = 0; scanned is null && position < sliceSize && uniform;)
                {
                    var length = checked((int)Math.Min(
                        sliceSize - position,
                        (ulong)Math.Min(bytes.Length, metadataScanChunkSize)));
                    var chunk = bytes.AsSpan(0, length);
                    if (!_backing.TryReadBacking(address + position, chunk))
                    {
                        throw SubmissionScheduler.Fatal(
                            $"Could not read DCC metadata backing: address=0x{address + position:X16} size=0x{length:X}.");
                    }

                    if (chunk.IndexOfAnyExcept(code) >= 0) // [local] vectorized; was a byte loop
                    {
                        uniform = false;
                    }

                    position += (ulong)length;
                }

                if (!uniform)
                {
                    continue;
                }

                _dccClearCounts[(int)DccClearCount.Cleared]++;

                using (var held = _lock.Hold())
                {
                    if (_slots.TryGet(imageIdentifier) == null)
                    {
                        return;
                    }

                    ClearDccImage(
                        imageIdentifier,
                        request,
                        new SubresourceRange(view.BaseLevel, view.LevelCount, imageFirst + slice, 1),
                        clear);
                }

                if (request.Role != ImageRole.DisplaySurface)
                {
                    _bufferCache.FillBuffer(address, sliceSize, uint.MaxValue, isGds: false);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    // [local] Whether the key byte of any of the slices decodes to a clear for this request.
    private bool AnyDccKeyDecodesToClear(in ImageRequest request, ulong metadataAddress, ulong sliceSize, uint first, uint count)
    {
        Span<byte> key = stackalloc byte[1];
        for (uint slice = 0; slice < count; slice++)
        {
            var address = metadataAddress + sliceSize * (first + slice);
            byte code;
            if (TryScanInPlace(address, sliceSize, scan: false, 0, out var aliasCode) is null)
            {
                if (!_backing.TryReadBacking(address, key))
                {
                    throw SubmissionScheduler.Fatal($"Could not read DCC metadata key: address=0x{address:X16}.");
                }

                code = key[0];
            }
            else
            {
                code = aliasCode;
            }

            if (PackedClearValue.TryDecodeDccColor(
                    request.View.Format,
                    code * 0x01010101u,
                    request.Description.Metadata,
                    allowClearToRegister: request.Role == ImageRole.ColorTarget,
                    out _))
            {
                return true;
            }
        }

        return false;
    }

    // [local] Where MaterializeDccClear's calls go, for [PERF][DCC_CLEAR]: every call, DCC requests, those past the
    // single-level/sized early-outs, GPU-dirty readbacks, slices whose key was read, keys that decode to a clear,
    // the bytes scanned for them, slices cleared, and the time of the DCC calls that cleared nothing and of those
    // that cleared. Unsynchronized (render thread only).
    private enum DccClearCount { Calls, Dcc, Eligible, Readbacks, Slices, KeyClear, ScannedBytes, Cleared, NoClearTicks, ClearTicks, Count }

    private static readonly long[] _dccClearCounts = new long[(int)DccClearCount.Count];

    public static string TakeDccClearReport()
    {
        var counts = new long[(int)DccClearCount.Count];
        for (var index = 0; index < counts.Length; index++)
        {
            counts[index] = Interlocked.Exchange(ref _dccClearCounts[index], 0);
        }

        return FormattableString.Invariant(
            $"[PERF][DCC_CLEAR] calls={counts[0]} dcc={counts[1]} eligible={counts[2]} readbacks={counts[3]} slices={counts[4]} key_clear={counts[5]} scanned_kb={counts[6] / 1024} cleared={counts[7]} noclear_us={counts[8] * 1_000_000 / System.Diagnostics.Stopwatch.Frequency} clear_us={counts[9] * 1_000_000 / System.Diagnostics.Stopwatch.Frequency}");
    }

    // [local] SHARPEMU_DCC_SCAN_IN_PLACE (on by default): the metadata key, or whether the whole slice repeats it, read
    // through the backing alias without copying; null when the alias does not map the whole slice.
    private static readonly bool DccScanInPlace = Environment.GetEnvironmentVariable("SHARPEMU_DCC_SCAN_IN_PLACE") != "0";

    private unsafe bool? TryScanInPlace(ulong address, ulong size, bool scan, byte expected, out byte code)
    {
        code = 0;
        if (!DccScanInPlace || size == 0 || size > int.MaxValue || !_backing.TryEnterBackingAliasAccess())
        {
            return null;
        }

        try
        {
            if (!_backing.TryResolveBackingAlias(address, size, out var alias) || alias == 0)
            {
                return null;
            }

            var bytes = new ReadOnlySpan<byte>((void*)alias, (int)size);
            code = bytes[0];
            return !scan || bytes.IndexOfAnyExcept(expected) < 0;
        }
        finally
        {
            _backing.ExitBackingAliasAccess();
        }
    }

    private unsafe void ClearDccImage(
        ResourceSlotIdentifier imageIdentifier,
        in ImageRequest request,
        in SubresourceRange range,
        in ClearColorValue clear)
    {
        var image = _slots[imageIdentifier];
        ref readonly var description = ref image.Description;
        if (range.BaseLevel >= description.Resources.Levels || range.LevelCount == 0 ||
            range.LevelCount > description.Resources.Levels - range.BaseLevel)
        {
            throw SubmissionScheduler.Fatal(
                $"The DCC clear mip range is invalid: base={range.BaseLevel} count={range.LevelCount} levels={description.Resources.Levels}.");
        }

        var layers = description.IsVolume
            ? Math.Max(description.Extent.Depth >> (int)range.BaseLevel, 1)
            : image.Backing.Layers;
        if (range.LayerCount == 0 || range.BaseLayer >= layers || range.LayerCount > layers - range.BaseLayer)
        {
            throw SubmissionScheduler.Fatal(
                $"The DCC clear layer range is invalid: base={range.BaseLayer} count={range.LayerCount} layers={layers}.");
        }

        var fullImage = range.BaseLevel == 0 && range.LevelCount == description.Resources.Levels &&
                        range.BaseLayer == 0 && range.LayerCount == layers;
        WatchImage(imageIdentifier);
        if (!fullImage && (image.IsBufferModified || image.IsCpuDirty))
        {
            PopulateFromGuest(imageIdentifier, RefreshRequest(image), "before-dcc-clear");
            if (image.Description.Samples == 1 && (image.IsBufferModified || image.IsCpuDirty))
            {
                throw SubmissionScheduler.Fatal(
                    $"The DCC clear left guest ownership in place: address=0x{description.Data.Address:X16} bufferModified={image.IsBufferModified} cpuDirty={image.IsCpuDirty}.");
            }
        }

        var scheduled = _scheduler.Current;
        if (scheduled.IsInvalid)
        {
            throw SubmissionScheduler.Fatal("A DCC clear needs a command buffer that is recording.");
        }

        scheduled.EndRendering();
        var command = new CommandBuffer(scheduled.Handle);
        var needsAttachmentClear = request.View.Format != image.Backing.Format ||
                                   (description.IsVolume && !fullImage);
        if (needsAttachmentClear)
        {
            var viewDescription = new ImageViewDescription(
                request.View.Format,
                range.LayerCount == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray,
                ImageAspectFlags.ColorBit,
                range.BaseLevel,
                range.LevelCount,
                range.BaseLayer,
                range.LayerCount,
                default,
                ImageUsageFlags.ColorAttachmentBit);
            var view = image.GetOrCreateView(viewDescription);
            image.Transition(
                ImageLayout.ColorAttachmentOptimal,
                AccessFlags.ColorAttachmentWriteBit,
                range,
                command);
            var attachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { Color = clear },
            };
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(
                    new Offset2D(0, 0),
                    new Extent2D(
                        Math.Max(description.Extent.Width >> (int)range.BaseLevel, 1),
                        Math.Max(description.Extent.Height >> (int)range.BaseLevel, 1))),
                LayerCount = range.LayerCount,
                ColorAttachmentCount = 1,
                PColorAttachments = &attachment,
            };
            SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdBeginRendering(_device.Vk, command, &rendering); // [local]
            SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdEndRendering(_device.Vk, command); // [local]
        }
        else
        {
            image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, range, command);
            var nativeRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                range.BaseLevel,
                range.LevelCount,
                description.IsVolume ? 0u : range.BaseLayer,
                description.IsVolume ? 1u : range.LayerCount);
            var nativeClear = clear;
            SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdClearColorImage(_device.Vk, // [local]
                command,
                image.Backing.Handle,
                ImageLayout.TransferDstOptimal,
                &nativeClear,
                1,
                &nativeRange);
        }

        TakeGpuOwnership(image);
    }

    public bool IsMetadata(ulong address)
    {
        using var held = _lock.Hold();
        return _surfaceMetadata.TryGetValue(address, out var found) && found.Kind != SurfaceMetadataKind.PendingDcc;
    }

    public bool IsMetadataCleared(ulong address, uint slice, out uint fillValue)
    {
        fillValue = 0;
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        fillValue = found.FillValue;
        return (found.ClearMask & (1u << (int)slice)) != 0;
    }

    public bool IsMetadataCleared(ulong address, uint slice) => IsMetadataCleared(address, slice, out _);

    public bool OverlapsDccMetadata(ulong address, ulong size)
    {
        using var held = _lock.Hold();
        foreach (var (start, metadata) in _surfaceMetadata)
        {
            if (metadata.Kind == SurfaceMetadataKind.Dcc && metadata.Size != 0 &&
                address < start + metadata.Size && start < address + size)
            {
                return true;
            }
        }

        return false;
    }

    // A broad clear applies to CMask, FMask and HTile; DCC needs a validated fill value.
    public bool ClearMetadata(ulong address)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind is SurfaceMetadataKind.PendingDcc or SurfaceMetadataKind.Dcc)
        {
            return false;
        }

        found.ClearMask = uint.MaxValue;
        return true;
    }

    public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The DCC fill range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        // A DCC fill repeats one byte code; only the known deferred-clear codes count as clear.
        var code = (byte)fillValue;
        var dccClearMask = fillValue != code * 0x01010101u ? 0u : code switch
        {
            0x00 or 0x20 or 0x40 or 0x80 or 0xc0 => uint.MaxValue,
            _ => 0u,
        };
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found))
        {
            // The fill may precede color-target discovery; a pending entry stays invisible until then.
            _surfaceMetadata.Add(address, new SurfaceMetadata { Kind = SurfaceMetadataKind.PendingDcc, ClearMask = dccClearMask, FillValue = fillValue, FillSize = size });
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.PendingDcc)
        {
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.Dcc)
        {
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return true;
        }

        return false;
    }

    public static bool IsDccClearCode(byte code) => code is 0x00 or 0x20 or 0x40 or 0x80 or 0xc0;

    public bool TryReadGuestDccClear(ulong metadataAddress, ulong sliceSize, uint slice, out ulong sliceAddress, out byte code)
    {
        sliceAddress = 0;
        code = 0;
        if (metadataAddress == 0 || sliceSize == 0 || sliceSize > int.MaxValue ||
            (ulong)slice > (ulong.MaxValue - metadataAddress) / sliceSize)
        {
            return false;
        }

        var address = metadataAddress + (ulong)slice * sliceSize;
        if (!IsValidRange(address, sliceSize) || _bufferCache.HasGpuDirtyBytes(address, sliceSize))
        {
            return false;
        }

        Span<byte> first = stackalloc byte[1];
        if (!_backing.TryReadBacking(address, first) || !IsDccClearCode(first[0]))
        {
            return false;
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(sliceSize, 64 * 1024));
        try
        {
            for (ulong position = 0; position < sliceSize;)
            {
                var span = buffer.AsSpan(0, (int)Math.Min(sliceSize - position, (ulong)buffer.Length));
                if (!_backing.TryReadBacking(address + position, span) || span.IndexOfAnyExcept(first[0]) >= 0)
                {
                    return false;
                }

                position += (ulong)span.Length;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        sliceAddress = address;
        code = first[0];
        return true;
    }

    private static readonly bool DccSyncLog = Environment.GetEnvironmentVariable("SHARPEMU_DCC_SYNC_LOG") == "1"; // [local]
    private int _dccSyncLogs;
    private static readonly bool SkipDccSync = Environment.GetEnvironmentVariable("SHARPEMU_SKIP_DCC_SYNC") == "1"; // [local]

    public void SynchronizeGuestDccMetadata(ulong metadataAddress, ulong sliceSize, uint baseLayer, uint layerCount)
    {
        if (metadataAddress == 0 || sliceSize == 0 || layerCount == 0 ||
            (ulong)baseLayer + layerCount > (ulong.MaxValue - metadataAddress) / sliceSize)
        {
            return;
        }

        var address = metadataAddress + (ulong)baseLayer * sliceSize;
        var size = (ulong)layerCount * sliceSize;
        if (!SkipDccSync && IsValidRange(address, size) && _bufferCache.HasGpuDirtyBytes(address, size)) // [local] A/B switch
        {
            if (DccSyncLog && _dccSyncLogs++ < 40) Console.Error.WriteLine($"[DCC_SYNC] address=0x{address:X} size=0x{size:X}"); // [local]
            _ = _bufferCache.TrySynchronizeCpuRead(address, size);
        }
    }

    public bool SetMetadataSlice(ulong address, uint slice, bool isClear)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        if (isClear)
        {
            found.ClearMask |= 1u << (int)slice;
        }
        else
        {
            found.ClearMask &= ~(1u << (int)slice);
        }

        return true;
    }
}
