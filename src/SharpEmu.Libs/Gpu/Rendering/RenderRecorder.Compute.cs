// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed partial class RenderRecorder
{
    private ComputeProgram? _keptComputeProgram;
    private PipelineHandle _keptComputePipeline;
    private DispatchChain? _dispatchChain;

    public void Record(ResolvedDispatch record)
    {
        try
        {
            RecordDispatch(record);
        }
        finally
        {
            _pendingWrites?.Release(record.Sequence);
            if (_returnsRecords)
            {
                _pool.Return(record);
            }
        }
    }

    // [local] Hooks of ours that keep process-wide state, record host work of their own or submit the batch
    // (SHARPEMU_TLAS_HLE, SHARPEMU_RT_RAYQUERY, SHARPEMU_EARLY_WRITER_SUBMIT, SHARPEMU_CUT_POLICY and the dispatch
    // probes, all off by default) run on the render thread only: a worker hands such a dispatch back to it.
    internal static bool NeedsSerialDispatch => // [local] read by SHARPEMU_PARRES_PROBE
        Buffers.TlasHle.Instance is not null || Buffers.RayQueryHle.Instance is not null ||
        Buffers.GuestBufferCache.EarlyWriterSubmit || Buffers.CutPolicy.Words || Buffers.CutPolicy.GuestWindows ||
        Buffers.WriteDiffProbe.Enabled || Vulkan.IndirectArgsProbe.Enabled || Vulkan.ArgsWriterProbe.Enabled || Buffers.RangeDumpProbe.Enabled ||
        Buffers.GuestBufferCache.PostDispatchHook is not null || Buffers.GuestBufferCache.PostDispatchHooks.Count != 0;

    private static int _dispatchExecLogs; // [local] SHARPEMU_DISPATCH_EXEC_LOG

    private void RecordDispatch(ResolvedDispatch record)
    {
        using var setupScope = VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DispatchRecordSetup); // [local]
        if (!_host.IsRecording)
        {
            throw _host.Fatal("A dispatch has no recording command buffer.");
        }

        if (OnWorker && NeedsSerialDispatch) // [local]
        {
            throw new VideoOut.SerialRequiredException("local dispatch hooks", resumable: true);
        }

        // [local] SHARPEMU_GPU_THREAD_INDIRECT: the host records it on the render thread only (TryDispatchIndirectThreads
        // throws there otherwise); handing it back before anything is recorded keeps the rest of the slice on the worker.
        if (OnWorker && record.IndirectThreads && !record.Skipped)
        {
            throw new VideoOut.SerialRequiredException("indirect thread dispatch", resumable: true);
        }

        _host.RunPendingOperations();
        using var memoProgram = new Buffers.TableWordMemo.ProgramScope(); // [local] no program's once the dispatch is recorded
        SetDebugInformation(in record.Debug);
        var userScalars = record.UserScalars is { } scalars ? (ReadOnlySpan<uint>)scalars : ReadOnlySpan<uint>.Empty; // [local]
        if (record.ShaderAddress != 0 && Buffers.TlasHle.Instance is { } tlasHle) // [local] SHARPEMU_TLAS_HLE
        {
            var (skipDispatch, recorded) = tlasHle.BeforeDispatch(record.ShaderAddress, userScalars, _host.EndRendering);
            if (recorded)
                _host.ResetBindings();
            if (skipDispatch)
                return;
        }

        if (record.Skipped)
        {
            return;
        }

        var computeProgram = record.Program;
        if (computeProgram.ImageCopy is { } copy)
        {
            if (_host.TryCopyImage(copy.Source, copy.Destination))
            {
                _host.ResetBindings();
                return;
            }
            computeProgram = copy.Fallback();
            record.Program = computeProgram;
            if (!computeProgram.Available || computeProgram.Consumed)
                return;
        }
        var input = computeProgram.Input;
        var program = input.Stage.Program!;
        Buffers.TlasHle.Instance?.NoteProgram(record.ShaderAddress, program.Hash); // [local] SHARPEMU_TLAS_HLE
        if (Vulkan.IndirectArgsProbe.Enabled) // [local]
            _host.NoteIndirectArgsWriter(program.Hash, userScalars, input.Stage.Resources.FlattenedResourceTable, record.GroupsX);
        if (Buffers.RayQueryHle.Instance?.NoteDispatch(program.Hash) == true) // [local] SHARPEMU_RT_RAYQUERY / SHARPEMU_RQ_MAP
            _host.ResetBindings();
        if (record.IndirectArgumentsAddress == 0)
        {
            bool consumed;
            using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DispatchConsume))
            {
                consumed = TryConsume(record, program);
            }

            var kinds = (record.MetadataClear is not null ? 1 : 0) | (record.ConstantFill is not null ? 2 : 0) | (record.BoundedFill is not null ? 4 : 0) |
                (record.BoundedCopy is not null ? 8 : 0) | (record.ImageClear is not null ? 16 : 0);
            if (kinds != 0)
            {
                Diagnostics.DispatchRunTally.NoteConsume(program.Hash, kinds, consumed);
            }

            if (consumed)
            {
                _host.ResetBindings();
                return;
            }

            if (record.GroupsX == 0 || record.GroupsY == 0 || record.GroupsZ == 0)
            {
                return;
            }
        }

        using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DispatchEndRendering)) // [local]
            _host.EndRendering();
        using (_host.BeginPreparation())
        {
            PipelineHandle pipeline;
            if (ReferenceEquals(computeProgram, _keptComputeProgram))
            {
                pipeline = _keptComputePipeline;
            }
            else
            {
                long waitStarted = 0; // [local] SHARPEMU_COMPILE_LOG
                using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DispatchPipeline))
                {
                    while (!_pipelines.TryCreateComputePipeline(input, computeProgram.Program, out pipeline))
                    {
                        if (waitStarted == 0)
                            waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        using var waitScope = VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.ComputePipelineWait);
                        if (_pipelines.GetComputePipelineCompletion(computeProgram.Program) is { } completion)
                        {
                            completion.GetAwaiter().GetResult();
                        }
                        else
                        {
                            Thread.Yield();
                        }
                    }
                }

                if (waitStarted != 0 && Pipelines.CompileLog.Timed)
                    Pipelines.CompileLog.Note("dispatch-wait", System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds, program.Hash);
                _keptComputeProgram = computeProgram;
                _keptComputePipeline = pipeline;
            }

            if (Buffers.WriteDiffProbe.Enabled) Buffers.WriteDiffProbe.Arm(program.Hash); // [local] SHARPEMU_WRITE_DIFF
            var bindings = PrepareBindings(input.Stage);
            var bindScope = VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DispatchBind); // [local]
            if (program.UsesDeviceAddresses)
            {
                using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.DispatchBda)) // [local]
                    _host.PrepareDeviceAddresses();
            }

            _host.BindResources(bindings);
            bindScope.Dispose();
            Span<IPreparedBindings> stages = [bindings];
            var dispatchEmit = VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.PartDispatchEmit); // [local] SHARPEMU_PARREC_PROBE
            _host.CommitBindings(PipelineBindPoint.Compute, in pipeline, stages);
            var chain = _dispatchChain ??= new DispatchChain();
            var stage = input.Stage;
            // [local] SHARPEMU_DISPATCH_BARRIERS=0: neither the hazard barrier nor the access barrier after the dispatch.
            if (RenderExecutor.DispatchBarriers && chain.NeedsHazardBarrier(record, in stage, _host.DeferredBarrierPromotions))
            {
                _host.ShaderWriteHazardBarrier();
            }

            var imageSnapshot = _host.BeginComputeImageSnapshot(bindings);
            _host.BeginCheckedDispatch();
            _host.BindPipeline(PipelineBindPoint.Compute, in pipeline);
            var (groupsX, groupsY, groupsZ) = PhysicalGroups(input, record);
            if (RenderExecutor.DispatchExecLog && groupsX == 1 && groupsY == 1 && groupsZ == 1 && Interlocked.Increment(ref _dispatchExecLogs) <= 600) // [local]
                Console.Error.WriteLine($"[DISPATCH_EXEC] shader=0x{record.ShaderAddress:X} groups=1,1,1 indirect=0x{record.IndirectArgumentsAddress:X}");
            if (Buffers.WriteDiffProbe.Enabled) Buffers.WriteDiffProbe.BeforeDispatch(); // [local]
            if (Vulkan.IndirectArgsProbe.Enabled) Vulkan.IndirectArgsProbe.CurrentShader = record.ShaderAddress; // [local]
            if (Vulkan.ArgsWriterProbe.Enabled) // [local] SHARPEMU_ARGS_WRITER_PROBE
            {
                Vulkan.ArgsWriterProbe.CurrentProgram = program.Hash;
                _host.BeforeArgsWriterDispatch(program.Hash, userScalars);
            }

            if (record.IndirectThreads) // [local] SHARPEMU_GPU_THREAD_INDIRECT
            {
                if (!_host.TryDispatchIndirectThreads(record.IndirectArgumentsAddress, bindings))
                {
                    throw _host.Fatal($"The thread-unit indirect dispatch could not be recorded: shader=0x{record.ShaderAddress:X16}.");
                }
            }
            else if (record.IndirectArgumentsAddress == 0 || !_host.TryDispatchIndirect(record.IndirectArgumentsAddress, GroupAxisOrder(input)))
            {
                _host.Dispatch(groupsX, groupsY, groupsZ);
            }

            _host.EndCheckedDispatch();
            if (RenderExecutor.DispatchBarriers)
            {
                chain.Commit(_host.TryDeferShaderAccessBarrier(), _host.DeferredBarrierPromotions);
            }

            dispatchEmit.Dispose();
            _host.EndComputeImageSnapshot(imageSnapshot, bindings);
            if (record.IndirectArgumentsAddress == 0 && computeProgram.KnownWrite is { } known)
            {
                _host.NoteKnownGpuContents(known.Address, known.Words);
                known.Release();
            }

            if (Vulkan.ArgsWriterProbe.Enabled) // [local] SHARPEMU_ARGS_WRITER_PROBE
            {
                Vulkan.ArgsWriterProbe.CurrentProgram = 0;
                _host.NoteArgsWriterRecorded(program.Hash, bindings, userScalars, record.SubmitId, record.Debug.Argument0);
            }

            if (Buffers.RangeDumpProbe.Enabled) Buffers.RangeDumpProbe.AfterDispatch(program.Hash, input.Stage.Resources.UserData, input.Stage.Resources.Buffers); // [local] SHARPEMU_RANGE_DUMP
            if (Buffers.WriteDiffProbe.Enabled) Buffers.WriteDiffProbe.AfterDispatch(groupsX, groupsY, groupsZ, record.IndirectArgumentsAddress, input.Stage.Resources.UserData); // [local]
            if (!OnWorker) // [local] these belong to the render thread's batch (a worker never gets here with them: NeedsSerialDispatch)
            {
                var hook = Buffers.GuestBufferCache.PostDispatchHook;
                if (hook != null) { Buffers.GuestBufferCache.PostDispatchHook = null; hook(); }
                if (Buffers.GuestBufferCache.EarlySubmitPending) { Buffers.GuestBufferCache.EarlySubmitPending = false; Buffers.GuestBufferCache.EarlySubmit?.Invoke(); } // [local] SHARPEMU_EARLY_WRITER_SUBMIT
                if (Buffers.CutPolicy.DispatchCutPending) { Buffers.CutPolicy.DispatchCutPending = false; Buffers.CutPolicy.DispatchCut?.Invoke(); } // [local] SHARPEMU_CUT_POLICY
                if (Buffers.GuestBufferCache.PostDispatchHooks.Count != 0) { var hooks = Buffers.GuestBufferCache.PostDispatchHooks.ToArray(); Buffers.GuestBufferCache.PostDispatchHooks.Clear(); foreach (var h in hooks) h(); }
            }
        }

        _host.ResetBindings();
    }

    private bool TryConsume(ResolvedDispatch record, ShaderProgramInfo program)
    {
        if (record.MetadataClear is { } probes &&
            TryClearMetadata(probes, record.MetadataClearHasExclusiveOr, program.Frontend is not null && program.StoresUniformValues))
        {
            return true;
        }

        // [local] SHARPEMU_RECORD_FILLS: before the constant fill, as on the executor before the merge. A fill of exactly
        // one image's range clears the image on the GPU (unless SHARPEMU_RECORD_FILL_IMAGE_CLEAR=0); otherwise a DCC fill
        // or, last, a CPU fill of the guest buffer.
        if (record.RecordFill is { } recordFill &&
            ((record.RecordFillImageClear && _host.TryClearImageFromBuffer(recordFill.Address, recordFill.Size, recordFill.Value)) ||
             _host.TryAbsorbDccFill(recordFill.Address, recordFill.Size, recordFill.Value) ||
             _host.TryFillGuestBuffer(recordFill.Address, recordFill.Size, recordFill.Value)))
        {
            return true;
        }

        if (record.ConstantFill is { } fill)
        {
            var consumed = _host.TryClearImageFromBuffer(fill.Address, fill.Size, fill.Value) ||
                           _host.TryAbsorbDccFill(fill.Address, fill.Size, fill.Value);
            if (RenderTrace.Enabled && RenderTrace.MetadataClear())
            {
                RenderTrace.Write($"Constant fill: shader=0x{program.Hash:X16} address=0x{fill.Address:X16} size=0x{fill.Size:X} value=0x{fill.Value:X8} consumed={consumed}");
            }

            if (consumed)
            {
                return true;
            }
        }

        if (record.BoundedFill is { } bounded)
        {
            var consumed = _host.TryFillDccMetadata(bounded.Address, bounded.Size, bounded.Value);
            if (RenderTrace.Enabled && RenderTrace.MetadataClear())
            {
                RenderTrace.Write($"Bounded fill: shader=0x{program.Hash:X16} address=0x{bounded.Address:X16} size=0x{bounded.Size:X} value=0x{bounded.Value:X8} consumed={consumed}");
            }

            if (consumed)
            {
                return true;
            }
        }

        if (record.BoundedCopy is { } copy)
        {
            var consumed = _host.TryCopyWordsOnHost(copy.Destination, copy.Source, copy.Modulus, copy.Words);
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write(
                    $"Bounded copy: shader=0x{program.Hash:X16} src=0x{copy.Source:X16} dst=0x{copy.Destination:X16} words={copy.Words} modulus={copy.Modulus} consumed={consumed}");
            }

            if (consumed)
            {
                record.Program.KnownWrite?.Release();
                return true;
            }

            Diagnostics.DccWriterTrace.Refuse(program.Hash, $"copy refused by the host dst=0x{copy.Destination:X} words={copy.Words} modulus={copy.Modulus}");
        }

        if (record.ImageClear is { } clear)
        {
            var address = clear.Descriptor.Address;
            if (!_host.TryClearImageFromBuffer(address, clear.Size, clear.PackedClear))
            {
                var registered = _host.TryAbsorbDccFill(address, clear.Size, clear.PackedClear);
                var filledOnHost = !registered && _host.TryFillDccMetadata(address, clear.Size, clear.PackedClear);
                if (RenderTrace.Enabled && RenderTrace.MetadataClear())
                {
                    RenderTrace.Write(
                        $"{(registered ? "Tracked" : filledOnHost ? "Filled on the host" : "Deferred")} a metadata clear: shader=0x{program.Hash:X16} address=0x{address:X16} size=0x{clear.Size:X16} value=0x{clear.PackedClear:X8}");
                }

                return registered || filledOnHost;
            }

            if (RenderTrace.Enabled && RenderTrace.ImageClear())
            {
                RenderTrace.Write($"Consumed a compute image clear: shader=0x{program.Hash:X16} address=0x{address:X16} size=0x{clear.Size:X16} value=0x{clear.PackedClear:X8}");
            }

            return true;
        }

        return false;
    }

    private bool TryClearMetadata(MetadataBufferProbe[] probes, bool hasExclusiveOr, bool readsOnlyGuardStores)
    {
        foreach (var probe in probes)
        {
            if (_host.IsMetadata(probe.Address) && (!probe.Written || (probe.Read && !readsOnlyGuardStores)))
            {
                return false;
            }
        }

        if (hasExclusiveOr)
        {
            return false;
        }

        foreach (var probe in probes)
        {
            if (probe.Written && _host.ClearMetadata(probe.Address))
            {
                return true;
            }
        }

        return false;
    }

    private static int[] GroupAxisOrder(ComputeInputInfo input) =>
        Gen5SpirvTranslator.ComputeGroupAxisOrder(input.ThreadsX, input.ThreadsY, input.ThreadsZ, input.GroupIdX, input.GroupIdY, input.GroupIdZ);

    private static (uint X, uint Y, uint Z) PhysicalGroups(ComputeInputInfo input, ResolvedDispatch record)
    {
        var axisOrder = GroupAxisOrder(input);
        Span<uint> logical = [record.GroupsX, record.GroupsY, record.GroupsZ];
        Span<uint> physical = stackalloc uint[3];
        for (var axis = 0; axis < 3; axis++)
        {
            physical[axisOrder[axis]] = logical[axis];
        }

        return (physical[0], physical[1], physical[2]);
    }
}
