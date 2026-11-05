// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace SharpEmu.HLE.GuestMemory;

// [local] SHARPEMU_BVHL_PROBE=1 (off by default): hunts the writer of the "PSR_BVHL" qword that GTA V's crash at
// RIP 0x80296B758 reads out of a free node of its 16-byte descriptor pool (globals at 0x8052A69F8: block base, then
// the free-list head at +8, the node size at +0x10, the capacity at +0x14, the nodes in use at +0x18 and the peak at
// +0x1C). Two sides:
//   host: every emulator write into guest memory (backing views, managed writes, copies) that overlaps the pool or
//         its globals is logged with its site and kept in a ring, and the first qword of every write of 256 bytes or
//         more is compared with the magic (a BVH header lands through the emulator: CP write, DMA, readback, file read).
//   guest: a thread reads the pool every 50 ms and reports the first node whose first qword is the magic, with the
//         bytes that changed since the previous scan (the size of the clobber), the free-list walk and the ring.
// SHARPEMU_BVHL_POOL=<hex base>+<hex bytes> watches a fixed range instead of the globals (other builds of the game).
public static class BvhlProbe
{
    public const ulong Magic = 0x4C4856425F525350; // "PSR_BVHL"
    public const ulong Globals = 0x8052A69F8;
    private const int GlobalsBytes = 0x40;
    private const int ScanMs = 50;
    private const int RingSize = 512;
    private const int HeaderWriteMinimum = 256;

    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_BVHL_PROBE") == "1";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static ICpuMemory? _memory;
    private static Thread? _scanner;
    private static ulong _fixedBase, _fixedBytes;

    private static ulong _poolBase, _poolEnd;
    private static long _writesPool, _writesGlobals, _headerWrites, _scans, _hits;
    private static readonly ConcurrentDictionary<string, long> SiteCounts = new();
    private static readonly ConcurrentDictionary<string, long> HeaderSites = new();

    private readonly record struct Entry(long Ms, string Site, ulong Address, ulong Size, int Thread, int MagicOffset, string? Stack);

    private static readonly Entry[] Ring = new Entry[RingSize];
    private static long _ringNext;
    private static readonly Entry[] HeaderRing = new Entry[64];
    private static long _headerNext;

