// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] Host side of SHARPEMU_BVH_TRI_PROBE=1 (see Gen5SpirvTranslator.TriangleProbe.cs): the triangle test counters
// per 64 dispatches ([GPU][TRI_PROBE]) and each distinct index record once ([GPU][TRI_REC]).
internal sealed partial class RayQueryHle
{
    private static readonly bool TriProbe = Environment.GetEnvironmentVariable("SHARPEMU_BVH_TRI_PROBE") == "1";
    private const int TriProbeFirst = 44;
    private const int TriProbeWords = 20;
    private const int TriRecordFirst = 128;
    private const int TriRecordSlots = 16;
    private const int TriRecordWords = 8;
    private const int MaxTriRecords = 200_000;

    private readonly long[] _triProbe = new long[TriProbeWords];
    private long _leanCompared, _leanMismatches;
    private readonly HashSet<(uint Key, uint Node)> _triRecords = new();
    private long _triDispatches;

    // Under _lock.
    // [local] SHARPEMU_INDIRECT_IMAGE_DIM=2: lanes that selected an indirect candidate of another dimension (word 122)
    // of the lanes of such mixed accesses (word 123), per 256 frames and since the start.
    private long _dimOther, _dimTotal, _dimOtherAll, _dimTotalAll, _dimCases, _dimImages;

    private void NoteIndirectDim(ReadOnlySpan<uint> counters, long frame)
    {
        if (SharpEmu.ShaderCompiler.Resources.ResourceMaterializer.IndirectImageDimMode != 2)
            return;
        _dimOther += counters[122];
        _dimTotal += counters[123];
        _dimCases += counters[124];
        _dimImages += counters[125];
        if (frame % 256 != 0)
            return;
        _dimOtherAll += _dimOther;
        _dimTotalAll += _dimTotal;
        Console.Error.WriteLine($"[GPU][INDIRECT_DIM] frame={frame} other_dimension_lanes={_dimOther} mixed_access_lanes={_dimTotal} total_other={_dimOtherAll} total_mixed={_dimTotalAll} case_access_lanes={_dimCases} image_access_lanes={_dimImages}");
        _dimOther = _dimTotal = _dimCases = _dimImages = 0;
    }

    private void NoteTriProbe(ReadOnlySpan<uint> counters, long frame)
    {
        if (!TriProbe)
            return;
        for (var index = 0; index < TriProbeWords; index++)
            _triProbe[index] += counters[TriProbeFirst + index];
        _leanCompared += counters[120];
        _leanMismatches += counters[121];
        for (var slot = 0; slot < TriRecordSlots; slot++)
        {
            var record = counters.Slice(TriRecordFirst + slot * TriRecordWords, TriRecordWords);
            if (record[0] == 0 || _triRecords.Count >= MaxTriRecords || !_triRecords.Add((record[1], record[2])))
                continue;
            Console.Error.WriteLine($"[GPU][TRI_REC] key=0x{record[1]:X} node=0x{record[2]:X} flags=0x{record[3]:X} idx={record[4]},{record[5]},{record[6]} triid=0x{record[7]:X8}");
        }

        if (++_triDispatches % 64 != 0)
            return;
        var p = _triProbe;
        Console.Error.WriteLine($"[GPU][TRI_PROBE] frame={frame} tests={p[0]} hits={p[1]} den0_num0={p[2]} den0_num={p[3]} nan_inf={p[4]} rejected={p[5]} den0_num0_by_type=[{p[6]},{p[7]},{p[8]},{p[9]}] den0_tests={p[10]} other_nan={p[11]} native_nan_by_class=[{p[12]},{p[13]},{p[14]},{p[15]}] native_only_by_class=[{p[16]},{p[17]},{p[18]},{p[19]}] records={_triRecords.Count} lean_compared={_leanCompared} lean_mismatches={_leanMismatches}");
        Array.Clear(_triProbe);
    }
}
