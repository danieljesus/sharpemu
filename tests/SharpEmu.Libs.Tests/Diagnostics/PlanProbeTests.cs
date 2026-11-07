// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;

namespace SharpEmu.Libs.Tests.Diagnostics;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlanProbeCollection
{
    public const string Name = "PlanProbe";
}

// [local] SHARPEMU_PLAN_PROBE: the value hashes of the context groups, the shape key, the slot hashes, the store, the
// runs and the report.
[Collection(PlanProbeCollection.Name)]
public sealed class PlanProbeTests : IDisposable
{
    public PlanProbeTests() => PlanProbe.ResetForTests();

    public void Dispose() => PlanProbe.ResetForTests();

    [Fact]
    public void GroupHashDependsOnTheValuesNotTheOrderOfWrites()
    {
        var first = new PlanRegisterShadow();
        first.Write(DbZInfo, 0x11);
        first.Write(PaClViewportXScale, 0x3F80_0000);
        first.Write(CbColor0Info, 0x22);
        var second = new PlanRegisterShadow();
        second.Write(CbColor0Info, 0x22);
        second.Write(PaClViewportXScale, 0x3F80_0000);
        second.Write(DbZInfo, 0x11);
        Assert.Equal(first.Hashes, second.Hashes);

        var before = first.Hashes;
        first.Write(CbColor0Info, 0x33);
        Assert.NotEqual(before.ColorTargets, first.Hashes.ColorTargets);
        first.Write(CbColor0Info, 0x22); // back to the first value: back to the first hash
        Assert.Equal(before, first.Hashes);
    }

    [Fact]
    public void WrittenZeroDiffersFromUnwritten()
    {
        var written = new PlanRegisterShadow();
        written.Write(CbColor0Info, 0);
        Assert.NotEqual(default(PlanGroupHashes).ColorTargets, written.Hashes.ColorTargets);
        written.Clear();
        Assert.Equal(default, written.Hashes);
    }

    [Fact]
    public void RegistersLandInTheirStampGroup()
    {
        var shadow = new PlanRegisterShadow();
        shadow.Write(PaClViewportXScale, 1);
        Assert.NotEqual(0UL, shadow.Hashes.ContextDynamic);
        Assert.Equal(0UL, shadow.Hashes.ColorTargets);
        Assert.Equal(0UL, shadow.Hashes.PipelineState);

        // A target base address moves the targets group but not the part a pipeline can depend on.
        var formats = shadow.Hashes.TargetFormats;
        shadow.Write(CbColor0Base + 0xF * 3, 0x1000);
        Assert.NotEqual(0UL, shadow.Hashes.ColorTargets);
        Assert.Equal(formats, shadow.Hashes.TargetFormats);
        shadow.Write(CbColor0Info + 0xF * 3, 0x1234);
        Assert.NotEqual(formats, shadow.Hashes.TargetFormats);
        Assert.True(PlanRegisterShadow.IsTargetAddress(DbZReadBase));
        Assert.True(PlanRegisterShadow.IsTargetAddress(CbColor0DccBaseExt + 7));
        Assert.False(PlanRegisterShadow.IsTargetAddress(CbColor0Attrib));

        var depth = shadow.Hashes.DepthTargets;
        shadow.Write(DbDepthControl, 0x77);
        Assert.NotEqual(depth, shadow.Hashes.DepthTargets);
    }

    [Fact]
    public void BanksRestoreTheHashesOnPopAndClearThem()
    {
        var banks = new RegisterBanks(message => new InvalidOperationException(message));
        Assert.Equal(default, banks.PlanHashes);
        banks.NotePlanContextWrite(CbColor0Info, [0x10]);
        var pushed = banks.PlanHashes;
        banks.ApplyContextState(ContextStateOperation.Push);
        banks.NotePlanContextWrite(CbColor0Info, [0x20]);
        Assert.NotEqual(pushed, banks.PlanHashes);
        banks.ApplyContextState(ContextStateOperation.Pop);
        Assert.Equal(pushed, banks.PlanHashes);

        var clone = banks.Clone();
        Assert.Equal(pushed, clone.PlanHashes);
        banks.ApplyContextState(ContextStateOperation.Clear);
        Assert.Equal(default, banks.PlanHashes);
        Assert.Equal(pushed, clone.PlanHashes);
    }

