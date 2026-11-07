// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// Dirty state of one 4 MiB block: a page is CPU-dirty, GPU-dirty or clean, never both dirty.
public sealed class TrackedRegion
{
    private const ulong PageBytes = TrackerLayout.PageBytes;
    private const ulong BlockBytes = TrackerLayout.BlockBytes;

    private readonly PageGuard _pages;
    private PageMask _cpuDirty;
    private PageMask _gpuDirty;
    private PageMask _gpuEver;
    private PageMask _writable;
    private PageMask _protectedWhileDirty;
    private PageMask _publishedCpuDirty;
    private PageMask _readable;
    private PageMask _recentCpuUploads;
    private PageMask _repeatedCpuWrites;
    private PageMask _hotCpuWrites;
    private PageMask _uploadedHotPages;
    private long _uploadBatch = -1;
    private static long _hotUploadsSkipped;
    private readonly CpuDirtySummary? _summary;
    private readonly GuestPageTracker? _owner;
    private bool _gpuProtectionQueued;

    public static long TakeHotUploadsSkipped() => Interlocked.Exchange(ref _hotUploadsSkipped, 0);
    private int _cpuDirtyVersion;

    public int CpuDirtyVersion => Volatile.Read(ref _cpuDirtyVersion);

    public PageMask CpuDirtyPages(in PageMask pages) => _cpuDirty & pages;

    // [local] SHARPEMU_PROTECT_OUTSIDE_LOCK=1 (off by default; =2 also verifies every deferred upload): the deferred
    // upload workers decide the pages to protect under the region lock and make the kernel call after releasing it.
    // Every CPU-watch change of the region is then made under _protectGate, taken while holding the region lock, so
    // the watch changes keep the order of the region-lock sections that decided them (an unprotect of a page dirtied
    // meanwhile waits for the protect before it). A worker whose own pages another worker's call is protecting waits
    // for that call through the gate before it copies. Fixed per region at creation (tests set it per tracker).
    private readonly bool _protectOutsideLock;
    private int _protectGate;
    private long _cpuDirtyings;

    public TrackedRegion(PageGuard pages, ulong baseAddress, CpuDirtySummary? summary = null, GuestPageTracker? owner = null,
        bool protectOutsideLock = false)
    {
        _owner = owner;
        if (baseAddress % BlockBytes != 0)
        {
            PageGuard.OnFatal("Cannot create the tracking region with these parameters.");
        }

        _pages = pages;
        _summary = summary;
        _protectOutsideLock = protectOutsideLock;
        BaseAddress = baseAddress;
        _cpuDirty.Fill();
        _publishedCpuDirty.Fill();
        _writable.Fill();
        _readable.Fill();
        _summary?.Set(BaseAddress / BlockBytes, dirty: true);
        _summary?.NoteDirtied();
    }

    public RegionLock Lock { get; } = new();

    public bool ProtectsOutsideLock => _protectOutsideLock;

    // [local] Times a page of the region turned CPU-dirty or changed GPU ownership (a download writes the bytes of a
    // page it leaves clean). Written under the lock; the deferred upload verify reads it.
    public long CpuDirtyings => Volatile.Read(ref _cpuDirtyings);

    // [local] SHARPEMU_LITE_OBTAIN=2: times a page of the region turned CPU-dirty, counted after the bits and the block
    // summary were published (CpuDirtyings moves before them, and CpuDirtyVersion on cleans too). Written under the lock.
    private long _cpuDirtyPublications;

    public long CpuDirtyPublications => Volatile.Read(ref _cpuDirtyPublications);

    // [local] SHARPEMU_LITE_OBTAIN tests only: runs on the marking thread inside ChangeState after CpuDirtyings moved and
    // before the dirty bits, the block summary and CpuDirtyVersion are published (a lock-free reader there sees clean).
    [ThreadStatic]
    internal static Action? AfterCpuDirtyingCountedForTests;

    public ulong BaseAddress { get; }

    public bool IsModified(WriteOrigin side, ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return GetDirtyMask(side).AnyInRange(start, end);
    }

    public bool WasGpuWritten(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return _gpuEver.AnyInRange(start, end);
    }

    // Read without the lock by GuestPageTracker.InvalidateRegion ([local] SHARPEMU_CPU_DIRTY_SKIP).
    public bool IsCpuDirtyThroughout(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return _cpuDirty.AllInRange(start, end);
    }

