// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_CP_WRITE_DEFER_PROBE=1: whether the command-processor writes into device buffers (WRITE_DATA) could
// wait for the end of the rendering scope they break. Each write recorded while a scope is open stays pending until the
// scope ends for a reason of its own (another target, a transfer, a seal); meanwhile every buffer range a draw or
// dispatch obtains (bindings, device-address ranges) is checked against it, and a program that reads through the
// device-address table (unknown read set) marks every pending write. Render thread only.
internal static class CpWriteDeferProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_CP_WRITE_DEFER_PROBE") == "1";

    private struct Pending
    {
        public ulong Begin, End;
        public bool Conflict, Unknown;
    }

    private static readonly List<Pending> _pending = new();
    private static bool _inWrite, _afterBreak;
    internal static bool Suppress;
    private static long _endSamples;
    // [local] WAR: was the destination read since the last barrier (the pre-copy barrier orders those reads)?
    private static readonly List<(ulong Begin, ulong End)> _readsSinceBarrier = new();
    private static bool _unknownSinceBarrier;
    private static long _warHazard, _warUnknown, _warFree, _barriers, _readsSeen, _unknownSeen, _commandsSeen;
    // Reads of the draw being prepared join the committed set when its draw or dispatch is recorded; only a barrier
    // that orders every earlier command (a CP write's pre-copy barrier, a global barrier) clears the committed set.
    private static readonly List<(ulong Begin, ulong End)> _prepReads = new();
    private static bool _prepUnknown;
    internal static void OnBarrier() => _barriers++;
    internal static void OnCommand()
    {
        _commandsSeen++;
        if (_readsSinceBarrier.Count + _prepReads.Count <= 16384) _readsSinceBarrier.AddRange(_prepReads); else _unknownSinceBarrier = true;
        _unknownSinceBarrier |= _prepUnknown;
        _prepReads.Clear();
        _prepUnknown = false;
    }
    internal static void OnFullBarrier()
    {
        _readsSinceBarrier.Clear();
        _unknownSinceBarrier = false;
    }
    private static readonly Dictionary<string, long> _barrierKinds = new();

    internal static void NoteBarrier(string kind) => _barrierKinds[kind] = _barrierKinds.GetValueOrDefault(kind) + 1;
    private static readonly Dictionary<string, long> _endSites = new();
    internal static bool AfterBreak => _afterBreak;
    private static long _writes, _inScope, _outScope, _deferrable, _unknownOnly, _conflicts, _reopenSame, _reopenOther, _scopesBroken;
    private static readonly long[] _sizes = new long[4];
    private static long _report = System.Diagnostics.Stopwatch.GetTimestamp();

    internal static void BeginWrite(ulong address, ulong size, bool renderingActive)
    {
        var hazard = false;
        foreach (var (begin, end) in _readsSinceBarrier)
            if (begin < address + size && address < end) { hazard = true; break; }
        if (hazard) _warHazard++; else if (_unknownSinceBarrier) _warUnknown++; else _warFree++;
        OnFullBarrier(); // the copy's own barrier orders everything before it
        _writes++;
        _sizes[size <= 16 ? 0 : size <= 64 ? 1 : size <= 256 ? 2 : 3]++;
        if (!renderingActive && _pending.Count == 0)
        {
            _outScope++;
            return;
        }

        _inScope++;
        _pending.Add(new Pending { Begin = address, End = address + size });
        _afterBreak |= renderingActive;
        _inWrite = true;
    }

    internal static void EndWrite() => _inWrite = false;

    // The rendering scope ended: inside a write it is the break the write causes, otherwise a natural end.
    internal static void OnEndRendering()
    {
        if (_inWrite)
        {
            _scopesBroken++;
            return;
        }

        _afterBreak = false;
        Resolve();
    }

    // A barrier, dispatch or transfer outside a write would have ended the scope anyway.
    internal static void OnOtherOp()
    {
        if (_inWrite || Suppress || _pending.Count == 0)
            return;
        if (_afterBreak && (++_endSamples & 7) == 0) // [local] what ends a broken scope before it reopens
        {
            var frames = new System.Diagnostics.StackTrace(1, false).GetFrames();
            var site = string.Join(" < ", frames.Take(6).Select(frame => frame.GetMethod() is { } method ? $"{method.DeclaringType?.Name}.{method.Name}" : "?"));
            _endSites[site] = _endSites.GetValueOrDefault(site) + 1;
        }

        _afterBreak = false;
        Resolve();
    }

    // A scope begins after a write broke one: the same state continues the pass; any other ends it.
    internal static void OnBeginRendering(bool sameAsBroken)
    {
        if (!_afterBreak)
            return;
        _afterBreak = false;
        if (sameAsBroken)
        {
            _reopenSame++;
            return;
        }

        _reopenOther++;
        Resolve();
    }

    internal static void OnRead(ulong address, ulong size)
    {
        _readsSeen++;
        if (!_inWrite && _prepReads.Count < 8192) _prepReads.Add((address, address + size));
        if (_pending.Count == 0 || _inWrite)
            return;
        var end = address + size;
        for (var index = 0; index < _pending.Count; index++)
        {
            var pending = _pending[index];
            if (pending.Begin < end && address < pending.End && !pending.Conflict)
            {
                pending.Conflict = true;
                _pending[index] = pending;
            }
        }
    }

    internal static void OnUnknownReads()
    {
        _unknownSeen++;
        _prepUnknown = true;
        for (var index = 0; index < _pending.Count; index++)
        {
            var pending = _pending[index];
            pending.Unknown = true;
            _pending[index] = pending;
        }
    }

    private static void Resolve()
    {
        foreach (var pending in _pending)
        {
            if (pending.Conflict)
                _conflicts++;
            else if (pending.Unknown)
                _unknownOnly++;
            else
                _deferrable++;
        }

        _pending.Clear();
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_report).TotalSeconds < 5)
            return;
        _report = System.Diagnostics.Stopwatch.GetTimestamp();
        Console.Error.WriteLine($"[PERF][CP_WRITE_DEFER] writes={_writes} in_scope={_inScope} out_of_scope={_outScope} deferrable={_deferrable} unknown_reads_only={_unknownOnly} conflicts={_conflicts} scopes_broken={_scopesBroken} reopen_same={_reopenSame} reopen_other={_reopenOther} size_le16={_sizes[0]} le64={_sizes[1]} le256={_sizes[2]} gt256={_sizes[3]}");
        Console.Error.WriteLine($"[PERF][CP_WRITE_WAR] war_hazard={_warHazard} war_unknown={_warUnknown} war_free={_warFree} barriers={_barriers} reads_seen={_readsSeen} unknown_seen={_unknownSeen} commands_seen={_commandsSeen}");
        _warHazard = _warUnknown = _warFree = _barriers = _readsSeen = _unknownSeen = _commandsSeen = 0;
        foreach (var (site, count) in _endSites.OrderByDescending(pair => pair.Value).Take(10))
            Console.Error.WriteLine($"[PERF][CP_WRITE_DEFER_END] sampled={count * 8} site={site}");
        _endSites.Clear();
        foreach (var (kind, count) in _barrierKinds.OrderByDescending(pair => pair.Value).Take(8))
            Console.Error.WriteLine($"[PERF][CP_WRITE_DEFER_BARRIER] n={count} {kind}");
        _barrierKinds.Clear();
        _writes = _inScope = _outScope = _deferrable = _unknownOnly = _conflicts = _reopenSame = _reopenOther = _scopesBroken = 0;
        Array.Clear(_sizes);
    }
}
