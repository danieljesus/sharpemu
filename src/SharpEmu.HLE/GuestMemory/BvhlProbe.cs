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
//         its globals is logged with its site and kept in a ring; for data writes the bytes about to land are compared
//         with the guest's (how many change, how many free-list links are overwritten) and searched for the magic.
//         The first qword of every write of 256 bytes or more is compared with the magic too (a BVH header lands
//         through the emulator: CP write, DMA, readback, file read).
//   guest: a thread reads the pool every 50 ms and reports the first node whose first qword is the magic, with the
//         bytes that changed since the previous scan (the size of the clobber), the free-list walk and the ring.
// SHARPEMU_BVHL_POOL=<hex base>+<hex bytes> watches a fixed range instead of the globals (other builds of the game).
public static class BvhlProbe
{
    public const ulong Magic = 0x4C4856425F525350; // "PSR_BVHL"
    public const ulong Globals = 0x8052A69F8;
    private const int GlobalsBytes = 0x40;
    private const int ScanMs = 10;
    private const int RingSize = 512;
    private const int HeaderWriteMinimum = 256;
    private const int MagicReports = 50;
    private const int WalkReports = 10;

    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_BVHL_PROBE") == "1";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static ICpuMemory? _memory;
    private static Thread? _scanner;
    private static ulong _fixedBase, _fixedBytes;

    private static ulong _poolBase, _poolEnd;
    private static long _writesPool, _writesGlobals, _headerWrites, _scans, _magicHits, _walkHits, _changingWrites, _revertedLinks;
    private static readonly ConcurrentDictionary<string, long> SiteCounts = new();
    private static readonly ConcurrentDictionary<string, long> ChangingSites = new();
    private static readonly ConcurrentDictionary<string, long> HeaderSites = new();

    private readonly record struct Entry(long Ms, string Site, ulong Address, ulong Size, int Thread, int MagicOffset, int Changed, int Reverted, ulong FirstChanged);

    private static readonly Entry[] Ring = new Entry[RingSize];
    private static long _ringNext;
    private static readonly Entry[] HeaderRing = new Entry[64];
    private static long _headerNext;

    // The scanner's last snapshot of the block and of the globals, for the dump at a native exception.
    private static byte[]? _snapshot, _snapshotGlobals;
    private static ulong _snapshotBase;
    private static long _snapshotMs;

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

