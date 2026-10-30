// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_RT_RAYQUERY: the triangles of one GTA V BLAS blob ("PSR_BVHL", a GFX10 BVH with pair lists) as an
// index buffer over the blob itself read as floats (vertex stride 4), so an acceleration structure build reads the
// vertices in place.
//
// Header (dwords, measured on 219 blobs, rqm3): 4 total size, 8 the leaf-to-geometry table (= the pair records' end),
// 20 node units, 22 pair records (num_records of the pair V#, from the blob base), 32 the first triangle node unit,
// 33 the first pair record. Pair k (at blob + 8k) is (triangle node pointer (unit << 3) | type, flags); bit 31 of the
// flags ends a leaf's list, bit 30 is opaque. Every pair of [first pair, records) is reached by exactly one leaf
// (walked from the root 0x25 in all 12 dumped blobs, contiguous) and points at its own triangle node.
// A triangle node (64-byte unit) holds four float3 vertices; type 0 tests (V0, V1, V2) and type 1 (V1, V3, V2) as
// (P1, P2, P3), and dword 15 (triangle_id) gives, per type, which of them the guest's i and j weigh (2 bits each at
// type * 8 and type * 8 + 2: 0 = P1, 1 = P2, 2 = P3). The triangle goes to the build as (the third one, P_i, P_j), so
// the hardware barycentrics are the guest's i and j. Primitive p is pair FirstPair + p.
internal static class RayQueryBlas
{
    public sealed record Geometry(uint[] Indices, uint FirstPair, uint PrimitiveCount, uint MaxVertex, Stats Stats);

    public sealed record Stats(int Type0, int Type1, int BadRotations, int Opaque);

    public const int HeaderBytes = 256;

    public static Geometry? Extract(ReadOnlySpan<byte> blob, out string reason)
    {
        reason = string.Empty;
        if (blob.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(blob) != 0x5F525350u ||
            BinaryPrimitives.ReadUInt32LittleEndian(blob[4..]) != 0x4C485642u)
        {
            reason = "magic";
            return null;
        }

        var header = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(blob[..HeaderBytes]);
        var nodeUnits = header[20];
        var records = header[22];
        var firstTriangle = header[32];
        var firstPair = header[33];
        if (records <= firstPair || (ulong)records * 8 > (ulong)blob.Length || (ulong)nodeUnits * 64 > (ulong)blob.Length ||
            firstTriangle >= nodeUnits)
        {
            reason = "header";
            return null;
        }

        var count = records - firstPair;
        var indices = new uint[count * 3];
        int type0 = 0, type1 = 0, badRotations = 0, opaque = 0;
        uint maxVertex = 0;
        for (var p = 0u; p < count; p++)
        {
            var record = (int)((firstPair + p) * 8);
            var node = BinaryPrimitives.ReadUInt32LittleEndian(blob[record..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(blob[(record + 4)..]);
            var type = node & 7;
            var unit = node >> 3;
            if (type > 1 || unit < firstTriangle || unit >= nodeUnits)
            {
                reason = type > 1 ? $"triangle_type_{type}" : "triangle_range";
                return null;
            }

            if (type == 0) type0++; else type1++;
            if ((flags & 0x4000_0000) != 0) opaque++;
            var v0 = unit * 16; // float index of V0
            Span<uint> points = type == 0 ? [v0, v0 + 3, v0 + 6] : [v0 + 3, v0 + 9, v0 + 6];
            var triangleId = BinaryPrimitives.ReadUInt32LittleEndian(blob[(int)(unit * 64 + 60)..]);
            var sourceI = (int)((triangleId >> (int)(type * 8)) & 3);
            var sourceJ = (int)((triangleId >> (int)(type * 8 + 2)) & 3);
            if (sourceI == 3 || sourceJ == 3 || sourceI == sourceJ)
            {
                badRotations++;
                (sourceI, sourceJ) = (1, 2);
            }

            indices[p * 3] = points[3 - sourceI - sourceJ];
            indices[p * 3 + 1] = points[sourceI];
            indices[p * 3 + 2] = points[sourceJ];
            maxVertex = Math.Max(maxVertex, Math.Max(points[0], Math.Max(points[1], points[2])));
        }

        return new Geometry(indices, firstPair, count, maxVertex, new Stats(type0, type1, badRotations, opaque));
    }
}
