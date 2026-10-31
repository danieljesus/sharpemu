// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] Host side of the ray query work for GTA V's reflections pass 0x8395 (see Gen5SpirvTranslator.RayQuery.cs).
//
// SHARPEMU_RQ_MAP=1 (probe): after every 0x8395 dispatch the map and counters of the fault buffer's ray query area are
// read back and cleared. The BLASes entered ([GPU][RQ_MAP] per 64 dispatches) are kept by base; the header (256 bytes)
// of each new one is read once ([GPU][RQ_BLAS]). Every compute binding the guest writes is checked against the known
// blobs (the writers of each blob, by program hash and by frame, a frame being one 0x8395 dispatch), and one blob per
// frame is read twice, in that frame and the next one, to tell by content which blobs change from frame to frame
// ([GPU][RQ_CHANGE]). SHARPEMU_RQ_DUMP=<dir> (1: %TEMP%/gta/rqblobs): the first SHARPEMU_RQ_DUMP_COUNT (12) change checks also write both
// reads to files.
internal sealed partial class RayQueryHle
{
    public const ulong Hash8395 = 0x8395E43F382309DF;
    public static RayQueryHle? Instance;
    public static bool Enabled => DeviceAddressPaging.RqAreaEnabled;

    private const int Ring = 8;
    private const ulong MaxBlobBytes = 32UL << 20;
    private static readonly string? DumpDirectory = Environment.GetEnvironmentVariable("SHARPEMU_RQ_DUMP") is { Length: > 0 } dump
        ? dump == "1" ? Path.Combine(Path.GetTempPath(), "gta", "rqblobs") : dump
        : null;
    private static readonly int DumpCount = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RQ_DUMP_COUNT"), out var count) ? count : 12;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly GuestBufferCache _cache;
    private readonly GpuBuffer _statsDownload;
    private int _nextSlot;
    private readonly object _lock = new();

    internal sealed class Blob
    {
        public ulong Base;
        public ulong Size;
        public uint Root;
        public uint[]? Header;
        public bool HeaderRequested;
        public long Lanes, Subgroups, Dispatches;
        public long FirstFrame, LastFrame;
        public readonly Dictionary<ulong, (long Writes, long Frames, long LastFrame)> Writers = new();
        public long Checks, ChangedNextFrame, ChangedSinceLast;
        public ulong LastHash;
        // SHARPEMU_RT_RAYQUERY
        public AsState State;
        public string? FailReason;
        public RayQueryBlas.Geometry? Geometry;
        public long ReadWriteSeq, LastWriteSeq, WriteFrames, LastWriteFrame;
        public BuiltAs? Built;
        // SHARPEMU_BLAS_CYCLE
        public long MaxSteps;
        public long GraphChecks, LastGraphFrame;
    }

    private readonly Dictionary<ulong, Blob> _blobs = new();
    private Blob[] _sorted = [];
    private ulong _maxSize;
    private long _frame;
    private int _roundRobin;
    private int _dumps;
    private Check? _pendingCheck;
    private bool _dumpThis;
    private long _subgroups, _lanes, _nonBoxRoots, _overflows, _windowDispatches, _windowBlobs, _windowMaxBlobs;

