// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_RANGE_DUMP=<spec>[;<spec>...], spec = <hash>/<range>/<count>[/<skip>]: after the dispatches
// skip..skip+count-1 of that program, copies the range from its device buffer in stream order to
// %TEMP%/gta/rangedump/<hash>_<n>_<addr>.bin. Range: <hex addr>+<hex size>; ud<i>+<off>+<size> (relative to the 48-bit
// pointer in user data i and i+1); pud<i>+<off>+<size> (at the pointer stored there, read synchronizing: probe only);
// slot<k> (the k-th bound buffer's footprint).
internal static class RangeDumpProbe
{
    private sealed record Entry(ulong Hash, string Range, int Count, int Skip)
    {
        public int Seen;
    }

    private static readonly Entry[] Entries = (Environment.GetEnvironmentVariable("SHARPEMU_RANGE_DUMP") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(spec => spec.Split('/'))
        .Where(parts => parts.Length >= 3)
        .Select(parts => new Entry(Convert.ToUInt64(parts[0], 16), parts[1], int.Parse(parts[2]), parts.Length >= 4 ? int.Parse(parts[3]) : 0))
        .ToArray();
    public static readonly bool Enabled = Entries.Length != 0;
    internal static GuestBufferCache? Cache;

    public static void AfterDispatch(ulong hash, uint[] userData, uint[][] descriptors)
    {
        if (Cache is not { } cache)
            return;
        foreach (var entry in Entries)
        {
            if (entry.Hash != hash)
                continue;
            var n = entry.Seen++;
            if (n < entry.Skip || n >= entry.Skip + entry.Count)
                continue;
            if (!TryResolve(entry.Range, cache, userData, descriptors, out var address, out var size))
                continue;
            var directory = Path.Combine(Path.GetTempPath(), "gta", "rangedump");
            Directory.CreateDirectory(directory);
            Console.Error.WriteLine($"[RANGE_DUMP] hash=0x{hash:X16} n={n} range={entry.Range} address=0x{address:X} size=0x{size:X} userdata={string.Join(' ', userData.Select(word => word.ToString("X8")))}");
            cache.DumpDeviceRange(address, size, Path.Combine(directory, $"{hash:X16}_{n}_{address:X}.bin"));
            if (BlasHeaders && entry.Range == "slot0")
                DumpBlasHeaders(cache, address, size, directory, hash, n);
        }
    }

    // SHARPEMU_RANGE_DUMP_BLAS=1: with a slot0 entry on 0x994A, also dumps 256 B at the BLAS base of every 96-byte source
    // record in that range (dwords 0-1), as <hash>_<n>_blas_<base>.bin.
    private static readonly bool BlasHeaders = Environment.GetEnvironmentVariable("SHARPEMU_RANGE_DUMP_BLAS") == "1";

    private static void DumpBlasHeaders(GuestBufferCache cache, ulong address, ulong size, string directory, ulong hash, int n)
    {
        var bases = new HashSet<ulong>();
        for (ulong offset = 0; offset + 8 <= size; offset += 96)
        {
            var blas = cache.ReadQwordSynchronized(address + offset) & 0xFFFF_FFFF_FFFFUL;
            if (blas != 0 && bases.Add(blas))
                cache.DumpDeviceRange(blas, 0x200, Path.Combine(directory, $"{hash:X16}_{n}_blas_{blas:X}.bin"));
        }

        Console.Error.WriteLine($"[RANGE_DUMP] blas headers={bases.Count}");
    }

    private static bool TryResolve(string range, GuestBufferCache cache, uint[] userData, uint[][] descriptors, out ulong address, out ulong size)
    {
        address = size = 0;
        if (range.StartsWith("slot", StringComparison.Ordinal))
        {
            var slot = int.Parse(range[4..]);
            if (slot >= descriptors.Length || descriptors[slot].Length < 4)
                return false;
            var words = BufferDescriptorWords.From(descriptors[slot]);
            address = words.Address;
            size = words.Footprint() ?? 0;
            return address != 0 && size != 0 && size <= (64UL << 20);
        }

        var throughPointer = range.StartsWith("p", StringComparison.Ordinal);
        if (throughPointer)
            range = range[1..];
        var parts = range.Split('+');
        if (range.StartsWith("ud", StringComparison.Ordinal))
        {
            var index = int.Parse(parts[0][2..]);
            if (userData.Length <= index + 1)
                return false;
            address = (userData[index] | ((ulong)(userData[index + 1] & 0xFFFF) << 32)) + Convert.ToUInt64(parts[1], 16);
            size = Convert.ToUInt64(parts[2], 16);
        }
        else
        {
            address = Convert.ToUInt64(parts[0], 16);
            size = Convert.ToUInt64(parts[1], 16);
        }

        if (throughPointer)
        {
            var pointer = cache.ReadQwordSynchronized(address);
            Console.Error.WriteLine($"[RANGE_DUMP] pointer at 0x{address:X} = 0x{pointer:X}");
            address = pointer & 0xFFFF_FFFF_FFFFUL;
        }

        return address != 0 && size != 0;
    }
}
