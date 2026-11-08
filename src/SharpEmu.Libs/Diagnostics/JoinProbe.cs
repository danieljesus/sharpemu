// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_JOIN_PROBE=1 (off by default; needs SHARPEMU_RENDER_PIPELINE=1): the join points a distributed
// resolution would have, in shadow. Each write to guest memory the stream makes (command-processor stores: labels,
// WRITE_DATA, occlusion dumps, DMA; GPU writes: written buffers and device-address ranges) is kept with its stream
// position (draws+dispatches before it). Each later reader looks up the latest overlapping write before it in stream
// order: CPU reads (the front's direct reads, the render thread's reads, WAIT_REG_MEM reads of memory), GPU reads
// (buffer obtains) and GPU writes over earlier writes (WAW). A read joins when its writer is still inside the open
// segment and closer than the window N; the segment then restarts at the reader's draw. Conservative written ranges
// (the planner's 40 MB tables of 10CD/6A53) are counted three ways: U = they write everything (any read after them
// joins), D = they write their declared range, B = they write nothing (an optimistic bound for exact ranges, R1).
// Groups: cpu = CPU-side reads, GPU reads of CP-written bytes (uploads) and memory waits; all = cpu + GPU reads of
// GPU-written bytes + GPU WAW (the strict overlap rule). Hard drains (FlushAndWait, SynchronizeGpu, GDS reads,
// synchronous writes, Drain) restart every segment. The probe never flushes, reads or waits: it only observes.
// Not seen: image writes and texture reads (render targets sampled later) and the recorder's cache mutations.
// SHARPEMU_JOIN_REPORT_S (default 10) is the report window; lines [PERF][JOIN*] on the render thread.
internal static class JoinProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_JOIN_PROBE") == "1";

    private static readonly long ReportTicks = (long)((double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_JOIN_REPORT_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 10) * Stopwatch.Frequency);

    internal enum WKind { Label, WriteData, Occlusion, OtherCp, Dma, Gpu, GpuCons }
    internal enum RClass { FrontRead, RenderRead, WaitMem, GpuRead, GpuWaw, Game }
    internal enum WaitBranch { Own, Deferred, Memory, Pending, Blocked, Rewind }
    internal enum DrainKind { BackRead, CmdRead, FlushWait, SyncGpu, Gds, WriteNow, Drain }

    private const int WKinds = 7, RClasses = 6, WaitBranches = 6, DrainKinds = 7;
    private const int Buckets = 12; // 0,1,2-3,4-7,8-15,16-31,32-63,64-127,128-255,256-1023,1024+,none
    private const int Ring = 1 << 16;
    private const int Instances = 4;
    private const ulong SmallCpBytes = 256;
    private const int GpuBlockShift = 16;
    private const int GpuBlockCap = 32;
    private const int RecordCap = 250_000;
    private static readonly int[] Windows = [16, 64, 256, int.MaxValue];
    private static readonly string[] Scenarios = ["B_cpu", "D_cpu", "U_cpu", "B_all", "D_all", "U_all"];

    private struct Instance
    {
        public long Seq;
        public int Draw;
        public int MinCpu, MinGpu, MinGame;
        public bool Valid;
    }

    private sealed class Rec(ulong start, ulong end, WKind kind)
    {
        public readonly ulong Start = start, End = end;
        public readonly WKind Kind = kind;
        public readonly Instance[] Ring = new Instance[Instances];
        public int Head = -1;
    }

    private sealed class CpSlot
    {
        public readonly Rec?[] Recs = new Rec?[4];
        public int Next;
    }

    private struct DrawEntry
    {
        public int HasCpu, HasGpu, Hard, ConsWriter;
        public int WCpu, WCpuDecl, WGpu, WGpuDecl;
    }

    private struct Sim
    {
        public int Start;
        public long Joins, Segments, SegDraws, ShortDraws, LongDraws;
        public int[] Hist;
    }

    // Front (the command-stream thread).
    private static int _frontThread;
    private static int _df;
    private static bool _inPrep;
    private static long _frontSeq;
    private static readonly Dictionary<(ulong, ulong), Rec> CpRecs = new();
    private static readonly ConcurrentDictionary<ulong, CpSlot> CpGranules = new();
    private static readonly ConcurrentDictionary<ulong, byte> CpPages = new();
    private static Rec[] _cpBig = [];

    // Render thread.
    private static int _renderThread;
    private static int _dr;
    private static bool _inDraw;
    private static long _renderSeq = -1;
    private static int _finalized;
    private static int _lastCons = -1;
    private static readonly Dictionary<ulong, List<Rec>> GpuBlocks = new();
    private static readonly Dictionary<ulong, List<Rec>> GpuBig = new(); // records of 256 KB or more, by 1 MB block
    private static int _gpuBigCount;
    private const int BigBlockShift = 20;
    private const ulong BigGpuBytes = 256 * 1024;
    private const ulong ScanBlocksMax = 64; // a read over 4 MB sees the big records only (counted in big_unscanned)
    private static long _bigUnscanned;
    private static readonly Dictionary<(ulong, ulong, bool), Rec> GpuRecs = new();
    private static readonly DrawEntry[] Entries = NewEntries();
    private static readonly Sim[] Sims = NewSims();
    private static readonly ConcurrentQueue<(ulong Address, ulong Size, long Seq, int Draw)> GameReads = new();
    private static long _windowStart;
    private static int _frames;

    // Window counters (render thread unless noted).
    private static readonly long[] Writes = new long[WKinds];
    private static readonly long[] Reads = new long[RClasses];
    private static readonly long[] ReadHits = new long[RClasses];
    private static readonly long[,] ReadDist = new long[RClasses, Buckets];
    private static readonly long[,] FirstCpu = new long[WKinds, Buckets];
    private static readonly long[,] FirstGpu = new long[WKinds, Buckets];
    private static readonly long[,] FirstGame = new long[WKinds, Buckets];
    private static readonly long[] Waits = new long[WaitBranches];
    private static readonly long[] Drains = new long[DrainKinds];
    private static long _cpResets, _gpuResets;
    private static long _draws, _dispatches, _consObtains, _consDraws, _falseShare, _evictions;
    private static readonly ConcurrentDictionary<ulong, long> ConsHashes = new();
    private static readonly ConcurrentDictionary<string, long> OtherCallers = new();
    private static readonly ConcurrentDictionary<ulong, long> WawHashes = new();

    private static DrawEntry[] NewEntries()
    {
        var entries = new DrawEntry[Ring];
        for (var index = 0; index < Ring; index++)
            Reset(ref entries[index]);
        return entries;
    }

    private static Sim[] NewSims()
    {
        var sims = new Sim[Scenarios.Length * Windows.Length];
        for (var index = 0; index < sims.Length; index++)
            sims[index].Hist = new int[1026];
        return sims;
    }

    private static void Reset(ref DrawEntry entry) =>
        entry = new DrawEntry { WCpu = -1, WCpuDecl = -1, WGpu = -1, WGpuDecl = -1 };

    private static bool OnFront => Environment.CurrentManagedThreadId == _frontThread;
    private static bool OnRender => Environment.CurrentManagedThreadId == _renderThread;

    private static int Bucket(long distance) => distance switch
    {
        < 0 => 0,
        0 => 0,
        1 => 1,
        < 4 => 2,
        < 8 => 3,
        < 16 => 4,
        < 32 => 5,
        < 64 => 6,
        < 128 => 7,
        < 256 => 8,
        < 1024 => 9,
        _ => 10,
    };

    private static void Max(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    private static void Min(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value < current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    // ---- stream positions ----

    // BackQueue.Enqueue, under its gate, for every queued item.
    internal static void Enqueued() => Interlocked.Increment(ref _frontSeq);

    internal static void BindRender() => _renderThread = Environment.CurrentManagedThreadId;

    // The front starts preparing a draw or dispatch; its reads belong to it until FrontDrawQueued.
    internal static void FrontDrawBegin()
    {
        if (_frontThread == 0) _frontThread = Environment.CurrentManagedThreadId;
        if (!OnFront) return;
        _df++;
        _inPrep = true;
    }

    internal static void FrontDrawQueued()
    {
        if (OnFront) _inPrep = false;
    }

    private static int FrontReaderDraw => _inPrep ? _df : _df + 1;

    // BackQueue.RunPending, before each item.
    internal static void BeginItem()
    {
        _renderSeq++;
        _inDraw = false;
        while (GameReads.TryDequeue(out var read))
            GameRead(read.Address, read.Size, read.Seq, read.Draw);
    }

    internal static void EndItem()
    {
        _inDraw = false;
        var now = Stopwatch.GetTimestamp();
        if (_windowStart == 0) _windowStart = now;
        else if (now - _windowStart >= ReportTicks)
        {
            Report(now);
            _windowStart = now;
        }
    }

    internal static void RenderDraw(bool dispatch)
    {
        if (!OnRender) return;
        _dr++;
        _inDraw = true;
        if (dispatch) _dispatches++;
        else _draws++;
        while (_finalized < _dr - 1)
            Finalize(++_finalized);
    }

    private static int RenderReaderDraw => _inDraw ? _dr : _dr + 1;

    internal static void NoteFrame() => Interlocked.Increment(ref _frames);

    // ---- writers ----

    internal static WKind KindOfCaller(string caller)
    {
        if (caller.Contains("Label", StringComparison.Ordinal) || caller.Contains("Flip", StringComparison.Ordinal) ||
            caller.Contains("EndOfPipe", StringComparison.Ordinal))
            return WKind.Label;
        if (caller.Contains("RaiseEvent", StringComparison.Ordinal) || caller.Contains("Occlusion", StringComparison.Ordinal))
            return WKind.Occlusion;
        if (caller.StartsWith("WriteData", StringComparison.Ordinal))
            return WKind.WriteData;
        OtherCallers.AddOrUpdate(caller, 1, static (_, count) => count + 1);
        return WKind.OtherCp;
    }

    // A command-processor store, on the front, at its stream position.
    internal static void CpWrite(ulong address, ulong size, WKind kind)
    {
        if (size == 0 || _frontThread == 0 || !OnFront) return;
        Interlocked.Increment(ref Writes[(int)kind]);
        var end = address + size;
        var seq = Volatile.Read(ref _frontSeq);
        if (CpRecs.Count >= RecordCap)
        {
            // Bounded memory: forget the history (counted), as if the stream started here.
            CpRecs.Clear();
            CpGranules.Clear();
            CpPages.Clear();
            Volatile.Write(ref _cpBig, []);
            Interlocked.Increment(ref _cpResets);
        }

        if (!CpRecs.TryGetValue((address, end), out var rec))
        {
            rec = new Rec(address, end, kind);
            CpRecs[(address, end)] = rec;
            for (var page = address >> 12; page <= (end - 1) >> 12; page++)
                CpPages.TryAdd(page, 0);
            if (size > SmallCpBytes)
            {
                Volatile.Write(ref _cpBig, [.. _cpBig, rec]);
            }
            else
            {
                for (var granule = address >> 4; granule <= (end - 1) >> 4; granule++)
                {
                    var slot = CpGranules.GetOrAdd(granule, static _ => new CpSlot());
                    lock (slot)
                    {
                        slot.Recs[slot.Next] = rec;
                        slot.Next = (slot.Next + 1) & 3;
                    }
                }
            }
        }

        Push(rec, seq, _df, front: true);
    }

    private static void Push(Rec rec, long seq, int draw, bool front)
    {
        lock (rec)
        {
            var head = (rec.Head + 1) % Instances;
            ref var old = ref rec.Ring[head];
            if (old.Valid) Close(rec.Kind, ref old);
            old = new Instance { Seq = seq, Draw = draw, MinCpu = int.MaxValue, MinGpu = int.MaxValue, MinGame = int.MaxValue, Valid = true };
            rec.Head = head;
        }
    }

    private static void Close(WKind kind, ref Instance instance)
    {
        var k = (int)kind;
        Interlocked.Increment(ref FirstCpu[k, instance.MinCpu == int.MaxValue ? Buckets - 1 : Bucket(instance.MinCpu)]);
        Interlocked.Increment(ref FirstGpu[k, instance.MinGpu == int.MaxValue ? Buckets - 1 : Bucket(instance.MinGpu)]);
        Interlocked.Increment(ref FirstGame[k, instance.MinGame == int.MaxValue ? Buckets - 1 : Bucket(instance.MinGame)]);
    }

    // A GPU write (a written buffer or device-address range), on the render thread inside its item.
    internal static void GpuWrite(ulong address, ulong size, bool conservative, ulong hash)
    {
        if (size == 0 || !OnRender) return;
        var kind = conservative ? WKind.GpuCons : WKind.Gpu;
        Writes[(int)kind]++;
        var draw = _dr;
        if (conservative)
        {
            _consObtains++;
            ConsHashes.AddOrUpdate(hash, 1, static (_, count) => count + 1);
            ref var self = ref Entries[draw & (Ring - 1)];
            if (self.ConsWriter == 0)
            {
                self.ConsWriter = 1;
                _consDraws++;
            }
        }

        // The strict rule: a write over an earlier write (WAW) joins too. A big write only checks the big records.
        var waw = Lookup(address, size, RClass.GpuWaw, _renderSeq, inclusive: false, RenderReaderDraw, countRead: true, scanBlocks: size < BigGpuBytes);
        if (waw >= 0 && RenderReaderDraw - waw < 16) WawHashes.AddOrUpdate(hash, 1, static (_, count) => count + 1);
        var end = address + size;
        if (GpuRecs.Count >= RecordCap)
        {
            GpuRecs.Clear();
            GpuBlocks.Clear();
            GpuBig.Clear();
            _gpuBigCount = 0;
            _gpuResets++;
        }

        if (!GpuRecs.TryGetValue((address, end, conservative), out var rec))
        {
            rec = new Rec(address, end, kind);
            GpuRecs[(address, end, conservative)] = rec;
            if (size >= BigGpuBytes)
            {
                _gpuBigCount++;
                for (var block = address >> BigBlockShift; block <= (end - 1) >> BigBlockShift; block++)
                {
                    if (!GpuBig.TryGetValue(block, out var big))
                        GpuBig[block] = big = new List<Rec>(2);
                    big.Add(rec);
                }
            }
            else for (var block = address >> GpuBlockShift; block <= (end - 1) >> GpuBlockShift; block++)
            {
                if (!GpuBlocks.TryGetValue(block, out var list))
                    GpuBlocks[block] = list = new List<Rec>(4);
                if (list.Count >= GpuBlockCap)
                {
                    list.RemoveAt(0);
                    _evictions++;
                }

                list.Add(rec);
            }
        }

        Push(rec, _renderSeq, draw, front: false);
    }

    // A written device-address range. The planner sizes the store tables of 10CD/6A53 by scanning (40 MB per waterfall):
    // a written range of SHARPEMU_JOIN_CONS_MB (default 4) MB or more counts as the conservative set.
    internal static readonly ulong ConsBytes = (ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_JOIN_CONS_MB"), out var mb) && mb > 0 ? mb : 4) << 20;
    [ThreadStatic] internal static bool SuppressObtain;
    private static readonly long[] BdaSizes = new long[5]; // <64K, <1M, <4M, <16M, >=16M

    internal static void BdaWrite(ulong address, ulong size, ulong hash)
    {
        if (size == 0 || !OnRender) return;
        BdaSizes[size < (64UL << 10) ? 0 : size < (1UL << 20) ? 1 : size < (4UL << 20) ? 2 : size < (16UL << 20) ? 3 : 4]++;
        GpuWrite(address, size, size >= ConsBytes, hash);
    }

    // ---- readers ----

    // The front's direct read (NeedsBackRead said no): only command-processor stores can be there.
    internal static void FrontRead(ulong address, ulong size)
    {
        if (size == 0 || _frontThread == 0 || !OnFront) return;
        Interlocked.Increment(ref Reads[(int)RClass.FrontRead]);
        var reader = FrontReaderDraw;
        ref var entry = ref Entries[reader & (Ring - 1)];
        Volatile.Write(ref entry.HasCpu, 1);
        if (!CpPagesHit(address, size)) return;
        var w = LookupCp(address, size, long.MaxValue, inclusive: true, reader, RClass.FrontRead);
        if (w >= 0)
        {
            Interlocked.Increment(ref ReadHits[(int)RClass.FrontRead]);
            Interlocked.Increment(ref ReadDist[(int)RClass.FrontRead, Bucket(reader - w)]);
            Max(ref entry.WCpu, w);
        }
    }

    // A read on the render thread (synchronized word or byte reads, back reads for the front).
    internal static void RenderRead(ulong address, ulong size)
    {
        if (size == 0 || !OnRender) return;
        Lookup(address, size, RClass.RenderRead, _renderSeq, inclusive: false, RenderReaderDraw, countRead: true);
    }

    internal static void GpuRead(ulong address, ulong size)
    {
        if (size == 0 || !OnRender) return;
        Lookup(address, size, RClass.GpuRead, _renderSeq, inclusive: false, RenderReaderDraw, countRead: true);
    }

    // A WAIT_REG_MEM, on the front, by the branch that answered it.
    internal static void Wait(WaitBranch branch, ulong address, ulong size)
    {
        if (_frontThread == 0 || !OnFront) return;
        Interlocked.Increment(ref Waits[(int)branch]);
        if (branch != WaitBranch.Memory) return;
        Interlocked.Increment(ref Reads[(int)RClass.WaitMem]);
        var reader = FrontReaderDraw;
        ref var entry = ref Entries[reader & (Ring - 1)];
        Volatile.Write(ref entry.HasCpu, 1);
        if (!CpPagesHit(address, size)) return;
        var w = LookupCp(address, size, long.MaxValue, inclusive: true, reader, RClass.WaitMem);
        if (w >= 0)
        {
            Interlocked.Increment(ref ReadHits[(int)RClass.WaitMem]);
            Interlocked.Increment(ref ReadDist[(int)RClass.WaitMem, Bucket(reader - w)]);
            Max(ref entry.WCpu, w);
        }
    }

    internal static void Drain(DrainKind kind)
    {
        if (_frontThread == 0 || !OnFront) return;
        Interlocked.Increment(ref Drains[(int)kind]);
        if (kind is DrainKind.BackRead or DrainKind.CmdRead) return;
        Volatile.Write(ref Entries[FrontReaderDraw & (Ring - 1)].Hard, 1);
    }

    // A guest CPU thread reads GPU-written memory (a fault download): looked up later on the render thread.
    internal static void GameReadQueued(ulong address, ulong size)
    {
        if (size == 0 || OnRender || OnFront || _renderThread == 0) return;
        GameReads.Enqueue((address, size, Volatile.Read(ref _renderSeq), Volatile.Read(ref _dr)));
    }

    private static void GameRead(ulong address, ulong size, long seq, int draw)
    {
        Reads[(int)RClass.Game]++;
        var (cp, gpu, decl) = Find(address, size, seq + 1, inclusive: false, draw, RClass.Game);
        var w = Math.Max(cp, Math.Max(gpu, decl));
        if (w < 0) return;
        ReadHits[(int)RClass.Game]++;
        ReadDist[(int)RClass.Game, Bucket(draw - w)]++;
    }

    private static bool CpPagesHit(ulong address, ulong size)
    {
        var last = (address + size - 1) >> 12;
        for (var page = address >> 12; page <= last; page++)
            if (CpPages.ContainsKey(page)) return true;
        return false;
    }

    private static int Lookup(ulong address, ulong size, RClass reader, long seq, bool inclusive, int readerDraw, bool countRead, bool scanBlocks = true)
    {
        if (countRead) Reads[(int)reader]++;
        ref var entry = ref Entries[readerDraw & (Ring - 1)];
        var gpuSide = reader is RClass.GpuRead or RClass.GpuWaw;
        if (gpuSide) Volatile.Write(ref entry.HasGpu, 1);
        else Volatile.Write(ref entry.HasCpu, 1);
        var (cp, gpu, decl) = Find(address, size, seq, inclusive, readerDraw, reader, scanBlocks);
        var w = Math.Max(cp, Math.Max(gpu, decl));
        if (w < 0) return w;
        if (countRead)
        {
            ReadHits[(int)reader]++;
            ReadDist[(int)reader, Bucket(readerDraw - w)]++;
        }

        if (gpuSide)
        {
            // A GPU read of command-processor bytes is an upload the resolution makes from guest memory: a CPU read.
            if (cp >= 0 && reader == RClass.GpuRead) Max(ref entry.WCpu, cp);
            if (gpu >= 0) Max(ref entry.WGpu, gpu);
            if (decl >= 0) Max(ref entry.WGpuDecl, decl);
            if (cp >= 0 && reader == RClass.GpuWaw) Max(ref entry.WGpu, cp);
        }
        else
        {
            Max(ref entry.WCpu, Math.Max(cp, gpu));
            if (decl >= 0) Max(ref entry.WCpuDecl, decl);
        }

        return w;
    }

    // The latest instance before the reader of each overlapping record: (command processor, exact GPU, conservative GPU).
    private static (int Cp, int Gpu, int Decl) Find(ulong address, ulong size, long seq, bool inclusive, int readerDraw, RClass reader,
        bool scanBlocks = true)
    {
        var cp = CpPagesHit(address, size) ? LookupCp(address, size, seq, inclusive, readerDraw, reader) : -1;
        int gpu = -1, decl = -1;
        var end = address + size;
        var lastBig = (end - 1) >> BigBlockShift;
        if (lastBig - (address >> BigBlockShift) > 256) lastBig = (address >> BigBlockShift) + 256;
        Rec? seen = null;
        for (var block = address >> BigBlockShift; block <= lastBig; block++)
        {
            if (!GpuBig.TryGetValue(block, out var big)) continue;
            foreach (var rec in big)
            {
                if (ReferenceEquals(rec, seen) || rec.Start >= end || rec.End <= address) continue;
                var draw = Hit(rec, seq, inclusive, readerDraw, reader);
                if (draw < 0) continue;
                if (rec.Kind == WKind.GpuCons) decl = Math.Max(decl, draw);
                else gpu = Math.Max(gpu, draw);
                seen = rec;
            }
        }

        var firstBlock = address >> GpuBlockShift;
        var lastBlock = (end - 1) >> GpuBlockShift;
        if (!scanBlocks) return (cp, gpu, decl);
        if (lastBlock - firstBlock >= ScanBlocksMax)
        {
            _bigUnscanned++;
            return (cp, gpu, decl);
        }

        Rec? previous = null;
        for (var block = firstBlock; block <= lastBlock; block++)
        {
            if (!GpuBlocks.TryGetValue(block, out var list)) continue;
            foreach (var rec in list)
            {
                if (ReferenceEquals(rec, previous)) continue;
                if (rec.Start >= end || rec.End <= address)
                {
                    if ((rec.Start >> 12) <= ((end - 1) >> 12) && ((rec.End - 1) >> 12) >= (address >> 12)) _falseShare++;
                    continue;
                }

                // A record spanning several blocks is seen once per block; the latest instance answers the same each time.
                var draw = Hit(rec, seq, inclusive, readerDraw, reader);
                if (draw < 0) continue;
                if (rec.Kind == WKind.GpuCons) decl = Math.Max(decl, draw);
                else gpu = Math.Max(gpu, draw);
                previous = rec;
            }
        }

        return (cp, gpu, decl);
    }

    private static int LookupCp(ulong address, ulong size, long seq, bool inclusive, int readerDraw, RClass reader)
    {
        var w = -1;
        var end = address + size;
        if (size <= 4096)
        {
            for (var granule = address >> 4; granule <= (end - 1) >> 4; granule++)
            {
                if (!CpGranules.TryGetValue(granule, out var slot)) continue;
                lock (slot)
                {
                    foreach (var rec in slot.Recs)
                    {
                        if (rec is null || rec.Start >= end || rec.End <= address) continue;
                        w = Math.Max(w, Hit(rec, seq, inclusive, readerDraw, reader));
                    }
                }
            }
        }

        foreach (var rec in Volatile.Read(ref _cpBig))
        {
            if (rec.Start >= end || rec.End <= address) continue;
            w = Math.Max(w, Hit(rec, seq, inclusive, readerDraw, reader));
        }

        return w;
    }

    // The draw of the latest instance of the record before the reader, or -1; notes the reader's distance on it.
    private static int Hit(Rec rec, long seq, bool inclusive, int readerDraw, RClass reader)
    {
        lock (rec)
        {
            if (rec.Head < 0) return -1;
            for (var step = 0; step < Instances; step++)
            {
                ref var instance = ref rec.Ring[(rec.Head - step + Instances) % Instances];
                if (!instance.Valid) return -1;
                if (inclusive ? instance.Seq > seq : instance.Seq >= seq) continue;
                var distance = readerDraw - instance.Draw;
                if (distance < 0) distance = 0;
                switch (reader)
                {
                    case RClass.GpuRead or RClass.GpuWaw:
                        if (distance < instance.MinGpu) instance.MinGpu = distance;
                        break;
                    case RClass.Game:
                        if (distance < instance.MinGame) instance.MinGame = distance;
                        break;
                    default:
                        if (distance < instance.MinCpu) instance.MinCpu = distance;
                        break;
                }

                return instance.Draw;
            }

            return -1;
        }
    }

    // ---- segments ----

    private static void Finalize(int draw)
    {
        ref var entry = ref Entries[draw & (Ring - 1)];
        var hard = Volatile.Read(ref entry.Hard) != 0;
        var hasCpu = Volatile.Read(ref entry.HasCpu) != 0;
        var hasAny = hasCpu || Volatile.Read(ref entry.HasGpu) != 0;
        var cpu = Volatile.Read(ref entry.WCpu);
        var cpuDecl = Math.Max(cpu, Volatile.Read(ref entry.WCpuDecl));
        var cpuUniv = hasCpu ? Math.Max(cpuDecl, _lastCons) : cpuDecl;
        var all = Math.Max(cpu, Volatile.Read(ref entry.WGpu));
        var allDecl = Math.Max(all, Math.Max(cpuDecl, Volatile.Read(ref entry.WGpuDecl)));
        var allUniv = hasAny ? Math.Max(allDecl, _lastCons) : allDecl;
        Span<int> writers = [cpu, cpuDecl, cpuUniv, all, allDecl, allUniv];
        for (var scenario = 0; scenario < Scenarios.Length; scenario++)
        {
            var w = writers[scenario];
            for (var window = 0; window < Windows.Length; window++)
            {
                ref var sim = ref Sims[scenario * Windows.Length + window];
                var join = hard || (w >= 0 && w >= sim.Start && draw - w < Windows[window]);
                if (!join) continue;
                sim.Joins++;
                Segment(ref sim, draw - sim.Start);
                sim.Start = draw;
            }
        }

        if (entry.ConsWriter != 0) _lastCons = draw;
        Reset(ref entry);
    }

    private static void Segment(ref Sim sim, int length)
    {
        if (length <= 0) return;
        sim.Segments++;
        sim.SegDraws += length;
        if (length < 8) sim.ShortDraws += length;
        if (length >= 50) sim.LongDraws += length;
        sim.Hist[Math.Min(length, 1025)]++;
    }

    private static int Median(int[] hist, bool weighted)
    {
        long total = 0;
        for (var length = 1; length < hist.Length; length++) total += weighted ? (long)hist[length] * length : hist[length];
        if (total == 0) return 0;
        long seen = 0;
        for (var length = 1; length < hist.Length; length++)
        {
            seen += weighted ? (long)hist[length] * length : hist[length];
            if (seen * 2 >= total) return length;
        }

        return hist.Length - 1;
    }

    // ---- report ----

    private static string Row(long[,] table, int row, double perFrame)
    {
        var builder = new StringBuilder();
        for (var bucket = 0; bucket < Buckets; bucket++)
        {
            if (bucket != 0) builder.Append('/');
            builder.Append((table[row, bucket] / perFrame).ToString("F1", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static void Report(long now)
    {
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        var frames = Interlocked.Exchange(ref _frames, 0);
        var f = frames > 0 ? frames : 1.0;
        var inv = CultureInfo.InvariantCulture;
        var head = new StringBuilder();
        head.Append(inv, $"[PERF][JOIN] window_s={seconds:F1} frames={frames} draws={_draws / f:F0} dispatches={_dispatches / f:F0}");
        head.Append(inv, $" cons_obtains={_consObtains / f:F1} cons_draws={_consDraws / f:F1} false_share={_falseShare / f:F0} big_unscanned={_bigUnscanned / f:F0} evictions={_evictions / f:F1}");
        head.Append(" writes=");
        for (var kind = 0; kind < WKinds; kind++) head.Append(inv, $"{(WKind)kind}:{Interlocked.Exchange(ref Writes[kind], 0) / f:F1},");
        head.Append(" waits=");
        for (var branch = 0; branch < WaitBranches; branch++) head.Append(inv, $"{(WaitBranch)branch}:{Interlocked.Exchange(ref Waits[branch], 0) / f:F1},");
        head.Append(" drains=");
        for (var kind = 0; kind < DrainKinds; kind++) head.Append(inv, $"{(DrainKind)kind}:{Interlocked.Exchange(ref Drains[kind], 0) / f:F1},");
        Console.Error.WriteLine(head.ToString());

        var reads = new StringBuilder("[PERF][JOIN_READS] buckets=0,1,2-3,4-7,8-15,16-31,32-63,64-127,128-255,256-1023,1024+,none");
        for (var reader = 0; reader < RClasses; reader++)
        {
            reads.Append(inv, $" {(RClass)reader}={Interlocked.Exchange(ref Reads[reader], 0) / f:F0}/hit{Interlocked.Exchange(ref ReadHits[reader], 0) / f:F0}[{Row(ReadDist, reader, f)}]");
        }

        Console.Error.WriteLine(reads.ToString());
        lock (FirstCpu)
        {
            var first = new StringBuilder("[PERF][JOIN_FIRST] write->first reader, per frame, buckets as JOIN_READS (last = never read)");
            for (var kind = 0; kind < WKinds; kind++)
                first.Append(inv, $" {(WKind)kind}=cpu[{Row(FirstCpu, kind, f)}]gpu[{Row(FirstGpu, kind, f)}]game[{Row(FirstGame, kind, f)}]");
            Console.Error.WriteLine(first.ToString());
            Array.Clear(FirstCpu);
            Array.Clear(FirstGpu);
            Array.Clear(FirstGame);
        }

        Array.Clear(ReadDist);
        var items = (_draws + _dispatches) / f;
        for (var scenario = 0; scenario < Scenarios.Length; scenario++)
        {
            var line = new StringBuilder();
            line.Append(inv, $"[PERF][JOIN_SEG] scenario={Scenarios[scenario]}");
            for (var window = 0; window < Windows.Length; window++)
            {
                ref var sim = ref Sims[scenario * Windows.Length + window];
                var n = Windows[window] == int.MaxValue ? "inf" : Windows[window].ToString(inv);
                var segDraws = sim.SegDraws > 0 ? sim.SegDraws : 1;
                line.Append(inv, $" N{n}=joins:{sim.Joins / f:F1},serial:{sim.Joins / f / Math.Max(items, 1) * 100:F1}%,short8:{sim.ShortDraws * 100.0 / segDraws:F1}%,long50:{sim.LongDraws * 100.0 / segDraws:F1}%,");
                line.Append(inv, $"mean:{(sim.Segments > 0 ? sim.SegDraws / (double)sim.Segments : 0):F1},median:{Median(sim.Hist, false)},wmedian:{Median(sim.Hist, true)}");
                sim.Joins = sim.Segments = sim.SegDraws = sim.ShortDraws = sim.LongDraws = 0;
                Array.Clear(sim.Hist);
            }

            Console.Error.WriteLine(line.ToString());
        }

        var bda = string.Join("/", BdaSizes.Select(count => (count / f).ToString("F1", inv)));
        Array.Clear(BdaSizes);
        var hashes = string.Join(",", ConsHashes.OrderByDescending(pair => pair.Value).Take(6)
            .Select(pair => string.Create(inv, $"{pair.Key:X16}:{pair.Value / f:F1}")));
        var callers = string.Join(",", OtherCallers.OrderByDescending(pair => pair.Value).Take(6)
            .Select(pair => string.Create(inv, $"{pair.Key}:{pair.Value / f:F1}")));
        var waws = string.Join(",", WawHashes.OrderByDescending(pair => pair.Value).Take(6)
            .Select(pair => string.Create(inv, $"{pair.Key:X16}:{pair.Value / f:F1}")));
        WawHashes.Clear();
        Console.Error.WriteLine($"[PERF][JOIN_MISC] waw16_hashes={waws} bda_written_sizes(<64K/<1M/<4M/<16M/>=16M)={bda} cons_mb={ConsBytes >> 20} cons_hashes={hashes} other_cp_callers={callers} cp_records={CpRecs.Count} gpu_records={GpuRecs.Count} gpu_blocks={GpuBlocks.Count} gpu_big={_gpuBigCount} resets={_cpResets}/{_gpuResets}");
        ConsHashes.Clear();
        OtherCallers.Clear();
        _draws = _dispatches = _consObtains = _consDraws = _falseShare = _evictions = _bigUnscanned = 0;
    }
}
