// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// [local] IMAGE_BVH_INTERSECT_RAY under the reference lowering and under the SHARPEMU_BVH_UNIFIED_FETCH
// and SHARPEMU_BVH_LEAN_SLABS switches, on random rays and nodes: every output dword must match.
// Each lane tests its own node; a few lanes sit on the page end (a float32 box straddling two pages),
// on the second page, at the T#'s last node and beyond it. Each round is run with the second page
// mapped and unmapped.
public sealed class RayIntersectDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong PageSize = Gen5SpirvTranslator.DeviceAddressPageSize;
    private const ulong GuestBase = 64 * PageSize;
    private const ulong TableEntries = 4096;
    private const uint Lanes = 32;
    private const uint InputBytes = Lanes * 64;
    private const uint OutputBytes = Lanes * 16;
    private const uint LastNode = 300;
    private const uint ScalarResource = 12;
    private const uint AddressBase = 8;
    private const uint ResultBase = 24;
    private const int Rounds = 300;
    private const int RoundsPerRunner = 25;

    private static readonly (string Name, string Unified, string Lean, string PageCache)[] Variants =
    [
        ("reference", "0", "0", "0"),
        ("unified", "1", "0", "0"),
        ("unified-hoisted", "2", "0", "0"),
        ("unified-table", "3", "0", "0"),
        ("unified+page-cache", "1", "0", "1"),
        ("lean", "0", "1", "0"),
        ("unified+lean", "1", "1", "0"),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchedLowerings_MatchTheReference(bool bvh64)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        var program = IntersectProgram(bvh64);
        var compiled = new List<(string Name, ShaderCompileRequest Request, byte[] Spirv)>();
        foreach (var (name, unified, lean, pageCache) in Variants)
        {
            var (plan, resources, layout) = Prepare(program);
            var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = Lanes, ThreadCountX = Lanes };
            var spirv = Compile(request, unified, lean, pageCache);
            compiled.Add((name, request, spirv));
            if (Environment.GetEnvironmentVariable("LOCAL_SPV_OUT") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, $"intersect{(bvh64 ? 64 : 32)}_{name}.spv"), spirv);
            }
        }

        using var harness = new ImageTestHarness(vulkan);
        // A runner's descriptor pool holds 64 sets, so the dispatching runners are rebuilt every
        // RoundsPerRunner rounds; this one only owns the buffers.
        var owner = new LayoutComputeRunner(harness, compiled[0].Request, compiled[0].Spirv);
        LayoutComputeRunner[] runners = [];
        try
        {
            var pages = new[] { owner.CreateBuffer(PageSize), owner.CreateBuffer(PageSize) };
            var io = owner.CreateBuffer(InputBytes + OutputBytes);
            var fault = owner.CreateBuffer(TableEntries / 8);
            var bothPages = owner.CreatePageTable(TableEntries, [(GuestBase, pages[0], 0ul), (GuestBase + PageSize, pages[1], 0ul)]);
            var firstPage = owner.CreatePageTable(TableEntries, [(GuestBase, pages[0], 0ul)]);
            var random = new System.Random(bvh64 ? 0x5EED64 : 0x5EED32);
            var stats = new Stats();
            for (var round = 0; round < Rounds; round++)
            {
                if (round % RoundsPerRunner == 0)
                {
                    foreach (var stale in runners)
                        stale.Dispose();
                    runners = compiled.Select(entry => new LayoutComputeRunner(harness, entry.Request, entry.Spirv)).ToArray();
                }

                var scene = Scene.Create(random, bvh64);
                pages[0].Write(0, scene.Pages[0]);
                pages[1].Write(0, scene.Pages[1]);
                foreach (var table in new[] { bothPages, firstPage })
                {
                    byte[]? reference = null;
                    for (var variant = 0; variant < runners.Length; variant++)
                    {
                        io.Write(0, scene.Input);
                        io.Write(InputBytes, new byte[OutputBytes]);
                        var registers = new uint[64];
                        registers[0] = (uint)GuestBase;
                        registers[1] = (uint)(GuestBase >> 32);
                        registers[6] = InputBytes + OutputBytes;
                        scene.TSharp.CopyTo(registers, (int)ScalarResource);
                        var bound = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
                        {
                            [DescriptorBindingKind.Buffers] = [io],
                            [DescriptorBindingKind.DeviceAddressPageTable] = [table],
                            [DescriptorBindingKind.FaultBuffer] = [fault],
                        };
                        var runner = runners[variant];
                        harness.Run(() => runner.Dispatch(registers, bound, 1, flattenedTable: new uint[64]));
                        var result = runner.ReadBack(io, InputBytes, OutputBytes);
                        if (reference is null)
                        {
                            reference = result;
                            stats.Count(scene, result);
                            continue;
                        }

                        var lean = Variants[variant].Lean == "1";
                        for (var lane = 0; lane < Lanes; lane++)
                        {
                            if (lean && scene.NanPlanes[lane])
                            {
                                stats.LeanSkipped++;
                                continue;
                            }

                            for (var component = 0; component < 4; component++)
                            {
                                var offset = (lane * 4 + component) * 4;
                                var expected = BinaryPrimitives.ReadUInt32LittleEndian(reference.AsSpan(offset));
                                var actual = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(offset));
                                if (expected != actual)
                                {
                                    var unitBytes = (ulong)UnitOf(lane) * 64;
                                    var page = scene.Pages[(int)(unitBytes / PageSize)];
                                    output.WriteLine("input " + string.Join(" ", Enumerable.Range(0, 16).Select(d =>
                                        BinaryPrimitives.ReadUInt32LittleEndian(scene.Input.AsSpan(lane * 64 + d * 4)).ToString("X8"))));
                                    output.WriteLine("node " + string.Join(" ", Enumerable.Range(0, 28).Select(d =>
                                        BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan((int)(unitBytes % PageSize) + d * 4)).ToString("X8"))));
                                    output.WriteLine("tsharp " + string.Join(" ", scene.TSharp.Select(w => w.ToString("X8"))));
                                    output.WriteLine("reference " + string.Join(" ", Enumerable.Range(0, 4).Select(c =>
                                        BinaryPrimitives.ReadUInt32LittleEndian(reference.AsSpan((lane * 4 + c) * 4)).ToString("X8"))));
                                    output.WriteLine("actual " + string.Join(" ", Enumerable.Range(0, 4).Select(c =>
                                        BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan((lane * 4 + c) * 4)).ToString("X8"))));
                                }

                                Assert.True(expected == actual,
                                    $"{compiled[variant].Name}: round {round} lane {lane} type {scene.Types[lane]} dword {component}: " +
                                    $"0x{actual:X8} != reference 0x{expected:X8}");
                            }
                        }
                    }
                }
            }

            output.WriteLine($"bvh64={bvh64} rounds={Rounds} lanes={Lanes}: {stats} on {vulkan.DeviceName}");
            // The random scenes reach every path the switches change.
            Assert.True(stats.TriangleHits > 0 && stats.TriangleMisses > 0, stats.ToString());
            Assert.True(stats.BoxChildHits > 0 && stats.BoxChildMisses > 0, stats.ToString());
            Assert.True(stats.StraddleHits > 0, stats.ToString());
            harness.AssertNoValidationMessages();
        }
        finally
        {
            foreach (var runner in runners)
                runner.Dispose();
            owner.Dispose();
        }
    }

    private static readonly string[] SwitchNames = ["SHARPEMU_BVH_UNIFIED_FETCH", "SHARPEMU_BVH_LEAN_SLABS", "SHARPEMU_BVH_PAGE_CACHE"];

    private static byte[] Compile(ShaderCompileRequest request, params string[] values)
    {
        var previous = SwitchNames.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            for (var index = 0; index < SwitchNames.Length; index++)
                Environment.SetEnvironmentVariable(SwitchNames[index], values[index]);
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            return shader.Spirv;
        }
        finally
        {
            for (var index = 0; index < SwitchNames.Length; index++)
                Environment.SetEnvironmentVariable(SwitchNames[index], previous[index]);
        }
    }

    // v1 = lane * 64; v[8:23] = input[v1]; v[24:27] = intersect(v[8:...], s[12:15]); output[lane * 16] = v[24:27];
    // v30 = load(s[0:1]).
    private static Gen5ShaderProgram IntersectProgram(bool bvh64)
    {
        var opcode = bvh64 ? "ImageBvh64IntersectRay" : "ImageBvhIntersectRay";
        var control = new Gen5RayIntersectControl(AddressBase, [], ResultBase, ScalarResource, A16: false);
        var addressCount = (bvh64 ? 2 : 1) + 1 + 3 + 6;
        var sources = Enumerable.Range(0, addressCount)
            .Select(component => Gen5Operand.Vector(control.GetAddressRegister(component)))
            .Append(Gen5Operand.Scalar(ScalarResource))
            .ToArray();
        var destinations = Enumerable.Range((int)ResultBase, 4).Select(index => Gen5Operand.Vector((uint)index)).ToArray();
        return Program(
            Vop2(0, "VLshlrevB32", 1, Operand(6), Gen5Operand.Vector(0)),
            BufferAccess(4, "BufferLoadDwordx4", 4, offset: 0, dwords: 4, vectorData: 8, offsetEnabled: true, vectorAddress: 1),
            BufferAccess(12, "BufferLoadDwordx4", 4, offset: 16, dwords: 4, vectorData: 12, offsetEnabled: true, vectorAddress: 1),
            BufferAccess(20, "BufferLoadDwordx4", 4, offset: 32, dwords: 4, vectorData: 16, offsetEnabled: true, vectorAddress: 1),
            BufferAccess(28, "BufferLoadDwordx4", 4, offset: 48, dwords: 4, vectorData: 20, offsetEnabled: true, vectorAddress: 1),
            new Gen5ShaderInstruction(36, Gen5ShaderEncoding.Mimg, opcode, [0u, 0u], sources, destinations, control),
            Vop2(48, "VLshlrevB32", 2, Operand(4), Gen5Operand.Vector(0)),
            Vop2(52, "VAddU32", 2, Operand(InputBytes), Gen5Operand.Vector(2)),
            BufferAccess(60, "BufferStoreDwordx4", 4, offset: 0, dwords: 4, vectorData: ResultBase, offsetEnabled: true, vectorAddress: 2),
            // A global load through s[0:1] declares the page table: the resource plan does not see the
            // intersect itself (its control is not an image control), so a program needs another access.
            MoveVector(68, 3, 0),
            GlobalMemory(76, "GlobalLoadDword", 0, 3, 30, 30),
            // The intersect names only the T#'s first register; these reads make s[13:15] user data too.
            MoveScalarRegister(84, 40, ScalarResource + 1),
            MoveScalarRegister(88, 41, ScalarResource + 2),
            MoveScalarRegister(92, 42, ScalarResource + 3),
            EndProgram(96));
    }

    private sealed class Stats
    {
        public int TriangleHits, TriangleMisses, BoxChildHits, BoxChildMisses, StraddleHits, Invalid, LeanSkipped;

        public void Count(Scene scene, byte[] result)
        {
            for (var lane = 0; lane < Lanes; lane++)
            {
                var words = Enumerable.Range(0, 4)
                    .Select(component => BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan((lane * 4 + component) * 4)))
                    .ToArray();
                if (words.All(word => word == uint.MaxValue))
                {
                    Invalid++;
                    continue;
                }

                if (scene.Types[lane] <= 1)
                {
                    if (BitConverter.UInt32BitsToSingle(words[0]) == float.PositiveInfinity && words[1] == 0x3F80_0000)
                        TriangleMisses++;
                    else
                        TriangleHits++;
                    continue;
                }

                var hits = words.Count(word => word != uint.MaxValue);
                BoxChildHits += hits;
                BoxChildMisses += 4 - hits;
                if (lane == StraddleLane)
                    StraddleHits += hits;
            }
        }

        public override string ToString() =>
            $"triangle hits={TriangleHits} misses={TriangleMisses} box children hit={BoxChildHits} missed={BoxChildMisses} " +
            $"straddle children hit={StraddleHits} all-invalid lanes={Invalid} lean lane comparisons skipped (NaN plane)={LeanSkipped}";
    }

    private const int StraddleLane = 28;

    // The node unit of each lane: two 64-byte units per lane on the first page, then the special lanes.
    private static uint UnitOf(int lane) => lane switch
    {
        StraddleLane => (uint)(PageSize / 64) - 1, // the last unit of the first page
        29 => (uint)(PageSize / 64) + 3,           // on the second page
        30 => LastNode,                            // a float32 box here reads one unit past the last node
        31 => LastNode + 100,                      // beyond the last node
        _ => (uint)lane * 2,
    };

    private sealed class Scene
    {
        public required byte[][] Pages { get; init; }
        public required byte[] Input { get; init; }
        public required uint[] TSharp { get; init; }
        public required uint[] Types { get; init; }

        // A box lane where some child has a NaN plane distance, (bound - origin) * inverse: there the
        // reference's NaN-propagating max3/min3 is what the driver makes of it (NVIDIA folds the select
        // into a NaN-dropping max), while SHARPEMU_BVH_LEAN_SLABS always misses, as the lowering intends.
        public required bool[] NanPlanes { get; init; }

        private static readonly uint[] TypeChoices = [0, 0, 0, 1, 1, 1, 4, 4, 5, 5, 5, 5, 5, 2, 3, 6, 7];

        public static Scene Create(System.Random random, bool bvh64)
        {
            var pages = new[] { new byte[PageSize], new byte[PageSize] };
            random.NextBytes(pages[0]);
            random.NextBytes(pages[1]);
            var input = new byte[InputBytes];
            var types = new uint[Lanes];
            var nanPlanes = new bool[Lanes];
            for (var lane = 0; lane < Lanes; lane++)
            {
                var type = lane == StraddleLane ? 5u : TypeChoices[random.Next(TypeChoices.Length)];
                types[lane] = type;
                var unit = UnitOf(lane);
                var ray = Ray.Create(random);
                var node = type switch
                {
                    0 or 1 => TriangleNode(random, ray),
                    4 => Box16Node(random, ray),
                    5 => Box32Node(random, ray),
                    _ => null,
                };
                if (type is 4 or 5)
                    nanPlanes[lane] = HasNanPlane(node!, type == 4, ray);
                if (node is not null)
                {
                    for (var dword = 0; dword < node.Length; dword++)
                    {
                        var byteAddress = (ulong)unit * 64 + (ulong)dword * 4;
                        var page = (int)(byteAddress / PageSize);
                        BinaryPrimitives.WriteUInt32LittleEndian(pages[page].AsSpan((int)(byteAddress % PageSize)), node[dword]);
                    }
                }

                var registers = new uint[16];
                var next = 0;
                registers[next++] = (unit << 3) | type;
                if (bvh64)
                    registers[next++] = 0;
                registers[next++] = Bits(ray.Extent);
                foreach (var value in ray.Origin.Concat(ray.Direction).Concat(ray.Inverse))
                    registers[next++] = Bits(value);
                for (var dword = 0; dword < registers.Length; dword++)
                    BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(lane * 64 + dword * 4), registers[dword]);
            }

            var baseUnits = GuestBase >> 8;
            var boxSort = random.Next(4) != 0;
            var boxGrow = random.Next(3) == 0 ? (uint)random.Next(256) : 0u;
            var barycentrics = random.Next(2) == 0;
            uint[] tSharp =
            [
                (uint)baseUnits,
                (uint)(baseUnits >> 32) & 0xFF | boxGrow << 23 | (boxSort ? 1u << 31 : 0),
                LastNode,
                barycentrics ? 1u << 24 : 0,
            ];
            return new Scene { Pages = pages, Input = input, TSharp = tSharp, Types = types, NanPlanes = nanPlanes };
        }

        private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

        private static bool HasNanPlane(uint[] node, bool fp16, Ray ray)
        {
            for (var child = 0; child < 4; child++)
            {
                for (var bound = 0; bound < 6; bound++)
                {
                    var index = child * 6 + bound;
                    var value = fp16
                        ? (float)BitConverter.UInt16BitsToHalf((ushort)(node[4 + index / 2] >> (index % 2 * 16)))
                        : BitConverter.UInt32BitsToSingle(node[4 + index]);
                    var axis = bound % 3;
                    if (float.IsNaN((value - ray.Origin[axis]) * ray.Inverse[axis]))
                        return true;
                }
            }

            return false;
        }

        private static float Uniform(System.Random random, float low, float high) => low + (float)random.NextDouble() * (high - low);

        // Occasionally an exact infinity or NaN, so the slab and triangle tests see them.
        private static float Special(System.Random random, float value) => random.Next(150) switch
        {
            0 => float.PositiveInfinity,
            1 => float.NegativeInfinity,
            2 => float.NaN,
            _ => value,
        };

        private static uint[] TriangleNode(System.Random random, Ray ray)
        {
            var node = new uint[16];
            var t = Uniform(random, -1, 6);
            var degenerate = random.Next(20) == 0;
            for (var vertex = 0; vertex < 4; vertex++)
            {
                for (var axis = 0; axis < 3; axis++)
                {
                    var center = ray.Origin[axis] + ray.Direction[axis] * t;
                    var value = degenerate ? center : center + Uniform(random, -1, 1);
                    node[vertex * 3 + axis] = Bits(random.Next(60) == 0 ? Special(random, value) : value);
                }
            }

            for (var dword = 12; dword < 16; dword++)
                node[dword] = (uint)random.Next() ^ (uint)random.Next() << 16;
            return node;
        }

        private static (float[] Min, float[] Max) ChildBox(System.Random random, Ray ray)
        {
            var min = new float[3];
            var max = new float[3];
            var t = Uniform(random, -2, 8);
            for (var axis = 0; axis < 3; axis++)
            {
                var center = ray.Origin[axis] + ray.Direction[axis] * t + Uniform(random, -0.5f, 0.5f);
                var half = Uniform(random, 0.05f, 1.5f);
                min[axis] = center - half;
                max[axis] = center + half;
                switch (random.Next(30))
                {
                    case 0:
                        (min[axis], max[axis]) = (max[axis], min[axis]);
                        break;
                    case 1:
                        min[axis] = ray.Origin[axis]; // with a zero direction component: 0 * inf
                        break;
                    case 2:
                        max[axis] = ray.Origin[axis];
                        break;
                    case 3:
                        min[axis] = float.NegativeInfinity;
                        max[axis] = float.PositiveInfinity;
                        break;
                    default:
                        min[axis] = Special(random, min[axis]);
                        max[axis] = Special(random, max[axis]);
                        break;
                }
            }

            if (random.Next(15) == 0)
            {
                Array.Fill(min, float.PositiveInfinity);
                Array.Fill(max, float.NegativeInfinity);
            }

            return (min, max);
        }

        private static uint ChildPointer(System.Random random) =>
            random.Next(8) == 0 ? uint.MaxValue : (uint)random.Next() ^ (uint)random.Next() << 16;

        private static uint[] Box32Node(System.Random random, Ray ray)
        {
            var node = new uint[28];
            for (var child = 0; child < 4; child++)
            {
                node[child] = ChildPointer(random);
                var (min, max) = ChildBox(random, ray);
                for (var axis = 0; axis < 3; axis++)
                {
                    node[4 + child * 6 + axis] = Bits(min[axis]);
                    node[4 + child * 6 + 3 + axis] = Bits(max[axis]);
                }
            }

            return node;
        }

        private static uint[] Box16Node(System.Random random, Ray ray)
        {
            var node = new uint[16];
            var halves = new ushort[24];
            for (var child = 0; child < 4; child++)
            {
                node[child] = ChildPointer(random);
                var (min, max) = ChildBox(random, ray);
                for (var axis = 0; axis < 3; axis++)
                {
                    halves[child * 6 + axis] = BitConverter.HalfToUInt16Bits((Half)min[axis]);
                    halves[child * 6 + 3 + axis] = BitConverter.HalfToUInt16Bits((Half)max[axis]);
                }
            }

            for (var dword = 0; dword < 12; dword++)
                node[4 + dword] = halves[dword * 2] | (uint)halves[dword * 2 + 1] << 16;
            return node;
        }
    }

    private sealed class Ray
    {
        public required float[] Origin { get; init; }
        public required float[] Direction { get; init; }
        public required float[] Inverse { get; init; }
        public required float Extent { get; init; }

        public static Ray Create(System.Random random)
        {
            var origin = new float[3];
            var direction = new float[3];
            var inverse = new float[3];
            for (var axis = 0; axis < 3; axis++)
            {
                origin[axis] = (float)(random.NextDouble() * 2 - 1);
                direction[axis] = random.Next(8) switch
                {
                    0 => 0f,
                    1 => -0f,
                    _ => (float)(random.NextDouble() * 2 - 1),
                };
                inverse[axis] = 1f / direction[axis];
            }

            var extent = random.Next(10) == 0 ? float.PositiveInfinity : (float)(0.5 + random.NextDouble() * 10);
            return new Ray { Origin = origin, Direction = direction, Inverse = inverse, Extent = extent };
        }
    }
}
