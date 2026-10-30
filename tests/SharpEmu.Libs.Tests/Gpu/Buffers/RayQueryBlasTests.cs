// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_RT_RAYQUERY: the index buffer of a BLAS blob addresses each pair's triangle in the blob (float
// indices, vertex stride 4) in the order that makes the hardware barycentrics the guest's i and j.
public sealed class RayQueryBlasTests
{
    // A blob with a 256-byte header, two box units, `triangles` triangle units, then the pair records.
    private static byte[] Blob(uint[] triangleIds, uint[] types)
    {
        var triangles = (uint)triangleIds.Length;
        const uint firstTriangle = 6;
        var nodeUnits = firstTriangle + triangles;
        var firstPair = nodeUnits * 8; // pair records right after the nodes
        var records = firstPair + triangles;
        var bytes = new byte[records * 8 + 64];
        void Put(uint byteOffset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)byteOffset), value);
        Put(0, 0x5F525350);
        Put(4, 0x4C485642);
        Put(4 * 4, (uint)bytes.Length);
        Put(8 * 4, records * 8);
        Put(20 * 4, nodeUnits);
        Put(22 * 4, records);
        Put(32 * 4, firstTriangle);
        Put(33 * 4, firstPair);
        for (var t = 0u; t < triangles; t++)
        {
            var unit = firstTriangle + t;
            Put(unit * 64 + 60, triangleIds[t]);
            Put((firstPair + t) * 8, (unit << 3) | types[t]);
            Put((firstPair + t) * 8 + 4, (t == triangles - 1 ? 0x8000_0000u : 0) | 0x4000_0000u | t);
        }

        return bytes;
    }

    [Fact]
    public void IdentityRotationKeepsTheTestedVertexOrder()
    {
        // Type 0 tests (V0, V1, V2); i weighs P2, j weighs P3 (sources 1 and 2).
        var geometry = RayQueryBlas.Extract(Blob([0b1001], [0]), out var reason);

        Assert.NotNull(geometry);
        Assert.Equal(string.Empty, reason);
        Assert.Equal(1u, geometry!.PrimitiveCount);
        Assert.Equal(7u * 8, geometry.FirstPair);
        var v0 = 6u * 16;
        Assert.Equal([v0, v0 + 3, v0 + 6], geometry.Indices);
        Assert.Equal(1, geometry.Stats.Opaque);
    }

    [Fact]
    public void RotatedAndSecondTriangleOrdersFollowTheGuest()
    {
        // Type 1 tests (V1, V3, V2) as (P1, P2, P3); bits 8-11: i weighs P1 (0), j weighs P3 (2).
        var triangleId = (2u << 10) | (0u << 8);
        var geometry = RayQueryBlas.Extract(Blob([0b1001, triangleId], [0, 1]), out _);

        Assert.NotNull(geometry);
        var v0 = 7u * 16;
        // (the third one = P2 = V3, P_i = P1 = V1, P_j = P3 = V2)
        Assert.Equal([v0 + 9, v0 + 3, v0 + 6], geometry!.Indices[3..6]);
        Assert.Equal(1, geometry.Stats.Type1);
        Assert.Equal(0, geometry.Stats.BadRotations);
    }

    [Fact]
    public void TrianglesTwoAndThreeUseTheFifthVertexAndPaddingIsDegenerate()
    {
        // Identity rotations at bits 16-19 (type 2) and 24-27 (type 3).
        var geometry = RayQueryBlas.Extract(Blob([0x9_0000, 0x900_0000, 0], [2, 3, 7]), out var reason);

        Assert.NotNull(geometry);
        Assert.Equal(string.Empty, reason);
        var first = 6u * 16;
        var second = 7u * 16;
        Assert.Equal([first + 6, first + 9, first + 12], geometry!.Indices[0..3]);
        Assert.Equal([second + 6, second + 12, second], geometry.Indices[3..6]);
        Assert.Equal([first, first, first], geometry.Indices[6..9]);
        Assert.Equal((1, 1, 1), (geometry.Stats.Type2, geometry.Stats.Type3, geometry.Stats.Skipped));
    }

    [Fact]
    public void ABlobWithoutTheMagicOrWithAPairOutsideTheTrianglesIsRefused()
    {
        var blob = Blob([0b1001], [0]);
        blob[0] = 0;
        Assert.Null(RayQueryBlas.Extract(blob, out var magic));
        Assert.Equal("magic", magic);

        blob = Blob([0b1001], [0]);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(7 * 64), 2u << 3);
        Assert.Null(RayQueryBlas.Extract(blob, out var range));
        Assert.Equal("triangle_range", range);
    }
}
