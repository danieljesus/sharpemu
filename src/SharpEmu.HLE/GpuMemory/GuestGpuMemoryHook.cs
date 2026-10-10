// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public static class GuestGpuMemoryHook
{
    private static readonly ulong _tracePage = ParseTracePage();
    private static readonly bool _traceFirstDeviceFault = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GPU_MEMORY_ADDRESS"), "auto", StringComparison.OrdinalIgnoreCase);
    private static long _selectedTracePage;

    public static void SelectDeviceFaultTracePage(ulong address)
    {
        if (!_traceFirstDeviceFault || !TrySelectTracePage(ref _selectedTracePage, address)) return;
        Trace(address, 1, "device-address-trace-selected");
    }

    internal static bool TrySelectTracePage(ref long selectedPage, ulong address)
    {
        var page = address & ~(TrackerLayout.PageBytes - 1);
        return page != 0 && Interlocked.CompareExchange(ref selectedPage, unchecked((long)page), 0) == 0;
    }

    private static ulong ParseTracePage()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GPU_MEMORY_ADDRESS");
        if (value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
            value = value[2..];
        return ulong.TryParse(value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var address) && address != 0
            ? address & ~(TrackerLayout.PageBytes - 1) : 0;
    }

    public static ulong TraceAddress =>
        _traceFirstDeviceFault ? unchecked((ulong)Interlocked.Read(ref _selectedTracePage)) : _tracePage;

    public static bool TraceEnabled => _traceFirstDeviceFault || _tracePage != 0;

    public static bool Traces(ulong address, ulong size) =>
        OverlapsTracePage(TraceAddress, address, size) || TracesExtra(address, size);

    // [local] extra pages selected at runtime (BVH header pages); noisy per-shader lines are skipped for them.
    public static readonly HashSet<ulong> ExtraPages = new();
    public static bool TracesExtra(ulong address, ulong size)
    {
        if (size == 0 || ExtraPages.Count == 0) return false; // [local] no lock while nothing is watched
        lock (ExtraPages)
        {
            if (ExtraPages.Count == 0) return false;
            var first = address >> 12; var last = (address + size - 1) >> 12;
            if (last - first > 4096)
            {
                foreach (var page in ExtraPages) if (page >= first && page <= last) return true;
                return false;
            }
            for (var page = first; page <= last; page++) if (ExtraPages.Contains(page)) return true;
            return false;
        }
    }

    internal static bool OverlapsTracePage(ulong page, ulong address, ulong size) =>
        page != 0 && size != 0 && (address <= page
            ? page - address < size : address - page < TrackerLayout.PageBytes);

    public static void Trace(ulong address, ulong size, string detail)
    {
        if (!OverlapsTracePage(TraceAddress, address, size) && (detail.StartsWith("device-address-program", StringComparison.Ordinal) || detail.StartsWith("device-address-range", StringComparison.Ordinal) && !detail.Contains("written=True", StringComparison.Ordinal) || detail.StartsWith("device-address-preparation", StringComparison.Ordinal) || detail.StartsWith("device-address-touch", StringComparison.Ordinal))) return; // [local] (written device-address ranges are kept)
        if (Traces(address, size))
            Console.Error.WriteLine($"[GPU][MEMORY_TRACE] tid={Environment.CurrentManagedThreadId} addr=0x{address:X} size=0x{size:X} {detail}");
    }

    private static GuestGpuMemory? _current;

    // Keep these counts for the shutdown summary.
    private static long _mapped;
    private static long _unmapped;
    private static long _faultsResolved;
    private static long _faultsDeclined;
    private static int _summaryTaken;

    public static GuestGpuMemory? Current => _current;

    // [local] Guest mappings whose protection lets the GPU write (ORBIS GPU_WRITE).
    private static readonly SpanSet _gpuWritable = new();

    public static void NoteGpuWritable(ulong address, ulong size, bool writable)
    {
        lock (_gpuWritable)
        {
            if (writable)
                _gpuWritable.Add(address, size);
            else
                _gpuWritable.Remove(address, size);
        }
    }

    public static bool IsGpuWritable(ulong address, ulong size)
    {
        lock (_gpuWritable)
            return _gpuWritable.Contains(address, size);
    }

    public static void Attach(GuestGpuMemory? memory)
    {
        if (memory != null && _current != null)
        {
            Environment.FailFast("Cannot attach a GPU memory manager while another manager is attached.");
        }

        if (memory != null)
        {
            Interlocked.Exchange(ref _mapped, 0);
            Interlocked.Exchange(ref _unmapped, 0);
            Interlocked.Exchange(ref _faultsResolved, 0);
            Interlocked.Exchange(ref _faultsDeclined, 0);
            Interlocked.Exchange(ref _summaryTaken, 0);
            Interlocked.Exchange(ref _selectedTracePage, 0);
        }

        _current = memory;
    }

    public static bool IsWithinGpuAddressSpace(ulong address, ulong size)
    {
        // Exclude the upper address limit here. GuestSpan.IsValid includes that limit.
        return address != 0 && size != 0 && address < TrackerLayout.SpaceBytes && size < TrackerLayout.SpaceBytes - address;
    }

    public static void NoteMapped(ulong address, ulong size, GuestPageProtection protection)
    {
        if (_current == null || !IsWithinGpuAddressSpace(address, size))
        {
            return;
        }

        Interlocked.Increment(ref _mapped);
        _current.Register(address, size, protection);
    }

    // True when the manager applied the protection itself; the caller then does not protect.
    public static bool NoteProtected(ulong address, ulong size, GuestPageProtection protection) =>
        _current != null && IsWithinGpuAddressSpace(address, size) && _current.NoteProtected(address, size, protection);

    public static bool OverlapsRegistered(ulong address, ulong size) =>
        _current != null && IsWithinGpuAddressSpace(address, size) && _current.OverlapsRegistered(address, size);

    public static void NoteUnmapped(ulong address, ulong size)
    {
        if (_current == null || !IsWithinGpuAddressSpace(address, size))
        {
            return;
        }

        Interlocked.Increment(ref _unmapped);
        _current.Unregister(address, size);
    }

    public static bool TryResolveFault(FaultKind kind, ulong address)
    {
        if (_current == null && Traces(address, 8))
            Trace(address, 8, $"fault={kind} result=no-manager");
        if (_current != null && _current.TryResolveFault(kind, address))
        {
            Interlocked.Increment(ref _faultsResolved);
            return true;
        }

        Interlocked.Increment(ref _faultsDeclined);
        return false;
    }

    // Notify the stores before a managed write changes guest memory.
    public static void MarkCpuWrite(ulong address, ulong size)
    {
        if (size == 0 || _current is not { } current)
        {
            return;
        }

        _ = current.MarkCpuWrite(address, size);
    }

    // Shutdown can skip disposal. Report the counts once for each attached session.
    public static bool TryTakeShutdownSummary(out string summary)
    {
        summary = string.Empty;
        if (_current == null || Interlocked.Exchange(ref _summaryTaken, 1) != 0)
        {
            return false;
        }

        summary = GetSummary();
        SharpEmu.HLE.GuestMemory.GuestMemoryProfile.WriteReport();
        return true;
    }

    public static string GetSummary() =>
        $"gpu_memory: mapped={Interlocked.Read(ref _mapped)} unmapped={Interlocked.Read(ref _unmapped)} faults_resolved={Interlocked.Read(ref _faultsResolved)} faults_declined={Interlocked.Read(ref _faultsDeclined)}";
}
