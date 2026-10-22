// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_RANGE_DUMP=<hash>/<hex addr>+<hex size>/<count>[/<skip>]: after the dispatches skip..skip+count-1 of that
// program, copies the range from its device buffer in stream order to %TEMP%/gta/rangedump/<hash>_<n>_<addr>.bin.
internal static class RangeDumpProbe
{
    private static readonly string[] Spec = (Environment.GetEnvironmentVariable("SHARPEMU_RANGE_DUMP") ?? "").Split('/');
    public static readonly bool Enabled = Spec.Length >= 3;
    private static readonly ulong Hash = Enabled ? Convert.ToUInt64(Spec[0], 16) : 0;
    // <hex addr>+<hex size>, or ud<i>+<hex offset>+<hex size>: relative to the 48-bit pointer in user data i and i+1.
    // pud<i>+<hex offset>+<hex size>: the range starts at the 48-bit pointer stored there (read synchronizing; probe).
    private static readonly bool ThroughPointer = Enabled && Spec[1].StartsWith("p", StringComparison.Ordinal);
    private static readonly string Range = ThroughPointer ? Spec[1][1..] : Enabled ? Spec[1] : "";
    private static readonly bool Relative = Enabled && Range.StartsWith("ud", StringComparison.Ordinal);
    private static readonly int UserIndex = Relative ? int.Parse(Range[2..].Split('+')[0]) : 0;
    private static readonly ulong Address = Enabled ? Convert.ToUInt64(Range.Split('+')[Relative ? 1 : 0].TrimStart('u', 'd'), 16) : 0;
    private static readonly ulong Size = Enabled ? Convert.ToUInt64(Range.Split('+')[Relative ? 2 : 1], 16) : 0;
    private static readonly int Count = Enabled ? int.Parse(Spec[2]) : 0;
    private static readonly int Skip = Spec.Length >= 4 ? int.Parse(Spec[3]) : 0;
    private static int _seen;
    internal static GuestBufferCache? Cache;

    public static void AfterDispatch(ulong hash, uint[] userData)
    {
        if (hash != Hash || Cache is not { } cache)
            return;
        var n = _seen++;
        if (n < Skip || n >= Skip + Count)
            return;
        var directory = Path.Combine(Path.GetTempPath(), "gta", "rangedump");
        Directory.CreateDirectory(directory);
        Console.Error.WriteLine($"[RANGE_DUMP] hash=0x{hash:X16} n={n} userdata={string.Join(' ', userData.Select(word => word.ToString("X8")))}");
        var address = Relative && userData.Length > UserIndex + 1
            ? (userData[UserIndex] | ((ulong)(userData[UserIndex + 1] & 0xFFFF) << 32)) + Address
            : Address;
        if (ThroughPointer)
        {
            var pointer = cache.ReadQwordSynchronized(address);
            Console.Error.WriteLine($"[RANGE_DUMP] pointer at 0x{address:X} = 0x{pointer:X}");
            address = pointer & 0xFFFF_FFFF_FFFFUL;
            if (address == 0)
                return;
        }

        cache.DumpDeviceRange(address, Size, Path.Combine(directory, $"{hash:X16}_{n}_{address:X}.bin"));
    }
}
