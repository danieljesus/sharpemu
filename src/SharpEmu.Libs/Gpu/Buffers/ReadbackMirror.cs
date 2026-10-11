// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_READBACK_MIRROR=1 (diseno-espejo-readbacks.md, after Kyty 269c9e96): host-visible copies of the
// 64-byte lines of GPU-written memory the render thread keeps reading back. A line turns hot on its first read; from
// the next submit on, the end of every command buffer copies its dirty hot lines into one download buffer. A read whose
// last writer was submitted then waits only for the tick of that copy and reads the line instead of a readback.
// Mode 2 also reads back and compares. Render thread only.
internal sealed unsafe class ReadbackMirror(GpuDeviceInfo device, SubmissionScheduler scheduler)
{
    public static readonly int Mode = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_READBACK_MIRROR"), out var mode) ? mode : 0;
    // SHARPEMU_READBACK_MIRROR_FLUSH=1: a write of a line read within the last FlushLifetime reads asks for the
    // batch to be submitted before the next draw or dispatch, so that read finds its writer submitted.
    public static readonly bool FlushHotWrites = Environment.GetEnvironmentVariable("SHARPEMU_READBACK_MIRROR_FLUSH") == "1";
    private const long FlushLifetime = 4096;
    public const int MaxRead = 256;
    private const ulong LineSize = 64;
    private const int MaxLines = 2048;
    private const int PageShift = 12;

    private sealed class Line
    {
        public int Slot;
        public ulong CopyTick;
        public long LastUse;
        public bool Queued;
    }

    internal enum Miss { NoLine, Stale, WriterNotSubmitted, NoOwner, Partial, Count }

    private readonly Dictionary<ulong, Line> _lines = new();
    private readonly Dictionary<ulong, int> _pages = new();
    private readonly List<ulong> _queued = new();
    private readonly Stack<int> _freeSlots = new(Enumerable.Range(0, MaxLines).Reverse());
    private GpuBuffer? _host;
    private long _uses;
    private readonly long[] _misses = new long[(int)Miss.Count];
    private long _flushes;
    private long _hits, _hitsNoWait, _waitTicks, _copies, _evictions, _invalidations, _mismatches, _compared;
    private long _reported = System.Diagnostics.Stopwatch.GetTimestamp();

    public void NoteMiss(Miss reason)
    {
        _misses[(int)reason]++;
        Report();
    }

    // Every line of the range has a copy at least as new as needed, recorded in a submitted command buffer; wait is the
    // newest such copy. A missing line is registered and a stale one queued again, for the next submit.
    public bool TryLocate(ulong address, ulong size, ulong needed, ulong currentTick, out ulong wait)
    {
        wait = 0;
        var hit = true;
        for (var line = address & ~(LineSize - 1); line < address + size; line += LineSize)
        {
            if (!_lines.TryGetValue(line, out var entry))
            {
                Add(line);
                NoteMiss(Miss.NoLine);
                hit = false;
                continue;
            }

            entry.LastUse = ++_uses;
            if (entry.CopyTick == 0 || entry.CopyTick < needed || entry.CopyTick >= currentTick)
            {
                Queue(line, entry);
                if (hit) NoteMiss(Miss.Stale);
                hit = false;
                continue;
            }

            wait = Math.Max(wait, entry.CopyTick);
        }

        return hit;
    }

    public void Read(ulong address, Span<byte> destination, bool waited, long waitTicks)
    {
        var host = _host!;
        var end = address + (ulong)destination.Length;
        for (var line = address & ~(LineSize - 1); line < end; line += LineSize)
        {
            var slotOffset = (ulong)_lines[line].Slot * LineSize;
            host.Invalidate(slotOffset, LineSize);
            var begin = Math.Max(line, address);
            var stop = Math.Min(line + LineSize, end);
            host.Mapped.Slice((int)(slotOffset + begin - line), (int)(stop - begin)).CopyTo(destination[(int)(begin - address)..]);
        }

        _hits++;
        if (!waited) _hitsNoWait++;
        _waitTicks += waitTicks;
        Report();
    }

    public void NoteCompare(bool equal, ulong address, ulong needed, ReadOnlySpan<byte> mirrored, ReadOnlySpan<byte> real)
    {
        _compared++;
        if (equal)
        {
            return;
        }

        if (_mismatches++ < 40)
        {
            Console.Error.WriteLine($"[MIRROR_MISMATCH] address=0x{address:X} needed={needed} tick={scheduler.CurrentTick} mirror={Convert.ToHexString(mirrored)} real={Convert.ToHexString(real)}");
        }
    }

