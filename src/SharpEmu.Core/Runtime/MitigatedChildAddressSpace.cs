// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Runtime;

// What the parent adds to the mitigated child's launch so the host's own startup allocations stay out of the
// guest window.
public readonly record struct MitigatedChildAddressSpacePlan(ulong Policy1Bits, string? GcRegionRange)
{
    public bool IsActive => Policy1Bits != 0;
}

// [local] mapeo2: SHARPEMU_CHILD_ASLR=low turns bottom-up and high-entropy ASLR off for the mitigated child.
// With both on, the CLR's thread stacks and the process heap start at a random base in the low TiB, which
// is almost always inside the guest window and sometimes inside a band the game maps MAP_FIXED. With both
// off they are placed first-fit from the bottom of the address space, far below the window. First-fit also
// applies to the GC's region range, so the toggle bounds it with DOTNET_GCRegionRange unless the user set
// that variable or SHARPEMU_CHILD_GC_REGION_RANGE ("keep" leaves the runtime default).
// [local] fixedmap (2026-10-18): "low" is the default; SHARPEMU_CHILD_ASLR=high restores the runtime's ASLR. With ASLR
// on, the CLR's startup threads (main, finalizer, tiering, the server GC's) reserve their stacks before the guest window
// is pre-reserved, at random bases inside it: GTA V's pre-reservation found 33 foreign regions (11 allocations of
// 0x180000-0x200000, thread stacks) in the window, and a MAP_FIXED band landing on one of them killed the boot
// ("Fixed mapping cannot claim its host range", then an access violation in the guest; m3*R 10-16, gcV3 10-17).
public static class MitigatedChildAddressSpace
{
    public const ulong BottomUpAslrAlwaysOff = 0x2UL << 16;
    public const ulong HighEntropyAslrAlwaysOff = 0x2UL << 20;
    public const string ToggleVariable = "SHARPEMU_CHILD_ASLR";
    public const string GcRangeVariable = "SHARPEMU_CHILD_GC_REGION_RANGE";
    public const string DotnetGcRegionRangeVariable = "DOTNET_GCRegionRange";
    public const string DefaultGcRegionRange = "0x400000000";

    // Low unless the toggle names another mode ("high" keeps the runtime's ASLR).
    public static bool IsLow(Func<string, string?> environment)
    {
        var configured = environment(ToggleVariable)?.Trim();
        return string.IsNullOrEmpty(configured) || string.Equals(configured, "low", StringComparison.OrdinalIgnoreCase);
    }

    public static MitigatedChildAddressSpacePlan Resolve(Func<string, string?> environment)
    {
        if (!IsLow(environment))
        {
            return default;
        }

        const ulong bits = BottomUpAslrAlwaysOff | HighEntropyAslrAlwaysOff;
        if (!string.IsNullOrWhiteSpace(environment(DotnetGcRegionRangeVariable)))
        {
            return new MitigatedChildAddressSpacePlan(bits, null);
        }

        var configured = environment(GcRangeVariable)?.Trim();
        if (string.Equals(configured, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return new MitigatedChildAddressSpacePlan(bits, null);
        }

        var range = !string.IsNullOrEmpty(configured) && HostWindowSurvey.TryParseNumber(configured, out var value) &&
            value != 0
                ? $"0x{value:X}"
                : DefaultGcRegionRange;
        return new MitigatedChildAddressSpacePlan(bits, range);
    }
}
