// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RepeatProbeCollection
{
    public const string Name = "RepeatProbe";
}

// [local] SHARPEMU_REPEAT_PROBE: what the part hashes take in and leave out, the pairing of two frames' items, their
// order and runs, and the frame rollover.
[Collection(RepeatProbeCollection.Name)]
public sealed class RepeatProbeTests : IDisposable
{
    private const ulong RingAddress = 0x2_0000_1000;
    private const ulong OutputAddress = 0x3_0000_0000;

    public RepeatProbeTests() => RepeatProbe.ResetForTests();

    public void Dispose() => RepeatProbe.ResetForTests();

    private static uint[] BufferWords(ulong address, uint stride, uint records) =>
        [(uint)address, (uint)((address >> 32) & 0xFFFF) | (stride << 16), records, 0x0002_7FAC];

    private static ShaderProgramInfo Program(uint maxByteExtent = 0) => new()
    {
        Stage = ShaderStageKind.Pixel,
        Hash = 0x1234,
        UserDataBase = 0,
        Resources = new SpecializedResourceInfo
        {
            Info = new ShaderResourceInfo
            {
                Buffers = [new BufferResource { Read = true, MaxByteExtent = maxByteExtent }, new BufferResource { Written = true }],
            },
        },
        Bindings = new BindingLayout { UserDataRegisters = [2u, 3u] },
    };

    private static ShaderStageResources Stage(ShaderProgramInfo program, ulong ring = RingAddress, ulong output = OutputAddress, uint[]? userData = null) =>
        new(program, new ResourceSnapshot
        {
            Buffers = [BufferWords(ring, 16, 4), BufferWords(output, 4, 64)],
            Images = [[1, 2, 3, 4, 5, 6, 7, 8]],
            Samplers = [[9, 10, 11, 12]],
            UserData = userData ?? [0xAAAA, 0xBBBB, 0x10, 0x20],
        }, 0x9000);

    private static RepeatProbe.Parts Hash(in ShaderStageResources stage, RepeatProbe.GuestReader? reader = null, int cap = 1024)
    {
        var parts = default(RepeatProbe.Parts);
        var content = new RepeatProbe.ContentSource(reader, cap);
        RepeatProbe.HashStage(ref parts, in stage, new ShaderProgram(7, 8), in content);
        return parts;
    }

    [Fact]
    public void EqualInputsBuiltApartHashEqual()
    {
        var program = Program();
        var first = Hash(Stage(program));
        var second = Hash(Stage(program, userData: [0xAAAA, 0xBBBB, 0x10, 0x20]));
        Assert.Equal(first.Full(0), second.Full(0));
        Assert.Equal(first.NoRing(0), second.NoRing(0));
        Assert.NotEqual(first.Full(0), first.Full(1)); // the kind is part of the hash

        var left = new DynamicDrawState(1, 2, 3, 4, 0, 1, new ScissorRectangle(0, 0, 8, 8), 1, 0, 0, 0, 0, true, false,
            Silk.NET.Vulkan.CompareOp.Less, false, 0, 0, 0, false, default, default, 1, 0xF);
        var right = new DynamicDrawState(1, 2, 3, 4, 0, 1, new ScissorRectangle(0, 0, 8, 8), 1, 0, 0, 0, 0, true, false,
            Silk.NET.Vulkan.CompareOp.Less, false, 0, 0, 0, false, default, default, 1, 0xF);
        Assert.Equal(RepeatProbe.Bytes(in left), RepeatProbe.Bytes(in right));
        var moved = right with { ViewportX = 5 };
        Assert.NotEqual(RepeatProbe.Bytes(in left), RepeatProbe.Bytes(in moved));
    }

    [Fact]
    public void MovingAReadOnlyBufferChangesTheRingOnly()
    {
        var program = Program();
        var before = Hash(Stage(program));
        var after = Hash(Stage(program, ring: RingAddress + 0x100));
        Assert.Equal(1, before.Masked);
        Assert.NotEqual(before.Ring, after.Ring);
        Assert.Equal(before.Descriptors, after.Descriptors);
        Assert.NotEqual(before.Full(0), after.Full(0));
        Assert.Equal(before.NoRing(0), after.NoRing(0));
    }