    // [local] With a deferred upload, a clean page waiting for its protection is still writable: callers that skip a
    // write on this answer also check IsCpuDirtyThroughout.
    public bool IsCpuWritableThroughout(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return _writable.AllInRange(start, end);
    }

    public bool IsGpuOwned(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return _gpuDirty.AllInRange(start, end);
    }

    public bool IsCpuWriteHot(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        for (var page = start; page < end; page++)
        {
            if (!_hotCpuWrites.Get(page))
            {
                return false;
            }
        }

        return true;
    }

    public ulong CountCpuWriteHotBytes(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        ulong bytes = 0;
        foreach (var (first, last) in new PageMask(_hotCpuWrites, start, end))
        {
            bytes += Math.Min((ulong)last * PageBytes, offset + size) - Math.Max((ulong)first * PageBytes, offset);
        }
        return bytes;
    }

    // Count only clean-to-dirty transitions. Repeated reads cannot make a page hot.
    // Returns whether this write changed ownership from clean to CPU-dirty.
    // Callers use the result to avoid repeating protection bookkeeping for a
    // page that was already CPU-owned.
    public bool MarkCpuWrite(ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        var changed = false;
        for (var page = start; page < end; page++)
        {
            if (_cpuDirty.Get(page))
            {
                continue;
            }

            changed = true;

            if (_recentCpuUploads.Get(page))
            {
                if (_repeatedCpuWrites.Get(page))
                {
                    _hotCpuWrites.Set(page);
                }
                else
                {
                    _repeatedCpuWrites.Set(page);
                }

                _recentCpuUploads.Unset(page);
            }
            else
            {
                _repeatedCpuWrites.Unset(page);
            }
        }

        // The page is already writable and CPU-owned. Re-running ChangeState
        // scans every page in the 4 MiB region and republishes the same summary,
        // once for every managed guest-store; do neither when ownership did not
        // change. Callers hold Lock while reading and updating these masks. A dirty page an emulator write kept
        // protected still needs ChangeState to release it.
        if (!changed && !_protectedWhileDirty.AnyInRange(start, end))
        {
            return false;
        }

        ChangeState(WriteOrigin.Cpu, enable: true, address, size);
        return true;
    }

    public void MarkCpuWriteKeepingProtection(ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        if (new PageMask(_gpuDirty, start, end).Any)
        {
            PageGuard.OnFatal("Cannot mark GPU-dirty pages as CPU-dirty.");
        }

        foreach (var (runStart, runEnd) in new PageMask(~_cpuDirty, start, end))
        {
            _protectedWhileDirty.SetRange(runStart, runEnd);
        }

        Volatile.Write(ref _cpuDirtyings, _cpuDirtyings + 1); // [local]
        _cpuDirty.SetRange(start, end);
        UpdateCpuProtection(ProtectionCause.UploadReprotect, ProtectionCause.EmulatorWrite);
    }

    public void ChangeState(WriteOrigin side, bool enable, ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        if (enable && side == WriteOrigin.Cpu && _gpuDirty.AnyInRange(start, end))
        {
            PageGuard.OnFatal("Cannot mark GPU-dirty pages as CPU-dirty.");
        }

        if (enable && side == WriteOrigin.Gpu && _cpuDirty.AnyInRange(start, end))
        {
            PageGuard.OnFatal("Cannot mark CPU-dirty pages as GPU-dirty.");
        }

        ref var bits = ref GetDirtyMask(side);
        if (enable || side == WriteOrigin.Gpu)
        {
            Volatile.Write(ref _cpuDirtyings, _cpuDirtyings + 1); // [local]
        }

        if (enable && side == WriteOrigin.Cpu)
        {
            AfterCpuDirtyingCountedForTests?.Invoke(); // [local] SHARPEMU_LITE_OBTAIN tests: counted, not yet published
        }

        if (enable && side == WriteOrigin.Cpu)
        {
            _protectedWhileDirty.UnsetRange(start, end);
        }

        if (enable && side == WriteOrigin.Gpu)
        {
            bits.SetRangeAndClear(start, end, ref _recentCpuUploads, ref _repeatedCpuWrites, ref _hotCpuWrites);
            _gpuEver.SetRange(start, end);
        }
        else if (enable)
        {
            bits.SetRange(start, end);
        }
        else
        {
            bits.UnsetRange(start, end);
        }

        if (side == WriteOrigin.Cpu)
        {
            UpdateCpuProtection();
        }
        else
        {
            UpdateGpuProtection();
        }
    }

