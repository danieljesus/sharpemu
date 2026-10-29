// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

public readonly record struct DownloadPiece(GpuBuffer Buffer, ulong SourceOffset, ulong Address, ulong Size);

public readonly record struct OverlapSpan(int First, int Last, ulong Begin, ulong End, bool HasStreamLeap);

// Guest memory mirrored in device buffers: upload on use, download on CPU fault, page table for BDA.
public sealed unsafe class GuestBufferCache : IGuestBufferStore, IDisposable
{
    public const int CachingPageBits = 14;
    public const ulong CachingPageSize = 1UL << CachingPageBits;
    public const ulong CachingPageCount = 1UL << (40 - CachingPageBits);
    public const ulong BdaPageTableSize = CachingPageCount * sizeof(ulong);
    public static readonly ResourceSlotIdentifier NullBufferId = new(0, 1);

    private const ulong MiB = 1024 * 1024;
    private const ulong GdsBufferSize = 64 * 1024;
    private readonly record struct PlannedDownload(GpuBuffer Buffer, ulong Address, BufferDownloadPlacement Placement);

    private enum ShutdownOutcome
    {
        Pending,
        Drained,
        Failed,
    }

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly IGpuQueueRelay _relay;
    private readonly GuestBufferUploader _uploader;
    private readonly DeferredUploadWorker? _deferredUploads; // [local]
    private readonly ReadbackMirror? _mirror; // [local]

    // [local] Every deferred upload so far has protected its pages and copied them (the scheduler does this before
    // each submit).
    internal void WaitDeferredUploads() => _deferredUploads?.WaitIdle();
    private readonly IGuestBackedSpace _backing;
    private readonly BdaFaultProcessor _faults;
    private readonly GpuBuffer _gds;
    private readonly GpuBuffer _bdaPageTable;
    // [local] SHARPEMU_DA_ZERO_PAGE: one page of zeros, its address in the page table's extra last entry.
    private readonly GpuBuffer? _zeroPage;
    private bool _zeroPageReady;
    private readonly GuestBufferRegistry<GpuBuffer> _registry = new(CachingPageSize, PageOwnerTable.AddressSpaceSize);
    private readonly SpanSet _gpuModifiedRanges = new();
    // [local] SHARPEMU_FAULT_READ_EMULATE: the render thread changes _gpuModifiedRanges under this gate (its own reads
    // need none); an emulated faulting load checks and reads the backing under it from the faulting thread.
    private readonly object _gpuModifiedGate = new();
    private static readonly ulong DirtyWatch = Convert.ToUInt64(Environment.GetEnvironmentVariable("SHARPEMU_DIRTY_WATCH") ?? "0", 16); // [local]
    private int _dirtyWatchLogs; // [local]
    internal static readonly HashSet<ulong> WatchedModified = new(); // [local] 32-byte-aligned addresses whose dirty-mark removal is logged
    private static int _watchedRemovals; // [local]

    private void RemoveGpuModified(ulong address, ulong size) // [local] logging wrapper
    {
        if (WatchedModified.Count != 0 && _watchedRemovals < 3000)
        {
            lock (WatchedModified)
                foreach (var watched in WatchedModified)
                    if (watched + 24 > address && watched < address + size && _gpuModifiedRanges.Overlaps(watched, 24))
                    {
                        _watchedRemovals++;
                        Console.Error.WriteLine($"[DIRTY_REMOVE] watched=0x{watched:X} range=0x{address:X}+0x{size:X} at {Environment.StackTrace.Replace(Environment.NewLine, " | ")}");
                        break;
                    }
        }

        lock (_gpuModifiedGate) _gpuModifiedRanges.Remove(address, size); // [local] SHARPEMU_FAULT_READ_EMULATE
        _preciseGpuModifiedRanges.Remove(address, size); // [local]
        if (StoreTableVerify) _exactGpuModifiedRanges.Remove(address, size); // [local]
        Interlocked.Increment(ref _gpuModifiedRemovals); // [local] invalidates the written-obtain memo
    }

    private long _gpuModifiedRemovals; // [local]

    // [local] experiment: writes through bindings of 16 MB or more are tracked as "broad";
    // a word dirty only through broad bindings may be read speculatively by table reads.
    private readonly SpanSet _preciseGpuModifiedRanges = new();

    // [local] SHARPEMU_STORE_TABLE_EXACT=2: the GPU-modified ranges without the store-table ranges the exact store-handle
    // evaluation excluded. A readback byte outside them was, by that analysis, not written by the GPU since it was last
    // synchronized, so the downloaded byte must equal the backing; a difference is a discrepancy (pages the CPU dirtied
    // since are skipped and counted apart).
    private static readonly bool StoreTableVerify = SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.ExactMode == 2;
    private readonly SpanSet _exactGpuModifiedRanges = new();
    private static long _verifyPieces, _verifyCheckedBytes, _verifyDiscrepancies, _verifyDiffBytes, _verifyCpuDirtySkipped;
    private static int _verifyLogs;

    public static string? TakeStoreTableVerifyReport() =>
        StoreTableVerify
            ? $"[PERF][STORE_TABLE_VERIFY] pieces={Interlocked.Exchange(ref _verifyPieces, 0)} checked_bytes={Interlocked.Exchange(ref _verifyCheckedBytes, 0)} discrepancies={Interlocked.Exchange(ref _verifyDiscrepancies, 0)} diff_bytes={Interlocked.Exchange(ref _verifyDiffBytes, 0)} cpu_dirty_skipped={Interlocked.Exchange(ref _verifyCpuDirtySkipped, 0)}"
            : null;

    private void VerifyExcludedBytes(ulong address, ReadOnlySpan<byte> bytes)
    {
        Interlocked.Increment(ref _verifyPieces);
        var end = address + (ulong)bytes.Length;
        var covered = _exactGpuModifiedRanges.GetOverlappingRanges(address, (ulong)bytes.Length);
        var cursor = address;
        for (var index = 0; index <= covered.Count && cursor < end; index++)
        {
            var gapEnd = index < covered.Count ? Math.Min(Math.Max(covered[index].Address, cursor), end) : end;
            if (gapEnd > cursor)
                VerifyGap(cursor, bytes.Slice((int)(cursor - address), (int)(gapEnd - cursor)));
            if (index < covered.Count)
                cursor = Math.Max(cursor, covered[index].Address + covered[index].Size);
        }
    }

    private void VerifyGap(ulong address, ReadOnlySpan<byte> downloaded)
    {
        if (_tracker.HasCpuDirtyPages(address, (ulong)downloaded.Length))
        {
            Interlocked.Increment(ref _verifyCpuDirtySkipped);
            return;
        }

        var backing = System.Buffers.ArrayPool<byte>.Shared.Rent(downloaded.Length);
        try
        {
            var current = backing.AsSpan(0, downloaded.Length);
            if (!_backing.TryReadBacking(address, current))
                return;
            Interlocked.Add(ref _verifyCheckedBytes, downloaded.Length);
            var first = current.CommonPrefixLength(downloaded);
            if (first == downloaded.Length)
                return;
            var differing = 0;
            for (var offset = first; offset < downloaded.Length; offset++)
                if (current[offset] != downloaded[offset]) differing++;
            Interlocked.Increment(ref _verifyDiscrepancies);
            Interlocked.Add(ref _verifyDiffBytes, differing);
            if (Interlocked.Increment(ref _verifyLogs) <= 40)
                Console.Error.WriteLine($"[STORE_TABLE_VERIFY_MISMATCH] gap=0x{address:X}+0x{downloaded.Length:X} first=0x{address + (ulong)first:X} differing={differing} tick={_scheduler.CurrentTick}");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(backing);
        }
    }
    public const ulong BroadWriteBytes = 16UL * 1024 * 1024;
    public bool HasPreciseGpuDirtyBytes(ulong guestAddress, ulong size) => _preciseGpuModifiedRanges.Overlaps(guestAddress, size);

    // [local] Ranges a GPU binding has ever written (exact binding ranges). Sticky and safe to read from any
    // thread: a front thread that interprets ahead cannot see dirty state the render thread
    // has not recorded yet, so it treats every block here as possibly GPU-written.
    private readonly SpanSet _everGpuWritten = new();
    private readonly ReaderWriterLockSlim _everGpuWrittenLock = new();

    // [local] SHARPEMU_EVER_WRITTEN_FAST (on by default, =0 off): the thread that adds to the set asks Contains without
    // the lock. Only that thread modifies the set (every other thread only reads it under the read lock), so its own
    // unlocked read sees the set exactly as the locked one would. Another thread still takes the locked path.
    private static readonly bool EverWrittenFast = Environment.GetEnvironmentVariable("SHARPEMU_EVER_WRITTEN_FAST") != "0";
    private int _everGpuWrittenWriter;

    private void NoteEverGpuWritten(ulong guestAddress, ulong size)
    {
        if (size == 0)
            return;
        if (EverWrittenFast)
        {
            var thread = Environment.CurrentManagedThreadId;
            var writer = Volatile.Read(ref _everGpuWrittenWriter);
            if (writer == 0)
            {
                var previous = Interlocked.CompareExchange(ref _everGpuWrittenWriter, thread, 0);
                writer = previous == 0 ? thread : previous;
            }
            if (writer == thread)
            {
                if (_everGpuWritten.Contains(guestAddress, size))
                    return;
            }
            else if (writer != -1 && Interlocked.Exchange(ref _everGpuWrittenWriter, -1) != -1)
            {
                // A second writer thread: the unlocked read is no longer exact, so it stops for good.
                Console.Error.WriteLine($"[GPU][WARN] SHARPEMU_EVER_WRITTEN_FAST: a second thread ({thread}) adds GPU-written ranges; the unlocked check is off.");
            }
        }

        _everGpuWrittenLock.EnterUpgradeableReadLock();
        try
        {
            if (_everGpuWritten.Contains(guestAddress, size))
                return;
            _everGpuWrittenLock.EnterWriteLock();
            try { _everGpuWritten.Add(guestAddress, size); }
            finally { _everGpuWrittenLock.ExitWriteLock(); }
        }
        finally
        {
            _everGpuWrittenLock.ExitUpgradeableReadLock();
        }

        if (VideoOut.FrontPrepReads.Mode != 0) // [local] SHARPEMU_FRONT_PREP_RANGES
        {
            lock (_firstWrites)
            {
                var version = Interlocked.Increment(ref _everGpuWrittenVersion);
                _firstWrites[(int)(version & (FirstWriteRing - 1))] = (version, guestAddress, size);
            }
        }
        else
        {
            Interlocked.Increment(ref _everGpuWrittenVersion);
        }

        FirstGpuWrite?.Invoke(guestAddress, size);
    }

    // [local] SHARPEMU_FRONT_PREP_RANGES: the range added at each version, for the last FirstWriteRing versions.
    private const int FirstWriteRing = 1024;
    private readonly (long Version, ulong Address, ulong Size)[] _firstWrites = new (long, ulong, ulong)[FirstWriteRing];

    // [local] SHARPEMU_FRONT_PREP_RANGES: 1 when a range first GPU-written after `version` overlaps the pages,
    // 0 when none does, -1 when the ranges since `version` are no longer known.
    internal int FirstWritesSinceOverlap(long version, VideoOut.FrontPrepReads.Pages pages)
    {
        lock (_firstWrites)
        {
            var current = Volatile.Read(ref _everGpuWrittenVersion);
            if (current - version > FirstWriteRing - 1)
            {
                return -1;
            }

            for (var next = version + 1; next <= current; next++)
            {
                var entry = _firstWrites[(int)(next & (FirstWriteRing - 1))];
                if (entry.Version != next)
                {
                    return -1;
                }

                if (VideoOut.FrontPrepReads.Overlaps(pages, entry.Address, entry.Size))
                {
                    return 1;
                }
            }

            return 0;
        }
    }

    // [local] Bumped after every range is added to the ever-GPU-written set (any thread may read it). A front
    // preparation taken at version V read guest memory directly only where no GPU write had been recorded; while the
    // version is still V when the render thread executes it, no range became GPU-written in between.
    private long _everGpuWrittenVersion;

    public long EverGpuWrittenVersion => Volatile.Read(ref _everGpuWrittenVersion);

    // [local] SHARPEMU_FRONT_STALE_CHECK: a range a GPU binding writes for the first time (render thread).
    internal static Action<ulong, ulong>? FirstGpuWrite;

    public bool EverGpuWritten(ulong guestAddress, ulong size)
    {
        _everGpuWrittenLock.EnterReadLock();
        try { return _everGpuWritten.Overlaps(guestAddress, size); }
        finally { _everGpuWrittenLock.ExitReadLock(); }
    }
    private long _gpuModifiedVersion;
    private readonly GuestPageTracker _tracker;
    private readonly GpuRingBuffer _staging;
    private readonly GpuRingBuffer _stream;
    private readonly GpuRingBuffer _download;
    private readonly GpuRingBuffer _deviceRing;
    private readonly object _shutdownGate = new();
    private readonly BufferRetirementPolicy _retirementPolicy = new();
    private ShutdownOutcome _outcome;
    private bool _faultProcessPending;
    private bool _disposed;