    [Fact]
    public void MovingAWrittenBufferChangesTheDescriptors()
    {
        var program = Program();
        var before = Hash(Stage(program));
        var after = Hash(Stage(program, output: OutputAddress + 0x100));
        Assert.Equal(before.Ring, after.Ring);
        Assert.NotEqual(before.Descriptors, after.Descriptors);
        Assert.NotEqual(before.NoRing(0), after.NoRing(0));
    }

    [Fact]
    public void OnlyTheUserDataTheLayoutPushesCounts()
    {
        var program = Program();
        var before = Hash(Stage(program));
        var unconsumed = Hash(Stage(program, userData: [0x1111, 0x2222, 0x10, 0x20]));
        var consumed = Hash(Stage(program, userData: [0xAAAA, 0xBBBB, 0x11, 0x20]));
        Assert.Equal(before.Full(0), unconsumed.Full(0));
        Assert.NotEqual(before.User, consumed.User);
        Assert.Equal(before.Descriptors, consumed.Descriptors);
    }

    [Fact]
    public void ContentReadsTheBytesTheProgramCanReadAndNoMore()
    {
        var memory = new byte[256];
        bool Read(ulong address, Span<byte> destination)
        {
            if (address != RingAddress || destination.Length > memory.Length)
            {
                return false;
            }

            memory.AsSpan(0, destination.Length).CopyTo(destination);
            return true;
        }

        // A 64-byte footprint (16 x 4) read through a program that reads 32 bytes of it.
        var program = Program(maxByteExtent: 32);
        var before = Hash(Stage(program), Read);
        memory[40] = 1; // outside the extent
        Assert.Equal(before.Content, Hash(Stage(program), Read).Content);
        memory[8] = 1;
        var changed = Hash(Stage(program), Read);
        Assert.NotEqual(before.Content, changed.Content);
        Assert.Equal(before.NoRing(0), changed.NoRing(0));
        Assert.NotEqual(before.WithContent(0), changed.WithContent(0));

        // The cap bounds a read the extent does not.
        var unbounded = Program();
        var capped = Hash(Stage(unbounded), Read, cap: 16);
        memory[20] = 1;
        Assert.Equal(capped.Content, Hash(Stage(unbounded), Read, cap: 16).Content);
        var unread = Hash(Stage(unbounded)).Content; // no reader: nothing read
        memory[0] = 2;
        Assert.Equal(unread, Hash(Stage(unbounded)).Content);
        Assert.NotEqual(capped.Content, Hash(Stage(unbounded), Read, cap: 16).Content);
    }

    [Fact]
    public void VertexBuffersAreMaskedAndDynamicStateCounts()
    {
        static RepeatProbe.Parts Draw(ulong vertexAddress, float viewportX)
        {
            var program = Program();
            var vertexInput = new VertexInputInfo
            {
                Buffers = [new VertexInputBuffer(vertexAddress, 12, 100)],
                Stage = Stage(program),
            };
            var record = new ResolvedDraw { Kind = ResolvedDrawKind.Draw };
            record.Draw = new DrawCall("DrawAuto", default, 3, 1, 0);
            record.Dynamic = new DynamicDrawState(viewportX, 0, 8, 8, 0, 1, default, 1, 0, 0, 0, 0, false, false,
                Silk.NET.Vulkan.CompareOp.Always, false, 0, 0, 0, false, default, default, 1, 0xF);
            record.State.Programs = new GraphicsPrograms(vertexInput, new PixelInputInfo()) { Vertex = new ShaderProgram(1) };
            var parts = default(RepeatProbe.Parts);
            var content = new RepeatProbe.ContentSource(null, 0);
            RepeatProbe.HashDraw(ref parts, record, ref record.State, vertexInput, null, in content);
            return parts;
        }

        var before = Draw(0x5000, 0);
        var moved = Draw(0x6000, 0);
        var viewport = Draw(0x5000, 4);
        Assert.Equal(2, before.Masked);
        Assert.Equal(before.NoRing(0), moved.NoRing(0));
        Assert.NotEqual(before.Full(0), moved.Full(0));
        Assert.NotEqual(before.Dynamic, viewport.Dynamic);
        Assert.Equal(before.Ring, viewport.Ring);
    }