    private static uint[] BufferWords(ulong address, uint stride, uint records, uint word3 = 0x0002_7FAC) =>
        [(uint)address, (uint)((address >> 32) & 0xFFFF) | (stride << 16), records, word3];

    private static VertexInputInfo Vertices(ulong address, uint records, uint word3 = 0x0002_7FAC) => new()
    {
        Buffers = [new VertexInputBuffer(address, 16, records)],
        Attributes = [new VertexAttributeResource(BufferDescriptorWords.From(BufferWords(address, 16, records, word3)), 4, 4, 0, 0, 0, 0)],
    };

    [Fact]
    public void VertexLayoutIgnoresTheBaseAndRecordsButNotTheFormat()
    {
        var layout = PlanProbe.VertexLayout(Vertices(0x2_0000_1000, 64));
        Assert.Equal(layout, PlanProbe.VertexLayout(Vertices(0x2_0080_0000, 300)));
        Assert.NotEqual(layout, PlanProbe.VertexLayout(Vertices(0x2_0000_1000, 64, word3: 0x0002_7FAD)));
    }

    [Fact]
    public void ShapeKeyTakesTheTopologyRestartAndInterpolators()
    {
        ulong Key(int topology = 3, bool restart = false, uint interpolator = 0, PlanProbe.DrawClass drawClass = PlanProbe.DrawClass.Indexed) =>
            PlanProbe.ShapeKey(1, 2, 3, 4, 5, drawClass, 1, 1, topology, restart, 6, [interpolator, 0]);
        var key = Key();
        Assert.Equal(key, Key());
        Assert.NotEqual(key, Key(topology: 4));
        Assert.NotEqual(key, Key(restart: true));
        Assert.NotEqual(key, Key(interpolator: 1));
        Assert.NotEqual(key, Key(drawClass: PlanProbe.DrawClass.Indexed | PlanProbe.DrawClass.DisableBlending));
        Assert.True(PlanProbe.Supported(PlanProbe.DrawClass.Indexed | PlanProbe.DrawClass.Indirect));
        Assert.False(PlanProbe.Supported(PlanProbe.DrawClass.Mesh));
    }