    // Hot read-only pages stay writable and dirty, so each obtain observes current CPU bytes.
    // [local] With deferProtection the cleared pages stay writable until ApplyPendingCpuProtection; returns whether
    // any are pending. The caller must read their bytes only after that call.
    public bool ForEachCpuUploadRange(
        bool preserveHotPages,
        ulong address,
        ulong size,
        List<(TrackedRegion Region, ulong Address, ulong Size)> cleared,
        Action<ulong, ulong> visitUpload,
        long uploadBatch = -1,
        bool deferProtection = false)
    {
        var (start, end) = GetPageRange(address, size);
        if (!_cpuDirty.AnyInRange(start, end))
        {
            return UpdateCpuProtection(ProtectionCause.UploadReprotect, ProtectionCause.CpuWriteRelease, deferProtection);
        }

        var upload = new PageMask(_cpuDirty, start, end);
        var dedupeHotPages = preserveHotPages && uploadBatch >= 0;
        if (dedupeHotPages)
        {
            if (_uploadBatch != uploadBatch)
            {
                _uploadedHotPages = default;
                _uploadBatch = uploadBatch;
            }

            var alreadyUploaded = upload & _hotCpuWrites & _uploadedHotPages;
            if (alreadyUploaded.Any)
            {
                upload = upload & ~alreadyUploaded;
                Interlocked.Increment(ref _hotUploadsSkipped);
            }
        }

        var clearedPages = preserveHotPages ? upload & ~_hotCpuWrites : upload;
        if (DeferredProtectProbe.Enabled) // [local]
            DeferredProtectProbe.NoteCleared(deferProtection, clearedPages.Count, (clearedPages & _hotCpuWrites).Count);
        foreach (var (runStart, runEnd) in clearedPages)
        {
            cleared.Add((this, BaseAddress + (ulong)runStart * PageBytes, (ulong)(runEnd - runStart) * PageBytes));
            _cpuDirty.UnsetRange(runStart, runEnd);
            _recentCpuUploads.SetRange(runStart, runEnd);
        }

        var pending = UpdateCpuProtection(ProtectionCause.UploadReprotect, ProtectionCause.CpuWriteRelease, deferProtection);
        foreach (var (runStart, runEnd) in upload)
        {
            visitUpload(BaseAddress + (ulong)runStart * PageBytes, (ulong)(runEnd - runStart) * PageBytes);
        }

        if (dedupeHotPages)
        {
            foreach (var (runStart, runEnd) in upload & _hotCpuWrites)
            {
                _uploadedHotPages.SetRange(runStart, runEnd);
            }
        }

        return pending;
    }

    // [local] Protects the clean pages a deferred upload left writable. Call with the lock held.
    public void ApplyPendingCpuProtection() => UpdateCpuProtection();

    // [local] SHARPEMU_PROTECT_OUTSIDE_LOCK: ApplyPendingCpuProtection in two halves. With the lock held, this waits
    // for another thread's call in flight, takes the gate and returns the pages to protect, already recorded as
    // protected; the caller releases the lock and then calls CompletePendingCpuProtection, which makes the call and
    // releases the gate. Always pair the two, also when the mask is empty. The CPU-dirty summary and version need no
    // update here: every change to _cpuDirty already published them through UpdateCpuProtection.
    public PageMask TakePendingCpuProtection()
    {
        EnterProtectGate();
        try
        {
            _protectedWhileDirty = _protectedWhileDirty & _cpuDirty;
            _summary?.Set(BaseAddress / BlockBytes, _cpuDirty.Any);
            var writable = _cpuDirty & ~_protectedWhileDirty;
            var release = writable & ~_writable;
            if (release.Any)
            {
                // Not expected (dirty pages are released when they turn dirty); kept under the lock, as in
                // UpdateCpuProtection.
                _pages.RemoveWatchMask(BaseAddress, release, blockReads: false, ProtectionCause.CpuWriteRelease);
            }

            var protect = _writable & ~writable;
            _writable = writable;
            return protect;
        }
        catch
        {
            ExitProtectGate();
            throw;
        }
    }