    [Fact]
    public void MatchPairsOccurrencesInOrderAsAMultiset()
    {
        var matcher = new RepeatProbe.Matcher();
        ulong[] previous = [10, 10, 20];
        ulong[] current = [10, 20, 10, 10, 30];
        var match = new int[current.Length];
        var pairs = matcher.Match(previous, [true, true, true], current, [true, true, true, true, true], match);
        Assert.Equal(3, pairs);
        Assert.Equal(new[] { 0, 2, 1, -1, -1 }, match);

        // Items that were not hashed never pair, on either side.
        pairs = matcher.Match(previous, [false, true, true], current, [true, true, false, true, true], match);
        Assert.Equal(2, pairs);
        Assert.Equal(new[] { 1, 2, -1, -1, -1 }, match);
    }

    [Fact]
    public void InOrderKeepsOneLongestIncreasingPairing()
    {
        var matcher = new RepeatProbe.Matcher();
        int[] match = [2, 0, 1, -1, 3];
        var inOrder = new bool[match.Length];
        Assert.Equal(3, matcher.InOrder(match, inOrder));
        Assert.Equal(new[] { false, true, true, false, true }, inOrder);
    }

    [Fact]
    public void RunsAreConsecutivePairsOfConsecutiveItems()
    {
        int[] match = [0, 1, 2, -1, 5, 6, 4, 9];
        var runs = new int[match.Length];
        Assert.Equal(3, RepeatProbe.Matcher.Runs(match, runs));
        Assert.Equal(new[] { 3, 3, 3, 0, 2, 2, 1, 1 }, runs);
    }

    private static RepeatProbe.Parts PartsOf(ulong program, ulong ring) => new() { Program = program, Ring = ring, Masked = 1 };

    private static void Item(bool dispatch, RepeatProbe.Parts? parts)
    {
        RepeatProbe.Begin(dispatch);
        if (parts is { } hashed)
        {
            RepeatProbe.NoteForTests(in hashed);
        }

        RepeatProbe.End();
    }

    [Fact]
    public void AFlipClosesTheFrameAndTheNextOneIsCompared()
    {
        Item(false, PartsOf(1, 100));
        Item(false, PartsOf(2, 200));
        Item(true, PartsOf(3, 300));
        Item(false, null); // not hashed: counts, never repeats
        RepeatProbe.CloseFrameForTests();
        Item(false, PartsOf(1, 100)); // same
        Item(false, PartsOf(2, 201)); // its ring moved
        Item(true, PartsOf(4, 300)); // another dispatch
        Item(false, null);
        RepeatProbe.CloseFrameForTests();
        RepeatProbe.CloseFrameForTests(); // an empty frame closes nothing

        var window = RepeatProbe.WindowForTests;
        Assert.Equal(2, window.Frames);
        Assert.Equal(1, window.Compared);
        Assert.Equal(0, window.ContentPairs);
        Assert.Equal(3, window.Items[0]);
        Assert.Equal(1, window.Items[1]);
        Assert.Equal(1, window.RepeatItems[0 * 3 + 0]); // draws, full
        Assert.Equal(2, window.RepeatItems[0 * 3 + 1]); // draws, noring
        Assert.Equal(0, window.RepeatItems[1 * 3 + 0]); // dispatches, full
        Assert.Equal(2, window.Run2Items[0 * 3 + 1]); // the two draws pair in a run of two under noring
        Assert.Equal(1, window.PartItems[1 * RepeatProbe.PartCount + 6]); // the dispatch's ring repeats

        var lines = RepeatProbe.Report(window, 5.0, 0);
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("[PERF][REPEAT] ", lines[0]);
        Assert.Contains("kind=draw", lines[0]);
        Assert.Contains(" noring=66.7/", lines[0]);
        Assert.Contains("kind=dispatch", lines[1]);
    }

    [Fact]
    public void HashesOutsideAnItemAreDropped()
    {
        Item(false, PartsOf(1, 1));
        var parts = PartsOf(2, 2);
        RepeatProbe.NoteForTests(in parts);
        RepeatProbe.CloseFrameForTests();
        Item(false, PartsOf(1, 1));
        RepeatProbe.CloseFrameForTests();
        var window = RepeatProbe.WindowForTests;
        Assert.Equal(2, window.Frames);
        Assert.Equal(1, window.Items[0]);
        Assert.Equal(1, window.Hashed[0]);
        Assert.Equal(1, window.RepeatItems[0]);
    }
}
