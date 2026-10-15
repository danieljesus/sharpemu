// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;

namespace SharpEmu.Core.Memory;

// One host allocation (VirtualQuery AllocationBase) seen inside a surveyed range.
internal readonly record struct HostAllocationSummary(
    ulong AllocationBase,
    ulong Start,
    ulong End,
    ulong Bytes,
    ulong CommittedBytes,
    int Runs,
    bool Tracked);

internal readonly record struct HostSurvey(
    IReadOnlyList<HostAllocationSummary> Allocations,
    int Runs,
    ulong Bytes,
    ulong CommittedBytes,
    ulong BytesOutsideBaseline);

// [local] Host-memory survey helpers for the fixed-mapping collision study (mapeo2).
internal static unsafe class HostWindowSurvey
{
    private const int MaxSteps = 1 << 18;

    // Groups the host regions of [start, end) that pass `include` by allocation base, with no cap.
    // Bytes outside every baseline extent are the ones that arrived after the baseline was taken.
    internal static HostSurvey Survey(
        IHostMemory host,
        ulong start,
        ulong end,
        Func<ulong, ulong, bool> isOwned,
        Func<ulong, bool> isTracked,
        IReadOnlyList<(ulong Start, ulong End)>? baseline = null)
    {
        var byBase = new SortedDictionary<ulong, HostAllocationSummary>();
        var runs = 0;
        ulong bytes = 0, committed = 0, outside = 0;
        var cursor = start;
        var steps = 0;
        while (steps++ < MaxSteps && cursor < end && host.Query(cursor, out var info) && info.RegionSize != 0)
        {
            var regionStart = Math.Max(cursor, info.BaseAddress);
            var regionEnd = info.RegionSize > ulong.MaxValue - info.BaseAddress
                ? end
                : Math.Min(end, info.BaseAddress + info.RegionSize);
            if (regionEnd <= cursor)
            {
                break;
            }

            var length = regionEnd - regionStart;
            if (info.State != HostRegionState.Free && !isOwned(regionStart, length))
            {
                var isCommitted = info.State == HostRegionState.Committed;
                runs++;
                bytes += length;
                committed += isCommitted ? length : 0;
                outside += baseline is null ? 0 : BytesOutside(regionStart, regionEnd, baseline);
                byBase[info.AllocationBase] = byBase.TryGetValue(info.AllocationBase, out var existing)
                    ? existing with
                    {
                        Start = Math.Min(existing.Start, regionStart),
                        End = Math.Max(existing.End, regionEnd),
                        Bytes = existing.Bytes + length,
                        CommittedBytes = existing.CommittedBytes + (isCommitted ? length : 0),
                        Runs = existing.Runs + 1,
                    }
                    : new HostAllocationSummary(info.AllocationBase, regionStart, regionEnd, length,
                        isCommitted ? length : 0, 1, isTracked(info.AllocationBase));
            }

            cursor = regionEnd;
        }

        return new HostSurvey(byBase.Values.ToList(), runs, bytes, committed, outside);
    }

    private static ulong BytesOutside(ulong start, ulong end, IReadOnlyList<(ulong Start, ulong End)> baseline)
    {
        var inside = 0UL;
        foreach (var (lo, hi) in baseline)
        {
            var a = Math.Max(start, lo);
            var b = Math.Min(end, hi);
            if (a < b)
            {
                inside += b - a;
            }
        }

        return (end - start) - Math.Min(end - start, inside);
    }

    internal static string Format(HostSurvey survey) =>
        $"foreign_regions={survey.Runs} foreign_bytes=0x{survey.Bytes:X} foreign_allocations={survey.Allocations.Count} " +
        $"foreign_committed=0x{survey.CommittedBytes:X} allocs=[{string.Join(",", survey.Allocations.Select(FormatAllocation))}]";

    internal static string FormatAllocation(HostAllocationSummary a) =>
        $"0x{a.AllocationBase:X}@0x{a.Start:X}+0x{a.End - a.Start:X}:b0x{a.Bytes:X}:c0x{a.CommittedBytes:X}:r{a.Runs}" +
        (a.Tracked ? ":sharpemu" : string.Empty);

    // SHARPEMU_TEST_HOST_BLOCKER=addr[:size[:commitBytes]][,addr...]: host memory placed before the guest window
    // is pre-reserved, standing in for a host allocation that landed inside a fixed guest band.
    internal const string TestHostBlockerVariable = "SHARPEMU_TEST_HOST_BLOCKER";
    internal const ulong DefaultBlockerSize = 0x20_0000;