    private static ShaderStageResources Stage(ulong ring, ulong output, uint image0 = 1) =>
        new(new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Pixel,
            Hash = 0x1234,
            Resources = new SpecializedResourceInfo
            {
                Info = new ShaderResourceInfo
                {
                    Buffers = [new BufferResource { Read = true }, new BufferResource { Written = true }],
                },
            },
        }, new ResourceSnapshot
        {
            Buffers = [BufferWords(ring, 16, 4), BufferWords(output, 4, 64)],
            Images = [[image0, 2, 3, 4, 5, 6, 7, 8]],
            Samplers = [[9, 10, 11, 12]],
        });

    [Fact]
    public void SlotsIgnoreReadOnlyBasesAndCountChanges()
    {
        var first = new ulong[2];
        var count = PlanProbe.StageSlots(Stage(0x2_0000_1000, 0x3_0000_0000), ref first, 0);
        Assert.Equal(4, count);

        var moved = new ulong[2];
        PlanProbe.StageSlots(Stage(0x2_0000_8000, 0x3_0000_0000), ref moved, 0);
        Assert.Equal(0, PlanProbe.ChangedSlots(first.AsSpan(0, 4), moved.AsSpan(0, 4)));

        var written = new ulong[2];
        PlanProbe.StageSlots(Stage(0x2_0000_1000, 0x3_0000_4000, image0: 7), ref written, 0);
        Assert.Equal(2, PlanProbe.ChangedSlots(first.AsSpan(0, 4), written.AsSpan(0, 4))); // the T# and the written V#
        Assert.Equal(1, PlanProbe.ChangedSlots(first.AsSpan(0, 4), moved.AsSpan(0, 3))); // a missing slot counts
    }

    [Fact]
    public void StoreEvictsTheLeastRecentlyUsedShape()
    {
        var store = new PlanProbe.Store(3);
        foreach (var key in new ulong[] { 1, 2, 3 })
        {
            store.Take(key).Valid = true;
        }

        Assert.True(store.TryGet(1, out _)); // 1 is now the most recent: 2 goes first
        store.Take(4);
        Assert.Equal(3, store.Count);
        Assert.False(store.Contains(2));
        Assert.True(store.Contains(1) && store.Contains(3) && store.Contains(4));
        store.Take(5);
        Assert.False(store.Contains(3));
        Assert.Equal(2, store.Evictions);
    }

    [Fact]
    public void RunsFollowTheScopeAndTargets()
    {
        var runs = new PlanProbe.Runs();
        for (var index = 0; index < 20; index++) runs.Note(7, 0xA); // one run of 20
        runs.Note(7, 0xB); // other targets
        runs.Note(7, 0xB);
        runs.Note(8, 0xB); // another scope
        runs.Note(0, 0xB); // no scope: never continues
        runs.Note(0, 0xB);
        runs.Close();
        Assert.Equal(25, runs.Draws);
        Assert.Equal(5, runs.Started);
        Assert.Equal(5, runs.Count);
        Assert.Equal(20, runs.DrawsIn16);
        Assert.Equal(1, runs.Percentile(0.5)); // lengths 20, 2, 1, 1, 1
        Assert.Equal(20, runs.Percentile(0.9));
    }

    [Fact]
    public void TopShareCountsTheShapesWithMostDraws()
    {
        var shapes = new Dictionary<ulong, PlanProbe.ShapeCount>
        {
            [1] = new() { Draws = 60, Covered = true },
            [2] = new() { Draws = 30, Covered = false },
            [3] = new() { Draws = 10, Covered = true },
        };
        var scratch = new List<PlanProbe.ShapeCount>();
        Assert.Equal((60.0, 60.0), PlanProbe.TopShare(shapes, 1, 100, scratch));
        Assert.Equal((90.0, 60.0), PlanProbe.TopShare(shapes, 2, 100, scratch));
        Assert.Equal((100.0, 70.0), PlanProbe.TopShare(shapes, 200, 100, scratch));
    }

    [Fact]
    public void ReportLineHasTheGateFields()
    {
        var window = new PlanProbe.Window { Frames = 2, Draws = 4, Hits = 2, TargetsEqual = 2, PipelineEqual = 1, DynamicEqual = 2, AllEqual = 1 };
        window.SlotsChanged[0] = 1;
        window.SlotsChanged[3] = 1;
        window.NoteShape(1, true, 0);
        window.NoteShape(1, true, 0);
        window.NoteShape(2, false, 1);
        window.NoteShape(2, false, 1);
        window.Runs.Note(1, 1);
        window.Runs.Note(1, 1);
        window.Runs.Close();
        var line = PlanProbe.Report(window, 5, 0, 0, 0, new List<PlanProbe.ShapeCount>());
        Assert.StartsWith("[PERF][PLAN] window_s=5.0 frames=2 draws_per_frame=2 shapes=2 shapes_per_frame=1 top10=100.0", line);
        Assert.Contains(" covered=50.0 store_hit=50.0 ", line);
        Assert.Contains(" state_equal=targets:100.0,pipeline:50.0,dynamic:100.0,all:50.0 ", line);
        Assert.Contains(" slots_changed=0:50.0,1:0.0,2:0.0,3+:50.0 ", line);
        Assert.Contains(" pipeline_key_mismatch=0 ", line);
        Assert.Contains(" run_len=p50:2,p90:2 ", line);
        Assert.Contains(" run_start_share=0.500 gate_ns=", line);
    }

    [Fact]
    public void FieldDiffNamesTheFieldsThatDiffer()
    {
        var left = new ContextRegisters();
        var right = left.Copy();
        Assert.Equal("none", PlanProbe.ContextDiff(left, right));
        right.RenderTargetMask = 0xF;
        right.ColorTargets[2] = right.ColorTargets[2] with { Info = 5 };
        var diff = PlanProbe.ContextDiff(left, right);
        Assert.Contains("RenderTargetMask", diff);
        Assert.Contains("ColorTargets[2]", diff);
    }
}