    public void CompletePendingCpuProtection(in PageMask protect)
    {
        try
        {
            if (protect.Any)
            {
                _pages.AddWatchMask(BaseAddress, protect, blockReads: false, ProtectionCause.UploadReprotect);
            }
        }
        finally
        {
            ExitProtectGate();
        }
    }

    // [local] Spin gate (runs in the fault handler, which must not block in the kernel). Contended waits are counted
    // for SHARPEMU_UPLOAD_JOB_PROBE.
    private void EnterProtectGate()
    {
        if (Interlocked.CompareExchange(ref _protectGate, 1, 0) == 0)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        do
        {
            Thread.SpinWait(1);
        }
        while (Interlocked.CompareExchange(ref _protectGate, 1, 0) != 0);
        DeferredProtectProbe.NoteGateWait(System.Diagnostics.Stopwatch.GetTimestamp() - started);
    }

    private void ExitProtectGate() => Volatile.Write(ref _protectGate, 0);

    public PageMask TakeCpuDirtyPages(in PageMask candidates)
    {
        var taken = _cpuDirty & candidates;
        if (taken.None)
        {
            return taken;
        }

        foreach (var (runStart, runEnd) in taken)
        {
            _cpuDirty.UnsetRange(runStart, runEnd);
            _recentCpuUploads.SetRange(runStart, runEnd);
        }

        UpdateCpuProtection(ProtectionCause.SweepReprotect, ProtectionCause.CpuWriteRelease);
        return taken;
    }

    public void ReturnCpuDirtyPages(in PageMask pages)
    {
        foreach (var (runStart, runEnd) in pages)
        {
            _recentCpuUploads.UnsetRange(runStart, runEnd);
        }

        foreach (var (runStart, runEnd) in pages & ~_gpuDirty)
        {
            _protectedWhileDirty.UnsetRange(runStart, runEnd);
            _cpuDirty.SetRange(runStart, runEnd);
        }

        Volatile.Write(ref _cpuDirtyings, _cpuDirtyings + 1); // [local]
        UpdateCpuProtection();
    }

    public (ulong Address, ulong Size) ExtendCpuDirtyRun(ulong address, ulong size, ulong lowerBound, ulong upperBound, bool preserveHotPages, int budgetPages)
    {
        var (start, end) = GetPageRange(address, size);
        var lowPage = (int)((Math.Max(lowerBound, BaseAddress) - BaseAddress) / PageBytes);
        var highPage = (int)((Math.Min(upperBound, BaseAddress + BlockBytes) - BaseAddress) / PageBytes);
        var first = start;
        while (first > lowPage && budgetPages > 0 && IsCpuUploadable(first - 1, preserveHotPages))
        {
            first--;
            budgetPages--;
        }

        var last = end;
        while (last < highPage && budgetPages > 0 && IsCpuUploadable(last, preserveHotPages))
        {
            last++;
            budgetPages--;
        }

        if (first == start && last == end)
        {
            return (address, size);
        }

        var runAddress = Math.Max(BaseAddress + (ulong)first * PageBytes, Math.Min(address, lowerBound));
        var runEnd = Math.Min(BaseAddress + (ulong)last * PageBytes, Math.Max(address + size, upperBound));
        return (runAddress, runEnd - runAddress);
    }

    private bool IsCpuUploadable(int page, bool preserveHotPages) =>
        _cpuDirty.Get(page) && !(preserveHotPages && _hotCpuWrites.Get(page));

    public void ResetCpuWriteHeat(ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        _recentCpuUploads.UnsetRange(start, end);
        _repeatedCpuWrites.UnsetRange(start, end);
        _hotCpuWrites.UnsetRange(start, end);
    }

    public void CancelCpuUpload(ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        _recentCpuUploads.UnsetRange(start, end);
    }