    internal static bool TryParseBlockers(string? text, out List<(ulong Address, ulong Size, ulong Commit)> blockers)
    {
        blockers = new List<(ulong, ulong, ulong)>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = entry.Split(':');
            if (fields.Length > 3 || !TryParseNumber(fields[0], out var address) || address == 0)
            {
                blockers.Clear();
                return false;
            }

            var size = DefaultBlockerSize;
            ulong commit = 0;
            if ((fields.Length > 1 && (!TryParseNumber(fields[1], out size) || size == 0)) ||
                (fields.Length > 2 && !TryParseNumber(fields[2], out commit)) ||
                commit > size || size > ulong.MaxValue - address)
            {
                blockers.Clear();
                return false;
            }

            blockers.Add((address, size, commit));
        }

        return blockers.Count != 0;
    }

    internal static bool TryParseNumber(string text, out ulong value)
    {
        text = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // Places each blocker as plain reserved (and optionally committed) host memory, never a placeholder, so the
    // guest owner cannot claim it. Returns the blockers that landed at their exact address.
    internal static List<(ulong Address, ulong Size)> ApplyTestHostBlockers(IHostMemory host, string? text, Action<string> log)
    {
        var placed = new List<(ulong, ulong)>();
        if (!TryParseBlockers(text, out var blockers))
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                log($"[LOADER][ERROR] Test host blocker: cannot parse {TestHostBlockerVariable}='{text}'.");
            }

            return placed;
        }

        foreach (var (address, size, commit) in blockers)
        {
            var got = host.Reserve(address, size, HostPageProtection.ReadWrite);
            if (got != address)
            {
                if (got != 0)
                {
                    host.Free(got);
                }

                log($"[LOADER][ERROR] Test host blocker: reserve 0x{address:X}+0x{size:X} returned 0x{got:X}; not placed.");
                continue;
            }

            var committed = commit == 0 || host.Commit(address, commit, HostPageProtection.ReadWrite);
            placed.Add((address, size));
            log($"[LOADER][INFO] Test host blocker: placed 0x{address:X}+0x{size:X} committed_head=0x{commit:X} " +
                $"commit_ok={committed}.");
        }

        return placed;
    }

    // Child-side view of where the host runtime put itself: the ASLR policy the process actually runs with and
    // the GC reservation that holds a managed object. Large reservations below the window end are listed because
    // first-fit placement (bottom-up ASLR off) can put them over the eboot base or the guest window.
    internal static string DescribeHostLayout(IHostMemory host)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "unsupported-host";
        }

        var builder = new StringBuilder();
        uint flags = 0;
        var ok = GetProcessMitigationPolicy(GetCurrentProcess(), ProcessAslrPolicy, &flags, sizeof(uint));
        builder.Append(ok
            ? $"aslr=0x{flags:X} bottom_up={flags & 1} force_relocate={(flags >> 1) & 1} high_entropy={(flags >> 2) & 1} "
            : "aslr=unknown ");
        builder.Append($"process_heap=0x{(ulong)GetProcessHeap():X} ");

        var probe = new byte[64];
        var handle = GCHandle.Alloc(probe, GCHandleType.Pinned);
        var objectAddress = (ulong)handle.AddrOfPinnedObject();
        handle.Free();
        if (host.Query(objectAddress, out var info))
        {
            var reservation = ReservationExtent(host, info.AllocationBase);
            builder.Append($"gc_reservation=0x{info.AllocationBase:X}+0x{reservation:X} ");
        }

        var gcRange = Environment.GetEnvironmentVariable("DOTNET_GCRegionRange");
        builder.Append($"DOTNET_GCRegionRange={(string.IsNullOrEmpty(gcRange) ? "unset" : gcRange)} ");
        var large = Survey(host, 0x1_0000, GuestSpaceOwner.GuestWindowEnd, (_, _) => false, _ => false).Allocations
            .Where(a => a.End - a.Start >= 0x4000_0000)
            .Select(a => $"0x{a.Start:X}+0x{a.End - a.Start:X}");
        builder.Append($"large_below_window_end=[{string.Join(",", large)}]");
        return builder.ToString();
    }

    private static ulong ReservationExtent(IHostMemory host, ulong allocationBase)
    {
        var cursor = allocationBase;
        var steps = 0;
        while (steps++ < MaxSteps && host.Query(cursor, out var info) && info.RegionSize != 0 &&
            info.State != HostRegionState.Free && info.AllocationBase == allocationBase &&
            info.RegionSize <= ulong.MaxValue - info.BaseAddress)
        {
            cursor = info.BaseAddress + info.RegionSize;
        }

        return cursor - allocationBase;
    }

    private const int ProcessAslrPolicy = 1;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern nint GetProcessHeap();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(nint process, int policy, void* buffer, nuint length);
}