    // A host write of data about to land at address: pool/global overlap (what changes, which free-list links are
    // overwritten, whether the magic lands) and a header-shaped first qword.
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
        var fresh = data.Slice((int)(begin - address), (int)(end - begin));
        var magic = -1;
        for (var offset = 0; offset + 8 <= fresh.Length; offset += 8)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(fresh.Slice(offset, 8)) == Magic)
            {
                magic = (int)(begin - address) + offset;
                break;
            }
        }

        var changed = 0;
        var reverted = 0;
        ulong firstChanged = 0;
        var memory = _memory;
        if (memory is not null)
        {
            var old = new byte[fresh.Length];
            if (memory.TryRead(begin, old))
            {
                var poolBase = Volatile.Read(ref _poolBase);
                var poolEnd = Volatile.Read(ref _poolEnd);
                for (var offset = 0; offset < old.Length; offset++)
                {
                    if (old[offset] != fresh[offset])
                    {
                        if (changed == 0)
                            firstChanged = begin + (ulong)offset;
                        changed++;
                    }
                }

                // A 16-byte slot whose first qword was a link into the block and is overwritten with something else.
                for (var slot = (int)((begin + 15 & ~15UL) - begin); slot + 8 <= old.Length; slot += 16)
                {
                    var link = BinaryPrimitives.ReadUInt64LittleEndian(old.AsSpan(slot, 8));
                    var value = BinaryPrimitives.ReadUInt64LittleEndian(fresh.Slice(slot, 8));
                    if (link != value && link >= poolBase && link < poolEnd && (link & 15) == 0)
                        reverted++;
                }
            }
        }

        Record(site, address, size, magic, changed, reverted, firstChanged);
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

        Record(site, address, size, magic, -1, -1, 0);
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

    private static void Record(string site, ulong address, ulong size, int magic, int changed, int reverted, ulong firstChanged)
    {
        var count = SiteCounts.AddOrUpdate(site, 1, static (_, value) => value + 1);
        var changing = 0L;
        if (changed > 0)
        {
            changing = ChangingSites.AddOrUpdate(site, 1, static (_, value) => value + 1);
            Interlocked.Increment(ref _changingWrites);
            Interlocked.Add(ref _revertedLinks, reverted);
        }

        var entry = new Entry(Ms, site, address, size, Environment.CurrentManagedThreadId, magic, changed, reverted, firstChanged);
        var slot = Interlocked.Increment(ref _ringNext) - 1;
        Ring[slot % RingSize] = entry;
        var stack = count <= 3 || magic >= 0 || (changing is > 0 and <= 5) || reverted > 0 && changing <= 20;
        if (stack || count % 1000 == 0 || (changing > 0 && changing % 100 == 0))
        {
            Console.Error.WriteLine($"[BVHL] host-write site={site} n={count} changing_n={changing} ms={entry.Ms} addr=0x{address:X}+0x{size:X} thread={entry.Thread} magic_offset={(magic >= 0 ? "0x" + magic.ToString("X", CultureInfo.InvariantCulture) : "none")} changed={changed} reverted_links={reverted} first_changed=0x{firstChanged:X}" +
                (stack ? Environment.NewLine + Environment.StackTrace : ""));
            if (magic >= 0 || reverted > 0)
                Console.Error.Flush();
        }
    }

    private static void NoteHeader(string site, ulong address, ulong size)
    {
        Interlocked.Increment(ref _headerWrites);
        var count = HeaderSites.AddOrUpdate(site, 1, static (_, value) => value + 1);
        var slot = Interlocked.Increment(ref _headerNext) - 1;
        HeaderRing[slot % HeaderRing.Length] = new Entry(Ms, site, address, size, Environment.CurrentManagedThreadId, 0, 0, 0, 0);
        if (count <= 2)
            Console.Error.WriteLine($"[BVHL] header-write site={site} n={count} ms={Ms} addr=0x{address:X}+0x{size:X} thread={Environment.CurrentManagedThreadId}{Environment.NewLine}{Environment.StackTrace}");
    }

    private static void ScanLoop()
    {
        byte[]? previous = null;
        var current = Array.Empty<byte>();
        var globals = new byte[GlobalsBytes];
        var after = new byte[GlobalsBytes];
        var lastHeartbeat = 0L;
        var located = false;
        ulong lastMagicNode = 0, lastWalkNode = 0;
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

                // The game pops and pushes while the block is read: walk the list only when head and used held still.
                var still = _fixedBase != 0 || (memory.TryRead(Globals, after) && after.AsSpan(8, 8).SequenceEqual(globals.AsSpan(8, 8)) && after.AsSpan(0x18, 4).SequenceEqual(globals.AsSpan(0x18, 4)));

                if (Volatile.Read(ref _poolBase) != poolBase || Volatile.Read(ref _poolEnd) != poolBase + (ulong)bytes)
                {
                    Volatile.Write(ref _poolEnd, poolBase + (ulong)bytes);
                    Volatile.Write(ref _poolBase, poolBase);
                    Console.Error.WriteLine($"[BVHL] pool base=0x{poolBase:X} bytes=0x{bytes:X} capacity={capacity} head=0x{head:X} used={used} peak={peak} ms={Ms}");
                    located = true;
                }

                var scans = Interlocked.Increment(ref _scans);
                var hit = FindMagic(current);
                var walk = still ? WalkFreeList(current, poolBase, head, capacity) : new FreeWalk(-1, null, 0);
                if (hit >= 0)
                {
                    var node = poolBase + (ulong)hit;
                    if (node != lastMagicNode)
                    {
                        lastMagicNode = node;
                        if (Interlocked.Increment(ref _magicHits) <= MagicReports)
                            ReportHit("MAGIC", poolBase, head, used, peak, capacity, hit, walk, current, previous);
                    }
                }
                else
                {
                    lastMagicNode = 0;
                }

                if (walk.Problem is not null)
                {
                    if (walk.BadNode != lastWalkNode)
                    {
                        lastWalkNode = walk.BadNode;
                        if (Interlocked.Increment(ref _walkHits) <= WalkReports)
                            ReportHit("WALK", poolBase, head, used, peak, capacity, -1, walk, current, previous);
                    }
                }
                else if (still)
                {
                    lastWalkNode = 0;
                }

                previous ??= new byte[bytes];
                current.AsSpan().CopyTo(previous);
                lock (Gate)
                {
                    if (_snapshot is null || _snapshot.Length != bytes)
                        _snapshot = new byte[bytes];
                    current.AsSpan().CopyTo(_snapshot);
                    _snapshotGlobals ??= new byte[GlobalsBytes];
                    globals.AsSpan().CopyTo(_snapshotGlobals);
                    _snapshotBase = poolBase;
                    _snapshotMs = Ms;
                }

                if (Ms - lastHeartbeat >= 60_000)
                {
                    lastHeartbeat = Ms;
                    Console.Error.WriteLine($"[BVHL] alive ms={Ms} scans={scans} used={used} peak={peak} capacity={capacity} free_walk={walk.Steps} writes_pool={Interlocked.Read(ref _writesPool)} changing={Interlocked.Read(ref _changingWrites)} reverted_links={Interlocked.Read(ref _revertedLinks)} writes_globals={Interlocked.Read(ref _writesGlobals)} header_writes={Interlocked.Read(ref _headerWrites)} magic_hits={Interlocked.Read(ref _magicHits)} walk_hits={Interlocked.Read(ref _walkHits)} sites=[{Summary(SiteCounts)}] changing_sites=[{Summary(ChangingSites)}] header_sites=[{Summary(HeaderSites)}]");
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

    // Runs of changed bytes between two images of the block, of at least minRun bytes (offset, length).
    public static List<(int Offset, int Length)> FindChangedRuns(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int minRun)
    {
        var runs = new List<(int, int)>();
        var index = 0;
        var length = Math.Min(before.Length, after.Length);
        while (index < length)
        {
            if (before[index] == after[index])
            {
                index++;
                continue;
            }

            var start = index;
            var lastChanged = index;
            // A run tolerates up to 15 equal bytes inside it (a clobber that happens to match).
            while (index < length && index - lastChanged <= 15)
            {
                if (before[index] != after[index])
                    lastChanged = index;
                index++;
            }

            if (lastChanged - start + 1 >= minRun)
                runs.Add((start, lastChanged - start + 1));
        }

        return runs;
    }

    // At a native exception: the pool now against the scanner's last snapshot (the runs that changed since, the
    // first nodes of the free list as it was), the globals and the rings. Called on the faulting thread.
    public static void DumpOnCrash(ulong rbx)
    {
        if (!Enabled)
            return;
        try
        {
            var memory = _memory;
            byte[]? snapshot, snapshotGlobals;
            ulong snapshotBase;
            long snapshotMs;
            lock (Gate)
            {
                snapshot = _snapshot;
                snapshotGlobals = _snapshotGlobals;
                snapshotBase = _snapshotBase;
                snapshotMs = _snapshotMs;
            }

            var text = new StringBuilder();
            text.Append($"[BVHL] CRASH ms={Ms} rbx=0x{rbx:X} snapshot_ms={snapshotMs} snapshot_base=0x{snapshotBase:X}");
            text.AppendLine();
            if (memory is null || snapshot is null || snapshotGlobals is null)
            {
                text.Append("[BVHL] no snapshot");
                text.AppendLine();
                Console.Error.Write(text.ToString());
                Console.Error.Flush();
                return;
            }

            var globals = new byte[GlobalsBytes];
            if (memory.TryRead(Globals, globals))
                text.Append($"[BVHL] globals now: base=0x{BinaryPrimitives.ReadUInt64LittleEndian(globals):X} head=0x{BinaryPrimitives.ReadUInt64LittleEndian(globals.AsSpan(8)):X} used={BinaryPrimitives.ReadUInt32LittleEndian(globals.AsSpan(0x18))} peak={BinaryPrimitives.ReadUInt32LittleEndian(globals.AsSpan(0x1C))}; snapshot: head=0x{BinaryPrimitives.ReadUInt64LittleEndian(snapshotGlobals.AsSpan(8)):X} used={BinaryPrimitives.ReadUInt32LittleEndian(snapshotGlobals.AsSpan(0x18))}");
            else
                text.Append("[BVHL] globals unreadable");
            text.AppendLine();
            var current = new byte[snapshot.Length];
            if (!memory.TryRead(snapshotBase, current))
            {
                text.Append("[BVHL] block unreadable");
                text.AppendLine();
                Console.Error.Write(text.ToString());
                Console.Error.Flush();
                return;
            }

            if (rbx >= snapshotBase && rbx < snapshotBase + (ulong)snapshot.Length)
                text.Append($"[BVHL] rbx is slot {(rbx - snapshotBase) / 16} of the pool");
            else
                text.Append("[BVHL] rbx is not in the pool");
            text.AppendLine();
            var magicNow = FindMagic(current);
            var magicBefore = FindMagic(snapshot);
            text.Append($"[BVHL] magic now at {(magicNow >= 0 ? "0x" + (snapshotBase + (ulong)magicNow).ToString("X", CultureInfo.InvariantCulture) : "none")}, in the snapshot at {(magicBefore >= 0 ? "0x" + (snapshotBase + (ulong)magicBefore).ToString("X", CultureInfo.InvariantCulture) : "none")}");
            text.AppendLine();
            var runs = FindChangedRuns(snapshot, current, 16);
            text.Append($"[BVHL] changed runs since the snapshot (>=16 bytes): {runs.Count}");
            text.AppendLine();
            var shown = 0;
            foreach (var (offset, length) in runs)
            {
                if (++shown > 40)
                    break;
                var count = Math.Min(length, 288);
                text.Append($"[BVHL] run 0x{snapshotBase + (ulong)offset:X}+0x{length:X} slot={offset / 16} now={Convert.ToHexString(current, offset, count)}");
                text.AppendLine();
                if (shown <= 12)
                {
                    text.Append($"[BVHL]     before={Convert.ToHexString(snapshot, offset, count)}");
                    text.AppendLine();
                }
            }

            // The free list as the snapshot had it: the nodes the game was about to pop.
            var node = BinaryPrimitives.ReadUInt64LittleEndian(snapshotGlobals.AsSpan(8));
            text.Append("[BVHL] free list of the snapshot (first 32 nodes: address, before, now):");
            text.AppendLine();
            for (var step = 0; step < 32 && node != 0; step++)
            {
                if (node < snapshotBase || node >= snapshotBase + (ulong)snapshot.Length || (node & 15) != 0)
                {
                    text.Append($"  #{step} 0x{node:X} outside the block");
                    text.AppendLine();
                    break;
                }

                var at = (int)(node - snapshotBase);
                var before = snapshot.AsSpan(at, 16);
                var now = current.AsSpan(at, 16);
                text.Append($"  #{step} 0x{node:X} before={Convert.ToHexString(before)} now={Convert.ToHexString(now)}{(before.SequenceEqual(now) ? "" : " CHANGED")}");
                text.AppendLine();
                node = BinaryPrimitives.ReadUInt64LittleEndian(before);
            }

            text.Append($"[BVHL] host writes over the pool (last {RingSize}, newest last):");
            text.AppendLine();
            var next = Interlocked.Read(ref _ringNext);
            for (var index = Math.Max(0, next - RingSize); index < next; index++)
            {
                var entry = Ring[index % RingSize];
                text.Append($"  ms={entry.Ms} site={entry.Site} addr=0x{entry.Address:X}+0x{entry.Size:X} thread={entry.Thread} magic_offset={entry.MagicOffset} changed={entry.Changed} reverted_links={entry.Reverted} first_changed=0x{entry.FirstChanged:X}");
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
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[BVHL] crash dump failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

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

    private static void ReportHit(string kind, ulong poolBase, ulong head, uint used, uint peak, uint capacity, int hit, FreeWalk walk, byte[] current, byte[]? previous)
    {
        var text = new StringBuilder();
        var node = hit >= 0 ? poolBase + (ulong)hit : walk.BadNode;
        text.Append($"[BVHL] HIT {kind} ms={Ms} node=0x{node:X} slot={(hit >= 0 ? hit / 16 : -1)} head=0x{head:X} used={used} peak={peak} capacity={capacity} free_walk={walk.Steps} problem={walk.Problem ?? "none"} bad=0x{walk.BadNode:X}");
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
            text.Append($"  ms={entry.Ms} site={entry.Site} addr=0x{entry.Address:X}+0x{entry.Size:X} thread={entry.Thread} magic_offset={entry.MagicOffset} changed={entry.Changed} reverted_links={entry.Reverted} first_changed=0x{entry.FirstChanged:X}");
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