    public RayQueryHle(GpuDeviceInfo device, SubmissionScheduler scheduler, GuestBufferCache cache)
    {
        _device = device;
        _scheduler = scheduler;
        _cache = cache;
        _statsDownload = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, Ring * DeviceAddressPaging.RqStatsBytes);
        Console.Error.WriteLine($"[GPU][RQ] mode={DeviceAddressPaging.RayQueryMode} map={DeviceAddressPaging.RqMapProbe} area=0x{DeviceAddressPaging.RqAreaOffset:X}+0x{DeviceAddressPaging.RqAreaBytes:X}");
    }

    // A compute dispatch whose program is known, before it is recorded: true when the host recorded work of its own
    // (the caller rebinds its state).
    public bool NoteDispatch(ulong hash)
    {
        if (hash != Hash8395)
            return false;
        GuestBufferCache.PostDispatchHooks.Add(After8395);
        return DeviceAddressPaging.RayQueryMode != 0 && RayQueryDevice.Enabled && Before8395();
    }

    // A buffer range a guest program writes (compute bindings and planned device-address ranges).
    public void NoteWrite(ulong hash, ulong address, ulong size)
    {
        if (size == 0 || hash == Hash8395)
            return;
        lock (_lock)
        {
            var sorted = _sorted;
            if (sorted.Length == 0)
                return;
            var end = address + size;
            var low = 0;
            var high = sorted.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (sorted[middle].Base < end) low = middle + 1; else high = middle;
            }

            for (var index = low - 1; index >= 0 && sorted[index].Base + _maxSize > address; index--)
            {
                var blob = sorted[index];
                if (blob.Base + blob.Size <= address)
                    continue;
                var (writes, frames, last) = blob.Writers.GetValueOrDefault(hash);
                blob.Writers[hash] = (writes + 1, last == _frame ? frames : frames + 1, _frame);
                NoteBlobWritten(blob);
            }
        }
    }

    private void After8395()
    {
        var faults = _cache.FaultBuffer;
        _scheduler.EndRendering();
        var slot = (ulong)(_nextSlot++ % Ring);
        var offset = slot * DeviceAddressPaging.RqStatsBytes;
        _statsDownload.CopyFrom(_scheduler.Current, faults, DeviceAddressPaging.RqMapOffset, offset, DeviceAddressPaging.RqStatsBytes,
            destinationAfter: AccessFlags.HostReadBit);
        faults.Fill(DeviceAddressPaging.RqMapOffset, DeviceAddressPaging.RqStatsBytes, 0);
        var frame = ++_frame;
        _scheduler.QueueCompletionAction(() =>
        {
            _statsDownload.Invalidate(offset, DeviceAddressPaging.RqStatsBytes);
            ParseStats(MemoryMarshal.Cast<byte, uint>(_statsDownload.Mapped.Slice((int)offset, (int)DeviceAddressPaging.RqStatsBytes)), frame);
        });
        RequestHeaders();
        RequestGraphChecks(frame);
        if (DeviceAddressPaging.RayQueryMode != 0)
            RequestBlobReads();
        else if (!CycleOnly)
            RequestChangeCheck(frame);
    }

    private void ParseStats(ReadOnlySpan<uint> words, long frame)
    {
        var counters = words[(int)(DeviceAddressPaging.RqMapSlots * DeviceAddressPaging.RqSlotWords)..];
        lock (_lock)
        {
            var distinct = 0;
            var added = false;
            for (var slot = 0; slot < DeviceAddressPaging.RqMapSlots; slot++)
            {
                var entry = words.Slice(slot * (int)DeviceAddressPaging.RqSlotWords, (int)DeviceAddressPaging.RqSlotWords);
                if (entry[0] == 0)
                    continue;
                distinct++;
                var key = (ulong)entry[0] << 8;
                if (!_blobs.TryGetValue(key, out var blob))
                {
                    _blobs[key] = blob = new Blob { Base = key, FirstFrame = frame };
                    added = true;
                }

                blob.Lanes += entry[1];
                if (CycleOnly)
                    blob.MaxSteps = Math.Max(blob.MaxSteps, entry[2]);
                else
                    blob.Subgroups += entry[2];
                blob.Root = entry[3];
                blob.Dispatches++;
                blob.LastFrame = frame;
            }

            NoteQueryCounters(counters, frame);
            _subgroups += counters[0];
            _overflows += counters[1];
            _lanes += counters[2];
            _nonBoxRoots += counters[3];
            _windowDispatches++;
            _windowBlobs += distinct;
            _windowMaxBlobs = Math.Max(_windowMaxBlobs, distinct);
            if (added)
                Resort();
            if (_windowDispatches == 64 && CycleOnly)
            {
                _windowDispatches = _windowBlobs = _windowMaxBlobs = _subgroups = _lanes = _nonBoxRoots = _overflows = 0;
            }
            else if (_windowDispatches == 64)
            {
                Console.Error.WriteLine($"[GPU][RQ_MAP] frame={frame} dispatches={_windowDispatches} blas_per_dispatch={(double)_windowBlobs / _windowDispatches:F1} max={_windowMaxBlobs} known={_blobs.Count} subgroups_per_dispatch={_subgroups / _windowDispatches} lanes_per_dispatch={_lanes / _windowDispatches} nonbox_root_subgroups={_nonBoxRoots} overflows={_overflows}");
                _windowDispatches = _windowBlobs = _windowMaxBlobs = _subgroups = _lanes = _nonBoxRoots = _overflows = 0;
            }

            if (frame % 1024 == 0 && _blobs.Count != 0)
                Report(frame);
        }
    }

    private void Resort()
    {
        _sorted = [.. _blobs.Values.Where(blob => blob.Size != 0).OrderBy(blob => blob.Base)];
        _maxSize = _sorted.Length == 0 ? 0 : _sorted.Max(blob => blob.Size);
    }

    private void Report(long frame)
    {
        foreach (var blob in _blobs.Values.OrderByDescending(blob => blob.Lanes))
        {
            var writers = string.Join(",", blob.Writers.OrderByDescending(pair => pair.Value.Frames).Take(8)
                .Select(pair => $"{pair.Key >> 48:X4}:{pair.Value.Writes}/{pair.Value.Frames}f"));
            Console.Error.WriteLine($"[GPU][RQ_BLOB] frame={frame} base=0x{blob.Base:X} size=0x{blob.Size:X} root=0x{blob.Root:X} frames={blob.FirstFrame}-{blob.LastFrame} dispatches={blob.Dispatches} lanes={blob.Lanes} subgroups={blob.Subgroups} max_steps={blob.MaxSteps} state={blob.State}{(blob.FailReason is { } reason ? $"/{reason}" : "")} write_frames={blob.WriteFrames} checks={blob.Checks} changed_next_frame={blob.ChangedNextFrame} changed_since_last={blob.ChangedSinceLast} writers=[{writers}]");
        }
    }

    private void RequestHeaders()
    {
        List<Blob> wanted;
        lock (_lock)
            wanted = _blobs.Values.Where(blob => !blob.HeaderRequested).Take(16).ToList();
        foreach (var blob in wanted)
        {
            blob.HeaderRequested = true;
            Read(blob.Base, 256, bytes =>
            {
                var header = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
                lock (_lock)
                {
                    blob.Header = header;
                    blob.Size = BlobSize(header);
                    Resort();
                }

                Console.Error.WriteLine($"[GPU][RQ_BLAS] base=0x{blob.Base:X} size=0x{blob.Size:X} root=0x{blob.Root:X} header=[{string.Join(" ", header.Take(48).Select(word => word.ToString("X8")))}]");
                if (DeviceAddressPaging.BlasCycleMode != 0)
                    RequestGraphCheck(blob);
            });
        }
    }

    // The blob's extent from its header: the total size at +0x10, or the pair records' end (+0x58 records of 8 bytes).
    internal static ulong BlobSize(ReadOnlySpan<uint> header)
    {
        var total = (ulong)header[4];
        var pairs = (ulong)header[0x58 / 4] * 8;
        var size = Math.Max(total, pairs);
        return size is >= 0x100 and <= MaxBlobBytes ? (size + 255) & ~255UL : 0;
    }

    // One blob per frame, read now and in the next frame: changed from one frame to the next, and since its last check.
    // Completion actions run in tick order, so the first read's hash is known when the second one completes.
    private sealed class Check
    {
        public required Blob Blob;
        public ulong First;
    }

    private void RequestChangeCheck(long frame)
    {
        if (_pendingCheck is { } pending)
        {
            _pendingCheck = null;
            ReadHashed(pending.Blob, frame, "b", hash =>
            {
                lock (_lock)
                {
                    pending.Blob.Checks++;
                    if (hash != pending.First) pending.Blob.ChangedNextFrame++;
                }
            });
            return;
        }

        Blob blob;
        lock (_lock)
        {
            if (_sorted.Length == 0)
                return;
            blob = _sorted[_roundRobin++ % _sorted.Length];
        }

        var check = new Check { Blob = blob };
        _pendingCheck = check;
        ReadHashed(blob, frame, "a", hash =>
        {
            lock (_lock)
            {
                check.First = hash;
                if (blob.LastHash != 0 && blob.LastHash != hash) blob.ChangedSinceLast++;
                blob.LastHash = hash;
            }
        });
    }

    private void ReadHashed(Blob blob, long frame, string stage, Action<ulong> done)
    {
        if (stage == "a")
            _dumpThis = DumpDirectory is { Length: > 0 } && _dumps++ < DumpCount;
        var dump = _dumpThis;
        Read(blob.Base, blob.Size, bytes =>
        {
            if (dump)
            {
                try
                {
                    Directory.CreateDirectory(DumpDirectory!);
                    File.WriteAllBytes(Path.Combine(DumpDirectory!, $"rqblob_{blob.Base:X}_{frame}_{stage}.bin"), bytes.ToArray());
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"[GPU][RQ] dump failed: {error.Message}");
                }
            }

            done(XxHash64.HashToUInt64(bytes));
        });
    }

    private void Read(ulong address, ulong size, Action<ReadOnlySpan<byte>> done)
    {
        if (size == 0)
            return;
        var (buffer, offset) = _cache.ObtainBuffer(address, size, isWritten: false);
        var download = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, size);
        _scheduler.EndRendering();
        download.CopyFrom(_scheduler.Current, buffer, offset, 0, size, destinationAfter: AccessFlags.HostReadBit);
        _scheduler.QueueCompletionAction(() =>
        {
            download.Invalidate(0, size);
            done(download.Mapped[..(int)size]);
            download.Dispose();
        });
    }
}