    // A GPU write of the range makes its lines stale.
    public void Invalidate(ulong address, ulong size)
    {
        if (_lines.Count == 0 || size == 0)
        {
            return;
        }

        var firstPage = address >> PageShift;
        var lastPage = (address + size - 1) >> PageShift;
        if (lastPage - firstPage > 64)
        {
            foreach (var (line, entry) in _lines)
            {
                if (line < address + size && address < line + LineSize)
                {
                    Stale(line, entry);
                }
            }

            return;
        }

        for (var page = firstPage; page <= lastPage; page++)
        {
            if (!_pages.ContainsKey(page))
            {
                continue;
            }

            var pageBase = page << PageShift;
            var from = Math.Max(pageBase, address & ~(LineSize - 1));
            var to = Math.Min(pageBase + (1UL << PageShift), address + size);
            for (var line = from; line < to; line += LineSize)
            {
                if (_lines.TryGetValue(line, out var entry))
                {
                    Stale(line, entry);
                }
            }
        }
    }

    public bool FlushRequested { get; private set; }

    public void OnBatchSubmitted()
    {
        if (FlushRequested)
        {
            _flushes++;
            FlushRequested = false;
        }
    }

    private void Stale(ulong line, Line entry)
    {
        if (FlushHotWrites && _uses - entry.LastUse < FlushLifetime)
        {
            FlushRequested = true;
        }

        entry.CopyTick = 0;
        _invalidations++;
        Queue(line, entry);
    }

    // Records the queued lines' copies at the end of the command buffer being submitted.
    public void RecordCopies(CommandBuffer command, Func<ulong, GpuBuffer?> findOwner)
    {
        if (_queued.Count == 0)
        {
            return;
        }

        _host ??= new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.TransferDstBit, MaxLines * LineSize);
        var bySource = new Dictionary<GpuBuffer, List<BufferCopy>>();
        var tick = scheduler.CurrentTick;
        foreach (var line in _queued)
        {
            if (!_lines.TryGetValue(line, out var entry))
            {
                continue;
            }

            entry.Queued = false;
            if (findOwner(line) is not { } owner)
            {
                NoteMiss(Miss.NoOwner);
                continue;
            }

            if (!bySource.TryGetValue(owner, out var regions))
            {
                bySource[owner] = regions = new List<BufferCopy>();
            }

            regions.Add(new BufferCopy(owner.Offset(line), (ulong)entry.Slot * LineSize, LineSize));
            entry.CopyTick = tick;
        }

        _queued.Clear();
        if (bySource.Count == 0)
        {
            return;
        }

        var vk = device.Vk;
        var before = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.TransferBit,
            DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
        };
        var beforeDependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &before };
        vk.CmdPipelineBarrier2(command, &beforeDependency);
        foreach (var (source, regions) in bySource)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(regions);
            fixed (BufferCopy* pointer = span)
            {
                vk.CmdCopyBuffer(command, source.Handle, _host.Handle, (uint)span.Length, pointer);
            }

            _copies += span.Length;
        }

        var after = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        var afterDependency = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &after };
        vk.CmdPipelineBarrier2(command, &afterDependency);
    }

    private void Add(ulong line)
    {
        if (_freeSlots.Count == 0)
        {
            EvictHalf();
        }

        var entry = new Line { Slot = _freeSlots.Pop(), LastUse = ++_uses };
        _lines[line] = entry;
        var page = line >> PageShift;
        _pages[page] = _pages.GetValueOrDefault(page) + 1;
        Queue(line, entry);
    }

    private void Queue(ulong line, Line entry)
    {
        if (!entry.Queued)
        {
            entry.Queued = true;
            _queued.Add(line);
        }
    }

    // The least recently read half goes; their queued copies are skipped because the lookup misses.
    private void EvictHalf()
    {
        var victims = _lines.OrderBy(pair => pair.Value.LastUse).Take(_lines.Count / 2).ToList();
        foreach (var (line, entry) in victims)
        {
            _lines.Remove(line);
            _freeSlots.Push(entry.Slot);
            var page = line >> PageShift;
            if (--_pages[page] == 0)
            {
                _pages.Remove(page);
            }
        }

        _evictions += victims.Count;
    }

    private void Report()
    {
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_reported).TotalSeconds < 5)
        {
            return;
        }

        _reported = System.Diagnostics.Stopwatch.GetTimestamp();
        Console.Error.WriteLine(FormattableString.Invariant(
            $"[PERF][MIRROR] hits={_hits} hits_no_wait={_hitsNoWait} wait_ms={_waitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} no_line={_misses[0]} stale={_misses[1]} writer_not_submitted={_misses[2]} no_owner={_misses[3]} partial={_misses[4]} lines={_lines.Count} copies={_copies} evictions={_evictions} invalidations={_invalidations} compared={_compared} mismatches={_mismatches} flushes={_flushes}"));
        _hits = _hitsNoWait = _waitTicks = _copies = _evictions = _invalidations = _compared = _flushes = 0;
        Array.Clear(_misses);
    }
}
