// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_BDA_WRITE_MEMO (0 off by default, 1 on, 2 verify): a written device-address range of a dispatch
// (ObtainDeviceAddressRanges on the render thread; the worker slices keep the generic path) obtained again with the same
// (base, requested size, conservative) in the same submission tick answers from the first obtain instead of repeating it.
// In GTA V's open world (bdc1) 6A53 and 10CD take ~11,500 written ranges per frame (~251 per dispatch), w_obtain 9.7 ms/f.
//
// What the repeated obtain still does, because a later reader can tell (each is the real call or its exact prefix):
//   - CompleteGuestPublications(range, Write): a publication added over the range in between is completed as before;
//   - ScopeBarrierTracker.NoteWrite (SHARPEMU_SCOPE_BARRIER_PRUNE, on by default): a global barrier recorded in between
//     cleared the unordered-write flag that the write sets again;
//   - GpuBuffer.NoteRepeatedGpuWrite: GpuWrites++ (the same-tick "written since" counter of write marks and the detiler's
//     shadows), the readback mirror's observer, the GpuWriteHistory entry (the ring stays the same) and the recording's
//     mapped-memory flag;
//   - TableWordMemo.NoteGpuWrite, unless no word was stored (or trust changed) since the first obtain and the program is
//     the same (TableWordMemo.TrustSerial): then the call would drop nothing;
//   - TouchBuffer, when the collection tick moved;
//   - the presenter still calls InvalidateMemoryCopiesFromGpu (an image registered over the range in between).
// What it skips, idempotent within the tick: ClampMappedSize (the same answer while the mapping generation and, for a
// clamped answer, the per-frame reset count stay), IsBackedRange (mapping), FindBuffer (the same page owner: the registry
// epoch, or the owner checked again), SynchronizeBuffer (nothing to upload while the range is in the GPU-modified set:
// the removal count, or the set checked again), the block write ticks, the buffer's WriteTickMap and LastGpuWriteTick
// (max with the same tick), the precise and ever-written sets and the GPU-modified add (sticky until a removal).
// Off (counted as guard) while a diagnostic that watches every written obtain is on, or SHARPEMU_WRITTEN_OBTAIN_MEMO.
//
// Re-tick (SHARPEMU_BDA_WRITE_MEMO_RETICK, on with the memo, =0 off): an entry of an earlier tick answers too, under the
// same checks, since none of the skipped steps depends on the tick except the write ticks: the mapping generation (and
// resets) keep the clamp and the backing, the page owner keeps FindBuffer, and the range staying in the GPU-modified and
// precise sets keeps SynchronizeBuffer and the adds a no-op. Both sets shrink only through RemoveGpuModified (the removal
// count): nothing prunes them when ticks complete. The hit then runs the full GpuBuffer.NoteGpuWrite (block ticks,
// LastGpuWriteTick, WriteTickMap.Assign at this tick, a pruned chunk created again, the prune check) instead of the
// repeat's prefix, touches the buffer when the collection tick moved, and becomes the entry of this tick. The other
// effects of the real path that read the tick or the batch (CutPolicy, hot windows and words, the table-read census,
// the readback tick log) belong to diagnostics or switches that turn the memo off. In GTA V (bwv1, =2) ~90 % of the
// misses were entries of an earlier tick.
//
// =2 answers every hit from the real path and first checks that it had nothing left to do: the range in the GPU-modified
// set (and in the precise set below 16 MB) and in the ever-written set, the buffer's write ticks at the current tick over
// the range, the page owner the remembered buffer and its use tick current; after it, the same buffer and offset, no
// upload, no GPU-modified change, no registry change and no table word dropped when the memo would have skipped the drop;
// the presenter clamps the size again and compares. Any difference is wrong ([GPU][BDA_WRITE_MEMO_WRONG], the first 20).
//
// [PERF][BDA_WRITE_MEMO] mode=N obtains=N hits=N hits_retick=N miss=cold:N,tick:N,mapping:N,registry:N,removed:N,guard:N
//     table_replays=N verified=N wrong=N wrong_total=N ticks=N retick_cpu_dirty=N
//     [retick_cut=other:N,cap:N,idle:N,readback:N,learn:N,policy:N]
// once per SHARPEMU_DRAWCOST_PROBE window, or every 5 s without it. obtains = written device-address ranges the memo was
// asked about; cold = no entry for the key (first obtain, or the slot held another key); tick = an entry of an earlier
// tick with re-tick off; hits_retick = hits on an entry of an earlier tick (hits counts the same-tick ones);
// table_replays = hits that still ran TableWordMemo.NoteGpuWrite. =2 checks re-tick hits as above, except that the write
// ticks are checked after the real path (at this tick) instead of before it. ticks = submission ticks the memo saw
// (per frame with DRAWCOST's frames of the same window); retick_cpu_dirty (=2) = verified re-tick hits whose range had a
// CPU-dirty page (MayHaveCpuDirtyPages) and whose real path still uploaded nothing: a written range already GPU-modified
// ignores CPU-dirty pages (SynchronizeBuffer), so they are no hit condition. retick_cut (with
// SHARPEMU_READBACK_ORIGIN_PROBE=1) = re-tick hits and tick misses by what ended the presenter's last batch.
internal static class BdaWriteMemo
{
    internal static readonly int DefaultMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_BDA_WRITE_MEMO"), out var mode) ? mode : 0;