    // Visits maximal dirty runs after the bits were cleared and the protection updated.
    public void ForEachModifiedRange(WriteOrigin side, bool clear, ulong address, ulong size, Action<ulong, ulong> visit)
    {
        var (start, end) = GetPageRange(address, size);
        var mask = new PageMask(GetDirtyMask(side), start, end);
        if (clear)
        {
            if (side == WriteOrigin.Gpu)
            {
                Volatile.Write(ref _cpuDirtyings, _cpuDirtyings + 1);
            }

            GetDirtyMask(side).UnsetRange(start, end);
            if (side == WriteOrigin.Cpu)
            {
                UpdateCpuProtection();
            }
            else
            {
                UpdateGpuProtection();
            }
        }

        foreach (var (runStart, runEnd) in mask)
        {
            visit(BaseAddress + (ulong)runStart * PageBytes, (ulong)(runEnd - runStart) * PageBytes);
        }
    }

    private ref PageMask GetDirtyMask(WriteOrigin side) => ref side == WriteOrigin.Cpu ? ref _cpuDirty : ref _gpuDirty;

    // Every change to the CPU-dirty mask ends here, so it also publishes the block summary.
    private void UpdateCpuProtection() => UpdateCpuProtection(ProtectionCause.UploadReprotect, ProtectionCause.CpuWriteRelease);

    // [local] With defer, the pages to protect stay writable (in _writable too) until ApplyPendingCpuProtection;
    // returns whether any are pending. Releases are never deferred.
    private bool UpdateCpuProtection(ProtectionCause protectCause, ProtectionCause releaseCause, bool defer = false)
    {
        _protectedWhileDirty = _protectedWhileDirty & _cpuDirty;
        _summary?.Set(BaseAddress / BlockBytes, _cpuDirty.Any);
        var changed = _cpuDirty ^ _publishedCpuDirty;
        if (changed.Any)
        {
            if ((_cpuDirty & changed).Any)
            {
                _summary?.NoteDirtied();
                Volatile.Write(ref _cpuDirtyPublications, _cpuDirtyPublications + 1); // [local] SHARPEMU_LITE_OBTAIN=2
            }

            _publishedCpuDirty = _cpuDirty;
            Interlocked.Increment(ref _cpuDirtyVersion);
        }

        var writable = _cpuDirty & ~_protectedWhileDirty;
        var protect = _writable & ~writable;
        var release = writable & ~_writable;
        // [local] SHARPEMU_PROTECT_OUTSIDE_LOCK: a watch change waits for a worker's call in flight (see the field).
        var gated = _protectOutsideLock && (release.Any || (!defer && protect.Any));
        if (gated)
        {
            EnterProtectGate();
        }

        try
        {
            if (!defer && protect.Any)
            {
                _pages.AddWatchMask(BaseAddress, protect, blockReads: false, protectCause);
            }

            if (release.Any)
            {
                _pages.RemoveWatchMask(BaseAddress, release, blockReads: false, releaseCause);
            }

            if (defer)
            {
                // The pending pages stay writable in the mask, which is what the pages really are.
                _writable = writable | protect;
                return protect.Any;
            }

            _writable = writable;
            return false;
        }
        finally
        {
            if (gated)
            {
                ExitProtectGate();
            }
        }
    }

    private void UpdateGpuProtection()
    {
        var release = ~_gpuDirty & ~_readable;
        if (release.Any)
        {
            _readable = _readable ^ release;
            _pages.RemoveWatchMask(BaseAddress, release, blockReads: true);
        }

        if ((_readable & _gpuDirty).None)
        {
            return;
        }

        if (_owner is { DefersGpuWriteProtection: true } owner)
        {
            if (!_gpuProtectionQueued)
            {
                _gpuProtectionQueued = true;
                owner.QueueGpuProtection(this);
            }

            return;
        }

        ApplyGpuProtection();
    }

    public void ApplyGpuProtection()
    {
        _gpuProtectionQueued = false;
        var block = _readable & _gpuDirty;
        if (block.None)
        {
            return;
        }

        _readable = _readable ^ block;
        _pages.AddWatchMask(BaseAddress, block, blockReads: true);
    }

    private (int Start, int End) GetPageRange(ulong address, ulong size)
    {
        if (size == 0 || address < BaseAddress || address >= BaseAddress + BlockBytes || size > BaseAddress + BlockBytes - address)
        {
            PageGuard.OnFatal("The requested range is outside the tracking region.");
            return (0, 0);
        }

        var offset = address - BaseAddress;
        return ((int)(offset / PageBytes), (int)((offset + size + PageBytes - 1) / PageBytes));
    }
}