    public GuestBufferCache(
        GpuDeviceInfo device,
        SubmissionScheduler scheduler,
        IGpuQueueRelay relay,
        PageGuard pages,
        ICpuMemory guest,
        IGuestBackedSpace backing)
    {
        _device = device;
        _scheduler = scheduler;
        if (EarlyWriterSubmit) EarlySubmit = () => { _earlySubmits++; _scheduler.Flush(); }; // [local]
        _relay = relay;
        _backing = backing;
        _faults = new BdaFaultProcessor(device, scheduler, this, CachingPageBits, CachingPageCount);
        _gds = new GpuBuffer(device, scheduler, GpuBufferUsage.Stream, 0, GpuBuffer.AllFlags, GdsBufferSize);
        _bdaPageTable = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags,
            BdaPageTableSize + (DeviceAddressPaging.ZeroPageMode != 0 ? sizeof(ulong) : 0UL)); // [local] SHARPEMU_DA_ZERO_PAGE
        if (DeviceAddressPaging.ZeroPageMode != 0)
            _zeroPage = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, CachingPageSize);
        _tracker = new GuestPageTracker(pages);
        RangeDumpProbe.Cache = this; // [local]
        if (TlasHle.Enabled) TlasHle.Instance = new TlasHle(device, scheduler, this); // [local] SHARPEMU_TLAS_HLE
        _staging = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Upload, 512 * MiB);
        _uploader = new GuestBufferUploader(device, scheduler, guest, _staging);
        if (ReadbackMirror.Mode != 0) // [local]
        {
            _mirror = new ReadbackMirror(device, scheduler);
            scheduler.BeforeSubmit += RecordMirrorCopies;
        }

        if (DeferredUploadWorker.Enabled) // [local]
        {
            _deferredUploads = new DeferredUploadWorker();
            // The capture runs on the recording thread; the join, before the submit, on whichever thread submits.
            var deferred = _deferredUploads;
            scheduler.CaptureSubmitTarget = () => deferred.Enqueued;
            scheduler.WaitSubmitTarget = deferred.WaitIdle;
        }
        _guestMemory = guest; // [local]
        _stream = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Stream, 64 * MiB);
        _download = new GpuRingBuffer(device, scheduler, GpuBufferUsage.Download, 32 * MiB);
        _deviceRing = new GpuRingBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 128 * MiB);
        StreamOffsetAlignment = device.MinUniformBufferOffsetAlignment;
        _gds.Mapped.Clear();
        _gds.Flush(0, _gds.Size);
        var nullId = _registry.AllocateBuffer(new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 16), 0, 16);
        if (nullId != NullBufferId)
        {
            throw SubmissionScheduler.Fatal("The null buffer occupies the wrong slot.");
        }
    }

    public IGuestImageCache? ImageCache { get; set; }

    public GpuBuffer GdsBuffer => _gds;

    public GpuBuffer BdaPageTableBuffer
    {
        get
        {
            // [local] SHARPEMU_DA_ZERO_PAGE: before the table's first binding, zero the page and publish its address.
            if (_zeroPage is { } zeroPage && !_zeroPageReady)
            {
                _zeroPageReady = true;
                zeroPage.Fill(0, zeroPage.Size, 0);
                Span<ulong> address = [zeroPage.DeviceAddress];
                WriteDataBuffer(_bdaPageTable, BdaPageTableSize, MemoryMarshal.AsBytes(address));
            }

            return _bdaPageTable;
        }
    }

    // [local] Whether the CPU interface can read the range (counted for SHARPEMU_DA_ZERO_PAGE fault pages).
    internal bool CanReadGuest(ulong guestAddress, ulong size) => _guestMemory.CanRead(guestAddress, size);

    public GpuBuffer FaultBuffer => _faults.FaultBuffer;

    public ulong TotalUsedMemory => _registry.RegisteredBytes;

    public int BufferCount => _registry.RegisteredCount;

    // The stream fast path aligns to this; the presenter raises it to its descriptor alignment.
    public ulong StreamOffsetAlignment { get; set; }

    public void ForEachBuffer(Action<GpuBuffer> visit)
    {
        for (var index = 0; index < _registry.RegisteredCount; index++)
        {
            visit(_registry.GetBuffer(_registry.GetRegisteredIdentifier(index)));
        }
    }

    public GpuBuffer GetBuffer(ResourceSlotIdentifier bufferIdentifier) => _registry.GetBuffer(bufferIdentifier);

    // [local] The small BVH blob's header range, watched for command-processor and CPU-side writes.
    public static ulong BvhWatchBase;
    public static bool BvhWatch(ulong address, ulong size) => (BvhWatchBase != 0 && address < BvhWatchBase + 0x100 && address + size > BvhWatchBase) || (BvhWatchPages.Count != 0 && (BvhWatchPages.Contains(address >> 12) || BvhWatchPages.Contains((address + size - 1) >> 12)));
    public static readonly HashSet<ulong> BvhWatchPages = new(); // [local] 4 KB pages of headers written by 0x56D6
    public static Action? PostDispatchHook; // [local] run once right after the next dispatch is recorded
    public static readonly List<Action> PostDispatchHooks = new(); // [local] run after the next dispatch (or draw) is recorded
    public static readonly bool ForceBvhCounts = Environment.GetEnvironmentVariable("SHARPEMU_FORCE_BVH_COUNTS") == "1"; // [local]

    // [local] writes dwords into the registered device buffer in the current command stream.
    public void PatchDeviceDwords(ulong guestAddress, uint value0, ulong guestAddress1, uint value1)
    {
        var buffer = _registry.GetBuffer(FindBuffer(guestAddress, 256));
        var command = _scheduler.Current;
        command.EndRendering();
        var native = new CommandBuffer(command.Handle);
        var vk = _device.Vk;
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstAccessMask = AccessFlags2.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer.Handle,
            Offset = 0,
            Size = buffer.Size,
        };
        VulkanSynchronization.PipelineBarrier(vk, native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit, 0, null, 1, &barrier, 0, null);
        var v0 = value0; var v1 = value1;
        var vkProbe228 = SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Start(); // [local]
        SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdUpdateBuffer(vk, native, buffer.Handle, buffer.Offset(guestAddress), 4, &v0); // [local]
        SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Stop(SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Kind.UpdateBuffer, vkProbe228); // [local]
        var vkProbe229 = SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Start(); // [local]
        SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdUpdateBuffer(vk, native, buffer.Handle, buffer.Offset(guestAddress1), 4, &v1); // [local]
        SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Stop(SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Kind.UpdateBuffer, vkProbe229); // [local]
        var after = barrier;
        after.SrcAccessMask = AccessFlags2.TransferWriteBit;
        after.DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
        VulkanSynchronization.PipelineBarrier(vk, native, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit, 0, null, 1, &after, 0, null);
        buffer.NoteGpuWrite(buffer.Offset(guestAddress), 4);
        buffer.NoteGpuWrite(buffer.Offset(guestAddress1), 4);
    }
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(ulong Address, ulong Tick)> BvhHeaderWrites = new(); // [local]

    // [local] Copies the device-side bytes of a guest range to a file once the current submission completes.
    private readonly List<GpuBuffer> _dumpBuffers = new();
    private readonly ICpuMemory _guestMemory; // [local]

    // [local] guest (CPU-side) bytes of a range, for comparison with DumpDeviceRange.
    public void DumpGuestRange(ulong guestAddress, ulong size, string path)
    {
        var bytes = new byte[size];
        if (_guestMemory.TryRead(guestAddress, bytes)) File.WriteAllBytes(path, bytes);
        else Console.Error.WriteLine($"[GPU][GUEST_DUMP] unreadable 0x{guestAddress:X}");
    }

    // [local] tracker state of a range.
    public string DescribePage(ulong guestAddress, ulong size) =>
        $"region={_tracker.HasRegion(guestAddress, size)} cpu_dirty={_tracker.HasCpuDirtyPages(guestAddress, size)} may_cpu_dirty={_tracker.MayHaveCpuDirtyPages(guestAddress, size)} gpu_dirty={_tracker.HasGpuDirtyPages(guestAddress, size)} may_gpu_dirty={_tracker.MayHaveGpuDirtyPages(guestAddress, size)} cpu_hot={_tracker.IsCpuWriteHotRange(guestAddress, size)} registered={IsRegionRegistered(guestAddress, size)}";

    // [local] experiment: push the guest bytes of a range into the device copies (as a CP write would).
    public bool RepairFromGuest(ulong guestAddress, ulong size)
    {
        var bytes = new byte[size];
        if (!_guestMemory.TryRead(guestAddress, bytes)) return false;
        WriteHostMemory(guestAddress, bytes);
        return true;
    }

    // [local] SHARPEMU_RANGE_DUMP: a qword as a CPU reader sees it now (synchronizing; probes only).
    internal ulong ReadQwordSynchronized(ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        return TrySynchronizeCpuRead(address, 8) && _guestMemory.TryRead(address, bytes)
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            : 0;
    }

    // [local] SHARPEMU_TLAS_HLE: guest bytes the GPU has not written since the CPU did (false otherwise), without waiting.
    internal bool TryReadGuestClean(ulong address, Span<byte> destination) =>
        !_tracker.HasGpuDirtyPages(address, (ulong)destination.Length) && _guestMemory.TryRead(address, destination);

    // [local] SHARPEMU_TLAS_HLE: guest bytes as they are, whatever the GPU has written since.
    internal bool TryReadGuestUnchecked(ulong address, Span<byte> destination) => _guestMemory.TryRead(address, destination);

    // [local] SHARPEMU_TLAS_HLE: bytes for a binding, in the stream ring.
    internal (GpuBuffer Buffer, ulong Offset) UploadStream(ReadOnlySpan<byte> source, ulong alignment) => (_stream, _stream.Copy(source, alignment));

    public void DumpDeviceRange(ulong guestAddress, ulong size, string path)
    {
        var buffer = _registry.GetBuffer(FindBuffer(guestAddress, size));
        var download = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, size);
        _dumpBuffers.Add(download);
        download.CopyFrom(_scheduler.Current, buffer, buffer.Offset(guestAddress), 0, size, destinationAfter: AccessFlags.HostReadBit);
        _scheduler.QueueCompletionAction(() =>
        {
            download.Invalidate(0, size);
            File.WriteAllBytes(path, download.Mapped.Slice(0, checked((int)size)).ToArray());
        });
    }

    public GpuRingBuffer GetUtilityBuffer(GpuBufferUsage usage) => usage switch
    {
        GpuBufferUsage.Upload => _staging,
        GpuBufferUsage.Stream => _stream,
        GpuBufferUsage.Download => _download,
        GpuBufferUsage.DeviceLocal => _deviceRing,
        _ => throw SubmissionScheduler.Fatal("The utility buffer usage is invalid."),
    };

    // [local] SHARPEMU_FAULT_READ_EMULATE: every backing write of the cache is noted for the watched fault reads.
    private bool TryWriteBackingNoted(ulong address, ReadOnlySpan<byte> data)
    {
        var written = _backing.TryWriteBacking(address, data);
        FaultReadWatch.Note(address, (ulong)data.Length);
        return written;
    }

    // A CPU write fault: true when the range is tracked and any GPU data reached guest memory.
    bool IGuestBufferStore.MarkCpuWrite(ulong address, ulong size)
    {
        FaultReadWatch.Note(address, size); // [local] SHARPEMU_FAULT_READ_EMULATE
        if (TableWordMemo.Enabled) TableWordMemo.NoteOtherWrite(address, size); // [local]
        SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidateTables(address, size, SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.InvalidationSource.CpuFault); // [local]
        var tracked = _tracker.InvalidateRegion(address, size, out var needsGpuFlush);
        var completed = !needsGpuFlush || ReadMemoryOrAwaitShutdown(address, size, isWrite: true,
            GuestMemoryProfile.ReadbackSource.CpuWriteInvalidation);
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"buffer-write tracked={tracked} completed={completed}");
        return tracked && completed;
    }

    // [local] SHARPEMU_TABLE_READ_CENSUS: the writer tick of the range itself and the one the readback window waits for.
    internal (ulong Own, ulong Window, ulong Precise, ulong Current, ulong Completed) CensusTicks(ulong address, ulong size)
    {
        var buffer = _registry.GetBuffer(FindBuffer(address, size));
        var own = buffer.BlockWriteTick(buffer.Offset(address), size);
        var window = 0UL;
        foreach (var copy in CollectReadbackWindow(buffer, address, size, out _, out _))
        {
            window = Math.Max(window, copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size));
        }

        return (own, window, GpuWriteHistory.Enabled ? GpuWriteHistory.LastWriteTick(address, size) : own, _scheduler.CurrentTick, _scheduler.Timeline.CompletedTick);
    }

    public bool TrySynchronizeCpuRead(ulong address, ulong size) =>
        TrySynchronizeCpuRead(address, size, GuestMemoryProfile.ReadbackSource.CpuReadSynchronization);

    public bool TrySynchronizeCpuRead(ulong address, ulong size, GuestMemoryProfile.ReadbackSource source) =>
        !_tracker.MayHaveGpuDirtyPages(address, size) ||
        !_tracker.HasGpuDirtyPages(address, size) ||
        ReadMemoryOrAwaitShutdown(address, size, isWrite: false, source);

    // [local] SHARPEMU_MATERIALIZE_SYNC_VALIDATE: the synchronizing read of ranges[0] (as TrySynchronizeCpuRead) with
    // the other ranges brought back in the same readback when their last writer has already completed, so nothing is
    // flushed or waited for them (a writer still in flight would move its wait here). Each download is the ordinary one (window, write ticks, stale pieces left GPU-owned); the
    // others are only started earlier than a later read would start them. On the GPU queue thread the relays run
    // inline and the waits block it, as the synchronous download does.
    public bool TrySynchronizeCpuReads(IReadOnlyList<(ulong Address, ulong Size)> ranges, GuestMemoryProfile.ReadbackSource source)
    {
        var (primary, primarySize) = ranges[0];
        if (ranges.Count == 1 || (!_relay.IsGpuQueueThread && SubmissionScheduler.InDeferredOperation) || AsyncReadback is not { } readback ||
            !GuestReadsAwaitOffQueue)
        {
            return TrySynchronizeCpuRead(primary, primarySize, source);
        }

        var pendings = new List<PendingDownload>(ranges.Count);
        if (!_relay.TryRunOnGpuQueue(() =>
            {
                for (var index = 0; index < ranges.Count; index++)
                {
                    // The readback queue has 7 slots; one is left for the ordinary reads.
                    if (pendings.Count >= 6)
                    {
                        break;
                    }

                    var (address, size) = ranges[index];
                    if (!_tracker.MayHaveGpuDirtyPages(address, size) || !_tracker.HasGpuDirtyPages(address, size) ||
                        pendings.Exists(pending => pending.WindowBegin <= address && address + size <= pending.WindowEnd) ||
                        (index != 0 && !WriterCompleted(address, size)))
                    {
                        continue;
                    }

                    if (BeginReadMemoryOnGpu(address, size, isWrite: false, source, allowPending: true) is { } started)
                    {
                        pendings.Add(started);
                    }
                }
            }))
        {
            return AwaitShutdown();
        }

        Interlocked.Add(ref _batchedSyncReads, pendings.Count);
        foreach (var pending in pendings)
        {
            readback.Wait(pending.Ticket);
        }

        if (pendings.Count != 0 && !_relay.TryRunOnGpuQueue(() =>
            {
                foreach (var pending in pendings)
                {
                    _ = CompleteReadMemoryOnGpu(pending, retrySynchronously: false);
                }
            }))
        {
            return AwaitShutdown();
        }

        // A piece the GPU wrote again meanwhile is still GPU-owned: the ordinary path takes the primary from here.
        return TrySynchronizeCpuRead(primary, primarySize, source);
    }

    private static long _batchedSyncReads;

    public static long TakeBatchedSyncReads() => Interlocked.Exchange(ref _batchedSyncReads, 0);

    private bool WriterCompleted(ulong address, ulong size)
    {
        var completed = _scheduler.Timeline.CompletedTick;
        foreach (var copy in CollectReadbackWindow(address, size, out _, out _))
        {
            var written = copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size);
            if (written == 0 || written > completed)
            {
                return false;
            }
        }

        return true;
    }

    // Lock-free on the GPU queue thread; see GuestPageTracker.MayHaveGpuDirtyPages.
    public bool MayHaveGpuDirtyPages(ulong address, ulong size) => _tracker.MayHaveGpuDirtyPages(address, size);

    // A CPU read fault: GPU-dirty pages download through the worker first.
    // [local] SHARPEMU_FAULT_READ_EMULATE: on the GPU queue thread, in the order the download path would run, the
    // asked bytes when none of them is GPU-modified (the download would leave them as the backing holds them).
    public bool TryReadUnmodified(ulong address, Span<byte> data)
    {
        var size = (ulong)data.Length;
        if (size == 0 || size > 16 || (!_relay.IsGpuQueueThread && SubmissionScheduler.InDeferredOperation))
        {
            return false;
        }

        // [local] Under the gate the check and the read see one point of the render thread's sequence of GPU-modified
        // changes, as a relay to the queue would, without interrupting it: no range can turn GPU-modified (and be
        // downloaded into the backing) between the check and the read.
        TlasHle.Instance?.NoteCpuRead(address, size, 0); // [local] as DownloadToCpuCore: a guest CPU read
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool served;
        long entered;
        lock (_gpuModifiedGate)
        {
            entered = System.Diagnostics.Stopwatch.GetTimestamp();
            served = _tracker.HasRegion(address, size) && !_gpuModifiedRanges.Overlaps(address, size) &&
                _backing.TryReadBacking(address, data);
        }

        FaultReadStats.NoteRelay(served, entered - started, System.Diagnostics.Stopwatch.GetTimestamp() - entered);
        return served;
    }

    // [local] SHARPEMU_FAULT_READ_EMULATE=1: the emulated read only where the ordinary download would wait for a writer
    // in flight (GTA V's label page, rewritten every tick). Elsewhere the download is cheap and leaves the page clean,
    // so later reads do not fault at all; emulating those turned one fault per page into one per read (0x57B67Dxxx,
    // ~1,700/s). Decided on the GPU queue thread, where the ordinary path would decide too; the bytes are read there.
    public bool TryReadUnmodifiedWhileWritten(ulong address, Span<byte> data)
    {
        var size = (ulong)data.Length;
        if (size == 0 || size > 16 || _relay.IsGpuQueueThread || SubmissionScheduler.InDeferredOperation ||
            !TryReadUnmodified(address, data))
        {
            return false;
        }

        var bytes = new byte[size];
        var served = false;
        if (!_relay.TryRunOnGpuQueue(() =>
            {
                if (!WriterCompleted(address, size))
                {
                    served = TryReadUnmodified(address, bytes);
                    if (served && FaultReadFlush)
                    {
                        FlushWindowWriter(address, size);
                    }
                }
            }))
        {
            return false;
        }

        if (served)
        {
            bytes.CopyTo(data);
        }

        return served;
    }

    // [local] SHARPEMU_FAULT_READ_FLUSH (default 1, 0 off): an emulated read still submits the batch being recorded
    // when it holds a writer of the page window, as the ordinary download does before it waits. That submission was
    // the poll's side effect the guest relied on: without it the batch waits for the render thread's own submit while
    // the front waits on a REWIND the guest patches only after its poll (FPS 19.8 with BZHI emulated, the label's
    // chains gone).
    // [local] SHARPEMU_FAULT_READ_PARK_MIN_US (default 0): a parked poll returns no sooner than this, even when its
    // bytes changed earlier (the ordinary path answered ~10 ms late, with progress in large steps).
    private static readonly long FaultReadParkMinTicks =
        (long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FAULT_READ_PARK_MIN_US"), out var parkMin) && parkMin > 0 ? parkMin : 0) *
        System.Diagnostics.Stopwatch.Frequency / 1_000_000;

    private static readonly bool FaultReadFlush = Environment.GetEnvironmentVariable("SHARPEMU_FAULT_READ_FLUSH") != "0";
    private int _faultReadFlushPosted;

    private void FlushWindowWriter(ulong address, ulong size)
    {
        var waitTick = 0UL;
        foreach (var copy in CollectReadbackWindow(address, size, out _, out _))
        {
            waitTick = Math.Max(waitTick, copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size));
        }

        if (waitTick >= _scheduler.CurrentTick)
        {
            Interlocked.Increment(ref _faultReadFlushes);
            _scheduler.Flush();
        }
    }

    private static long _faultReadFlushes;

    internal static long TakeFaultReadFlushes() => Interlocked.Exchange(ref _faultReadFlushes, 0);

    // A repeated poll does not relay: the check is posted, one at a time, and runs between the queue's commands.
    private void PostWindowWriterFlush(ulong address, ulong size)
    {
        if (!FaultReadFlush || Interlocked.CompareExchange(ref _faultReadFlushPosted, 1, 0) != 0)
        {
            return;
        }

        if (!_relay.TryPost(() =>
            {
                Volatile.Write(ref _faultReadFlushPosted, 0);
                FlushWindowWriter(address, size);
            }))
        {
            Volatile.Write(ref _faultReadFlushPosted, 0);
        }
    }

    // [local] SHARPEMU_FAULT_READ_EMULATE=1, a repeated poll: data holds the bytes this thread's previous emulated read
    // of the range returned. While the range still holds them (and stays unmodified), the thread waits for a write to
    // touch it or for the deadline (Stopwatch ticks), then reads again; the bytes returned are the backing's at the
    // last read, as a read at that instant would see them. False (the ordinary download) once a byte is GPU-modified.
    public bool TryReadUnmodifiedParked(ulong address, Span<byte> data, long deadline)
    {
        if (data.Length > 16)
        {
            return false;
        }

        Span<byte> previous = stackalloc byte[data.Length];
        data.CopyTo(previous);
        PostWindowWriterFlush(address, (ulong)data.Length);
        var slot = FaultReadWatch.Begin(address, (ulong)data.Length);
        if (slot < 0)
        {
            return TryReadUnmodified(address, data);
        }

        try
        {
            var woken = 0;
            var parked = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                var hits = FaultReadWatch.Hits(slot);
                if (!TryReadUnmodified(address, data))
                {
                    FaultReadStats.NotePark(FaultReadStats.ParkOutcome.Declined, woken, System.Diagnostics.Stopwatch.GetTimestamp() - parked);
                    return false;
                }

                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!data.SequenceEqual(previous) && now < parked + FaultReadParkMinTicks && now < deadline)
                {
                    // Changed before the floor: keep waiting (each write wakes the loop) and return the bytes then.
                    woken += FaultReadWatch.Wait(slot, FaultReadWatch.Hits(slot), Math.Min(deadline, parked + FaultReadParkMinTicks)) ? 1 : 0;
                    continue;
                }

                if (!data.SequenceEqual(previous))
                {
                    FaultReadStats.NotePark(FaultReadStats.ParkOutcome.Changed, woken, System.Diagnostics.Stopwatch.GetTimestamp() - parked);
                    return true;
                }

                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                {
                    FaultReadStats.NotePark(FaultReadStats.ParkOutcome.Deadline, woken, System.Diagnostics.Stopwatch.GetTimestamp() - parked);
                    return true;
                }

                woken += FaultReadWatch.Wait(slot, hits, deadline) ? 1 : 0;
            }
        }
        finally
        {
            FaultReadWatch.End(slot);
        }
    }

    // [local] SHARPEMU_FAULT_READ_EMULATE=2: a watch over the range while the ordinary download runs; the end returns
    // how many writes touched it (-1: no slot was free).
    public int BeginWriteWatch(ulong address, ulong size) => FaultReadWatch.Begin(address, size);

    public int EndWriteWatch(int watch)
    {
        if (watch < 0)
        {
            return -1;
        }

        var hits = FaultReadWatch.Hits(watch);
        FaultReadWatch.End(watch);
        return hits;
    }

    public bool DownloadToCpu(ulong address, ulong size)
    {
        if (Diagnostics.LabelLatencyProbe.Enabled && Diagnostics.LabelLatencyProbe.Overlaps(address, size)) // [local] SHARPEMU_LABEL_PROBE
        {
            var probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Diagnostics.LabelTimeline.Log('g', _scheduler.CurrentTick, _scheduler.Timeline.CompletedTick, size);
            var probeResult = DownloadToCpuCore(address, size);
            if (Diagnostics.LabelPollSplit.Enabled) // [local] does the fault's page stay GPU-dirty (the guest faults again)?
                Diagnostics.LabelPollSplit.NoteFault(probeStarted, _tracker.HasGpuDirtyPages(address & ~(TrackerLayout.PageBytes - 1), TrackerLayout.PageBytes));
            Span<byte> label = stackalloc byte[8];
            if (_backing.TryReadBacking(Diagnostics.LabelLatencyProbe.Address, label))
                Diagnostics.LabelLatencyProbe.Poll(probeStarted, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(label),
                    _scheduler.CurrentTick, _scheduler.Timeline.CompletedTick);
            return probeResult;
        }

        return DownloadToCpuCore(address, size);
    }

    private bool DownloadToCpuCore(ulong address, ulong size)
    {
        TlasHle.Instance?.NoteCpuRead(address, size, 0); // [local] SHARPEMU_TLAS_HLE_CONSUMERS: a guest CPU read
        var tracked = _tracker.HasRegion(address, size);
        var dirty = tracked && _tracker.HasGpuDirtyPages(address, size);
        if (dirty && ReadbackAudit && _storeDownloadLogs++ < 40) Console.Error.WriteLine($"[STORE_DOWNLOAD] address=0x{address:X} size=0x{size:X}"); // [local]
        var completed = tracked && (!dirty || ReadMemoryOrAwaitShutdown(address, size, isWrite: false,
            GuestMemoryProfile.ReadbackSource.StoreDownload));
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"buffer-read tracked={tracked} gpu_dirty={dirty} completed={completed}");
        return completed;
    }

    public void InvalidateMemory(ulong guestAddress, ulong size)
    {
        if (!IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("The memory invalidation range is invalid.");
        }

        _tracker.InvalidateRegion(guestAddress, size, () => ReadMemory(guestAddress, size, isWrite: true));
    }

    public void ReadMemory(ulong guestAddress, ulong size, bool isWrite = false)
    {
        if (!ReadMemoryOrAwaitShutdown(guestAddress, size, isWrite))
        {
            throw SubmissionScheduler.Fatal($"Cannot download buffer data after a failed shutdown: addr=0x{guestAddress:X16} size=0x{size:X16}");
        }
    }

    public ResourceSlotIdentifier FindBuffer(ulong guestAddress, ulong size)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferCacheLookup);
        if (guestAddress == 0)
        {
            return NullBufferId;
        }

        if (!IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("The buffer lookup range is invalid.");
        }

        var owner = _registry.FindContainingBuffer(guestAddress, size);
        if (owner.IsValid)
        {
            return owner;
        }

        return CreateBuffer(guestAddress, size);
    }

    // [local] Where ObtainBuffer's calls go, for [PERF][BUFFER_OBTAIN]: the stream ring (with or without a buffer the
    // caller already looked up), a failed stream map, or the cache buffer (prefound or looked up here); a cached
    // result that was a stream candidate too large or not hot enough to stream is counted again.
    private enum ObtainOutcome
    {
        Stream, StreamPrefound, StreamFailed, CachedPrefound, CachedLookup, CachedStreamCandidate,
        Written, WrittenMemoHit, WrittenMemoVerified, WrittenMemoMismatch, Count,
    }

    private static readonly long[] _obtainCounts = new long[(int)ObtainOutcome.Count];

    public static string TakeObtainReport()
    {
        var counts = new long[(int)ObtainOutcome.Count];
        for (var index = 0; index < counts.Length; index++)
        {
            counts[index] = Interlocked.Exchange(ref _obtainCounts[index], 0);
        }

        return FormattableString.Invariant(
            $"[PERF][BUFFER_OBTAIN] stream={counts[0]} stream_prefound={counts[1]} stream_failed={counts[2]} cached_prefound={counts[3]} cached_lookup={counts[4]} cached_stream_candidate={counts[5]} ") +
            FormattableString.Invariant($"written={counts[6]} written_memo_hits={counts[7]} written_memo_verified={counts[8]} written_memo_mismatches={counts[9]} written_memo_mismatches_total={Interlocked.Read(ref _writtenMemoMismatchTotal)}") +
            FormattableString.Invariant($" cp_writes={Interlocked.Exchange(ref _cpWrites, 0)} cp_bytes={Interlocked.Exchange(ref _cpBytes, 0)} cp_consecutive={Interlocked.Exchange(ref _cpConsecutive, 0)} cp_adjacent={Interlocked.Exchange(ref _cpAdjacent, 0)} cp_same_range={Interlocked.Exchange(ref _cpSameRange, 0)} cp_same_range_any={Interlocked.Exchange(ref _cpSameRangeAny, 0)}") +
            " " + TakeTexelCleanReport();
    }

    // [local] SHARPEMU_WRITTEN_OBTAIN_MEMO (idea from a Kyty fork, todojuegos67-bot c128d6b9): a written, unformatted
    // binding obtained again in the same command buffer, with no ownership change, no GPU-modified range removed
    // (readbacks, CPU invalidations) and the same collection tick, returns the buffer and offset of the first obtain.
    // Everything the full path would do again is already done: the range is in the GPU-modified set (so the dirty
    // sync stops at its first check), the block write ticks and the ever-written set hold the current tick and range,
    // and the buffer was touched at this collection tick. 1 = use the memo; 2 = verify: run the full path on every hit
    // and count a different buffer or offset, or a full path that uploaded or changed the GPU-modified set, as a
    // mismatch. Off while any diagnostic that observes each written obtain is on.
    internal static readonly int WrittenObtainMemoMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_WRITTEN_OBTAIN_MEMO"), out var writtenMemo) ? writtenMemo : 0;

    private bool WrittenObtainMemoAllowed => !WrittenSyncCpuDirty && !EagerReadbacksOn && !ReadbackTickLog && DirtyWatch == 0 &&
        !EarlyWriterSubmit && !Diagnostics.TableReadCensus.Enabled && !Diagnostics.DccWriterTrace.Enabled && ReadbackMirror.Mode == 0 &&
        !GpuWriteHistory.Recording;

    private readonly Dictionary<(ulong Address, ulong Size), (GpuBuffer Buffer, ulong Offset)> _writtenMemo = new();
    private ulong _writtenMemoTick = ulong.MaxValue;
    private ulong _writtenMemoCollectionTick;
    private long _writtenMemoRegistryEpoch;
    private long _writtenMemoRemovalEpoch;
    private long _syncUploads;
    private static long _writtenMemoMismatchTotal;
    private static int _writtenMemoMismatchLogs;

    private bool WrittenMemoCurrent() =>
        _writtenMemoTick == _scheduler.CurrentTick && _writtenMemoCollectionTick == _retirementPolicy.CurrentTick &&
        _writtenMemoRegistryEpoch == _registry.Epoch && _writtenMemoRemovalEpoch == Volatile.Read(ref _gpuModifiedRemovals);

    private void ResetWrittenMemo()
    {
        _writtenMemo.Clear();
        _writtenMemoTick = _scheduler.CurrentTick;
        _writtenMemoCollectionTick = _retirementPolicy.CurrentTick;
        _writtenMemoRegistryEpoch = _registry.Epoch;
        _writtenMemoRemovalEpoch = Volatile.Read(ref _gpuModifiedRemovals);
    }

    private static readonly bool ObtainFastChecks =Environment.GetEnvironmentVariable("SHARPEMU_OBTAIN_FAST_CHECKS") != "0";

    public (GpuBuffer Buffer, ulong Offset) ObtainBuffer(ulong guestAddress, ulong size, bool isWritten, bool isTexelBuffer = false, ResourceSlotIdentifier bufferIdentifier = default, bool conservativeWrite = false)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferAcquisitionChecks);
        if (CpWriteDeferProbe.Enabled) CpWriteDeferProbe.OnRead(guestAddress, size); // [local]
        if (isWritten && SharpEmu.Libs.Gpu.Vulkan.ScopeBarrierTracker.Enabled) SharpEmu.Libs.Gpu.Vulkan.ScopeBarrierTracker.NoteWrite(); // [local]
        var command = _scheduler.Current;
        if (command.IsInvalid || !IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("A buffer request requires a command buffer that is recording.");
        }

        var memoized = isWritten && !isTexelBuffer && WrittenObtainMemoMode != 0 && WrittenObtainMemoAllowed;
        var verifying = false;
        (GpuBuffer Buffer, ulong Offset) remembered = default;
        long uploadsBefore = 0, modifiedBefore = 0, removalsBefore = 0;
        if (isWritten)
        {
            _obtainCounts[(int)ObtainOutcome.Written]++;
        }

        if (memoized)
        {
            if (!WrittenMemoCurrent())
            {
                ResetWrittenMemo();
            }
            else if (_writtenMemo.TryGetValue((guestAddress, size), out remembered))
            {
                if (WrittenObtainMemoMode != 2)
                {
                    _obtainCounts[(int)ObtainOutcome.WrittenMemoHit]++;
                    return remembered;
                }

                verifying = true;
                uploadsBefore = _syncUploads;
                modifiedBefore = Volatile.Read(ref _gpuModifiedVersion);
                removalsBefore = Volatile.Read(ref _gpuModifiedRemovals);
            }
        }

        // [local] SHARPEMU_OBTAIN_FAST_CHECKS (on by default): the same predicate, cheapest first. A clean range
        // answers from the lock-free block summary, and on the GPU queue thread a clear MayHaveGpuDirtyPages is exact.
        var streamCandidate = ObtainFastChecks
            ? !isWritten && _tracker.HasCpuDirtyPages(guestAddress, size) &&
              (!_tracker.MayHaveGpuDirtyPages(guestAddress, size) || !_tracker.HasGpuDirtyPages(guestAddress, size))
            : !isWritten && !_tracker.HasGpuDirtyPages(guestAddress, size) && _tracker.HasCpuDirtyPages(guestAddress, size);
        if (streamCandidate &&
            (size <= CachingPageSize || (!isTexelBuffer && _tracker.IsCpuWriteHotRange(guestAddress, size))))
        {
            using var streamProfile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferStreamUpload);
            if (_stream.TryMap(size, out var streamOffset, StreamOffsetAlignment, allowWait: false) &&
                _backing.TryReadBacking(guestAddress, _stream.Mapped.Slice((int)streamOffset, (int)size)))
            {
                _stream.Commit();
                _obtainCounts[bufferIdentifier.IsValid ? (int)ObtainOutcome.StreamPrefound : (int)ObtainOutcome.Stream]++;
                return (_stream, streamOffset);
            }

            _obtainCounts[(int)ObtainOutcome.StreamFailed]++;
        }

        // A GPU write into memory without backing could never download later; refuse it now.
        if (isWritten && !_backing.IsBackedRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{guestAddress:X16} size=0x{size:X16}");
        }

        var buffer = _registry.TryGetRegisteredBuffer(bufferIdentifier);
        if (buffer == null || !buffer.IsInBounds(guestAddress, size))
        {
            bufferIdentifier = FindBuffer(guestAddress, size);
            buffer = _registry.GetBuffer(bufferIdentifier);
            _obtainCounts[(int)ObtainOutcome.CachedLookup]++;
        }
        else
        {
            _obtainCounts[(int)ObtainOutcome.CachedPrefound]++;
        }

        if (streamCandidate)
        {
            _obtainCounts[(int)ObtainOutcome.CachedStreamCandidate]++;
        }

        TouchBuffer(bufferIdentifier);
        // A persistent upload must track the next CPU write instead of uploading unchanged hot pages.
        // Streaming keeps hot pages writable; the fallback must restore protection before copying.
        _ = SynchronizeBuffer(buffer, guestAddress, size, isWritten, isTexelBuffer, preserveCpuWriteHotPages: false);
        if (isWritten)
        {
            buffer.NoteGpuWrite(buffer.Offset(guestAddress), size);
            if (Diagnostics.DccWriterTrace.Enabled)
            {
                Diagnostics.DccWriterTrace.Record(RequireImageCache(), guestAddress, size);
            }

            if (DirtyWatch != 0 && guestAddress <= DirtyWatch && DirtyWatch < guestAddress + size && _dirtyWatchLogs++ < 400) // [local]
                Console.Error.WriteLine($"[DIRTY_WATCH] range=0x{guestAddress:X}+0x{size:X} shader=0x{Diagnostics.GpuReadTrace.CurrentShader:X16} stage={Diagnostics.GpuReadTrace.CurrentStage} tick={_scheduler.CurrentTick} caller={(_dirtyWatchLogs < 6 ? new System.Diagnostics.StackTrace(1, false).ToString().Replace(Environment.NewLine, " | ") : "")}");
            if (Diagnostics.TableDirtyProbe.Overlaps(guestAddress, size)) Diagnostics.TableDirtyProbe.Note(conservativeWrite ? "obtain-conservative" : "obtain", guestAddress, size, _scheduler.CurrentTick); // [local]
            if (size < BroadWriteBytes) _preciseGpuModifiedRanges.Add(guestAddress, size); // [local]
            if (StoreTableVerify && !conservativeWrite) _exactGpuModifiedRanges.Add(guestAddress, size); // [local]
            NoteEverGpuWritten(guestAddress, size); // [local]
            if (TableWordMemo.Enabled) TableWordMemo.NoteGpuWrite(guestAddress, size); // [local]
            if (Diagnostics.TableReadCensus.Enabled) Diagnostics.TableReadCensus.NoteWrite(guestAddress, size, _scheduler.CurrentTick); // [local]
            if (EarlyWriterSubmit && !EarlySubmitPending) NoteHotWordWrite(guestAddress, size); // [local]
            if (ReadbackTickLog) // [local] diagnostic: last writer tick per 4 KB page
                for (var page = guestAddress >> 12; page <= (guestAddress + size - 1) >> 12; page++)
                    _pageWriteTicks[page] = _scheduler.CurrentTick;
            if (!_gpuModifiedRanges.Contains(guestAddress, size))
            {
                lock (_gpuModifiedGate) _gpuModifiedRanges.Add(guestAddress, size); // [local] SHARPEMU_FAULT_READ_EMULATE
                FaultReadWatch.Note(guestAddress, size); // [local]
                Interlocked.Increment(ref _gpuModifiedVersion);
            }

            NoteHotWindowWrite(guestAddress, size);
        }

        var result = (Buffer: buffer, Offset: buffer.Offset(guestAddress));
        if (memoized)
        {
            if (verifying)
            {
                _obtainCounts[(int)ObtainOutcome.WrittenMemoVerified]++;
                var uploaded = _syncUploads != uploadsBefore;
                var modified = Volatile.Read(ref _gpuModifiedVersion) != modifiedBefore || Volatile.Read(ref _gpuModifiedRemovals) != removalsBefore;
                if (!ReferenceEquals(remembered.Buffer, buffer) || remembered.Offset != result.Offset || uploaded || modified)
                {
                    _obtainCounts[(int)ObtainOutcome.WrittenMemoMismatch]++;
                    Interlocked.Increment(ref _writtenMemoMismatchTotal);
                    if (_writtenMemoMismatchLogs++ < 40)
                    {
                        Console.Error.WriteLine(
                            $"[GPU][WRITTEN_MEMO_MISMATCH] range=0x{guestAddress:X}+0x{size:X} same_buffer={ReferenceEquals(remembered.Buffer, buffer)} " +
                            $"offset=0x{remembered.Offset:X}/0x{result.Offset:X} uploaded={uploaded} modified={modified} tick={_scheduler.CurrentTick}");
                    }
                }
            }

            if (!WrittenMemoCurrent())
            {
                ResetWrittenMemo();
            }

            _writtenMemo[(guestAddress, size)] = result;
        }

        return result;
    }

    // [local] SHARPEMU_UNIFORM_IMAGE_SOURCE: an image source of one repeated byte (GTA V clears a 6 MB depth
    // image to 0xFF in 1 MB fills every frame) is filled in staging on the GPU instead of copied from guest
    // memory. The guest bytes are scanned in place through the backing alias; anything else is copied as before.
    private static readonly bool UniformImageSource = Environment.GetEnvironmentVariable("SHARPEMU_UNIFORM_IMAGE_SOURCE") == "1"; // opt-in: ImageUpload -0.6 ms, FPS within noise (rt456)
    private const ulong UniformImageSourceMin = 1UL << 20;
    private static long _uniformImageSources;

    internal static long TakeUniformImageSources() => Interlocked.Exchange(ref _uniformImageSources, 0);

    private bool TryFillUniformStaging(ulong guestAddress, ulong size, ulong stageOffset)
    {
        if (!UniformImageSource || size < UniformImageSourceMin || size > int.MaxValue || ((size | stageOffset) & 3) != 0 ||
            !_backing.TryEnterBackingAliasAccess())
        {
            return false;
        }

        byte value;
        try
        {
            if (!_backing.TryResolveBackingAlias(guestAddress, size, out var alias) || alias == 0)
            {
                return false;
            }

            var bytes = new ReadOnlySpan<byte>((void*)alias, (int)size);
            value = bytes[0];
            if (bytes.IndexOfAnyExcept(value) >= 0)
            {
                return false;
            }
        }
        finally
        {
            _backing.ExitBackingAliasAccess();
        }

        _staging.Fill(stageOffset, size, value * 0x01010101u);
        Interlocked.Increment(ref _uniformImageSources);
        return true;
    }

    public (GpuBuffer Buffer, ulong Offset) ObtainBufferForImage(ulong guestAddress, ulong size)
    {
        if (!IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("The image source range is invalid.");
        }

        var cpuModified = _tracker.HasCpuDirtyPages(guestAddress, size);
        var gpuModified = _tracker.HasGpuDirtyPages(guestAddress, size);
        var hasDirtyBufferSource = _gpuModifiedRanges.Overlaps(guestAddress, size);
        _tracker.ValidateGpuDirtyOwnership(_gpuModifiedRanges, guestAddress, size, "image source");

        var owner = FindOwner(guestAddress, size);
        if (hasDirtyBufferSource && owner == null)
        {
            if (!IsRegionRegistered(guestAddress, size))
            {
                throw SubmissionScheduler.Fatal("The GPU-dirty image source has no device buffer.");
            }

            owner = _registry.GetBuffer(FindBuffer(guestAddress, size));
        }

        if (owner != null && !cpuModified && (!gpuModified || hasDirtyBufferSource))
        {
            TouchBuffer(owner);
            return (owner, owner.Offset(guestAddress));
        }

        if (hasDirtyBufferSource && owner == null)
        {
            throw SubmissionScheduler.Fatal("Cannot find the device buffer that owns the GPU-dirty image source.");
        }

        if (!_staging.TryMap(size, out var stageOffset, 16))
        {
            throw SubmissionScheduler.Fatal(
                $"Cannot reserve image staging space: address=0x{guestAddress:X16} size=0x{size:X16} capacity=0x{_staging.Size:X16} tick={_scheduler.CurrentTick}.");
        }
        if (!TryFillUniformStaging(guestAddress, size, stageOffset) &&
            !_backing.TryReadBacking(guestAddress, _staging.Mapped.Slice((int)stageOffset, (int)size)) &&
            !KernelMemoryCompatExports.TryReadPrtBacking(_backing, guestAddress,
                _staging.Mapped.Slice((int)stageOffset, (int)size)))
        {
            throw SubmissionScheduler.Fatal(
                $"Could not read the mapped guest image backing: address=0x{guestAddress:X16} size=0x{size:X16} range_backed={_backing.IsBackedRange(guestAddress, size)} first_byte_backed={_backing.IsBackedRange(guestAddress, 1)} last_byte_backed={_backing.IsBackedRange(guestAddress + size - 1, 1)} tick={_scheduler.CurrentTick}.");
        }

        _staging.Commit();
        hasDirtyBufferSource = _gpuModifiedRanges.Overlaps(guestAddress, size);
        owner = FindOwner(guestAddress, size);
        if (hasDirtyBufferSource && owner == null)
        {
            throw SubmissionScheduler.Fatal("The GPU-dirty image source lost its device buffer owner.");
        }

        if (owner == null || (_tracker.HasGpuDirtyPages(guestAddress, size) && !hasDirtyBufferSource))
        {
            return (_staging, stageOffset);
        }

        TouchBuffer(owner);
        // Tracking reports whole dirty pages, which can extend beyond the image's
        // staging slice. Upload those pages through the normal buffer uploader so
        // every byte marked clean is copied from its actual guest backing.
        _ = SynchronizeBuffer(owner, guestAddress, size, false, false);
        return (owner, owner.Offset(guestAddress));
    }

    public void WriteHostMemory(ulong guestAddress, ReadOnlySpan<byte> data)
    {
        if (guestAddress == 0 || data.IsEmpty || (ulong)data.Length > ulong.MaxValue - guestAddress)
        {
            throw SubmissionScheduler.Fatal("The host DMA write range is invalid.");
        }

        if (!TryWriteBackingNoted(guestAddress, data))
        {
            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{guestAddress:X16} size=0x{data.Length:X16}");
        }

        // Registered buffers are ordered and disjoint: walk only the ones the write overlaps.
        var end = guestAddress + (ulong)data.Length;
        ulong covered = 0; // [local]
        for (var index = _registry.FindFirstOverlappingIndex(guestAddress);
             index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end;
             index++)
        {
            var address = _registry.GetRegisteredAddress(index);
            var bufferIdentifier = _registry.GetRegisteredIdentifier(index);
            var buffer = _registry.GetBuffer(bufferIdentifier);
            var begin = Math.Max(guestAddress, address);
            var rangeEnd = Math.Min(end, address + buffer.Size);
            if (begin >= rangeEnd)
            {
                continue;
            }

            WriteDataBuffer(buffer, begin, data.Slice((int)(begin - guestAddress), (int)(rangeEnd - begin)));
            TouchBuffer(bufferIdentifier);
            covered += rangeEnd - begin;
        }
        if (covered < (ulong)data.Length) // [local] bytes only the guest copy received
        {
            HostWriteGaps++;
            if (HostWriteGaps <= 40 || (HostWriteGaps & (HostWriteGaps - 1)) == 0)
                Console.Error.WriteLine($"[GPU][HOSTWRITE_GAP] address=0x{guestAddress:X} size={data.Length} covered={covered} count={HostWriteGaps} tick={_scheduler.CurrentTick}");
            if (HostWriteMarkDirty) _tracker.MarkCpuDirtyPages(guestAddress, (ulong)data.Length);
        }
    }

    private static long HostWriteGaps; // [local]
    private static readonly bool FillLog = Environment.GetEnvironmentVariable("SHARPEMU_FILL_LOG") == "1"; // [local]
    private readonly long[] _fillBuckets = new long[12];
    private int _fillBigLogs;
    private long _fillGpu, _fillGpuBytes, _fillCpu, _fillInvalidate, _fillObtain, _fillRecord, _fillReport, _fillClearMeta, _fillQuery, _fillImageInvalidate, _fillImageCount, _fillWrite;
    private static readonly ulong FillOnGpuMin = Convert.ToUInt64(Environment.GetEnvironmentVariable("SHARPEMU_FILL_ON_GPU_MIN") ?? "0", 16); // [local]
    // [local] Large-fill patterns of 256 KB, one per recent value (GTA V alternates 0 and 0xFFFFFFF0), replaced in turn.
    private readonly uint[]?[] _fillPatterns = new uint[]?[4];
    private readonly uint[] _fillPatternValues = new uint[4];
    private int _fillPatternNext;
    private static readonly bool ParallelFill = Environment.GetEnvironmentVariable("SHARPEMU_PARALLEL_FILL") != "0"; // [local] on by default (rt421)
    private const ulong ParallelFillMin = 2UL << 20;
    private static readonly bool FastFill = Environment.GetEnvironmentVariable("SHARPEMU_FAST_FILL") != "0"; // [local] on by default (rt415: CommandMemoryTransfer 5.1 -> 3.4 ms/frame)
    // [local] experiment: a written binding over a GPU-modified range still uploads CPU-dirty pages first (SHARPEMU_WRITTEN_SYNC_CPU_DIRTY=1).
    private static readonly bool WrittenSyncCpuDirty = Environment.GetEnvironmentVariable("SHARPEMU_WRITTEN_SYNC_CPU_DIRTY") == "1";
    private static readonly bool HostWriteMarkDirty = Environment.GetEnvironmentVariable("SHARPEMU_HOSTWRITE_MARK_DIRTY") == "1"; // [local]

    // [local] The 256 KB pattern of a value, built once while the value stays among the last four used.
    private uint[] FillPattern(uint value)
    {
        for (var index = 0; index < _fillPatterns.Length; index++)
        {
            if (_fillPatterns[index] is { } pattern && _fillPatternValues[index] == value)
            {
                return pattern;
            }
        }

        var slot = _fillPatternNext;
        _fillPatternNext = (slot + 1) % _fillPatterns.Length;
        var created = _fillPatterns[slot] ??= new uint[64 * 1024];
        created.AsSpan().Fill(value);
        _fillPatternValues[slot] = value;
        if (MemCopyProbe.Enabled) MemCopyProbe.Add(MemCopyProbe.Site.FillPatternRefill, created.Length * 4L); // [local] SHARPEMU_MEMCOPY_PROBE
        return created;
    }

    // [local] SkipLocalsInit: the stack chunk is filled whole before it is written out.
    [System.Runtime.CompilerServices.SkipLocalsInit]
    public void FillBuffer(ulong guestAddress, ulong size, uint value, bool isGds)
    {
        if ((guestAddress & 3) != 0 || size == 0 || (size & 3) != 0 || size > ulong.MaxValue - guestAddress)
        {
            throw SubmissionScheduler.Fatal("The fill range must be aligned to four bytes.");
        }

        if (isGds)
        {
            if (guestAddress > _gds.Size || size > _gds.Size - guestAddress)
            {
                throw SubmissionScheduler.Fatal("The GDS fill range is outside the buffer.");
            }

            _gds.Fill(guestAddress, size, value);
            return;
        }

        if (guestAddress == 0)
        {
            throw SubmissionScheduler.Fatal("The fill memory address is invalid.");
        }

        var images = RequireImageCache();
        var t0 = FillLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
        _ = images.ClearMetadata(guestAddress);
        var t1 = FillLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var region = images.QueryRegion(guestAddress, size);
        var t2 = FillLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (FillLog) { _fillClearMeta += t1 - t0; _fillQuery += t2 - t1; }
        // [local experiment] SHARPEMU_FILL_ON_GPU_MIN=<hex bytes>: a fill at least this large over a range a single
        // registered buffer covers goes down the GPU path (device fill, range GPU-owned) instead of a CPU memset of
        // the guest copy plus an upload (rt410/rt411: ~21 MB/frame of clears in GTA V, ~170 ms per 5 s of memset).
        var fillOnGpu = FillOnGpuMin != 0 && size >= FillOnGpuMin && _registry.FindContainingBuffer(guestAddress, size).IsValid;
        if (!fillOnGpu && !HasGpuDirtyBytes(guestAddress, size) && !region.GpuImageBytes)
        {
            if (region.ImageBytes)
            {
                var t3 = FillLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                images.InvalidateMemory(guestAddress, size);
                if (FillLog) { _fillImageInvalidate += System.Diagnostics.Stopwatch.GetTimestamp() - t3; _fillImageCount++; }
            }

            if (FillLog) // [local]
            {
                _fillCpu++;
                var bucket = size <= 4096 ? 0 : size <= 65536 ? 1 : size <= (1 << 20) ? 2 : 3;
                var contained = _registry.FindContainingBuffer(guestAddress, size).IsValid;
                var overlapIndex = _registry.FindFirstOverlappingIndex(guestAddress);
                var overlaps = overlapIndex < _registry.RegisteredCount && _registry.GetRegisteredAddress(overlapIndex) < guestAddress + size;
                _fillBuckets[bucket * 3 + (contained ? 0 : overlaps ? 1 : 2)] += (long)size;
                if (size > (1 << 20) && _fillBigLogs++ < 40)
                    Console.Error.WriteLine($"[FILL_BIG] address=0x{guestAddress:X} size=0x{size:X} value=0x{value:X} contained={contained} overlaps={overlaps} imageBytes={region.ImageBytes}");
            }
            var tw = FillLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try {
            // [local] The stack chunk below is 4 KB at most, so a fill above that takes the large path when FastFill is on;
            // the chunk is only built on the path that writes it.
            if (FastFill && size > 1024UL * sizeof(uint))
            {
                // [local] A large fill: the guest copy by memset, each registered buffer by one GPU fill,
                // instead of a staged copy per 4 KB chunk (rt406: ~21 MB/frame of fills in GTA V).
                // 256 KB pieces: per-call overhead of the backing write dominated the 4 KB loop.
                if (MemCopyProbe.Enabled) MemCopyProbe.Add(MemCopyProbe.Site.FillFast, (long)size); // [local] SHARPEMU_MEMCOPY_PROBE
                var patternArray = FillPattern(value);
                var pieceBytes = (ulong)patternArray.Length * sizeof(uint);
                if (ParallelFill && size >= ParallelFillMin)
                {
                    // [local] Large clears spread over cores: the backing writes take a shared lock.
                    var pieces = (long)((size + pieceBytes - 1) / pieceBytes);
                    var failed = 0;
                    Parallel.For(0, pieces, piece =>
                    {
                        var offset = (ulong)piece * pieceBytes;
                        var chunk = (int)Math.Min(size - offset, pieceBytes);
                        if (!TryWriteBackingNoted(guestAddress + offset, MemoryMarshal.AsBytes(patternArray.AsSpan())[..chunk]))
                            Interlocked.Exchange(ref failed, 1);
                    });
                    if (failed != 0)
                    {
                        throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{guestAddress:X16} size=0x{size:X}");
                    }
                }
                else
                {
                    var pattern = MemoryMarshal.AsBytes(patternArray.AsSpan());
                    for (ulong offset = 0; offset < size;)
                    {
                        var chunk = (int)Math.Min(size - offset, (ulong)pattern.Length);
                        if (!TryWriteBackingNoted(guestAddress + offset, pattern[..chunk]))
                        {
                            throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{guestAddress + offset:X16} size=0x{chunk:X}");
                        }

                        offset += (ulong)chunk;
                    }
                }

                var end = guestAddress + size;
                for (var index = _registry.FindFirstOverlappingIndex(guestAddress);
                     index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end;
                     index++)
                {
                    var fillIdentifier = _registry.GetRegisteredIdentifier(index);
                    var fillBuffer = _registry.GetBuffer(fillIdentifier);
                    var begin = Math.Max(guestAddress, fillBuffer.CpuAddress);
                    var rangeEnd = Math.Min(end, fillBuffer.CpuAddress + fillBuffer.Size);
                    if (begin >= rangeEnd)
                    {
                        continue;
                    }

                    GuestGpuMemoryHook.Trace(begin, rangeEnd - begin, $"host-fill-data submission_tick={_scheduler.CurrentTick}");
                    fillBuffer.Fill(fillBuffer.Offset(begin), rangeEnd - begin, value);
                    TouchBuffer(fillIdentifier);
                }

                return;
            }

            if (MemCopyProbe.Enabled) MemCopyProbe.Add(MemCopyProbe.Site.FillSmall, (long)size); // [local] SHARPEMU_MEMCOPY_PROBE
            // Labels and small clears dominate; fill a stack chunk once and repeat it.
            Span<uint> values = stackalloc uint[(int)Math.Min(size / sizeof(uint), 1024UL)];
            values.Fill(value);
            if (MemCopyProbe.Enabled) MemCopyProbe.Add(MemCopyProbe.Site.FillStackPattern, values.Length * 4L); // [local] SHARPEMU_MEMCOPY_PROBE
            var bytes = MemoryMarshal.AsBytes(values);
            for (ulong offset = 0; offset < size;)
            {
                var chunk = (int)Math.Min(size - offset, (ulong)bytes.Length);
                WriteHostMemory(guestAddress + offset, bytes[..chunk]);
                offset += (ulong)chunk;
            }

            return;
            } finally { if (FillLog) _fillWrite += System.Diagnostics.Stopwatch.GetTimestamp() - tw; }
        }

        var fillStarted = FillLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
        images.InvalidateMemoryFromGpu(guestAddress, size);
        var afterInvalidate = FillLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var bufferIdentifier = FindBuffer(guestAddress, size);
        var (destination, destinationOffset) = ObtainBuffer(guestAddress, size, true, true, bufferIdentifier);
        var afterObtain = FillLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        destination.Fill(destinationOffset, size, value);
        if (FillLog)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            _fillGpu++; _fillGpuBytes += (long)size; _fillInvalidate += afterInvalidate - fillStarted; _fillObtain += afterObtain - afterInvalidate; _fillRecord += now - afterObtain;
            if (now - _fillReport > 5 * System.Diagnostics.Stopwatch.Frequency)
            {
                _fillReport = now;
                double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Console.Error.WriteLine(FormattableString.Invariant($"[FILL_GPU] count={_fillGpu} bytes={_fillGpuBytes} invalidate_ms={Ms(_fillInvalidate):F0} obtain_ms={Ms(_fillObtain):F0} record_ms={Ms(_fillRecord):F0} cpu_path={_fillCpu} clear_meta_ms={Ms(_fillClearMeta):F0} query_ms={Ms(_fillQuery):F0} image_invalidate_ms={Ms(_fillImageInvalidate):F0}/{_fillImageCount} cpu_write_ms={Ms(_fillWrite):F0} bytes_by_size(<=4K,<=64K,<=1M,>1M)x(contained,overlap,none)=[{string.Join(",", _fillBuckets.Select(b => (b >> 20).ToString()))}]MB"));
                _fillGpu = _fillGpuBytes = _fillCpu = 0; _fillInvalidate = _fillObtain = _fillRecord = 0; _fillClearMeta = _fillQuery = _fillImageInvalidate = _fillImageCount = _fillWrite = 0; Array.Clear(_fillBuckets);
            }
        }
        if (images.OverlapsDccMetadata(guestAddress, size) && TryWriteFillToBacking(guestAddress, size, value))
        {
            RemoveGpuModified(guestAddress, size);
            var firstPage = guestAddress & ~(TrackerLayout.PageBytes - 1);
            for (var page = firstPage; page < guestAddress + size; page += TrackerLayout.PageBytes)
            {
                if (!_gpuModifiedRanges.Overlaps(page, TrackerLayout.PageBytes))
                {
                    _tracker.ClearGpuDirtyPages(page, TrackerLayout.PageBytes);
                }
            }
        }
    }

    public void FillDccMetadata(ulong guestAddress, ulong size, uint value)
    {
        if (guestAddress == 0 || (guestAddress & 3) != 0 || size == 0 || (size & 3) != 0 || size > ulong.MaxValue - guestAddress ||
            HasGpuDirtyBytes(guestAddress, size) || RequireImageCache().QueryRegion(guestAddress, size).ImageBytes)
        {
            FillBuffer(guestAddress, size, value, false);
            return;
        }

        var (destination, destinationOffset) = ObtainBuffer(guestAddress, size, false, false, FindBuffer(guestAddress, size));
        destination.Fill(destinationOffset, size, value);
        if (!TryWriteFillToBacking(guestAddress, size, value))
        {
            FillBuffer(guestAddress, size, value, false);
        }
    }

    private bool TryWriteFillToBacking(ulong guestAddress, ulong size, uint value)
    {
        var values = new uint[(int)Math.Min(size / sizeof(uint), 4096)];
        Array.Fill(values, value);
        var bytes = MemoryMarshal.AsBytes<uint>(values);
        for (ulong offset = 0; offset < size;)
        {
            var chunk = (int)Math.Min(size - offset, (ulong)bytes.Length);
            if (!TryWriteBackingNoted(guestAddress + offset, bytes[..chunk]))
            {
                return false;
            }

            offset += (ulong)chunk;
        }

        return true;
    }

    public const ulong MaxHostCopyWords = 64 * 1024;

    public bool TryCopyWordsOnHost(ulong destination, ulong source, ulong sourceWords, ulong words)
    {
        if (destination == 0 || source == 0 || sourceWords == 0 || words == 0 || ((destination | source) & 3) != 0 ||
            words > MaxHostCopyWords || sourceWords > MaxHostCopyWords)
        {
            return false;
        }

        var size = words * sizeof(uint);
        var sourceSize = Math.Min(sourceWords, words) * sizeof(uint);
        if (size > ulong.MaxValue - destination || sourceSize > ulong.MaxValue - source ||
            (source < destination + size && destination < source + sourceSize))
        {
            return false;
        }

        var images = RequireImageCache();
        var sourceRegion = images.QueryRegion(source, sourceSize);
        var destinationRegion = images.QueryRegion(destination, size);
        if (HasGpuDirtyBytes(source, sourceSize) || HasGpuDirtyBytes(destination, size) ||
            sourceRegion.GpuImageBytes || destinationRegion.GpuImageBytes)
        {
            return false;
        }

        var pattern = System.Buffers.ArrayPool<byte>.Shared.Rent((int)sourceSize);
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(size, 64UL * 1024));
        try
        {
            if (!_backing.TryReadBacking(source, pattern.AsSpan(0, (int)sourceSize)))
            {
                return false;
            }

            if (destinationRegion.ImageBytes)
            {
                images.InvalidateMemory(destination, size);
            }

            var chunkLength = (ulong)(chunk.Length - chunk.Length % sizeof(uint));
            for (ulong offset = 0; offset < size;)
            {
                var length = (int)Math.Min(size - offset, chunkLength);
                for (var filled = 0; filled < length;)
                {
                    var patternOffset = (int)((offset + (ulong)filled) % sourceSize);
                    var copied = Math.Min(length - filled, (int)sourceSize - patternOffset);
                    pattern.AsSpan(patternOffset, copied).CopyTo(chunk.AsSpan(filled, copied));
                    filled += copied;
                }

                WriteHostMemory(destination + offset, chunk.AsSpan(0, length));
                offset += (ulong)length;
            }

            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pattern);
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    public void CopyBuffer(ulong dstVaddr, ulong srcVaddr, ulong size, bool dstGds, bool srcGds)
    {
        var dstMemory = !dstGds;
        var srcMemory = !srcGds;
        if ((dstMemory && dstVaddr == 0) || (srcMemory && srcVaddr == 0) || size == 0 ||
            ((dstGds || srcGds) && ((dstVaddr | srcVaddr | size) & 3) != 0) ||
            size > ulong.MaxValue - dstVaddr || size > ulong.MaxValue - srcVaddr || (dstGds && srcGds) ||
            (dstGds && (dstVaddr > _gds.Size || size > _gds.Size - dstVaddr)) ||
            (srcGds && (srcVaddr > _gds.Size || size > _gds.Size - srcVaddr)))
        {
            throw SubmissionScheduler.Fatal(
                $"The buffer copy range is invalid: src=0x{srcVaddr:X16} dst=0x{dstVaddr:X16} size=0x{size:X16} src_gds={(srcGds ? 1 : 0)} dst_gds={(dstGds ? 1 : 0)}");
        }

        var images = RequireImageCache();
        var srcRegion = srcMemory ? images.QueryRegion(srcVaddr, size) : default;
        var dstRegion = dstMemory ? images.QueryRegion(dstVaddr, size) : default;
        if (srcMemory && dstMemory && !HasGpuDirtyBytes(srcVaddr, size) && !HasGpuDirtyBytes(dstVaddr, size) &&
            !srcRegion.GpuImageBytes && !dstRegion.GpuImageBytes)
        {
            if (dstRegion.ImageBytes)
            {
                images.InvalidateMemory(dstVaddr, size);
            }

            var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(size, 64UL * 1024));
            try
            {
                for (ulong offset = 0; offset < size;)
                {
                    var chunk = (int)Math.Min(size - offset, (ulong)bytes.Length);
                    if (!_backing.TryReadBacking(srcVaddr + offset, bytes.AsSpan(0, chunk)))
                    {
                        throw SubmissionScheduler.Fatal("The host DMA source has no direct backing.");
                    }

                    WriteHostMemory(dstVaddr + offset, bytes.AsSpan(0, chunk));
                    offset += (ulong)chunk;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
            }

            return;
        }

        var command = _scheduler.Current;
        if (dstMemory)
        {
            images.InvalidateMemoryFromGpu(dstVaddr, size);
        }

        var srcId = srcMemory ? FindBuffer(srcVaddr, size) : default;
        var dstId = dstMemory ? FindBuffer(dstVaddr, size) : default;
        var (src, srcOffset) = srcMemory ? ObtainBuffer(srcVaddr, size, false, true, srcId) : (_gds, srcVaddr);
        var (dst, dstOffset) = dstMemory ? ObtainBuffer(dstVaddr, size, true, true, dstId) : (_gds, dstVaddr);
        if (ReferenceEquals(src, dst) && srcOffset < dstOffset + size && dstOffset < srcOffset + size)
        {
            throw SubmissionScheduler.Fatal("The resolved Vulkan copy ranges overlap.");
        }

        if (dstMemory) GuestGpuMemoryHook.Trace(dstVaddr, size, $"host-copy src=0x{srcVaddr:X} src_memory={srcMemory} submission_tick={_scheduler.CurrentTick}"); // [local]
        dst.CopyFrom(command, src, srcOffset, dstOffset, size);
    }

    // Buffers are ordered and disjoint: the last one starting before the query end is the only candidate.
    public bool IsRegionRegistered(ulong guestAddress, ulong size)
    {
        if (!IsValidRange(guestAddress, size))
        {
            throw SubmissionScheduler.Fatal("The registered-region query is invalid.");
        }

        return _registry.HasOverlap(guestAddress, size);
    }

    public bool HasGpuDirtyPages(ulong guestAddress, ulong size) => _tracker.HasGpuDirtyPages(guestAddress, size);

    public bool HasGpuDirtyBytes(ulong guestAddress, ulong size) => _gpuModifiedRanges.Overlaps(guestAddress, size);

    public long GpuModifiedVersion => Volatile.Read(ref _gpuModifiedVersion);

    public bool HasCpuDirtyPages(ulong guestAddress, ulong size) => _tracker.HasCpuDirtyPages(guestAddress, size);

    public void ProcessFaultBuffer() => _faults.ProcessFaultBuffer();

    // Uploads every mapped range before a BDA draw; the fault pass runs at the next collection.
    // Every device-address program prepares all GPU-mapped memory; visiting each registered
    // buffer per dispatch cost ~17 % of the Demon's Souls render thread. The same work is done
    // in two cheaper parts: the recency touch, a no-op after the first one in a retirement tick,
    // runs once per tick (and when the mapping changes; new buffers register with the current
    // tick), and uploads only visit buffers in blocks the tracker does not know to be clean.
    // Hot pages stay dirty and writable, so every preparation still re-uploads them.
    private ulong _bdaTouchTick = ulong.MaxValue;
    private ulong _bdaTouchMapping;

    public static ulong MappingKey(IReadOnlyCollection<GuestSpan> spans)
    {
        var mapping = (ulong)spans.Count;
        foreach (var span in spans)
            mapping = (mapping ^ span.Address ^ (span.Size << 17)) * 0x100000001B3UL;
        return mapping;
    }

    public void PrepareBda(IEnumerable<GuestSpan> mapped)
    {
        var spans = mapped as IReadOnlyCollection<GuestSpan> ?? mapped.ToList();
        PrepareBda(spans, MappingKey(spans));
    }

    public void PrepareBda(IReadOnlyCollection<GuestSpan> mapped, ulong mapping)
    {
        var traceAddress = GuestGpuMemoryHook.TraceAddress;
        var traceCovered = false;
        if (traceAddress != 0)
        {
            foreach (var span in mapped)
            {
                if (traceAddress != 0 && GuestGpuMemoryHook.Traces(span.Address, span.Size))
                    traceCovered = true;
                SynchronizeBuffersInRange(span.Address, span.Size);
            }
        }
        else
        {
            var spans = mapped;
            if (_retirementPolicy.CurrentTick != _bdaTouchTick || mapping != _bdaTouchMapping)
            {
                foreach (var span in spans)
                    TouchBuffersInRange(span.Address, span.Size);
                _bdaTouchTick = _retirementPolicy.CurrentTick;
                _bdaTouchMapping = mapping;
            }

            var epoch = _tracker.CpuDirtyEpoch;
            if (epoch != _bdaSweepEpoch || mapping != _bdaSweepMapping)
            {
                foreach (var span in spans)
                    _tracker.ForEachPossiblyCpuDirtyRange(span.Address, span.Size, _uploadDirtyBuffersInRange ??= UploadDirtyBuffersInRange);
                _bdaSweepEpoch = epoch;
                _bdaSweepMapping = mapping;
            }
        }

        if (traceAddress != 0)
            GuestGpuMemoryHook.Trace(traceAddress, 1,
                $"device-address-preparation covered={traceCovered} registered={IsRegionRegistered(traceAddress, 1)} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
        _faultProcessPending = true;
    }

    private Action<ulong, ulong>? _uploadDirtyBuffersInRange;
    private long _bdaSweepEpoch = -1;
    private ulong _bdaSweepMapping;

    private void TouchBuffersInRange(ulong guestAddress, ulong size)
    {
        var end = guestAddress + size;
        var index = _registry.FindFirstOverlappingIndex(guestAddress);
        for (; index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end; index++)
            TouchBuffer(_registry.GetRegisteredIdentifier(index));
    }

    // [local] SHARPEMU_BDA_PRESERVE_HOT=1: device-address uploads leave pages the CPU writes after every upload
    // writable and dirty (uploaded on each visit) instead of protecting them again (a VirtualProtect, then a
    // guest write fault, per page and frame). Negative (rt457: 8.5 FPS, every bind streams the hot pages again).
    private static readonly bool BdaPreserveHot = Environment.GetEnvironmentVariable("SHARPEMU_BDA_PRESERVE_HOT") == "1";

    // The upload half of SynchronizeBuffersInRange, for a range that may hold CPU-dirty pages.
    private void UploadDirtyBuffersInRange(ulong guestAddress, ulong size)
    {
        var end = guestAddress + size;
        var index = _registry.FindFirstOverlappingIndex(guestAddress);
        for (; index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end; index++)
        {
            var buffer = _registry.GetBuffer(_registry.GetRegisteredIdentifier(index));
            var start = Math.Max(buffer.CpuAddress, guestAddress);
            var finish = Math.Min(buffer.CpuAddress + buffer.Size, end);
            if (start < finish && _tracker.MayHaveCpuDirtyPages(start, finish - start))
                _ = SynchronizeBuffer(buffer, start, finish - start, false, false, preserveCpuWriteHotPages: BdaPreserveHot);
        }
    }

    public void SynchronizeBuffersInRange(ulong guestAddress, ulong size)
    {
        var end = guestAddress + size;
        var index = _registry.FindFirstOverlappingIndex(guestAddress);
        for (; index < _registry.RegisteredCount && _registry.GetRegisteredAddress(index) < end; index++)
        {
            var identifier = _registry.GetRegisteredIdentifier(index);
            var buffer = _registry.GetBuffer(identifier);
            var start = Math.Max(buffer.CpuAddress, guestAddress);
            var finish = Math.Min(buffer.CpuAddress + buffer.Size, end);
            if (start < finish)
            {
                if (GuestGpuMemoryHook.Traces(start, finish - start))
                    GuestGpuMemoryHook.Trace(start, finish - start,
                        $"device-address-touch buffer={identifier} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
                // Clean buffers remain in use through their device addresses.
                TouchBuffer(identifier);
                // Device-address reads reuse persistent buffers; track writes after each upload.
                // A range without CPU-dirty pages has nothing to upload (the same early exit
                // SynchronizeBuffer takes for this read-only call), and the block summary
                // answers that without a lock for the common all-clean case.
                if (_tracker.HasCpuDirtyPages(start, finish - start))
                    _ = SynchronizeBuffer(buffer, start, finish - start, false, false, preserveCpuWriteHotPages: BdaPreserveHot);
            }
        }
    }

    // Tests use lower thresholds to check collection without large allocations.
    internal void SetCollectionThresholds(ulong collectionThreshold, ulong criticalThreshold)
    {
        _retirementPolicy.SetThresholds(collectionThreshold, criticalThreshold);
    }

    // Runs before the image readback flush and both collectors; the order matches the render loop.
    public void ProcessPendingFaultBuffer()
    {
        if (_faultProcessPending)
        {
            _faultProcessPending = false;
            ProcessFaultBuffer();
        }
    }

    public void RunGarbageCollector()
    {
        using var foreignRead = _device.Slabs.BeginForeignRead();
        ProcessPendingFaultBuffer();
        if (!_retirementPolicy.TryBeginCollection(_registry.RegisteredBytes, out var retirement))
        {
            return;
        }

        var dirtyBuffers = new List<ResourceSlotIdentifier>();
        var copies = new List<DownloadPiece>();
        var retireCount = 0;
        _registry.VisitRetirementCandidates(retirement.LatestEligibleTick, bufferIdentifier =>
        {
            var buffer = _registry.TryGetRegisteredBuffer(bufferIdentifier);
            if (buffer == null)
            {
                throw SubmissionScheduler.Fatal("The recency queue contains a deleted buffer.");
            }

            _tracker.ValidateGpuDirtyOwnership(_gpuModifiedRanges, buffer.CpuAddress, buffer.Size, "garbage collection");
            var dirty = _tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size);
            if (dirty && !retirement.DownloadDirtyBuffers)
            {
                return false;
            }

            if (GuestGpuMemoryHook.Traces(buffer.CpuAddress, buffer.Size))
                GuestGpuMemoryHook.Trace(buffer.CpuAddress, buffer.Size,
                    $"device-address-collection buffer={bufferIdentifier} dirty={dirty} aggressive={retirement.DownloadDirtyBuffers} cutoff_tick={retirement.LatestEligibleTick} collection_tick={_retirementPolicy.CurrentTick - 1} used_bytes={_registry.RegisteredBytes}");
            if (dirty)
            {
                CollectDirtyPieces(buffer, copies, "garbage collection");
                dirtyBuffers.Add(bufferIdentifier);
            }
            else
            {
                _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
                DeleteBuffer(bufferIdentifier);
            }

            return ++retireCount == retirement.MaximumBufferCount;
        });
        if (dirtyBuffers.Count == 0)
        {
            return;
        }

        if (copies.Count == 0)
        {
            throw SubmissionScheduler.Fatal("Dirty buffers have no download ranges.");
        }

        DownloadBufferMemory(copies);
        foreach (var bufferIdentifier in dirtyBuffers)
        {
            ReleaseDownloaded(bufferIdentifier);
        }
    }

    // Teardown: every GPU result reaches guest memory and every page returns to its guest protection.
    public void Shutdown()
    {
        var drained = false;
        try
        {
            AbandonEagerReadbacks();
            if (_scheduler.Active)
            {
                _scheduler.Finish();
            }

            using var foreignRead = _device.Slabs.BeginForeignRead();
            var copies = new List<DownloadPiece>();
            var dirtyBuffers = new List<ResourceSlotIdentifier>();
            foreach (var bufferIdentifier in _registry.SnapshotRegisteredIdentifiers())
            {
                var buffer = _registry.GetBuffer(bufferIdentifier);
                if (_tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size))
                {
                    CollectDirtyPieces(buffer, copies, "shutdown");
                    dirtyBuffers.Add(bufferIdentifier);
                }
            }

            if (copies.Count != 0)
            {
                DownloadBufferMemory(copies);
            }

            foreach (var bufferIdentifier in dirtyBuffers)
            {
                ReleaseDownloaded(bufferIdentifier);
            }

            foreach (var bufferIdentifier in _registry.SnapshotRegisteredIdentifiers())
            {
                var buffer = _registry.GetBuffer(bufferIdentifier);
                _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
                Unregister(bufferIdentifier);
                _registry.CompleteRetirement(bufferIdentifier);
            }

            if (_scheduler.Active)
            {
                _scheduler.Finish();
            }

            Dispose();
            drained = true;
        }
        finally
        {
            lock (_shutdownGate)
            {
                _outcome = drained ? ShutdownOutcome.Drained : ShutdownOutcome.Failed;
                Monitor.PulseAll(_shutdownGate);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Dispose();
        _deviceRing.Dispose();
        _download.Dispose();
        _stream.Dispose();
        _staging.Dispose();
        _bdaPageTable.Dispose();
        _zeroPage?.Dispose(); // [local]
        _gds.Dispose();
        _faults.Dispose();
    }

    // False only when the store closed and its drain failed; the caller then declines the fault.
    private int _storeDownloadLogs; // [local]
    private static readonly bool ReadbackAudit = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_AUDIT") == "1"; // [local]
    private static readonly Dictionary<(GuestMemoryProfile.ReadbackSource, ulong, ulong, int), (long Calls, double Ms)> _readbackAudit = new();
    private static long _readbackAuditReport = System.Diagnostics.Stopwatch.GetTimestamp();

    private bool ReadMemoryOrAwaitShutdown(ulong guestAddress, ulong size, bool isWrite,
        GuestMemoryProfile.ReadbackSource source = GuestMemoryProfile.ReadbackSource.ExplicitReadback)
    {
        if (!ReadbackAudit)
        {
            return ReadMemoryOrAwaitShutdownCore(guestAddress, size, isWrite, source);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = ReadMemoryOrAwaitShutdownCore(guestAddress, size, isWrite, source);
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lock (_readbackAudit)
        {
            var key = (source, Diagnostics.GpuReadTrace.CurrentShader, guestAddress & ~0xFFFFUL, 64 - System.Numerics.BitOperations.LeadingZeroCount(Math.Max(size, 1) - 1));
            var entry = _readbackAudit.GetValueOrDefault(key);
            _readbackAudit[key] = (entry.Calls + 1, entry.Ms + ms);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_readbackAuditReport).TotalSeconds >= 20)
            {
                foreach (var (k, v) in _readbackAudit.OrderByDescending(pair => pair.Value.Ms).Take(15))
                    Console.Error.WriteLine($"[READBACK_AUDIT] ms={v.Ms:F0} calls={v.Calls} source={k.Item1} shader=0x{k.Item2:X16} block=0x{k.Item3:X} size_le=0x{1UL << k.Item4:X}");
                _readbackAudit.Clear();
                _readbackAuditReport = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }

        return result;
    }

    private bool ReadMemoryOrAwaitShutdownCore(ulong guestAddress, ulong size, bool isWrite,
        GuestMemoryProfile.ReadbackSource source)
    {
        if (BvhWatch(guestAddress, size)) Console.Error.WriteLine($"[BVH_DOWNLOAD] address=0x{guestAddress:X} size=0x{size:X} write={isWrite} source={source} tick={_scheduler.CurrentTick}");
        if (!_relay.IsGpuQueueThread && SubmissionScheduler.InDeferredOperation)
        {
            throw SubmissionScheduler.Fatal(
                $"unsupported buffer readback from an asynchronous GPU completion, addr=0x{guestAddress:X16} size=0x{size:X16}");
        }

        if (!_relay.IsGpuQueueThread && AsyncReadback is { } readback && GuestReadsAwaitOffQueue)
        {
            // [local] A download the GPU overwrote meanwhile is retried from this thread, so the
            // GPU queue thread does not block on the new copy; the last attempt retries there.
            var slotWaits = 0;
            for (var attempt = 0; ; attempt++)
            {
                PendingDownload? pending = null;
                var slotsBusy = false;
                var waitForSlot = ReadbackSlotWait && slotWaits < MaxReadbackSlotWaits;
                var relayStarted = GuestReadSplit ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
                long relayEntered = 0;
                var split = source == GuestMemoryProfile.ReadbackSource.StoreDownload && Diagnostics.LabelPollSplit.Measures(guestAddress, size) // [local]
                    ? new Diagnostics.LabelPollSplit.Sample { Started = System.Diagnostics.Stopwatch.GetTimestamp() }
                    : null;
                if (!_relay.TryRunOnGpuQueue(() =>
                    {
                        if (GuestReadSplit) relayEntered = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (split is not null) BeginSplit(split);
                        pending = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending: true, markHot: false, waitForSlot);
                        slotsBusy = _beginSlotsBusy;
                        if (split is not null) EndSplitBegin(split, pending, guestAddress, size);
                    }))
                {
                    return AwaitShutdown();
                }

                if (slotsBusy)
                {
                    // [local] Adapted from 6d963114 by TarkusR: every asynchronous slot was busy. Wait here for one
                    // instead of downloading synchronously on the GPU queue thread, then start over.
                    var slotWaitStarted = Diagnostics.CheapProbe.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    slotWaits++;
                    attempt--;
                    readback.WaitForFreeSlot(1);
                    if (slotWaitStarted != 0) Diagnostics.CheapProbe.NoteSlotWait(System.Diagnostics.Stopwatch.GetTimestamp() - slotWaitStarted);
                    continue;
                }

                if (pending is null)
                {
                    if (split is not null)
                    {
                        split.Applied = true;
                        split.Completed = System.Diagnostics.Stopwatch.GetTimestamp();
                        Diagnostics.LabelPollSplit.Add(split);
                    }

                    return true;
                }

                var waitStarted = GuestReadSplit ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                readback.Wait(pending.Ticket);
                var timelineLabel = Diagnostics.LabelTimeline.Enabled && Diagnostics.LabelLatencyProbe.Overlaps(guestAddress, size); // [local]
                if (timelineLabel) Diagnostics.LabelTimeline.Log('a', _scheduler.Timeline.CompletedTick);
                if (GuestReadSplit) NoteGuestReadSplit(relayStarted, relayEntered, waitStarted, System.Diagnostics.Stopwatch.GetTimestamp());
                var lastAttempt = attempt >= AsyncReadbackRetries;
                var applied = false;
                // [local] SHARPEMU_RELAY_PUMP_IN_WAITS: a completion that cannot record a synchronous retry may also run while
                // the GPU worker waits for the GPU.
                Action complete = () =>
                {
                    if (timelineLabel) Diagnostics.LabelTimeline.Log('b', _scheduler.CurrentTick);
                    applied = CompleteReadMemoryOnGpu(pending, retrySynchronously: lastAttempt);
                };
                if (!(lastAttempt ? _relay.TryRunOnGpuQueue(complete) : _relay.TryRunNestableOnGpuQueue(complete)))
                {
                    return AwaitShutdown();
                }

                if (split is not null)
                {
                    split.Applied = applied;
                    split.Completed = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (split.DoneAt == 0) split.WaitStarted = split.WriterAt = split.AheadAt = split.DoneAt = split.Completed; // an eager download
                    Diagnostics.LabelPollSplit.Add(split);
                }

                if (applied || lastAttempt)
                {
                    return true;
                }

                Interlocked.Increment(ref _reportedAsyncRetried);
            }
        }

        var onQueue = _relay.IsGpuQueueThread;
        if (_relay.TryRunOnGpuQueue(() =>
            {
                if (!onQueue || isWrite || !TryUseEagerReadback(guestAddress, size))
                {
                    _ = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending: false, markHot: onQueue);
                }
            }))
        {
            return true;
        }

        return AwaitShutdown();
    }

    // [local] SHARPEMU_READBACK_WINDOW (bytes, power of two >= 4 KB): the GPU-written bytes one readback brings back
    // around the requested range. GTA V reads 4-16 bytes at a time. 64 KB by default: 512 KB downloaded ~9 MB/frame for
    // 22 KB/s asked for, and 64 KB cut the render thread's guest-memory copy by ~0.6 ms/frame without more waiting.
    private static readonly ulong ReadbackWindowBytes =
        ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_READBACK_WINDOW"), out var window) && window >= 4096 && (window & (window - 1)) == 0
            ? window
            : 64 * 1024;
    private const long HotWindowLifetime = 512;
    private const int MaxEagerReadbacks = 4;
    private readonly Dictionary<ulong, HotWindow> _hotWindows = new();
    private readonly HashSet<ulong> _eagerCandidates = new();
    private readonly List<PendingDownload> _eagerDownloads = new();
    private long _batchSerial;
    private static long _reportedEagerStarted;
    private static long _reportedEagerUsed;
    private static long _reportedEagerStale;

    private readonly record struct HotWindow(ulong Address, ulong Size, long LastHit);

    internal long EagerReadbacksStarted { get; private set; }

    internal long EagerReadbacksUsed { get; private set; }

    private void NoteHotReadback(ulong guestAddress, ulong size)
    {
        _hotWindows[guestAddress & ~(ReadbackWindowBytes - 1)] = new HotWindow(guestAddress, size, _batchSerial);
    }

    private static readonly bool EagerReadbacks = Environment.GetEnvironmentVariable("SHARPEMU_EAGER_READBACK") == "1"; // [local] off by default: rt251noeager 6,26 -> 6,64 FPS

    // Lets the read-ahead tests turn the feature on for one cache without the environment switch.
    internal bool ForceEagerReadbacks { get; set; }

    private bool EagerReadbacksOn => EagerReadbacks || ForceEagerReadbacks;

    private void NoteHotWindowWrite(ulong guestAddress, ulong size)
    {
        if (!EagerReadbacksOn || _hotWindows.Count == 0 || size == 0)
        {
            return;
        }

        var last = (guestAddress + size - 1) & ~(ReadbackWindowBytes - 1);
        for (var key = guestAddress & ~(ReadbackWindowBytes - 1); key <= last; key += ReadbackWindowBytes)
        {
            if (_hotWindows.TryGetValue(key, out var hot) && _batchSerial - hot.LastHit <= HotWindowLifetime &&
                !(MuteStaleEagerWindows && _eagerMute.IsMuted(key, _batchSerial)))
            {
                _eagerCandidates.Add(key);
                HotWritePending = true;
            }
        }
    }

    private bool _hotWritePending;

    public bool HotWritePending
    {
        get => _hotWritePending || (_mirror?.FlushRequested ?? false);
        private set => _hotWritePending = value;
    }

    public void OnBatchSubmitted()
    {
        _batchSerial++;
        HotWritePending = false;
        _mirror?.OnBatchSubmitted(); // [local]
        if (AsyncReadback is not { } readback)
        {
            return;
        }

        for (var index = _eagerDownloads.Count - 1; index >= 0; index--)
        {
            var pending = _eagerDownloads[index];
            if (readback.IsComplete(pending.Ticket))
            {
                _eagerDownloads.RemoveAt(index);
                if (!CompleteReadMemoryOnGpu(pending, retrySynchronously: false))
                {
                    Interlocked.Increment(ref _reportedEagerStale);
                }
            }
        }

        if ((_batchSerial & 1023) == 0)
        {
            foreach (var key in _hotWindows.Where(pair => _batchSerial - pair.Value.LastHit > HotWindowLifetime).Select(pair => pair.Key).ToList())
            {
                _hotWindows.Remove(key);
            }

            _eagerMute.Prune(_batchSerial);
        }

        if (_eagerCandidates.Count == 0)
        {
            return;
        }

        foreach (var key in _eagerCandidates)
        {
            if (_eagerDownloads.Count >= MaxEagerReadbacks)
            {
                break;
            }

            if (_hotWindows.TryGetValue(key, out var hot))
            {
                StartEagerReadback(hot);
            }
        }

        _eagerCandidates.Clear();
    }

    // [local] SHARPEMU_EAGER_MUTE (on by default; only acts with SHARPEMU_EAGER_READBACK=1): a read-ahead window whose
    // eager downloads went stale three times in a row is not read ahead for 4096 batches. 0 keeps reading it ahead.
    private static readonly bool MuteStaleEagerWindows = Environment.GetEnvironmentVariable("SHARPEMU_EAGER_MUTE") != "0";
    private readonly EagerWindowMute _eagerMute = new(strikeLimit: 3, muteBatches: 4096);
    private static long _reportedEagerMuted;

    private void NoteEagerOutcome(PendingDownload pending, bool wasted)
    {
        if (MuteStaleEagerWindows && pending.Eager && _eagerMute.Note(pending.HotKey, wasted, _batchSerial))
        {
            Interlocked.Increment(ref _reportedEagerMuted);
        }
    }

    private void StartEagerReadback(HotWindow hot)
    {
        if (FindOwner(hot.Address, hot.Size) is not { } buffer)
        {
            return;
        }

        var copies = CollectReadbackWindow(buffer, hot.Address, hot.Size, out var windowBegin, out var windowEnd);
        if (copies.Count == 0 || !TryBeginDownloadAsync(copies, out var ticket, out var writeTicks, out _))
        {
            return;
        }

        foreach (var copy in copies)
        {
            copy.Buffer.RetainForeignRead();
        }

        _eagerDownloads.Add(new PendingDownload
        {
            Ticket = ticket,
            Copies = copies,
            WriteTicks = writeTicks,
            WindowBegin = windowBegin,
            WindowEnd = windowEnd,
            GuestAddress = hot.Address,
            Size = hot.Size,
            IsWrite = false,
            Eager = true,
            HotKey = hot.Address & ~(ReadbackWindowBytes - 1),
            Source = GuestMemoryProfile.ReadbackSource.ShaderResourceRead,
            Started = System.Diagnostics.Stopwatch.GetTimestamp(),
        });
        EagerReadbacksStarted++;
        Interlocked.Increment(ref _reportedEagerStarted);
    }

    private PendingDownload? TakeEagerReadback(ulong guestAddress, ulong size)
    {
        for (var index = _eagerDownloads.Count - 1; index >= 0; index--)
        {
            var pending = _eagerDownloads[index];
            if (guestAddress >= pending.WindowBegin && size <= pending.WindowEnd - pending.WindowBegin &&
                guestAddress - pending.WindowBegin <= pending.WindowEnd - pending.WindowBegin - size)
            {
                _eagerDownloads.RemoveAt(index);
                return pending;
            }
        }

        return null;
    }

    private bool TryUseEagerReadback(ulong guestAddress, ulong size)
    {
        if (AsyncReadback is not { } readback || TakeEagerReadback(guestAddress, size) is not { } pending)
        {
            return false;
        }

        readback.Wait(pending.Ticket);
        if (!CompleteReadMemoryOnGpu(pending, retrySynchronously: false))
        {
            Interlocked.Increment(ref _reportedEagerStale);
            return false;
        }

        EagerReadbacksUsed++;
        Interlocked.Increment(ref _reportedEagerUsed);
        return !HasGpuDirtyBytes(guestAddress, size);
    }

    private void AbandonEagerReadbacks()
    {
        if (AsyncReadback is not { } readback)
        {
            return;
        }

        foreach (var pending in _eagerDownloads)
        {
            readback.Wait(pending.Ticket);
            readback.Complete(pending.Ticket, null);
            foreach (var copy in pending.Copies)
            {
                copy.Buffer.ReleaseForeignRead();
            }
        }

        _eagerDownloads.Clear();
        _eagerCandidates.Clear();
    }

    // [local] SHARPEMU_GUEST_READ_SPLIT=1: where a guest thread's readback waits, every 5 s: until the GPU queue
    // thread picks it up (relay), starting the download there (begin), and until the GPU finishes it (gpu).
    private static readonly bool GuestReadSplit = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_READ_SPLIT") == "1";
    private static long _splitCount, _splitRelay, _splitBegin, _splitGpu, _splitReport;

    private static void NoteGuestReadSplit(long started, long entered, long waitStarted, long finished)
    {
        Interlocked.Increment(ref _splitCount);
        Interlocked.Add(ref _splitRelay, entered - started);
        Interlocked.Add(ref _splitBegin, waitStarted - entered);
        Interlocked.Add(ref _splitGpu, finished - waitStarted);
        var last = Interlocked.Read(ref _splitReport);
        if (finished - last < 5 * System.Diagnostics.Stopwatch.Frequency || Interlocked.CompareExchange(ref _splitReport, finished, last) != last)
        {
            return;
        }

        var count = Interlocked.Exchange(ref _splitCount, 0);
        double Ms(ref long ticks) => Interlocked.Exchange(ref ticks, 0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[GUEST_READ_SPLIT] reads={count} relay_ms={Ms(ref _splitRelay):F0} begin_ms={Ms(ref _splitBegin):F0} gpu_ms={Ms(ref _splitGpu):F0}"));
    }

    // [local] SHARPEMU_LABEL_POLL_SPLIT: on the GPU queue thread, before and after the begin of a measured download.
    private bool _splitFlushed;

    private void BeginSplit(Diagnostics.LabelPollSplit.Sample split)
    {
        split.Entered = System.Diagnostics.Stopwatch.GetTimestamp();
        split.Current = _scheduler.CurrentTick;
        split.CompletedTick = _scheduler.Timeline.CompletedTick;
        _splitFlushed = false;
    }

    private void EndSplitBegin(Diagnostics.LabelPollSplit.Sample split, PendingDownload? pending, ulong address, ulong size)
    {
        split.Flushed = _splitFlushed;
        if (pending is null)
        {
            return;
        }

        split.Pending = true;
        split.Eager = pending.Eager;
        split.WaitTick = pending.Ticket.WaitTick;
        split.AheadCount = pending.Ticket.AheadCount;
        split.AheadTick = pending.Ticket.AheadTick;
        split.Pieces = pending.Copies.Count;
        var page = address & ~(TrackerLayout.PageBytes - 1);
        var log = Diagnostics.LabelPollSplit.LogPieces() ? new System.Text.StringBuilder() : null;
        for (var index = 0; index < pending.Copies.Count; index++)
        {
            var copy = pending.Copies[index];
            var tick = index < pending.WriteTicks.Length ? pending.WriteTicks[index] : copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size);
            if (copy.Address < page + TrackerLayout.PageBytes && page < copy.Address + copy.Size)
            {
                split.PagePieces++;
                split.PageTick = Math.Max(split.PageTick, tick);
            }

            if (copy.Address < address + size && address < copy.Address + copy.Size)
            {
                split.BytesPieces++;
                split.BytesTick = Math.Max(split.BytesTick, tick);
            }

            log?.Append(FormattableString.Invariant($" {(long)copy.Address - (long)address:+#;-#;0}+0x{copy.Size:X}@{(long)split.Current - (long)tick}"));
        }

        if (log is not null)
            Console.Error.WriteLine(FormattableString.Invariant($"[LABEL_POLL_PIECES] address=0x{address:X} window=0x{pending.WindowBegin:X}-0x{pending.WindowEnd:X} current={split.Current} completed={split.CompletedTick} flushed={split.Flushed} eager={pending.Eager} pieces(offset+size@lag):{log}"));
        if (!pending.Eager)
            pending.Ticket.Observe = split;
    }

    private static readonly bool GuestReadsAwaitOffQueue =
        Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_GUEST_READBACK") != "0";

    // [local] SHARPEMU_READBACK_SLOT_WAIT=1 (off by default): a guest read that finds every readback slot busy waits
    // off the GPU queue thread for a free one (up to MaxReadbackSlotWaits times 1 ms) instead of downloading
    // synchronously there. Adapted from 6d963114 by TarkusR. Off: GTA V never found the slots busy (0 waits/frame).
    internal static readonly bool ReadbackSlotWait = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_SLOT_WAIT") == "1";
    private const int MaxReadbackSlotWaits = 64;
    // Set by BeginReadMemoryOnGpu on the GPU queue thread: it returned null because every slot was busy and the
    // caller asked to wait for one.
    private bool _beginSlotsBusy;

    // [local] SHARPEMU_ASYNC_READBACK_RETRIES: stale guest readbacks retried off the GPU queue thread (0: old behaviour).
    private static readonly int AsyncReadbackRetries =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_READBACK_RETRIES"), out var retries) && retries >= 0 ? retries : 3;
    private static long _reportedAsyncRetried;

    private bool AwaitShutdown()
    {
        lock (_shutdownGate)
        {
            while (_outcome == ShutdownOutcome.Pending)
            {
                Monitor.Wait(_shutdownGate);
            }

            return _outcome == ShutdownOutcome.Drained;
        }
    }

    private void ReadMemoryOnGpu(ulong guestAddress, ulong size, bool isWrite, GuestMemoryProfile.ReadbackSource source) =>
        _ = BeginReadMemoryOnGpu(guestAddress, size, isWrite, source, allowPending: false);

    internal long PendingReadbacksApplied { get; private set; }

    internal long PendingReadbacksRetried { get; private set; }

    private static long _reportedApplied;
    private static long _reportedRetried;

    public static string TakeAsyncReadbackReport() => FormattableString.Invariant(
        $"[PERF][ASYNC_READBACK] applied={Interlocked.Exchange(ref _reportedApplied, 0)} retried={Interlocked.Exchange(ref _reportedRetried, 0)} async_retried={Interlocked.Exchange(ref _reportedAsyncRetried, 0)} published_pieces={Interlocked.Exchange(ref _reportedPublishedPieces, 0)} stale_pieces={Interlocked.Exchange(ref _reportedStalePieces, 0)} uniform_image_sources={TakeUniformImageSources()} eager_started={Interlocked.Exchange(ref _reportedEagerStarted, 0)} eager_used={Interlocked.Exchange(ref _reportedEagerUsed, 0)} eager_stale={Interlocked.Exchange(ref _reportedEagerStale, 0)} eager_muted={Interlocked.Exchange(ref _reportedEagerMuted, 0)} pumped_in_waits={GpuWorkerRelay.TakePumpedInWaits()}");

    private sealed record PendingDownload
    {
        public required VulkanAsyncReadback.Ticket Ticket { get; init; }
        public required List<DownloadPiece> Copies { get; init; }
        public required ulong[] WriteTicks { get; init; }
        public required ulong WindowBegin { get; init; }
        public required ulong WindowEnd { get; init; }
        public required ulong GuestAddress { get; init; }
        public required ulong Size { get; init; }
        public required bool IsWrite { get; init; }
        public bool Eager { get; init; } // [local]
        public ulong HotKey { get; init; } // [local]
        public required GuestMemoryProfile.ReadbackSource Source { get; init; }
        public required long Started { get; init; }
    }

    private PendingDownload? BeginReadMemoryOnGpu(ulong guestAddress, ulong size, bool isWrite, GuestMemoryProfile.ReadbackSource source,
        bool allowPending, bool markHot = false, bool waitForSlot = false)
    {
        _beginSlotsBusy = false;
        using var readbackScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BufferReadback);
        using var foreignRead = _device.Slabs.BeginForeignRead();
        var readbackStarted = GuestMemoryProfile.ReadbackDetailsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (isWrite && !IsRegionRegistered(guestAddress, size))
        {
            return null;
        }

        if (allowPending && TakeEagerReadback(guestAddress, size) is { } eager)
        {
            return eager with { GuestAddress = guestAddress, Size = size, IsWrite = isWrite, Source = source };
        }

        List<(ulong Address, byte[] Bytes)>? preciseCapture = null;
        if (!isWrite && (PreciseWait || ForcePreciseWait)) // [local]
        {
            var verify = PreciseVerify || ForcePreciseVerify;
            if (TryPreciseRead(guestAddress, size, source, readbackStarted, verify, out preciseCapture) && !verify)
            {
                return null;
            }
        }

        var copies = CollectReadbackWindow(guestAddress, size, out var windowBegin, out var windowEnd);
        if (Diagnostics.LabelTimeline.Enabled && Diagnostics.LabelLatencyProbe.Overlaps(guestAddress, size)) // [local] SHARPEMU_LABEL_TIMELINE
        {
            ulong block = 0, precise = 0;
            foreach (var copy in copies)
            {
                block = Math.Max(block, copy.Buffer.BlockWriteTick(copy.SourceOffset, copy.Size));
                if (GpuWriteHistory.Enabled) precise = Math.Max(precise, GpuWriteHistory.LastWriteTick(copy.Address, copy.Size));
            }

            Diagnostics.LabelTimeline.Log('w', block, precise, _scheduler.CurrentTick, copies.Count);
        }
        if (markHot && copies.Count != 0)
        {
            NoteHotReadback(guestAddress, size);
        }
        if (copies.Count != 0)
            CountReadback(); // [local]
        var slotsBusy = false;
        if (copies.Count != 0 && allowPending && TryBeginDownloadAsync(copies, out var ticket, out var writeTicks, out slotsBusy))
        {
            foreach (var copy in copies)
            {
                copy.Buffer.RetainForeignRead();
            }

            return new PendingDownload
            {
                Ticket = ticket,
                Copies = copies,
                WriteTicks = writeTicks,
                WindowBegin = windowBegin,
                WindowEnd = windowEnd,
                GuestAddress = guestAddress,
                Size = size,
                IsWrite = isWrite,
                Source = source,
                Started = readbackStarted,
            };
        }

        if (slotsBusy && waitForSlot)
        {
            // [local] Nothing was collected into a download yet: the caller waits for a slot and starts over.
            _beginSlotsBusy = true;
            return null;
        }

        var fallbackStarted = slotsBusy && Diagnostics.CheapProbe.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
        if (copies.Count != 0)
        {
            var classStarted = ReadbackClassLog ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // [local]
            var readbackClass = ReadbackClassLog ? ClassifyReadback(guestAddress, size, source) : -1;
            var verify = !isWrite && GpuWriteHistory.Mode == 2 ? BeginPreciseTickCheck(copies[0].Buffer, guestAddress, size) : null; // [local]
            var narrowed = !isWrite && GpuWriteHistory.Narrows && NarrowReadbackWindow(copies, guestAddress, size); // [local]
            byte[]? probeBefore = null; // [local] SHARPEMU_TABLE_DIRTY_PROBE: do the downloaded table bytes change?
            var probeBegin = Math.Max(windowBegin, Diagnostics.TableDirtyProbe.Base);
            if (Diagnostics.TableDirtyProbe.Overlaps(windowBegin, windowEnd - windowBegin))
            {
                probeBefore = new byte[Math.Min(windowEnd, Diagnostics.TableDirtyProbe.Base + Diagnostics.TableDirtyProbe.Bytes) - probeBegin];
                if (!_backing.TryReadBacking(probeBegin, probeBefore)) probeBefore = null;
            }

            DownloadBufferMemory(copies);
            if (probeBefore is not null)
            {
                var probeAfter = new byte[probeBefore.Length];
                if (_backing.TryReadBacking(probeBegin, probeAfter))
                    Diagnostics.TableDirtyProbe.NoteDownload(probeBegin, probeBefore, probeAfter, copies.Count, _scheduler.CurrentTick, _scheduler.Timeline.CompletedTick);
            }
            if (readbackClass >= 0) NoteReadbackClass(readbackClass, System.Diagnostics.Stopwatch.GetTimestamp() - classStarted);
            if (narrowed)
            {
                // [local] Pieces left out stay GPU-dirty, and so do the pages they touch.
                for (var page = windowBegin & ~(TrackerLayout.PageBytes - 1); page < windowEnd; page += TrackerLayout.PageBytes)
                {
                    if (!_gpuModifiedRanges.Overlaps(page, TrackerLayout.PageBytes))
                    {
                        _tracker.ClearGpuDirtyPages(page, TrackerLayout.PageBytes);
                    }
                }
            }
            else
            {
                _tracker.ClearGpuDirtyPages(windowBegin, windowEnd - windowBegin);
            }

            if (verify is not null)
            {
                EndPreciseTickCheck(verify, guestAddress, size);
            }

            if (narrowed && GpuWriteHistory.Mode == 3)
            {
                CheckNarrowedWindow(copies);
            }
        }

        if (isWrite)
        {
            _tracker.MarkCpuDirtyPages(guestAddress, size);
        }

        if (fallbackStarted != 0) Diagnostics.CheapProbe.NoteSlotFallback(System.Diagnostics.Stopwatch.GetTimestamp() - fallbackStarted);
        RecordReadback(windowBegin, windowEnd, isWrite, copies, readbackStarted, source);
        if (preciseCapture is not null) // [local] SHARPEMU_READBACK_PRECISE_WAIT=2
        {
            CheckPreciseCapture(preciseCapture);
        }

        return null;
    }

    // [local] SHARPEMU_EARLY_WRITER_SUBMIT=1: table words the render thread had to read while their writer was still in
    // the batch being recorded are remembered; the next dispatch that writes one of them submits the batch right
    // after it is recorded, so the GPU is already running it when the read comes.
    // =2 (2026-10-13): only words whose last synchronous read changed the value (a real producer -> reader dependency,
    // e.g. 0xB198's 0x1AE362D00 and 0x02F1's +0x8C words) count; words the GPU never changes (0xF43B's blob+0x48,
    // 0x994A's +0x28) no longer submit early.
    internal static readonly int EarlyWriterMode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_EARLY_WRITER_SUBMIT"), out var earlyMode) ? earlyMode : 0;
    internal static readonly bool EarlyWriterSubmit = EarlyWriterMode != 0;
    private readonly HashSet<ulong> _changedReadWords = new();

    internal void NoteSyncTableResult(ulong address, bool changed)
    {
        if (changed)
        {
            if (_changedReadWords.Count >= 4096)
                _changedReadWords.Clear();
            _changedReadWords.Add(address);
        }
        else
        {
            _changedReadWords.Remove(address);
        }
    }
    internal static bool EarlySubmitPending;
    internal static Action? EarlySubmit;
    private const int HotReadWordCount = 16;
    private readonly ulong[] _hotReadWords = new ulong[HotReadWordCount];
    private int _hotReadNext;
    private long _earlySubmits, _earlyReport = System.Diagnostics.Stopwatch.GetTimestamp();

    internal void NoteSyncTableRead(ulong address, ulong size)
    {
        if (!EarlyWriterSubmit)
        {
            return;
        }

        EarlySubmitPending = false;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_earlyReport).TotalSeconds >= 5)
        {
            _earlyReport = System.Diagnostics.Stopwatch.GetTimestamp();
            Console.Error.WriteLine($"[EARLY_WRITER_SUBMIT] submits={_earlySubmits} hot_words={string.Join(",", _hotReadWords.Where(word => word != 0).Select(word => $"0x{word:X}"))}");
            _earlySubmits = 0;
        }

        if (EarlyWriterMode == 2 && !_changedReadWords.Contains(address))
        {
            return;
        }

        if (address == 0 || FindOwner(address, size) is not { } owner ||
            owner.GpuWriteTick(owner.Offset(address), size) < _scheduler.CurrentTick ||
            Array.IndexOf(_hotReadWords, address) >= 0)
        {
            return;
        }

        _hotReadWords[_hotReadNext] = address;
        _hotReadNext = (_hotReadNext + 1) % HotReadWordCount;
    }

    // Only a binding about the size of the word's record counts as its writer: wide bindings (a 40 MB append queue,
    // a whole BVH blob) cover hot words without writing them and would submit after nearly every dispatch.
    private static readonly ulong EarlyWriterMaxBytes = Convert.ToUInt64(Environment.GetEnvironmentVariable("SHARPEMU_EARLY_WRITER_MAX") ?? "100", 16);

    private void NoteHotWordWrite(ulong guestAddress, ulong size)
    {
        if (size > EarlyWriterMaxBytes)
        {
            return;
        }

        foreach (var word in _hotReadWords)
        {
            if (word != 0 && guestAddress <= word && word < guestAddress + size)
            {
                EarlySubmitPending = true;
                return;
            }
        }
    }

    // [local] SHARPEMU_PRECISE_WRITE_TICKS=1: a synchronous read waits for the last writer of the bytes it needs, not
    // for the newest writer anywhere in its window. The window keeps every byte last written by that tick or
    // earlier; only the ranges written after it are cut out (they stay GPU-dirty for a later read). Cutting the
    // window down to the requested bytes instead turned one readback into several (one per header word).
    private long _narrowedReads, _narrowedDropped, _narrowedSplit, _narrowedFallback, _narrowedReport = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly List<(ulong Begin, ulong End)> _writtenAfter = new();
    private const int MaxNarrowedPieces = 64;

    private bool NarrowReadbackWindow(List<DownloadPiece> copies, ulong guestAddress, ulong size)
    {
        var owner = copies[0].Buffer;
        if (!owner.IsInBounds(guestAddress, size))
        {
            return false;
        }

        var needed = owner.GpuWriteTick(owner.Offset(guestAddress), size);
        var begin = ulong.MaxValue;
        var end = 0UL;
        var later = false;
        foreach (var copy in copies)
        {
            begin = Math.Min(begin, copy.Address);
            end = Math.Max(end, copy.Address + copy.Size);
            later |= copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size) > needed;
        }

        ReportNarrowing();
        if (!later)
        {
            return false;
        }

        _writtenAfter.Clear();
        if (!GpuWriteHistory.CollectWrittenAfter(begin, end, needed, _writtenAfter) || _writtenAfter.Count == 0)
        {
            _narrowedFallback++;
            return false;
        }

        _writtenAfter.Sort((left, right) => left.Begin.CompareTo(right.Begin));
        var narrowed = new List<DownloadPiece>(copies.Count + 4);
        foreach (var copy in copies)
        {
            if (copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size) <= needed)
            {
                narrowed.Add(copy);
                continue;
            }

            var cursor = copy.Address;
            var copyEnd = copy.Address + copy.Size;
            var pieces = 0;
            foreach (var (cutBegin, cutEnd) in _writtenAfter)
            {
                if (cutEnd <= cursor || cutBegin >= copyEnd)
                {
                    continue;
                }

                if (cutBegin > cursor)
                {
                    narrowed.Add(copy with { SourceOffset = copy.SourceOffset + (cursor - copy.Address), Address = cursor, Size = cutBegin - cursor });
                    pieces++;
                }

                cursor = Math.Max(cursor, cutEnd);
            }

            if (cursor < copyEnd)
            {
                narrowed.Add(copy with { SourceOffset = copy.SourceOffset + (cursor - copy.Address), Address = cursor, Size = copyEnd - cursor });
                pieces++;
            }

            if (pieces == 0) _narrowedDropped++; else _narrowedSplit++;
        }

        // The requested bytes must all still be downloaded; anything else keeps the whole window.
        var covered = 0UL;
        foreach (var piece in narrowed)
        {
            var overlapBegin = Math.Max(piece.Address, guestAddress);
            var overlapEnd = Math.Min(piece.Address + piece.Size, guestAddress + size);
            if (overlapBegin < overlapEnd) covered += overlapEnd - overlapBegin;
        }

        var requested = 0UL;
        foreach (var copy in copies)
        {
            var overlapBegin = Math.Max(copy.Address, guestAddress);
            var overlapEnd = Math.Min(copy.Address + copy.Size, guestAddress + size);
            if (overlapBegin < overlapEnd) requested += overlapEnd - overlapBegin;
        }

        if (covered != requested || narrowed.Count > MaxNarrowedPieces)
        {
            _narrowedFallback++;
            return false;
        }

        copies.Clear();
        copies.AddRange(narrowed);
        _narrowedReads++;
        return true;
    }

    private void ReportNarrowing()
    {
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_narrowedReport).TotalSeconds >= 5)
        {
            _narrowedReport = System.Diagnostics.Stopwatch.GetTimestamp();
            Console.Error.WriteLine($"[PRECISE_WRITE_TICKS] narrowed_reads={_narrowedReads} dropped_pieces={_narrowedDropped} split_pieces={_narrowedSplit} fallbacks={_narrowedFallback}");
            _narrowedReads = _narrowedDropped = _narrowedSplit = _narrowedFallback = 0;
        }
    }

    // [local] SHARPEMU_READBACK_PRECISE_WAIT=1 (off by default): a render-thread read of a few bytes (shader resources and
    // command memory, <= 256 B) whose GPU-modified bytes were all last written, by the precise write history, in a batch
    // that has finished downloads only those bytes, after only that batch. The ordinary path downloads the 64 KB window
    // after its block ticks, and waits for the batch being recorded whenever a neighbour in a 16 KB block is written
    // there (2D34<-91D5 and 91D5<-71AD: the precise writer finished in 100 % of the reads, the block tick was the
    // current batch in ~95 %). A requested byte whose writer has not finished, a range no single buffer holds, or no
    // recorded writer keeps the ordinary path. The rest of the window stays GPU-modified, and so do its pages.
    internal static readonly bool PreciseWait = GpuWriteHistory.PreciseWaitRequested;

    // [local] SHARPEMU_READBACK_PRECISE_WAIT=2 (verification): the precise copy is only captured; the read then takes the
    // ordinary path (window after the block ticks) and the captured bytes must match what it brought back.
    internal static readonly bool PreciseVerify = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_PRECISE_WAIT") == "2";

    // Lets the precise-read tests turn the path on for one cache without the environment switch.
    internal bool ForcePreciseWait { get; set; }

    internal bool ForcePreciseVerify { get; set; }

    internal long PreciseVerifyChecks { get; private set; }

    internal long PreciseVerifyMismatches { get; private set; }

    private void CheckPreciseCapture(List<(ulong Address, byte[] Bytes)> captured)
    {
        foreach (var (address, bytes) in captured)
        {
            var current = new byte[bytes.Length];
            PreciseVerifyChecks++;
            if (!_backing.TryReadBacking(address, current) || !bytes.AsSpan().SequenceEqual(current))
            {
                if (PreciseVerifyMismatches++ < 60)
                    Console.Error.WriteLine($"[READBACK_PRECISE_WAIT] mismatch address=0x{address:X} size=0x{bytes.Length:X} precise=0x{Convert.ToHexString(bytes)} ordinary=0x{Convert.ToHexString(current)} shader=0x{Diagnostics.GpuReadTrace.CurrentShader:X16}");
            }
        }
    }

    internal long PreciseWaitReads { get; private set; }

    internal long PreciseWaitFallbacks { get; private set; }

    private const int PreciseWaitScan = 8192;

    // [local] SHARPEMU_READBACK_PRECISE_WAIT_SCAN (default 8192): ring entries the requested range's lookup scans back.
    private static readonly int PreciseWaitRequestScan =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_READBACK_PRECISE_WAIT_SCAN"), out var requestScan) && requestScan > 0 ? requestScan : PreciseWaitScan;
    private long _preciseScanExhausted, _preciseNotSubmitted, _preciseInFlight;
    private long _preciseWaitReported, _preciseWaitReport = System.Diagnostics.Stopwatch.GetTimestamp();

    private bool TryPreciseRead(ulong guestAddress, ulong size, GuestMemoryProfile.ReadbackSource source, long started, bool captureOnly,
        out List<(ulong Address, byte[] Bytes)>? captured)
    {
        captured = null;
        if (size == 0 || size > 256 ||
            source is not (GuestMemoryProfile.ReadbackSource.ShaderResourceRead or GuestMemoryProfile.ReadbackSource.CommandMemoryRead) ||
            (SyncReadback ?? AsyncReadback) is not { } readback || !_gpuModifiedRanges.Overlaps(guestAddress, size))
        {
            return false;
        }

        ReportPreciseWait();
        var buffer = _registry.GetBuffer(FindBuffer(guestAddress, size));
        var precise = GpuWriteHistory.Queryable ? GpuWriteHistory.LastWriteTick(guestAddress, size, PreciseWaitRequestScan) : 0;
        if (!buffer.IsInBounds(guestAddress, size) || precise == 0 || precise >= _scheduler.CurrentTick || !_scheduler.IsTickComplete(precise))
        {
            if (GpuWriteHistory.LastScanExhausted) _preciseScanExhausted++;
            else if (precise >= _scheduler.CurrentTick) _preciseNotSubmitted++;
            else if (precise != 0) _preciseInFlight++;
            PreciseWaitFallbacks++;
            return false;
        }

        // [local] The whole readback window, as the ordinary path, but each GPU-modified piece only if its own last writer
        // (precise) finished; a piece with a writer still pending, or none recorded, keeps only the requested bytes of it
        // (finished: checked above) and the rest stays GPU-modified.
        var window = CollectReadbackWindow(buffer, guestAddress, size, out var windowBegin, out var windowEnd);
        var copies = new List<DownloadPiece>(window.Count);
        var waitTick = precise;
        foreach (var piece in window)
        {
            var tick = GpuWriteHistory.LastWriteTick(piece.Address, piece.Size, PreciseWaitScan);
            if (tick != 0 && tick < _scheduler.CurrentTick && _scheduler.IsTickComplete(tick))
            {
                copies.Add(piece);
                waitTick = Math.Max(waitTick, tick);
                continue;
            }

            PreciseWaitPendingPieces++;
            var begin = Math.Max(piece.Address, guestAddress);
            var end = Math.Min(piece.Address + piece.Size, guestAddress + size);
            if (begin < end)
            {
                copies.Add(piece with { SourceOffset = piece.SourceOffset + (begin - piece.Address), Address = begin, Size = end - begin });
            }
        }

        if (copies.Count == 0)
        {
            PreciseWaitFallbacks++;
            return false;
        }

        var pieces = new ReadbackPiece[copies.Count];
        for (var index = 0; index < pieces.Length; index++)
        {
            pieces[index] = new ReadbackPiece(copies[index].Buffer, copies[index].SourceOffset, copies[index].Size);
        }

        if (captureOnly)
        {
            var capture = new List<(ulong Address, byte[] Bytes)>(copies.Count);
            readback.Read(pieces, waitTick, (index, bytes) => capture.Add((copies[index].Address, bytes.ToArray())));
            captured = capture;
            return true;
        }

        readback.Read(pieces, waitTick, (index, bytes) =>
        {
            if (!_backing.TryWriteBacking(copies[index].Address, bytes))
            {
                throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{(ulong)bytes.Length:X16}");
            }
        });
        foreach (var copy in copies)
        {
            RemoveGpuModified(copy.Address, copy.Size);
        }

        // Pages still holding GPU-modified bytes stay GPU-dirty.
        for (var page = windowBegin & ~(TrackerLayout.PageBytes - 1); page < windowEnd; page += TrackerLayout.PageBytes)
        {
            if (!_gpuModifiedRanges.Overlaps(page, TrackerLayout.PageBytes))
            {
                _tracker.ClearGpuDirtyPages(page, TrackerLayout.PageBytes);
            }
        }

        PreciseWaitReads++;
        RecordReadback(windowBegin, windowEnd, isWrite: false, copies, started, source);
        return true;
    }

    internal long PreciseWaitPendingPieces { get; private set; }

    // [local] Every 20 s: the readbacks that downloaded something, ordinary and precise (always on: two counters).
    private static long _ordinaryReadbacks, _preciseReadbacksReported, _readbackCountReport = System.Diagnostics.Stopwatch.GetTimestamp();

    private void CountReadback()
    {
        _ordinaryReadbacks++;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_readbackCountReport).TotalSeconds < 20)
            return;
        _readbackCountReport = System.Diagnostics.Stopwatch.GetTimestamp();
        Console.Error.WriteLine($"[READBACK_COUNT] ordinary={_ordinaryReadbacks} precise={PreciseWaitReads - _preciseReadbacksReported} pending_pieces={PreciseWaitPendingPieces} fallbacks={PreciseWaitFallbacks}");
        _ordinaryReadbacks = 0;
        _preciseReadbacksReported = PreciseWaitReads;
    }

    private void ReportPreciseWait()
    {
        if (!PreciseWait || System.Diagnostics.Stopwatch.GetElapsedTime(_preciseWaitReport).TotalSeconds < 20)
        {
            return;
        }

        _preciseWaitReport = System.Diagnostics.Stopwatch.GetTimestamp();
        Console.Error.WriteLine($"[READBACK_PRECISE_WAIT] reads={PreciseWaitReads} fallbacks={PreciseWaitFallbacks} since_last={PreciseWaitReads - _preciseWaitReported} verify_checks={PreciseVerifyChecks} verify_mismatches={PreciseVerifyMismatches} scan_exhausted={_preciseScanExhausted} not_submitted={_preciseNotSubmitted} in_flight={_preciseInFlight}");
        _preciseWaitReported = PreciseWaitReads;
    }

    // [local] SHARPEMU_PRECISE_WRITE_TICKS=2: the requested bytes are also copied after only their precise writer
    // tick, and compared with what the normal readback (block ticks) brings back.
    private long _preciseChecks, _preciseMismatches, _preciseSkipped, _preciseReport = System.Diagnostics.Stopwatch.GetTimestamp();

    private byte[]? BeginPreciseTickCheck(GpuBuffer buffer, ulong guestAddress, ulong size)
    {
        if (AsyncReadback is not { } readback || size > 256 || !buffer.IsInBounds(guestAddress, size) || !GpuWriteHistory.Enabled)
        {
            return null;
        }

        var offset = buffer.Offset(guestAddress);
        var block = buffer.BlockWriteTick(offset, size);
        var precise = GpuWriteHistory.LastWriteTick(guestAddress, size);
        if (precise == 0 || precise >= block)
        {
            _preciseSkipped++;
            return null;
        }

        byte[]? captured = null;
        readback.Read([new ReadbackPiece(buffer, offset, size)], precise, (_, bytes) => captured = bytes.ToArray());
        return captured;
    }

    // [local] SHARPEMU_PRECISE_WRITE_TICKS=3: the pieces a narrowed window downloaded are copied again after the
    // block ticks (everything recorded so far) and must match what the narrowed readback wrote.
    private long _windowChecks, _windowMismatches, _windowReport = System.Diagnostics.Stopwatch.GetTimestamp();

    private void CheckNarrowedWindow(List<DownloadPiece> copies)
    {
        if (AsyncReadback is not { } readback)
        {
            return;
        }

        var wait = 0UL;
        var total = 0UL;
        var pieces = new ReadbackPiece[copies.Count];
        for (var index = 0; index < copies.Count; index++)
        {
            var copy = copies[index];
            wait = Math.Max(wait, copy.Buffer.BlockWriteTick(copy.SourceOffset, copy.Size));
            pieces[index] = new ReadbackPiece(copy.Buffer, copy.SourceOffset, copy.Size);
            total += copy.Size;
        }

        if (total > 1UL << 20)
        {
            return;
        }

        if (wait >= _scheduler.CurrentTick)
        {
            _scheduler.Flush();
        }

        readback.Read(pieces, wait, (index, bytes) =>
        {
            var copy = copies[index];
            var current = new byte[bytes.Length];
            _windowChecks++;
            if (!_backing.TryReadBacking(copy.Address, current) || !bytes.SequenceEqual(current))
            {
                if (_windowMismatches++ < 40)
                    Console.Error.WriteLine($"[PRECISE_WRITE_TICKS] window mismatch piece=0x{copy.Address:X}+0x{copy.Size:X}");
            }
        });
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_windowReport).TotalSeconds >= 5)
        {
            _windowReport = System.Diagnostics.Stopwatch.GetTimestamp();
            Console.Error.WriteLine($"[PRECISE_WRITE_TICKS] window_checks={_windowChecks} window_mismatches={_windowMismatches}");
        }
    }

    private void EndPreciseTickCheck(byte[] captured, ulong guestAddress, ulong size)
    {
        Span<byte> final = stackalloc byte[(int)size];
        if (_backing.TryReadBacking(guestAddress, final))
        {
            _preciseChecks++;
            if (!final.SequenceEqual(captured))
            {
                if (_preciseMismatches++ < 40)
                    Console.Error.WriteLine($"[PRECISE_WRITE_TICKS] mismatch address=0x{guestAddress:X} size=0x{size:X} precise=0x{Convert.ToHexString(captured)} block=0x{Convert.ToHexString(final)} shader=0x{Diagnostics.GpuReadTrace.CurrentShader:X16}");
            }
        }

        if (System.Diagnostics.Stopwatch.GetElapsedTime(_preciseReport).TotalSeconds >= 5)
        {
            _preciseReport = System.Diagnostics.Stopwatch.GetTimestamp();
            Console.Error.WriteLine($"[PRECISE_WRITE_TICKS] checks={_preciseChecks} mismatches={_preciseMismatches} skipped={_preciseSkipped}");
        }
    }

    // [local] The readback mirror (ReadbackMirror): the bytes of a small read whose last GPU writer was submitted, from
    // the line copies recorded at the end of command buffers. False sends the caller to the readback.
    internal bool TryReadMirrored(ulong address, Span<byte> destination)
    {
        if (_mirror is not { } mirror || destination.Length == 0 || destination.Length > ReadbackMirror.MaxRead || !_relay.IsGpuQueueThread)
        {
            return false;
        }

        var size = (ulong)destination.Length;
        if (!_gpuModifiedRanges.Contains(address, size))
        {
            mirror.NoteMiss(ReadbackMirror.Miss.Partial);
            return false;
        }

        if (FindOwner(address, size) is not { } owner)
        {
            mirror.NoteMiss(ReadbackMirror.Miss.NoOwner);
            return false;
        }

        var needed = owner.GpuWriteTick(owner.Offset(address), size);
        var current = _scheduler.CurrentTick;
        if (needed >= current)
        {
            mirror.NoteMiss(ReadbackMirror.Miss.WriterNotSubmitted);
            return false;
        }

        if (!mirror.TryLocate(address, size, needed, current, out var wait))
        {
            return false;
        }

        var waited = !_scheduler.IsTickComplete(wait);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (waited)
        {
            using var waitScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.GpuCompletionWait);
            _scheduler.Wait(wait);
        }

        _scheduler.WaitForPriorityOperations(wait);
        mirror.Read(address, destination, waited, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        if (ReadbackMirror.Mode == 2)
        {
            Span<byte> mirrored = stackalloc byte[destination.Length];
            destination.CopyTo(mirrored);
            if (!TrySynchronizeCpuRead(address, size, GuestMemoryProfile.ReadbackSource.ShaderResourceRead) || !_guestMemory.TryRead(address, destination))
            {
                return false;
            }

            mirror.NoteCompare(mirrored.SequenceEqual(destination), address, needed, mirrored, destination);
        }

        return true;
    }

    private void RecordMirrorCopies()
    {
        if (_mirror is { } mirror && _relay.IsGpuQueueThread)
        {
            mirror.RecordCopies(new Silk.NET.Vulkan.CommandBuffer(_scheduler.Current.Handle), line => FindOwner(line, 64));
        }
    }

    // [local] SHARPEMU_READBACK_CLASS_LOG=1 (step 3.0 of diseno-espejo-readbacks.md): the render thread's synchronous
    // readbacks of shader and command reads (<= 256 B), by where the requested bytes' last writer is: 0 the command
    // buffer being recorded (needs a submit), 1 submitted and still running, 2 finished. A mirror of hot lines only
    // saves classes 1 and 2; class 0 also needs the writer submitted early. Counts and wall ms per frame window.
    private static readonly bool ReadbackClassLog = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_CLASS_LOG") == "1";
    private readonly long[] _readbackClassCalls = new long[3];
    private readonly long[] _readbackClassTicks = new long[3];
    private long _readbackClassReported = System.Diagnostics.Stopwatch.GetTimestamp();

    private int ClassifyReadback(ulong guestAddress, ulong size, GuestMemoryProfile.ReadbackSource source)
    {
        if (size > 256 || source is not (GuestMemoryProfile.ReadbackSource.ShaderResourceRead or GuestMemoryProfile.ReadbackSource.CommandMemoryRead) ||
            FindOwner(guestAddress, size) is not { } owner)
        {
            return -1;
        }

        var tick = owner.GpuWriteTick(owner.Offset(guestAddress), size);
        return tick >= _scheduler.CurrentTick ? 0 : _scheduler.IsTickComplete(tick) ? 2 : 1;
    }

    private void NoteReadbackClass(int readbackClass, long ticks)
    {
        _readbackClassCalls[readbackClass]++;
        _readbackClassTicks[readbackClass] += ticks;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_readbackClassReported).TotalSeconds < 5)
        {
            return;
        }

        _readbackClassReported = System.Diagnostics.Stopwatch.GetTimestamp();
        var ms = (long value) => value * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[PERF][READBACK_CLASS] current={_readbackClassCalls[0]}/{ms(_readbackClassTicks[0]):F1}ms submitted={_readbackClassCalls[1]}/{ms(_readbackClassTicks[1]):F1}ms finished={_readbackClassCalls[2]}/{ms(_readbackClassTicks[2]):F1}ms"));
        Array.Clear(_readbackClassCalls);
        Array.Clear(_readbackClassTicks);
    }

    // [local] SHARPEMU_READBACK_PIECES (on by default): a finished download publishes every piece whose bytes the GPU
    // has not written again since it started, and keeps only the rewritten pieces GPU-modified; the readback counts
    // as applied once the requested range is clean, and only then is not retried. 0 restores all-or-nothing: one
    // rewritten piece discarded the whole download.
    private static readonly bool PublishCurrentPieces = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_PIECES") != "0";
    private static long _reportedPublishedPieces;
    private static long _reportedStalePieces;

    private bool CompleteReadMemoryOnGpu(PendingDownload pending, bool retrySynchronously) =>
        PublishCurrentPieces ? CompleteCurrentPieces(pending, retrySynchronously) : CompleteWholeDownload(pending, retrySynchronously);

    private bool CompleteCurrentPieces(PendingDownload pending, bool retrySynchronously)
    {
        using var readbackScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BufferReadback);
        var readback = AsyncReadback ?? throw SubmissionScheduler.Fatal("The pending readback lost its queue.");
        try
        {
            var copies = pending.Copies;
            var current = new bool[copies.Count];
            var published = 0;
            for (var index = 0; index < copies.Count; index++)
            {
                current[index] = IsPieceCurrent(pending, index);
                published += current[index] ? 1 : 0;
            }

            var splitSample = pending.Ticket.Observe; // [local] SHARPEMU_LABEL_POLL_SPLIT
            readback.Complete(pending.Ticket, published == 0 ? null : (index, bytes) =>
            {
                if (current[index] && MemCopyProbe.Enabled) MemCopyProbe.Add(MemCopyProbe.Site.ReadbackPieces, bytes.Length); // [local] SHARPEMU_MEMCOPY_PROBE
                if (current[index] && splitSample is not null) // [local] SHARPEMU_LABEL_POLL_SPLIT: does the download change the backing?
                {
                    var before = new byte[bytes.Length];
                    if (_backing.TryReadBacking(copies[index].Address, before))
                    {
                        if (bytes.SequenceEqual(before)) splitSample.SamePieces++;
                        else splitSample.ChangedPieces++;
                    }
                }
                if (current[index] && !TryWriteBackingNoted(copies[index].Address, bytes))
                {
                    throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{(ulong)bytes.Length:X16}");
                }
            });
            for (var index = 0; index < copies.Count; index++)
            {
                if (current[index])
                {
                    RemoveGpuModified(copies[index].Address, copies[index].Size);
                }
            }

            var windowSize = pending.WindowEnd - pending.WindowBegin;
            if (published == copies.Count && !_gpuModifiedRanges.Overlaps(pending.WindowBegin, windowSize))
            {
                _tracker.ClearGpuDirtyPages(pending.WindowBegin, windowSize);
            }
            else
            {
                for (var index = 0; index < copies.Count; index++)
                {
                    if (current[index])
                    {
                        ClearPagesOfPublishedPiece(copies[index].Address, copies[index].Size,
                            page => _gpuModifiedRanges.Overlaps(page, TrackerLayout.PageBytes),
                            (address, size) => _tracker.ClearGpuDirtyPages(address, size));
                    }
                }
            }

            var stale = copies.Count - published;
            Interlocked.Add(ref _reportedPublishedPieces, published);
            Interlocked.Add(ref _reportedStalePieces, stale);
            NoteEagerOutcome(pending, wasted: stale != 0);
            var clean = pending.IsWrite
                ? !_tracker.HasGpuDirtyPages(pending.GuestAddress, pending.Size)
                : !HasGpuDirtyBytes(pending.GuestAddress, pending.Size);
            if (!clean)
            {
                if (retrySynchronously)
                {
                    PendingReadbacksRetried++;
                    Interlocked.Increment(ref _reportedRetried);
                    ReadMemoryOnGpu(pending.GuestAddress, pending.Size, pending.IsWrite, pending.Source);
                }

                return false;
            }

            PendingReadbacksApplied++;
            Interlocked.Increment(ref _reportedApplied);
            if (pending.IsWrite)
            {
                _tracker.MarkCpuDirtyPages(pending.GuestAddress, pending.Size);
            }

            RecordReadback(pending.WindowBegin, pending.WindowEnd, pending.IsWrite, copies, pending.Started, pending.Source);
            return true;
        }
        finally
        {
            foreach (var copy in pending.Copies)
            {
                copy.Buffer.ReleaseForeignRead();
            }
        }
    }

    // [local] After a published piece left the GPU-modified set: the pages wholly inside it are clean, and a page it
    // only partly covers is clean unless another GPU-modified range still overlaps it.
    internal static void ClearPagesOfPublishedPiece(ulong address, ulong size, Func<ulong, bool> pageHasModifiedBytes, Action<ulong, ulong> clearPages)
    {
        if (size == 0)
        {
            return;
        }

        const ulong pageBytes = TrackerLayout.PageBytes;
        var end = address + size;
        var headPage = address & ~(pageBytes - 1);
        var tailPage = (end - 1) & ~(pageBytes - 1);
        var innerBegin = address == headPage ? headPage : headPage + pageBytes;
        var innerEnd = end == tailPage + pageBytes ? end : tailPage;
        if (innerBegin < innerEnd)
        {
            clearPages(innerBegin, innerEnd - innerBegin);
        }

        if (address != headPage && !pageHasModifiedBytes(headPage))
        {
            clearPages(headPage, pageBytes);
        }

        if (end != tailPage + pageBytes && (tailPage != headPage || address == headPage) && !pageHasModifiedBytes(tailPage))
        {
            clearPages(tailPage, pageBytes);
        }
    }

    private bool IsPieceCurrent(PendingDownload pending, int index)
    {
        var copy = pending.Copies[index];
        return copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size) == pending.WriteTicks[index] &&
               _gpuModifiedRanges.Contains(copy.Address, copy.Size) &&
               ReferenceEquals(FindOwner(copy.Address, copy.Size), copy.Buffer);
    }

    private bool CompleteWholeDownload(PendingDownload pending, bool retrySynchronously)
    {
        using var readbackScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BufferReadback);
        var readback = AsyncReadback ?? throw SubmissionScheduler.Fatal("The pending readback lost its queue.");
        try
        {
            var whole = IsPendingDownloadCurrent(pending);
            NoteEagerOutcome(pending, wasted: !whole);
            if (!whole)
            {
                readback.Complete(pending.Ticket, null);
                if (retrySynchronously)
                {
                    PendingReadbacksRetried++;
                    Interlocked.Increment(ref _reportedRetried);
                    ReadMemoryOnGpu(pending.GuestAddress, pending.Size, pending.IsWrite, pending.Source);
                }

                return false;
            }

            PendingReadbacksApplied++;
            Interlocked.Increment(ref _reportedApplied);

            var copies = pending.Copies;
            readback.Complete(pending.Ticket, (index, bytes) =>
            {
                if (MemCopyProbe.Enabled) MemCopyProbe.Add(MemCopyProbe.Site.ReadbackPending, bytes.Length); // [local] SHARPEMU_MEMCOPY_PROBE
                if (!TryWriteBackingNoted(copies[index].Address, bytes))
                {
                    throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{(ulong)bytes.Length:X16}");
                }
            });
            foreach (var copy in copies)
            {
                RemoveGpuModified(copy.Address, copy.Size);
            }

            _tracker.ClearGpuDirtyPages(pending.WindowBegin, pending.WindowEnd - pending.WindowBegin);
            if (pending.IsWrite)
            {
                _tracker.MarkCpuDirtyPages(pending.GuestAddress, pending.Size);
            }

            RecordReadback(pending.WindowBegin, pending.WindowEnd, pending.IsWrite, copies, pending.Started, pending.Source);
            return true;
        }
        finally
        {
            foreach (var copy in pending.Copies)
            {
                copy.Buffer.ReleaseForeignRead();
            }
        }
    }

    private bool IsPendingDownloadCurrent(PendingDownload pending)
    {
        var total = 0UL;
        for (var index = 0; index < pending.Copies.Count; index++)
        {
            var copy = pending.Copies[index];
            if (copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size) != pending.WriteTicks[index] ||
                !_gpuModifiedRanges.Contains(copy.Address, copy.Size) ||
                !ReferenceEquals(FindOwner(copy.Address, copy.Size), copy.Buffer))
            {
                return false;
            }

            total += copy.Size;
        }

        var current = 0UL;
        foreach (var range in _gpuModifiedRanges.GetOverlappingRanges(pending.WindowBegin, pending.WindowEnd - pending.WindowBegin))
        {
            current += range.Size;
        }

        return current == total;
    }

    private static void RecordReadback(ulong windowBegin, ulong windowEnd, bool isWrite, List<DownloadPiece> copies, long started,
        GuestMemoryProfile.ReadbackSource source)
    {
        if (!GuestMemoryProfile.ReadbackDetailsEnabled)
        {
            return;
        }

        var downloadedBytes = 0UL;
        foreach (var copy in copies)
            downloadedBytes += copy.Size;
        GuestMemoryProfile.RecordBufferReadback(windowBegin, windowEnd - windowBegin, isWrite, downloadedBytes,
            System.Diagnostics.Stopwatch.GetTimestamp() - started, source);
    }

    // The GPU-modified ranges to download for a read of the range, widened to a window so
    // nearby CPU reads share one GPU drain.
    private List<DownloadPiece> CollectReadbackWindow(ulong guestAddress, ulong size, out ulong windowBegin, out ulong windowEnd) =>
        CollectReadbackWindow(_registry.GetBuffer(FindBuffer(guestAddress, size)), guestAddress, size, out windowBegin, out windowEnd);

    private List<DownloadPiece> CollectReadbackWindow(GpuBuffer buffer, ulong guestAddress, ulong size, out ulong windowBegin, out ulong windowEnd)
    {
        var windowSize = ReadbackWindowBytes;
        var bufferEnd = buffer.CpuAddress + buffer.Size;
        windowBegin = Math.Max(guestAddress & ~(windowSize - 1), buffer.CpuAddress);
        windowEnd = Math.Min(Math.Max(windowBegin + windowSize, guestAddress + size), bufferEnd);

        var copies = new List<DownloadPiece>();
        _tracker.ForEachDownloadRange(
            windowBegin,
            windowEnd - windowBegin,
            clear: false,
            (address, bytes) => _tracker.ValidateGpuDirtyPages(_gpuModifiedRanges, address, bytes, "memory invalidation"),
            (address, bytes) =>
            {
                foreach (var range in _gpuModifiedRanges.GetOverlappingRanges(address, bytes))
                {
                    copies.Add(new DownloadPiece(buffer, buffer.Offset(range.Address), range.Address, range.Size));
                }
            });
        return copies;
    }

    private void CollectDirtyPieces(GpuBuffer buffer, List<DownloadPiece> copies, string operation)
    {
        _tracker.ForEachDownloadRange(
            buffer.CpuAddress,
            buffer.Size,
            clear: false,
            (address, bytes) => _tracker.ValidateGpuDirtyPages(_gpuModifiedRanges, address, bytes, operation),
            (address, bytes) =>
            {
                foreach (var range in _gpuModifiedRanges.GetOverlappingRanges(address, bytes))
                {
                    copies.Add(new DownloadPiece(buffer, range.Address - buffer.CpuAddress, range.Address, range.Size));
                }
            });
    }

    private void ReleaseDownloaded(ResourceSlotIdentifier bufferIdentifier)
    {
        var buffer = _registry.GetBuffer(bufferIdentifier);
        _tracker.ClearGpuDirtyPages(buffer.CpuAddress, buffer.Size);
        if (_tracker.HasGpuDirtyPages(buffer.CpuAddress, buffer.Size) || _gpuModifiedRanges.Overlaps(buffer.CpuAddress, buffer.Size))
        {
            throw SubmissionScheduler.Fatal("Buffer collection left GPU-owned memory.");
        }

        _tracker.UntrackMemory(buffer.CpuAddress, buffer.Size);
        Unregister(bufferIdentifier);
        _registry.CompleteRetirement(bufferIdentifier);
    }

    // [local] With SHARPEMU_PROFILE_GPU_PASSES: command-processor writes into device buffers, and how many follow the
    // previous one with nothing recorded in between (mergeable under one barrier pair), of those how many continue its
    // range in the same buffer (one copy), and how many rewrite exactly its range (the previous copy is dead).
    private static long _cpWrites, _cpBytes, _cpConsecutive, _cpAdjacent, _cpSameRange, _cpSameRangeAny;
    private long _cpLastSequence = -1;
    private GpuBuffer? _cpLastBuffer;
    private ulong _cpLastAddress, _cpLastEnd;

    private void NoteCommandWrite(GpuBuffer buffer, ulong address, ulong size, long sequenceBefore)
    {
        Interlocked.Increment(ref _cpWrites);
        Interlocked.Add(ref _cpBytes, (long)size);
        var consecutive = sequenceBefore == _cpLastSequence;
        var sameBuffer = ReferenceEquals(buffer, _cpLastBuffer);
        if (consecutive)
        {
            Interlocked.Increment(ref _cpConsecutive);
            if (sameBuffer && address == _cpLastEnd)
                Interlocked.Increment(ref _cpAdjacent);
            if (sameBuffer && address == _cpLastAddress && address + size == _cpLastEnd)
                Interlocked.Increment(ref _cpSameRange);
        }

        if (sameBuffer && address == _cpLastAddress && address + size == _cpLastEnd)
            Interlocked.Increment(ref _cpSameRangeAny);
        _cpLastBuffer = buffer;
        _cpLastAddress = address;
        _cpLastEnd = address + size;
    }

    private static readonly bool SyncvalSelfTest = Environment.GetEnvironmentVariable("SHARPEMU_SYNCVAL_SELFTEST") == "1"; // [local]
    private int _syncvalSelfTests;

    private void WriteDataBuffer(GpuBuffer buffer, ulong address, ReadOnlySpan<byte> source)
    {
        var sequenceBefore = SharpEmu.Libs.Gpu.Vulkan.VulkanCommandProfile.t_opSequence; // [local]
        if (SharpEmu.Libs.Gpu.Vulkan.VulkanCommandProfile.PassesEnabled)
            NoteCommandWrite(buffer, address, (ulong)source.Length, sequenceBefore);
        GuestGpuMemoryHook.Trace(address, (ulong)source.Length, $"host-write-data submission_tick={_scheduler.CurrentTick}"); // [local]
        if (SyncvalSelfTest && _syncvalSelfTests++ < 3) // [local] SHARPEMU_SYNCVAL_SELFTEST
            buffer.RecordSyncvalSelfTest(_scheduler.Current);
        while (!source.IsEmpty)
        {
            var chunk = (int)Math.Min((ulong)source.Length, _staging.Size);
            var offset = _staging.Copy(source[..chunk], 4);
            buffer.CopyFrom(_scheduler.Current, _staging, offset, buffer.Offset(address), (ulong)chunk, AccessFlags.HostWriteBit);
            source = source[chunk..];
            address += (ulong)chunk;
        }

        _cpLastSequence = SharpEmu.Libs.Gpu.Vulkan.VulkanCommandProfile.t_opSequence; // [local]
    }

    private void Register(ResourceSlotIdentifier bufferIdentifier) => UpdateRegistration(bufferIdentifier, insert: true);

    private void Unregister(ResourceSlotIdentifier bufferIdentifier) => UpdateRegistration(bufferIdentifier, insert: false);

    private void UpdateRegistration(ResourceSlotIdentifier bufferIdentifier, bool insert)
    {
        var buffer = _registry.GetBuffer(bufferIdentifier);
        if (!PageOwnerTable.TryGetPageRange(buffer.CpuAddress, buffer.Size, out var first, out var lastExclusive))
        {
            throw SubmissionScheduler.Fatal("The buffer is outside the page table.");
        }

        var sizePages = lastExclusive - first;
        if (GuestGpuMemoryHook.Traces(buffer.CpuAddress, buffer.Size))
            GuestGpuMemoryHook.Trace(buffer.CpuAddress, buffer.Size,
                $"device-address-registration insert={insert} buffer={bufferIdentifier} submission_tick={_scheduler.CurrentTick} collection_tick={_retirementPolicy.CurrentTick}");
        if (insert)
        {
            _registry.RegisterBuffer(bufferIdentifier, _retirementPolicy.CurrentTick);
            var addresses = new ulong[sizePages];
            for (ulong page = 0; page < sizePages; page++)
            {
                addresses[page] = buffer.DeviceAddress + (page << CachingPageBits);
            }

            WriteDataBuffer(_bdaPageTable, first * sizeof(ulong), MemoryMarshal.AsBytes<ulong>(addresses));
        }
        else
        {
            _registry.BeginRetirement(bufferIdentifier);
            _bdaPageTable.Fill(first * sizeof(ulong), sizePages * sizeof(ulong), 0);
        }
    }

    private void TouchBuffer(GpuBuffer buffer)
    {
        var identifier = _registry.FindContainingBuffer(buffer.CpuAddress, buffer.Size);
        if (identifier.IsValid && ReferenceEquals(_registry.GetBuffer(identifier), buffer))
        {
            TouchBuffer(identifier);
        }
    }

    private void TouchBuffer(ResourceSlotIdentifier identifier) =>
        _registry.MarkBufferUsed(identifier, _retirementPolicy.CurrentTick);

    private void DeleteBuffer(ResourceSlotIdentifier bufferIdentifier)
    {
        if (_registry.TryGetRegisteredBuffer(bufferIdentifier) == null)
        {
            return;
        }

        Unregister(bufferIdentifier);
        if (_scheduler.Active)
        {
            _scheduler.QueueCompletionAction(() => _registry.CompleteRetirement(bufferIdentifier));
        }
        else
        {
            _registry.CompleteRetirement(bufferIdentifier);
        }
    }

    // Set when a second queue can copy readbacks; null keeps every readback on the main queue.
    internal VulkanAsyncReadback? AsyncReadback { get; set; }

    // [local] SHARPEMU_SYNC_READBACK_QUEUE=1: the render thread's synchronous readbacks go through their own queue. On the
    // shared queue a copy whose writer finished long ago still waited behind an asynchronous guest-thread copy that waits
    // for the batch being recorded (the queue runs its submissions in order).
    internal VulkanAsyncReadback? SyncReadback { get; set; }

    // [local] SHARPEMU_STORE_TABLE_SYNC: the second readback queue when only store-table reads use it.
    internal VulkanAsyncReadback? TableReadback { get; set; }
    private bool _tableRead;

    // [local] SHARPEMU_STORE_TABLE_SYNC: TrySynchronizeCpuRead whose download goes through the table readback queue
    // (on the GPU queue thread). It still waits for the last writer tick of the pieces, as every readback does.
    public bool TrySynchronizeTableRead(ulong address, ulong size, GuestMemoryProfile.ReadbackSource source)
    {
        _tableRead = TableReadback is not null && _relay.IsGpuQueueThread;
        try
        {
            return TrySynchronizeCpuRead(address, size, source);
        }
        finally
        {
            _tableRead = false;
        }
    }

    private const ulong AsyncReadbackLimit = 64UL << 20;

    // Packs pieces into the download ring, waits for the copy, then writes each through the backing alias.
    private void DownloadBufferMemory(List<DownloadPiece> copies)
    {
        if (Diagnostics.TableDirtyProbe.Enabled && copies.Exists(copy => Diagnostics.TableDirtyProbe.Overlaps(copy.Address, copy.Size))) // [local]
        {
            var reason = (SyncReadback ?? AsyncReadback) is null ? 1 : copies.Exists(copy => copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size) == 0) ? 2 : 0;
            var waitTick = 0UL;
            foreach (var copy in copies) waitTick = Math.Max(waitTick, copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size));
            var current = _scheduler.CurrentTick;
            var completed = _scheduler.Timeline.CompletedTick;
            var probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var asyncDone = TryDownloadAsync(copies);
            var asyncTicks = System.Diagnostics.Stopwatch.GetTimestamp() - probeStarted;
            if (asyncDone)
            {
                foreach (var copy in copies)
                    RemoveGpuModified(copy.Address, copy.Size);
                Diagnostics.TableDirtyProbe.NoteDownloadPath(true, reason, asyncTicks, waitTick, current, completed, copies.Count);
                return;
            }

            DownloadBufferMemorySync(copies);
            Diagnostics.TableDirtyProbe.NoteDownloadPath(false, reason, System.Diagnostics.Stopwatch.GetTimestamp() - probeStarted, waitTick, current, completed, copies.Count);
            return;
        }

        if (TryDownloadAsync(copies))
        {
            foreach (var copy in copies)
            {
                RemoveGpuModified(copy.Address, copy.Size);
            }

            return;
        }

        DownloadBufferMemorySync(copies);
    }

    private void DownloadBufferMemorySync(List<DownloadPiece> copies)
    {
        var batch = new List<PlannedDownload>();
        var planner = new BufferDownloadBatchPlanner(_download.Size);
        foreach (var piece in copies)
        {
            var copy = piece;
            while (copy.Size != 0)
            {
                var placement = planner.Append(copy.SourceOffset, copy.Size, copy.Buffer.Size);
                batch.Add(new PlannedDownload(copy.Buffer, copy.Address, placement));
                copy = copy with
                {
                    SourceOffset = copy.SourceOffset + placement.DataSize,
                    Address = copy.Address + placement.DataSize,
                    Size = copy.Size - placement.DataSize,
                };
                if (planner.IsFull)
                {
                    FlushDownloads(batch, planner.PackedSize);
                    planner.Reset();
                }
            }
        }

        if (batch.Count != 0)
        {
            FlushDownloads(batch, planner.PackedSize);
        }

        foreach (var copy in copies)
        {
            RemoveGpuModified(copy.Address, copy.Size);
        }
    }

    private bool TryBeginDownloadAsync(List<DownloadPiece> copies, out VulkanAsyncReadback.Ticket ticket, out ulong[] writeTicks, out bool slotsBusy)
    {
        ticket = null!;
        writeTicks = [];
        slotsBusy = false;
        if (AsyncReadback is not { } readback || copies.Count == 0)
        {
            return false;
        }

        var waitTick = 0UL;
        var total = 0UL;
        var ticks = new ulong[copies.Count];
        for (var index = 0; index < copies.Count; index++)
        {
            var written = copies[index].Buffer.GpuWriteTick(copies[index].SourceOffset, copies[index].Size);
            if (written == 0)
            {
                return false;
            }

            ticks[index] = written;
            waitTick = Math.Max(waitTick, written);
            total += copies[index].Size;
        }

        if (total > AsyncReadbackLimit)
        {
            return false;
        }

        if (waitTick >= _scheduler.CurrentTick)
        {
            _splitFlushed = true; // [local] SHARPEMU_LABEL_POLL_SPLIT
            _scheduler.Flush();
        }

        var pieces = new ReadbackPiece[copies.Count];
        for (var index = 0; index < pieces.Length; index++)
        {
            pieces[index] = new ReadbackPiece(copies[index].Buffer, copies[index].SourceOffset, copies[index].Size);
        }

        if (!readback.TryBegin(pieces, waitTick, out var started, out slotsBusy) || started is null)
        {
            return false;
        }

        ticket = started;
        writeTicks = ticks;
        return true;
    }

    // Copies the pieces on the readback queue after only the tick that last wrote their
    // buffers, instead of appending the copy to the main queue and draining all of it.
    private static readonly bool ReadbackTickLog = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_TICK_LOG") == "1"; // [local]
    private readonly long[] _readbackTickKinds = new long[3], _readbackTickLag = new long[3], _readbackPageKinds = new long[4];
    private readonly Dictionary<ulong, ulong> _pageWriteTicks = new();
    private long _readbackTickReport;

    private bool TryDownloadAsync(List<DownloadPiece> copies)
    {
        if (((_tableRead ? TableReadback : null) ?? SyncReadback ?? AsyncReadback) is not { } readback || copies.Count == 0)
        {
            return false;
        }

        var waitTick = 0UL;
        var total = 0UL;
        foreach (var copy in copies)
        {
            var written = copy.Buffer.GpuWriteTick(copy.SourceOffset, copy.Size);
            // A GPU-modified range with no recorded writer: keep the conservative path.
            if (written == 0)
            {
                return false;
            }

            waitTick = Math.Max(waitTick, written);
            total += copy.Size;
        }

        if (total > AsyncReadbackLimit)
        {
            return false;
        }

        if (ReadbackTickLog) // [local] where the writer tick of a readback stands
        {
            var completed = _scheduler.Timeline.CompletedTick;
            var kind = waitTick >= _scheduler.CurrentTick ? 0 : waitTick > completed ? 1 : 2;
            var pageTick = 0UL;
            foreach (var copy in copies)
                for (var page = copy.Address >> 12; page <= (copy.Address + copy.Size - 1) >> 12; page++)
                    if (_pageWriteTicks.TryGetValue(page, out var t) && t > pageTick) pageTick = t;
            _readbackPageKinds[pageTick == 0 ? 3 : pageTick >= _scheduler.CurrentTick ? 0 : pageTick > completed ? 1 : 2]++;
            _readbackTickKinds[kind]++;
            _readbackTickLag[kind] += (long)(_scheduler.CurrentTick - Math.Min(waitTick, _scheduler.CurrentTick));
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _readbackTickReport > 5 * System.Diagnostics.Stopwatch.Frequency)
            {
                _readbackTickReport = now;
                Console.Error.WriteLine($"[READBACK_PAGE_TICK] current_batch={_readbackPageKinds[0]} in_flight={_readbackPageKinds[1]} completed={_readbackPageKinds[2]} unknown={_readbackPageKinds[3]}");
                Array.Clear(_readbackPageKinds);
                Console.Error.WriteLine($"[READBACK_TICK] current_batch={_readbackTickKinds[0]} in_flight={_readbackTickKinds[1]} completed={_readbackTickKinds[2]} lag_in_flight={(_readbackTickKinds[1] == 0 ? 0 : _readbackTickLag[1] / _readbackTickKinds[1])} lag_completed={(_readbackTickKinds[2] == 0 ? 0 : _readbackTickLag[2] / _readbackTickKinds[2])}");
                Array.Clear(_readbackTickKinds); Array.Clear(_readbackTickLag);
            }
        }

        // The last writer is still in the buffer being recorded; submit it (without
        // waiting) so the readback queue has a signal to wait for.
        if (waitTick >= _scheduler.CurrentTick)
        {
            _scheduler.Flush();
        }

        var pieces = new ReadbackPiece[copies.Count];
        for (var index = 0; index < pieces.Length; index++)
        {
            pieces[index] = new ReadbackPiece(copies[index].Buffer, copies[index].SourceOffset, copies[index].Size);
        }

        var runCompleted = Vulkan.VulkanCommandProfile.RunEnabled ? _scheduler.Timeline.CompletedTick : 0; // [local] SHARPEMU_PROFILE_GPU_RUN
        var runStarted = Vulkan.VulkanCommandProfile.RunEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        readback.Read(pieces, waitTick, (index, bytes) =>
        {
            if (StoreTableVerify) VerifyExcludedBytes(copies[index].Address, bytes); // [local]
            if (MemCopyProbe.Enabled) MemCopyProbe.Add(MemCopyProbe.Site.ReadbackAsync, bytes.Length); // [local] SHARPEMU_MEMCOPY_PROBE
            if (!TryWriteBackingNoted(copies[index].Address, bytes))
            {
                throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copies[index].Address:X16} size=0x{(ulong)bytes.Length:X16}");
            }
        });
        if (Vulkan.VulkanCommandProfile.RunEnabled &&
            System.Diagnostics.Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds >= Vulkan.VulkanCommandProfile.RunMinMilliseconds)
            Vulkan.VulkanCommandProfile.NoteRun(runCompleted + 1, waitTick);
        return true;
    }

    private void FlushDownloads(List<PlannedDownload> batch, ulong packedSize)
    {
        if (!_download.TryMap(packedSize, out var baseOffset, BufferDownloadBatchPlanner.Alignment))
        {
            throw SubmissionScheduler.Fatal("The download ring could not map the batch.");
        }

        foreach (var copy in batch)
        {
            var placement = copy.Placement;
            _download.CopyFrom(
                _scheduler.Current, copy.Buffer, placement.SourceOffset, baseOffset + placement.DestinationOffset, placement.TransferSize,
                AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
        }

        _download.Commit();
        var completionTick = _scheduler.CurrentTick;
        _scheduler.Finish();
        _scheduler.WaitForPriorityOperations(completionTick);
        foreach (var copy in batch)
        {
            var placement = copy.Placement;
            var offset = baseOffset + placement.DataOffset;
            _download.Invalidate(offset, placement.DataSize);
            if (MemCopyProbe.Enabled) MemCopyProbe.Add(MemCopyProbe.Site.ReadbackSync, (long)placement.DataSize); // [local] SHARPEMU_MEMCOPY_PROBE
            if (!TryWriteBackingNoted(copy.Address, _download.Mapped.Slice((int)offset, (int)placement.DataSize)))
            {
                throw SubmissionScheduler.Fatal($"Could not write the required direct backing: addr=0x{copy.Address:X16} size=0x{placement.DataSize:X16}");
            }
        }

        batch.Clear();
    }

    private OverlapSpan ResolveOverlaps(ulong guestAddress, ulong size)
    {
        var range = new BufferMergeRange(guestAddress, guestAddress + size);
        var first = _registry.FindFirstOverlappingIndex(range.Begin);
        var last = first;
        for (; last < _registry.RegisteredCount && _registry.GetRegisteredAddress(last) < range.End; last++)
        {
            var buffer = _registry.GetBuffer(_registry.GetRegisteredIdentifier(last));
            if (range.IncludeBuffer(buffer.CpuAddress, buffer.CpuAddress + buffer.Size, buffer.StreamScore))
            {
                first = _registry.FindFirstOverlappingIndex(range.Begin);
                if (first < _registry.RegisteredCount)
                {
                    range.IncludeEarlierBuffer(_registry.GetRegisteredAddress(first));
                }
            }
        }

        return new OverlapSpan(first, last, range.Begin, range.End, range.HasStreamExpansion);
    }

    private void MergeOverlappingBuffer(ResourceSlotIdentifier newBufferIdentifier, ResourceSlotIdentifier overlappingBufferIdentifier, bool accumulateStreamScore)
    {
        var newBuffer = _registry.GetBuffer(newBufferIdentifier);
        var overlap = _registry.GetBuffer(overlappingBufferIdentifier);
        if (GuestGpuMemoryHook.Traces(overlap.CpuAddress, overlap.Size))
            GuestGpuMemoryHook.Trace(overlap.CpuAddress, overlap.Size,
                $"device-address-merge old={overlappingBufferIdentifier} replacement={newBufferIdentifier} submission_tick={_scheduler.CurrentTick}");
        if (accumulateStreamScore)
        {
            newBuffer.AddStreamScore(overlap.StreamScore + 1);
        }

        GuestGpuMemoryHook.Trace(overlap.CpuAddress, overlap.Size, $"host-merge-copy new={newBufferIdentifier} submission_tick={_scheduler.CurrentTick}"); // [local]
        newBuffer.CopyFrom(_scheduler.Current, overlap, 0, overlap.CpuAddress - newBuffer.CpuAddress, overlap.Size);
        DeleteBuffer(overlappingBufferIdentifier);
    }

    private ResourceSlotIdentifier CreateBuffer(ulong guestAddress, ulong size)
    {
        if (_scheduler.Current.IsInvalid)
        {
            throw SubmissionScheduler.Fatal("Buffer creation requires a command buffer that is recording.");
        }

        var end = (guestAddress + size + CachingPageSize - 1) & ~(CachingPageSize - 1);
        guestAddress &= ~(CachingPageSize - 1);
        size = end - guestAddress;
        var overlap = ResolveOverlaps(guestAddress, size);
        var overlapping = new List<ResourceSlotIdentifier>();
        for (var index = overlap.First; index < overlap.Last; index++)
        {
            overlapping.Add(_registry.GetRegisteredIdentifier(index));
        }

        var bufferIdentifier = _registry.AllocateBuffer(new GpuBuffer(
            _device, _scheduler, GpuBufferUsage.DeviceLocal, overlap.Begin,
            GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, overlap.End - overlap.Begin, allowSlab: true)
            { GpuWriteObserver = _mirror is { } mirror ? mirror.Invalidate : null }, overlap.Begin, overlap.End - overlap.Begin);
        foreach (var oldId in overlapping)
        {
            MergeOverlappingBuffer(bufferIdentifier, oldId, !overlap.HasStreamLeap);
        }

        Register(bufferIdentifier);
        return bufferIdentifier;
    }

    // Kept apart from SynchronizeBuffer so that its closure is only allocated for buffers with
    // dirty ranges; most calls return before needing it.
    // [local] The state of one CollectUploadRanges call, with the tracker callbacks bound once: the lambdas allocated a
    // closure, two delegates and up to three lists on every synchronization (most of BufferDirtySync's allocation).
    // One per thread; a nested synchronization on the same thread takes a new one.
    private sealed class UploadCollection
    {
        public readonly List<BufferCopy> Collected = new();
        // Handed to a deferred upload job, then replaced.
        public List<TrackedRegion> Pending = new();
        public List<(ulong Address, ulong Offset, int Size)> Reads = new();
        public ulong CollectedSize;
        public GpuBuffer? Source;
        public GpuRingBuffer? Reserved;
        public bool InUse;
        private GuestBufferCache _cache = null!;
        private GpuBuffer _buffer = null!;
        private ulong _guestAddress;
        private ulong _size;
        public readonly Action<ulong, ulong> Range;
        public readonly Action<ulong, ulong> RangeDeferred;
        public readonly Action Upload;
        public readonly Action UploadDeferred;

        public UploadCollection()
        {
            Range = AddRange;
            RangeDeferred = AddRangeDeferred;
            Upload = PrepareUpload;
            UploadDeferred = ReserveUpload;
        }

        public void Begin(GuestBufferCache cache, GpuBuffer buffer, ulong guestAddress, ulong size)
        {
            InUse = true;
            _cache = cache;
            _buffer = buffer;
            _guestAddress = guestAddress;
            _size = size;
            Collected.Clear();
            Pending.Clear();
            Reads.Clear();
            CollectedSize = 0;
            Source = null;
            Reserved = null;
        }

        public void End()
        {
            InUse = false;
            _cache = null!;
            _buffer = null!;
            Source = null;
            Reserved = null;
        }

        private void AddRangeDeferred(ulong address, ulong bytes)
        {
            Collected.Add(new BufferCopy(CollectedSize, _buffer.Offset(address), bytes));
            CollectedSize += bytes;
        }

        private void AddRange(ulong address, ulong bytes)
        {
            Collected.Add(new BufferCopy(CollectedSize, _buffer.Offset(address), bytes));
            CollectedSize += bytes;
            if (UploadDupCheck) _cache.NoteUploadContent(address, bytes); // [local]
        }

        private void ReserveUpload()
        {
            Source = Reserved = _cache._uploader.ReserveSource(_buffer.CpuAddress, CollectionsMarshal.AsSpan(Collected), CollectedSize, Reads);
            if (Reserved == null)
            {
                // No staging for a later copy: protect now, then copy as before.
                GuestPageTracker.ApplyDeferredProtection(Pending);
                Pending.Clear();
                Source = _cache._uploader.PrepareSource(_buffer.CpuAddress, CollectionsMarshal.AsSpan(Collected), CollectedSize, _guestAddress, _size);
            }
        }

        private void PrepareUpload() =>
            Source = _cache._uploader.PrepareSource(_buffer.CpuAddress, CollectionsMarshal.AsSpan(Collected), CollectedSize, _guestAddress, _size);
    }

    [ThreadStatic]
    private static UploadCollection? _uploadCollection;

    // The copies stay in the returned collection until ReleaseUploadCollection.
    private UploadCollection CollectUploadRanges(GpuBuffer buffer, ulong guestAddress, ulong size, bool isWritten,
        bool preserveCpuWriteHotPages)
    {
        var collection = _uploadCollection is { InUse: false } free ? free : new UploadCollection();
        _uploadCollection ??= collection;
        collection.Begin(this, buffer, guestAddress, size);
        try
        {
            if (_deferredUploads is { } deferred && !isWritten) // [local]
            {
                var pending = collection.Pending;
                _tracker.ForEachUploadRange(
                    guestAddress,
                    size,
                    isWritten,
                    collection.RangeDeferred,
                    collection.UploadDeferred,
                    preserveCpuWriteHotPages,
                    pending);
                if (collection.Reserved is { } reserved)
                {
                    deferred.Enqueue(new DeferredUploadWorker.Job(pending, _uploader, reserved, collection.Reads));
                    collection.Pending = new List<TrackedRegion>();
                    collection.Reads = new List<(ulong Address, ulong Offset, int Size)>();
                }
                else if (pending.Count != 0)
                {
                    GuestPageTracker.ApplyDeferredProtection(pending);
                }

                return collection;
            }

            _tracker.ForEachUploadRange(
                guestAddress,
                size,
                isWritten,
                collection.Range,
                collection.Upload,
                preserveCpuWriteHotPages);
            return collection;
        }
        catch
        {
            collection.End();
            throw;
        }
    }

    // [local experiment] SHARPEMU_UPLOAD_DUP_CHECK=1: per 4 KB page, how many uploaded bytes equal what
    // the previous upload of that page carried (a shadow copy per page), reported every 5 s.
    private static readonly bool UploadDupCheck = Environment.GetEnvironmentVariable("SHARPEMU_UPLOAD_DUP_CHECK") == "1";
    private readonly Dictionary<ulong, byte[]> _uploadShadow = new();
    private long _dupBytes, _uploadBytes, _dupPages, _uploadPages, _dupReport;

    private void NoteUploadContent(ulong address, ulong bytes)
    {
        const ulong Page = 4096;
        var end = address + bytes;
        Span<byte> current = stackalloc byte[(int)Page];
        for (var page = address & ~(Page - 1); page < end; page += Page)
        {
            var begin = Math.Max(page, address);
            var stop = Math.Min(page + Page, end);
            var length = (int)(stop - begin);
            var slice = current[..length];
            if (!_guestMemory.TryRead(begin, slice)) continue;
            if (!_uploadShadow.TryGetValue(page, out var shadow))
                _uploadShadow[page] = shadow = new byte[Page];
            var offset = (int)(begin - page);
            var same = slice.SequenceEqual(shadow.AsSpan(offset, length));
            if (same) { _dupBytes += length; _dupPages++; }
            _uploadBytes += length; _uploadPages++;
            slice.CopyTo(shadow.AsSpan(offset, length));
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _dupReport > 5 * System.Diagnostics.Stopwatch.Frequency)
        {
            _dupReport = now;
            Console.Error.WriteLine($"[UPLOAD_DUP] bytes={_uploadBytes} dup_bytes={_dupBytes} pages={_uploadPages} dup_pages={_dupPages} shadow_pages={_uploadShadow.Count}");
            _dupBytes = _uploadBytes = _dupPages = _uploadPages = 0;
        }
    }

    private bool SynchronizeBuffer(GpuBuffer buffer, ulong guestAddress, ulong size, bool isWritten, bool isTexelBuffer,
        bool preserveCpuWriteHotPages = true)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferDirtySynchronization);
        var startedAt = BufferUploadProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if ((!preserveCpuWriteHotPages && !isWritten && !isTexelBuffer && !_tracker.MayHaveCpuDirtyPages(guestAddress, size)) ||
            (isWritten && !isTexelBuffer && _gpuModifiedRanges.Contains(guestAddress, size) &&
             (!WrittenSyncCpuDirty || !_tracker.MayHaveCpuDirtyPages(guestAddress, size))))
        {
            if (BufferUploadProfile.Enabled)
                BufferUploadProfile.Record(guestAddress, size, 0, 0, 0, System.Diagnostics.Stopwatch.GetTimestamp() - startedAt);
            return false;
        }

        profileScope.SwitchPhase(isWritten
            ? RenderPhaseProfile.Phase.BufferDirtySyncWritten
            : isTexelBuffer ? RenderPhaseProfile.Phase.BufferDirtySyncTexel : RenderPhaseProfile.Phase.BufferDirtySyncUpload);
        // [local] SHARPEMU_TEXEL_CLEAN_SKIP (opt-in =1, =2 verify, 0 by default): a texel read of a range without CPU-dirty
        // pages takes the same shortcut as any other read (first test above) for its upload, which has nothing to
        // copy, and still synchronizes from an image below. =2 runs the upload anyway and counts the ones that copied.
        // Off by default: 0 mismatches in both scenes, but panning lost ~2 FPS with it on (pdX: 26.7 without it,
        // 21.9 with it, the rest of the per-draw trims on in both); the skipped region visit is not free of effects.
        var texelClean = TexelCleanSkipMode != 0 && isTexelBuffer && !isWritten && !preserveCpuWriteHotPages &&
            !_tracker.MayHaveCpuDirtyPages(guestAddress, size);
        if (texelClean && TexelCleanSkipMode != 2)
        {
            _texelCleanSkips++;
            return SynchronizeTexelFromImage(buffer, guestAddress, size);
        }

        var collection = CollectUploadRanges(buffer, guestAddress, size, isWritten, preserveCpuWriteHotPages);
        if (texelClean)
        {
            _texelCleanChecked++;
            if (collection.Source != null || collection.Collected.Count != 0)
            {
                _texelCleanMismatches++;
                if (_texelCleanLogs++ < 20)
                    Console.Error.WriteLine($"[GPU][TEXEL_CLEAN_MISMATCH] range=0x{guestAddress:X}+0x{size:X} copies={collection.Collected.Count} bytes=0x{collection.CollectedSize:X}");
            }
        }

        try
        {
            var copies = collection.Collected;
            var totalSize = collection.CollectedSize;
            var source = collection.Source;
            if (source != null)
            {
                _syncUploads++; // [local] written-obtain memo verification
                if (Rendering.BatchSimulation.Enabled) Rendering.BatchSimulation.NoteUpload(); // [local]
                foreach (var copy in copies)
                    buffer.NoteGpuWrite(copy.DstOffset, copy.Size);
                var command = _scheduler.Current;
                command.EndRendering();
                var native = new CommandBuffer(command.Handle);
                var vk = _device.Vk;
                var before = new BufferMemoryBarrier2
                {
                    SType = StructureType.BufferMemoryBarrier2,
                    SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit | AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
                    DstAccessMask = AccessFlags2.TransferWriteBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = buffer.Handle,
                    Offset = 0,
                    Size = buffer.Size,
                };
                VulkanSynchronization.PipelineBarrier(vk,
                    native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
                    0, null, 1, &before, 0, null);
                var regions = CollectionsMarshal.AsSpan(copies);
                if (GuestGpuMemoryHook.TraceEnabled) // [local]
                    foreach (var copy in regions)
                        GuestGpuMemoryHook.Trace(buffer.CpuAddress + copy.DstOffset, copy.Size, $"host-upload-sync written={isWritten} preserve={preserveCpuWriteHotPages} binding=0x{guestAddress:X}+0x{size:X} submission_tick={_scheduler.CurrentTick}");
                fixed (BufferCopy* pointer = regions)
                {
                    var vkProbe2631 = SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Start(); // [local]
                    SharpEmu.Libs.Gpu.Vulkan.ScopeBarrierTracker.BeginSelfOrdered(); // [local] the upload's barrier below orders it
                    SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdCopyBuffer(vk, native, source.Handle, buffer.Handle, (uint)regions.Length, pointer); // [local]
                    SharpEmu.Libs.Gpu.Vulkan.ScopeBarrierTracker.EndSelfOrdered();
                    SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Stop(SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Kind.CopyBuffer, vkProbe2631); // [local]
                }

                var after = before;
                after.SrcAccessMask = AccessFlags2.TransferWriteBit;
                after.DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
                VulkanSynchronization.PipelineBarrier(vk,
                    native, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit,
                    0, null, 1, &after, 0, null);
            }

            if (BufferUploadProfile.Enabled)
            {
                var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
                ulong hotBytes = 0;
                foreach (var copy in copies)
                    hotBytes += _tracker.CountCpuWriteHotBytes(buffer.CpuAddress + copy.DstOffset, copy.Size);
                BufferUploadProfile.Record(guestAddress, size, copies.Count, totalSize, hotBytes, elapsedTicks);
            }
        }
        finally
        {
            collection.End();
        }

        if (isTexelBuffer)
        {
            return SynchronizeTexelFromImage(buffer, guestAddress, size);
        }

        return false;
    }

    private bool SynchronizeTexelFromImage(GpuBuffer buffer, ulong guestAddress, ulong size)
    {
        var copiedFromImage = RequireImageCache().TrySynchronizeBufferFromImage(buffer, guestAddress, size);
        if (copiedFromImage) GuestGpuMemoryHook.Trace(guestAddress, size, $"host-copy-from-image submission_tick={_scheduler.CurrentTick}"); // [local]
        if (copiedFromImage)
        {
            buffer.NoteGpuWrite(buffer.Offset(guestAddress), size);
        }

        return copiedFromImage;
    }

    internal static readonly int TexelCleanSkipMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEXEL_CLEAN_SKIP"), out var texelSkip) ? texelSkip : 0;
    private static long _texelCleanSkips, _texelCleanChecked, _texelCleanMismatches;
    private static int _texelCleanLogs;

    // [local] SHARPEMU_TEXEL_CLEAN_SKIP: skips, verified and mismatched texel uploads since the last report; the
    // mismatch total is kept.
    internal static string TakeTexelCleanReport() => FormattableString.Invariant(
        $"texel_clean_skips={Interlocked.Exchange(ref _texelCleanSkips, 0)} texel_clean_checked={Interlocked.Exchange(ref _texelCleanChecked, 0)} texel_clean_mismatches_total={Interlocked.Read(ref _texelCleanMismatches)}");

    private GpuBuffer? FindOwner(ulong guestAddress, ulong size)
    {
        return _registry.TryGetRegisteredBuffer(_registry.FindContainingBuffer(guestAddress, size));
    }

    private IGuestImageCache RequireImageCache() =>
        ImageCache ?? throw SubmissionScheduler.Fatal("The image cache is not connected.");

    private static bool IsValidRange(ulong guestAddress, ulong size) => guestAddress != 0 && size != 0 && new GuestSpan(guestAddress, size).IsValid;

}
