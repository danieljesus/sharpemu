// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_POOLED_ITEMS: a pooled item runs what the closure ran, on its own values, returns itself to
// the pool and drops its references; its delegates are made once.
public sealed class FrontQueuedItemsTests
{
    private sealed class Host : IFrontItemHost
    {
        public bool StillValid = true;
        public readonly List<string> Calls = [];
        public GraphicsPrograms? ExecutedPrograms;
        public RenderExecutor.DrawProgramInputs ExecutedInputs;
        public DrawIndexedArguments ExecutedIndexed;
        public DrawAutoArguments ExecutedAuto;
        public bool ExecutedIndexedDraw;
        public RegisterBanks? ExecutedBanks;
        public KeyValuePair<uint, uint>[]? ExecutedRegisters;
        public ComputeProgram? ExecutedCompute;
        public (uint X, uint Y, uint Z, uint Initiator, ulong Indirect) ExecutedDispatch;
        public readonly List<object> Returned = [];

        public GraphicsPrograms? PrepareGraphicsPrograms(RegisterBanks banks, out RenderExecutor.DrawProgramInputs inputs)
        {
            Calls.Add("prepare");
            inputs = new RenderExecutor.DrawProgramInputs { PixelActive = true, StartInstanceUserData = 7 };
            return new GraphicsPrograms();
        }

        public ComputeProgram? PrepareComputeProgram(RegisterBanks banks, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
        {
            Calls.Add($"prepare_compute {groupsX},{groupsY},{groupsZ}");
            return new ComputeProgram();
        }

        public bool FrontPreparationStillValid(long version, FrontPrepReads.Pages? pages, out bool verify)
        {
            Calls.Add($"valid {version}");
            verify = false;
            return StillValid;
        }

        public bool TablesStillValid(long mark)
        {
            Calls.Add($"tables {mark}");
            return true;
        }

        public GraphicsPrograms? VerifyGraphics(GraphicsPrograms prepared, ref RenderExecutor.DrawProgramInputs inputs,
            Func<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)> prepare, bool force) => prepared;

        public ComputeProgram? VerifyCompute(ComputeProgram prepared, Func<ComputeProgram?> prepare, bool force) => prepared;

        public ComputeProgram? ArmSpeculation(ulong key, ComputeProgram? prepared, FrontSpeculation.Reads? reads, Func<ComputeProgram?> prepare) => prepared;

        public void ExecuteQueuedDraw(ulong submitId, RegisterBanks banks, bool indexed, in DrawIndexedArguments indexedArguments, in DrawAutoArguments autoArguments,
            GraphicsPrograms? prepared, in RenderExecutor.DrawProgramInputs preparedInputs, KeyValuePair<uint, uint>[]? drawRegisters)
        {
            Calls.Add($"draw {submitId}");
            ExecutedBanks = banks;
            ExecutedIndexedDraw = indexed;
            ExecutedIndexed = indexedArguments;
            ExecutedAuto = autoArguments;
            ExecutedPrograms = prepared;
            ExecutedInputs = preparedInputs;
            ExecutedRegisters = drawRegisters;
        }

        public void ExecuteQueuedDispatch(ulong submitId, RegisterBanks banks, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator,
            ulong indirectArgumentsAddress, ComputeProgram? prepared)
        {
            Calls.Add($"dispatch {submitId}");
            ExecutedBanks = banks;
            ExecutedCompute = prepared;
            ExecutedDispatch = (groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress);
        }

        public void Return(FrontQueuedItems.Draw item) => Returned.Add(item);

        public void Return(FrontQueuedItems.Dispatch item) => Returned.Add(item);
    }

    private static Exception Fatal(string message) => new InvalidOperationException(message);

    [Fact]
    public void ADrawRunsItsOwnValuesAndReturnsItself()
    {
        var host = new Host();
        var item = new FrontQueuedItems.Draw(host, Fatal);
        var run = item.Run;
        var prepare = item.Prepare;
        var (programs, inputs) = item.Prepare();
        item.SubmitId = 42;
        item.Indexed = true;
        item.IndexedArguments = new DrawIndexedArguments(0x1000, PacketOpcode.DrawIndex2, 36, 0x2000, 0, 1, 0, 0, DrawOffsetSource.Packet);
        item.Registers = [new(1, 2)];
        item.Version = 9;
        item.TableMark = 3;
        item.Prepared = programs;
        item.Inputs = inputs;
        item.Run();

        Assert.Equal(["prepare", "valid 9", "tables 3", "draw 42"], host.Calls);
        Assert.Same(item.Banks, host.ExecutedBanks);
        Assert.Same(programs, host.ExecutedPrograms);
        Assert.Equal(7, host.ExecutedInputs.StartInstanceUserData);
        Assert.True(host.ExecutedIndexedDraw);
        Assert.Equal(36u, host.ExecutedIndexed.IndexCount);
        Assert.Equal(new KeyValuePair<uint, uint>(1, 2), Assert.Single(host.ExecutedRegisters!));
        Assert.Same(item, Assert.Single(host.Returned));
        Assert.Null(item.Prepared);
        Assert.Null(item.Registers);
        Assert.Null(item.PrepPages);
        Assert.Same(run, item.Run);
        Assert.Same(prepare, item.Prepare);
    }

    [Fact]
    public void AStalePreparationIsDroppedAsTheClosureDropsIt()
    {
        var host = new Host { StillValid = false };
        var item = new FrontQueuedItems.Draw(host, Fatal);
        var (programs, inputs) = item.Prepare();
        item.Prepared = programs;
        item.Inputs = inputs;
        item.AutoArguments = new DrawAutoArguments(0x1000, PacketOpcode.DrawIndexAuto, 3, 1, 0, 0, DrawOffsetSource.Packet);
        item.Run();

        Assert.Null(host.ExecutedPrograms);
        Assert.Equal(default, host.ExecutedInputs.StartInstanceUserData);
        Assert.False(host.ExecutedIndexedDraw);
        Assert.Equal(3u, host.ExecutedAuto.VertexCount);
    }

    [Fact]
    public void ADrawWithoutAPreparationSkipsTheChecks()
    {
        var host = new Host();
        var item = new FrontQueuedItems.Draw(host, Fatal) { SubmitId = 5 };
        item.Run();
        Assert.Equal(["draw 5"], host.Calls);
        Assert.Null(host.ExecutedPrograms);
    }

    [Fact]
    public void ADispatchRunsItsOwnValuesAndReturnsItself()
    {
        var host = new Host();
        var item = new FrontQueuedItems.Dispatch(host, Fatal)
        {
            SubmitId = 8,
            GroupsX = 4,
            GroupsY = 2,
            GroupsZ = 1,
            DispatchInitiator = 0x21,
            Version = 1,
            TableMark = -1,
        };
        item.Prepared = item.Prepare();
        var prepared = item.Prepared;
        item.Run();

        Assert.Equal(["prepare_compute 4,2,1", "valid 1", "tables -1", "dispatch 8"], host.Calls);
        Assert.Same(prepared, host.ExecutedCompute);
        Assert.Equal((4u, 2u, 1u, 0x21u, 0UL), host.ExecutedDispatch);
        Assert.Same(item, Assert.Single(host.Returned));
        Assert.Null(item.Prepared);
    }
}
