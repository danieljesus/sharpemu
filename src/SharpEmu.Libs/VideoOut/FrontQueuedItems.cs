// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_FRONT_POOLED_ITEMS (=1 on, off by default): the front queues every draw and dispatch to the render
// thread as a closure: the captures of the front method (the banks snapshot, the arguments, the prepared programs with
// their inputs, the read pages, the marks; one object of a few hundred bytes, allocated on the method's entry) and two
// delegates, the preparation and the queued work. The render thread runs it a few thousand items later, so in GTA V
// the open world's ~5,000 draws a frame put ~2.5 MB a frame of objects that outlive a gen0 collection into gen1, and
// the snapshot goes back to a ConcurrentStack (a node per push). With the switch an item holds the same fields and its
// own register banks; its two delegates are made once, and the render thread returns the item to the front's pool
// (a ConcurrentQueue) after running it. The item runs exactly the closure's steps on the same values, in the same
// order; with SHARPEMU_FRONT_PREP_WORKER the closures stay (the worker needs them).
// What a queued item asks the front host and the presenter for; FrontCommandStreamHost forwards to the presenter.
internal interface IFrontItemHost
{
    GraphicsPrograms? PrepareGraphicsPrograms(RegisterBanks banks, out RenderExecutor.DrawProgramInputs inputs);

    ComputeProgram? PrepareComputeProgram(RegisterBanks banks, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator);

    bool FrontPreparationStillValid(long version, FrontPrepReads.Pages? pages, out bool verify);

    bool TablesStillValid(long mark);

    GraphicsPrograms? VerifyGraphics(GraphicsPrograms prepared, ref RenderExecutor.DrawProgramInputs inputs,
        Func<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)> prepare, bool force);

    ComputeProgram? VerifyCompute(ComputeProgram prepared, Func<ComputeProgram?> prepare, bool force);

    ComputeProgram? ArmSpeculation(ulong key, ComputeProgram? prepared, FrontSpeculation.Reads? reads, Func<ComputeProgram?> prepare);

    void ExecuteQueuedDraw(ulong submitId, RegisterBanks banks, bool indexed, in DrawIndexedArguments indexedArguments, in DrawAutoArguments autoArguments,
        GraphicsPrograms? prepared, in RenderExecutor.DrawProgramInputs preparedInputs, KeyValuePair<uint, uint>[]? drawRegisters);

    void ExecuteQueuedDispatch(ulong submitId, RegisterBanks banks, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator,
        ulong indirectArgumentsAddress, ComputeProgram? prepared);

    void Return(FrontQueuedItems.Draw item);

    void Return(FrontQueuedItems.Dispatch item);
}

internal static class FrontQueuedItems
{
    internal static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_POOLED_ITEMS") == "1";

    internal sealed class Draw
    {
        private readonly IFrontItemHost _host;
        internal readonly RegisterBanks Banks;
        internal readonly Action Run;
        internal readonly Func<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)> Prepare;
        internal ulong SubmitId;
        internal bool Indexed;
        internal DrawIndexedArguments IndexedArguments;
        internal DrawAutoArguments AutoArguments;
        internal KeyValuePair<uint, uint>[]? Registers;
        internal long Version;
        internal GraphicsPrograms? Prepared;
        internal RenderExecutor.DrawProgramInputs Inputs;
        internal FrontPrepReads.Pages? PrepPages;
        internal long TableMark;

        internal Draw(IFrontItemHost host, Func<string, Exception> fatal)
        {
            _host = host;
            Banks = new RegisterBanks(fatal);
            Run = Execute;
            Prepare = PrepareOnFront;
        }

        private (GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs) PrepareOnFront() =>
            _host.PrepareGraphicsPrograms(Banks, out var inputs) is { } programs ? (programs, inputs) : default;

        // On the render thread, as the closure did.
        private void Execute()
        {
            try
            {
                var rangesVerify = false; // [local] SHARPEMU_FRONT_PREP_RANGES=2
                if (Prepared is null)
                    FrontPrepReads.Release(PrepPages);
                else if (!_host.FrontPreparationStillValid(Version, PrepPages, out rangesVerify) || !_host.TablesStillValid(TableMark))
                {
                    Prepared = null;
                    Inputs = default;
                }

                if (Prepared is not null && (FrontPrepVerify.Enabled || rangesVerify)) // [local] SHARPEMU_FRONT_PREP_VERIFY
                    Prepared = _host.VerifyGraphics(Prepared, ref Inputs, Prepare, rangesVerify);

                _host.ExecuteQueuedDraw(SubmitId, Banks, Indexed, in IndexedArguments, in AutoArguments, Prepared, in Inputs, Registers);
            }
            finally
            {
                Prepared = null;
                Registers = null;
                PrepPages = null;
                _host.Return(this);
            }
        }
    }

    internal sealed class Dispatch
    {
        private readonly IFrontItemHost _host;
        internal readonly RegisterBanks Banks;
        internal readonly Action Run;
        internal readonly Func<ComputeProgram?> Prepare;
        internal ulong SubmitId;
        internal uint GroupsX, GroupsY, GroupsZ, DispatchInitiator;
        internal ulong IndirectArgumentsAddress;
        internal long Version;
        internal ComputeProgram? Prepared;
        internal FrontPrepReads.Pages? PrepPages;
        internal FrontSpeculation.Reads? Speculated;
        internal long TableMark;
        internal int TimelineQueue;

        internal Dispatch(IFrontItemHost host, Func<string, Exception> fatal)
        {
            _host = host;
            Banks = new RegisterBanks(fatal);
            Run = Execute;
            Prepare = PrepareOnFront;
        }

        private ComputeProgram? PrepareOnFront() => _host.PrepareComputeProgram(Banks, GroupsX, GroupsY, GroupsZ, DispatchInitiator);

        private void Execute()
        {
            try
            {
                var prepared = Prepared;
                var rangesVerify = false; // [local] SHARPEMU_FRONT_PREP_RANGES=2
                if (prepared is null)
                    FrontPrepReads.Release(PrepPages);
                else if (!_host.FrontPreparationStillValid(Version, PrepPages, out rangesVerify) || !_host.TablesStillValid(TableMark))
                    prepared = null;
                if (prepared is not null && (FrontPrepVerify.Enabled || rangesVerify)) // [local] SHARPEMU_FRONT_PREP_VERIFY
                    prepared = _host.VerifyCompute(prepared, Prepare, rangesVerify);
                if (Speculated is not null) // [local] SHARPEMU_FRONT_SPECULATE
                {
                    if (prepared is null) Interlocked.Increment(ref FrontSpeculation.Invalidated);
                    prepared = _host.ArmSpeculation(Banks.Shader.Compute.Address, prepared, Speculated, Prepare);
                    Speculated = null;
                }

                Diagnostics.LabelTimeline.RenderQueue = TimelineQueue; // [local]
                _host.ExecuteQueuedDispatch(SubmitId, Banks, GroupsX, GroupsY, GroupsZ, DispatchInitiator, IndirectArgumentsAddress, prepared);
                Diagnostics.LabelTimeline.RenderQueue = -1; // [local]
            }
            finally
            {
                Prepared = null;
                PrepPages = null;
                Speculated = null;
                _host.Return(this);
            }
        }
    }
}
