// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// The cache owns the staging ring; temporary sources retire after their submission.
internal sealed class GuestBufferUploader(
    GpuDeviceInfo device, SubmissionScheduler scheduler, ICpuMemory guest, GpuRingBuffer staging)
{
    public GpuBuffer? PrepareSource(ulong bufferAddress, Span<BufferCopy> regions,
        ulong totalSize, ulong requestedAddress, ulong requestedSize)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferStagingUpload);
        if (regions.IsEmpty)
        {
            return null;
        }

        if (staging.TryMap(totalSize, out var baseOffset, 4))
        {
            foreach (ref var copy in regions)
            {
                ReadGuestBytes(bufferAddress + copy.DstOffset,
                    staging.Mapped.Slice((int)(baseOffset + copy.SrcOffset), (int)copy.Size), requestedAddress, requestedSize);
                copy.SrcOffset += baseOffset;
            }

            staging.Commit();
            return staging;
        }

        var temporary = new GpuBuffer(device, scheduler, GpuBufferUsage.Upload, 0, BufferUsageFlags.TransferSrcBit, totalSize);
        foreach (ref readonly var copy in regions)
        {
            ReadGuestBytes(bufferAddress + copy.DstOffset,
                temporary.Mapped.Slice((int)copy.SrcOffset, (int)copy.Size), requestedAddress, requestedSize);
        }

        temporary.Flush(0, totalSize);
        scheduler.QueueCompletionAction(temporary.Dispose);
        return temporary;
    }

    // [local] PrepareSource without the copy: reserves the staging bytes and lists the reads for a later copy.
    // Null when the staging ring is not coherent (its flush would come before the bytes) or cannot serve the size.
    public GpuRingBuffer? ReserveSource(ulong bufferAddress, Span<BufferCopy> regions, ulong totalSize,
        List<(ulong Address, ulong Offset, int Size)> reads)
    {
        if (regions.IsEmpty || !staging.IsCoherent || !staging.TryMap(totalSize, out var baseOffset, 4))
        {
            return null;
        }

        foreach (ref var copy in regions)
        {
            reads.Add((bufferAddress + copy.DstOffset, baseOffset + copy.SrcOffset, (int)copy.Size));
            copy.SrcOffset += baseOffset;
        }

        staging.Commit();
        return staging;
    }

    // [local] The deferred copy of ReserveSource's reads.
    public void ReadInto(GpuRingBuffer source, List<(ulong Address, ulong Offset, int Size)> reads)
    {
        foreach (var (address, offset, size) in reads)
        {
            ReadGuestBytes(address, source.Mapped.Slice((int)offset, size), address, (ulong)size);
        }
    }

    private void ReadGuestBytes(ulong address, Span<byte> destination, ulong requestedAddress, ulong requestedSize)
    {
        if (!guest.TryRead(address, destination))
        {
            Console.Error.WriteLine($"[GPU][READ_FAILURE] addr=0x{address:X16} size=0x{destination.Length:X}");
            Console.Error.WriteLine($"[GPU][READ_FAILURE] binding=0x{requestedAddress:X16} size=0x{requestedSize:X}");
            try
            {
                Console.Error.WriteLine(guest.DescribeReadRange(address, (ulong)destination.Length));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[GPU][READ_FAILURE] mapping query failed: {error.GetType().Name}");
            }
            throw SubmissionScheduler.Fatal($"Could not read guest memory: addr=0x{address:X16} size=0x{destination.Length:X}");
        }
    }
}