    static BvhlProbe()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_BVHL_POOL")?.Split('+') is [var b, var n])
        {
            _fixedBase = Convert.ToUInt64(b, 16);
            _fixedBytes = Convert.ToUInt64(n, 16);
        }
    }

    public static long Ms => Clock.ElapsedMilliseconds;

    // The memory the scanner reads the pool through (the game's; tests never enable the probe).
    public static void Register(ICpuMemory memory)
    {
        if (!Enabled)
            return;
        lock (Gate)
        {
            _memory = memory;
            if (_scanner is null)
            {
                _scanner = new Thread(ScanLoop) { IsBackground = true, Name = "SharpEmu-BvhlProbe" };
                _scanner.Start();
                Console.Error.WriteLine($"[BVHL] probe armed ms={Ms} fixed=0x{_fixedBase:X}+0x{_fixedBytes:X}");
            }
        }
    }

    // A host write of data at address: pool/global overlap and a header-shaped first qword.
    public static void NoteWrite(string site, ulong address, ReadOnlySpan<byte> data)
    {
        if (!Enabled || data.IsEmpty)
            return;
        var size = (ulong)data.Length;
        if (data.Length >= HeaderWriteMinimum && BinaryPrimitives.ReadUInt64LittleEndian(data) == Magic)
            NoteHeader(site, address, size);
        var (begin, end) = Overlap(address, size);
        if (begin == end)
            return;
        var magic = -1;
        for (var offset = (int)(begin - address) & ~7; offset + 8 <= data.Length && address + (ulong)offset < end; offset += 8)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)) == Magic)
            {
                magic = offset;
                break;
            }
        }

        Record(site, address, size, magic);
    }

    // A host copy that already landed at address (the bytes are read back from the guest).
    public static void NoteCopy(string site, ulong address, ulong size)
    {
        if (!Enabled || size == 0)
            return;
        var memory = _memory;
        if (size >= HeaderWriteMinimum && memory is not null)
        {
            Span<byte> head = stackalloc byte[8];
            if (memory.TryRead(address, head) && BinaryPrimitives.ReadUInt64LittleEndian(head) == Magic)
                NoteHeader(site, address, size);
        }

        var (begin, end) = Overlap(address, size);
        if (begin == end)
            return;
        var magic = -1;
        if (memory is not null)
        {
            var bytes = new byte[end - begin];
            if (memory.TryRead(begin, bytes))
            {
                var found = FindMagic(bytes);
                if (found >= 0)
                    magic = (int)(begin - address) + found;
            }
        }

        Record(site, address, size, magic);
    }

    public static int FindMagic(ReadOnlySpan<byte> bytes)
    {
        for (var offset = 0; offset + 8 <= bytes.Length; offset += 8)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8)) == Magic)
                return offset;
        }

        return -1;
    }

    private static (ulong Begin, ulong End) Overlap(ulong address, ulong size)
    {
        var end = address + size;
        var poolBase = Volatile.Read(ref _poolBase);
        var poolEnd = Volatile.Read(ref _poolEnd);
        if (poolBase != 0 && address < poolEnd && poolBase < end)
        {
            Interlocked.Increment(ref _writesPool);
            return (Math.Max(address, poolBase), Math.Min(end, poolEnd));
        }

        if (_fixedBase == 0 && address < Globals + GlobalsBytes && Globals < end)
        {
            Interlocked.Increment(ref _writesGlobals);
            return (Math.Max(address, Globals), Math.Min(end, Globals + GlobalsBytes));
        }

        return (0, 0);
    }

    private static void Record(string site, ulong address, ulong size, int magic)
    {
        var count = SiteCounts.AddOrUpdate(site, 1, static (_, value) => value + 1);
        string? stack = count <= 3 || magic >= 0 ? Environment.StackTrace : null;
        var entry = new Entry(Ms, site, address, size, Environment.CurrentManagedThreadId, magic, stack);
        var slot = Interlocked.Increment(ref _ringNext) - 1;
        Ring[slot % RingSize] = entry;
        if (count <= 3 || magic >= 0 || count % 1000 == 0)
        {
            Console.Error.WriteLine($"[BVHL] host-write site={site} n={count} ms={entry.Ms} addr=0x{address:X}+0x{size:X} thread={entry.Thread} magic_offset={(magic >= 0 ? "0x" + magic.ToString("X", CultureInfo.InvariantCulture) : "none")}" +
                (stack is not null ? Environment.NewLine + stack : ""));
            if (magic >= 0)
                Console.Error.Flush();
        }
    }

    private static void NoteHeader(string site, ulong address, ulong size)
    {
        Interlocked.Increment(ref _headerWrites);
        var count = HeaderSites.AddOrUpdate(site, 1, static (_, value) => value + 1);
        var slot = Interlocked.Increment(ref _headerNext) - 1;
        HeaderRing[slot % HeaderRing.Length] = new Entry(Ms, site, address, size, Environment.CurrentManagedThreadId, 0, null);
        if (count <= 2)
            Console.Error.WriteLine($"[BVHL] header-write site={site} n={count} ms={Ms} addr=0x{address:X}+0x{size:X} thread={Environment.CurrentManagedThreadId}{Environment.NewLine}{Environment.StackTrace}");
    }

    private static void ScanLoop()
    {
        byte[]? previous = null;
        var current = Array.Empty<byte>();
        var globals = new byte[GlobalsBytes];
        var lastHeartbeat = 0L;
        var located = false;
        ulong lastHitNode = 0;
        while (true)
        {
            Thread.Sleep(ScanMs);
            var memory = _memory;
            if (memory is null)
                continue;
            try
            {
                ulong head = 0, poolBase;
                uint capacity, used = 0, peak = 0, nodeSize = 16;
                if (_fixedBase != 0)
                {
                    poolBase = _fixedBase;
                    capacity = (uint)(_fixedBytes / 16);
                }
                else
                {
                    if (!memory.TryRead(Globals, globals))
                        continue;
                    poolBase = BinaryPrimitives.ReadUInt64LittleEndian(globals);
                    head = BinaryPrimitives.ReadUInt64LittleEndian(globals.AsSpan(8));
                    nodeSize = BinaryPrimitives.ReadUInt32LittleEndian(globals.AsSpan(0x10));
                    capacity = BinaryPrimitives.ReadUInt32LittleEndian(globals.AsSpan(0x14));
                    used = BinaryPrimitives.ReadUInt32LittleEndian(globals.AsSpan(0x18));
                    peak = BinaryPrimitives.ReadUInt32LittleEndian(globals.AsSpan(0x1C));
                }

                if (poolBase == 0 || nodeSize != 16 || capacity == 0 || capacity > (1u << 24))
                    continue;
                var bytes = (int)capacity * 16;
                if (current.Length != bytes)
                {
                    current = new byte[bytes];
                    previous = null;
                }

                if (!memory.TryRead(poolBase, current))
                {
                    if (!located)
                        Console.Error.WriteLine($"[BVHL] pool unreadable base=0x{poolBase:X} bytes=0x{bytes:X} ms={Ms}");
                    continue;
                }

                if (Volatile.Read(ref _poolBase) != poolBase || Volatile.Read(ref _poolEnd) != poolBase + (ulong)bytes)
                {
                    Volatile.Write(ref _poolEnd, poolBase + (ulong)bytes);
                    Volatile.Write(ref _poolBase, poolBase);
                    Console.Error.WriteLine($"[BVHL] pool base=0x{poolBase:X} bytes=0x{bytes:X} capacity={capacity} head=0x{head:X} used={used} peak={peak} ms={Ms}");
                    located = true;
                }

                var scans = Interlocked.Increment(ref _scans);
                var hit = FindMagic(current);
                var walk = WalkFreeList(current, poolBase, head, capacity);
                if (hit >= 0 || walk.Problem is not null)
                {
                    var node = hit >= 0 ? poolBase + (ulong)hit : walk.BadNode;
                    if (node != lastHitNode)
                    {
                        lastHitNode = node;
                        var hits = Interlocked.Increment(ref _hits);
                        if (hits <= 20)
                            ReportHit(poolBase, head, used, peak, capacity, hit, walk, current, previous);
                    }
                }
                else
                {
                    lastHitNode = 0;
                }

                previous ??= new byte[bytes];
                current.AsSpan().CopyTo(previous);
                if (Ms - lastHeartbeat >= 60_000)
                {
                    lastHeartbeat = Ms;
                    Console.Error.WriteLine($"[BVHL] alive ms={Ms} scans={scans} used={used} peak={peak} capacity={capacity} free_walk={walk.Steps} writes_pool={Interlocked.Read(ref _writesPool)} writes_globals={Interlocked.Read(ref _writesGlobals)} header_writes={Interlocked.Read(ref _headerWrites)} hits={Interlocked.Read(ref _hits)} sites=[{Summary(SiteCounts)}] header_sites=[{Summary(HeaderSites)}]");
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[BVHL] scanner error: {exception.GetType().Name}: {exception.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    public readonly record struct FreeWalk(int Steps, string? Problem, ulong BadNode);

    // Follows the free list inside the block: every node 16-aligned and inside, no more steps than nodes.
    public static FreeWalk WalkFreeList(ReadOnlySpan<byte> block, ulong poolBase, ulong head, uint capacity)
    {
        var node = head;
        var steps = 0;
        while (node != 0)
        {
            if (node < poolBase || node >= poolBase + (ulong)block.Length || (node & 15) != 0)
                return new FreeWalk(steps, "node outside the block or unaligned", node);
            if (steps > capacity)
                return new FreeWalk(steps, "cycle", node);
            node = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice((int)(node - poolBase), 8));
            steps++;
        }

        return new FreeWalk(steps, null, 0);
    }

    private static void ReportHit(ulong poolBase, ulong head, uint used, uint peak, uint capacity, int hit, FreeWalk walk, byte[] current, byte[]? previous)
    {
        var text = new StringBuilder();
        var node = hit >= 0 ? poolBase + (ulong)hit : walk.BadNode;
        text.Append($"[BVHL] HIT ms={Ms} node=0x{node:X} slot={(hit >= 0 ? hit / 16 : -1)} magic={(hit >= 0)} head=0x{head:X} used={used} peak={peak} capacity={capacity} free_walk={walk.Steps} problem={walk.Problem ?? "none"} bad=0x{walk.BadNode:X}");
        text.AppendLine();
        if (previous is not null)
        {
            var first = -1;
            var last = -1;
            var changed = 0;
            for (var index = 0; index < current.Length; index++)
            {
                if (current[index] != previous[index])
                {
                    changed++;
                    if (first < 0)
                        first = index;
                    last = index;
                }
            }

            text.Append($"[BVHL] diff since previous scan: bytes={changed} first=0x{(first >= 0 ? poolBase + (ulong)first : 0):X} last=0x{(last >= 0 ? poolBase + (ulong)last : 0):X}");
            text.AppendLine();
            if (hit >= 0)
            {
                // The clobber around the node: how far the changed bytes run on both sides of it.
                var lo = hit;
                while (lo > 0 && current[lo - 1] != previous[lo - 1]) lo--;
                var hi = hit;
                while (hi < current.Length && current[hi] != previous[hi]) hi++;
                text.Append($"[BVHL] changed run around the node: 0x{poolBase + (ulong)lo:X}..0x{poolBase + (ulong)hi:X} ({hi - lo} bytes)");
                text.AppendLine();
            }
        }

        if (hit >= 0)
        {
            var from = Math.Max(0, hit - 64);
            var to = Math.Min(current.Length, hit + 256);
            text.Append($"[BVHL] bytes 0x{poolBase + (ulong)from:X}: {Convert.ToHexString(current, from, to - from)}");
            text.AppendLine();
            if (previous is not null)
            {
                text.Append($"[BVHL] before 0x{poolBase + (ulong)from:X}: {Convert.ToHexString(previous, from, to - from)}");
                text.AppendLine();
            }
        }

        text.Append($"[BVHL] host writes over the pool (last {RingSize}, newest last):");
        text.AppendLine();
        var next = Interlocked.Read(ref _ringNext);
        for (var index = Math.Max(0, next - RingSize); index < next; index++)
        {
            var entry = Ring[index % RingSize];
            text.Append($"  ms={entry.Ms} site={entry.Site} addr=0x{entry.Address:X}+0x{entry.Size:X} thread={entry.Thread} magic_offset={entry.MagicOffset}");
            text.AppendLine();
        }

        text.Append($"[BVHL] header-shaped host writes (last {HeaderRing.Length}):");
        text.AppendLine();
        var headerNext = Interlocked.Read(ref _headerNext);
        for (var index = Math.Max(0, headerNext - HeaderRing.Length); index < headerNext; index++)
        {
            var entry = HeaderRing[index % HeaderRing.Length];
            text.Append($"  ms={entry.Ms} site={entry.Site} addr=0x{entry.Address:X}+0x{entry.Size:X} thread={entry.Thread}");
            text.AppendLine();
        }

        Console.Error.Write(text.ToString());
        Console.Error.Flush();
    }

    private static string Summary(ConcurrentDictionary<string, long> counts) =>
        string.Join("; ", counts.ToArray().OrderByDescending(pair => pair.Value).Take(8).Select(pair => $"{pair.Key}:{pair.Value}"));
}