    internal static readonly bool Retick = Environment.GetEnvironmentVariable("SHARPEMU_BDA_WRITE_MEMO_RETICK") != "0";

    private const int DumpLimit = 20;

    internal enum Miss
    {
        Cold,
        Tick,
        Mapping,
        Registry,
        Removed,
        Guard,
        Count,
    }

    private static readonly string[] MissNames = ["cold", "tick", "mapping", "registry", "removed", "guard"];

    // The window's counts: plain increments on the render thread, diagnostics only, except the wrong total.
    internal static long Obtains;
    internal static long Hits;
    internal static long HitsRetick;
    internal static long Ticks;
    internal static ulong LastTick = ulong.MaxValue;
    internal static long RetickCpuDirty;
    internal static readonly long[] RetickByCut = new long[Diagnostics.ReadbackOriginProbe.CutReasons];
    internal static long TableReplays;
    internal static long Verified;
    internal static long Wrong;
    internal static readonly long[] MissCounts = new long[(int)Miss.Count];
    private static long _wrongTotal;
    private static int _dumps;
    private static long _lastReport = Stopwatch.GetTimestamp();

    internal static long WrongTotal => Interlocked.Read(ref _wrongTotal);

    internal static void NoteMiss(Miss cause) => MissCounts[(int)cause]++;

    // A re-tick hit or a tick miss: what ended the batch before this tick.
    internal static void NoteRetick()
    {
        if (Diagnostics.ReadbackOriginProbe.Enabled)
            RetickByCut[(int)Diagnostics.ReadbackOriginProbe.LastCut]++;
    }

    internal static void NoteWrong(string reason, ulong address, ulong size, string detail)
    {
        Wrong++;
        Interlocked.Increment(ref _wrongTotal);
        if (_dumps++ < DumpLimit)
            Console.Error.WriteLine(FormattableString.Invariant($"[GPU][BDA_WRITE_MEMO_WRONG] reason={reason} range=0x{address:X}+0x{size:X} {detail}"));
    }

    // Looks at the clock once every 1,024 obtains (Obtains starts again at 0 after each line).
    internal static void MaybeReport(int mode)
    {
        if ((Obtains & 1023) != 0 || mode == 0 || Diagnostics.DrawCostProbe.Enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _lastReport >= 5 * Stopwatch.Frequency)
        {
            _lastReport = now;
            Report(mode);
        }
    }

    internal static void Report(int mode)
    {
        if (mode == 0)
        {
            return;
        }

        Console.Error.WriteLine(FormatAndClear(mode));
    }

    internal static string FormatAndClear(int mode)
    {
        var miss = string.Join(",", MissNames.Select((name, index) => FormattableString.Invariant($"{name}:{MissCounts[index]}")));
        var line = FormattableString.Invariant(
            $"[PERF][BDA_WRITE_MEMO] mode={mode} obtains={Obtains} hits={Hits} hits_retick={HitsRetick} miss={miss} table_replays={TableReplays} verified={Verified} wrong={Wrong} wrong_total={WrongTotal} ticks={Ticks} retick_cpu_dirty={RetickCpuDirty}");
        if (Diagnostics.ReadbackOriginProbe.Enabled)
            line += " retick_cut=" + string.Join(",", RetickByCut.Select((count, index) =>
                FormattableString.Invariant($"{Diagnostics.ReadbackOriginProbe.CutName(index)}:{count}")));
        Obtains = Hits = HitsRetick = TableReplays = Verified = Wrong = Ticks = RetickCpuDirty = 0;
        Array.Clear(MissCounts);
        Array.Clear(RetickByCut);
        return line;
    }

    // For tests: clears the window, the total and the dump budget.
    internal static void ResetForTests()
    {
        FormatAndClear(0);
        LastTick = ulong.MaxValue;
        Interlocked.Exchange(ref _wrongTotal, 0);
        _dumps = 0;
    }
}
