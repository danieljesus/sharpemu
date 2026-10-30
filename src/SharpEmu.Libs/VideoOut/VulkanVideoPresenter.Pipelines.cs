// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE.GpuMemory;
using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

// This partial creates the graphics and compute pipelines of the shader cache and the presentation pipeline.
internal static unsafe partial class VulkanVideoPresenter
{
    private const string PushDescriptorExtensionName = "VK_KHR_push_descriptor";

    private sealed partial class Presenter : IShaderPipelineHost
    {
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private const uint PushConstantBytes = PushData.ByteSize;
        private static int _shaderModuleDumpSequence;
        private static int _vertexSwizzleLines;
        private static int _vertexOutOfBoundsLines;

        // One created pipeline with the layout it binds and the description its rectangle-list variants derive from.
        private sealed class RenderPipelineEntry
        {
            public ulong Id;
            public Pipeline Pipeline;
            public PipelineLayout Layout;
            public DescriptorSetLayout SetLayout;
            public DescriptorSetDemand Demand;
            public bool UsesPushDescriptors;
            public GraphicsPipelineDescription? Description;
            public Pipeline StripVariant;
            public Pipeline ListVariant;
            public Pipeline RectangleVariant;
            public ulong ProfileVertexHash;
            public ulong ProfilePixelHash;
            public ulong ProfileComputeHash;

            public bool RectangleList => Description is { StaticParameters.Topology: PrimitiveTopology.PatchList };
        }

        private readonly Dictionary<ulong, ShaderModule> _shaderModules = new();
        private readonly Dictionary<ulong, int> _shaderModuleSpirvBytes = new();
        private readonly Dictionary<ulong, string> _shaderModuleCacheIdentities = new();
        private long _pipelineCreationMilliseconds;
        private KhrPushDescriptor _pushDescriptorApi = null!;
        private uint _maxPushDescriptors;
        private SampleCountFlags _noAttachmentSampleCounts;
        private DescriptorHeap _descriptorHeap = null!;

        uint IShaderPipelineHost.MaxPushDescriptors => _maxPushDescriptors;

        // Wave64 compute runs natively when the device's subgroup is that wide; smaller devices emulate it.
        bool IShaderPipelineHost.ComputeWave64Supported => Volatile.Read(ref _nativeSubgroupSize) >= 64;

        // Only a 64-invocation wave64 workgroup is translated for either host subgroup width. Every
        // other compute translation maps a guest wave to 32-lane host subgroups, and a 64-lane host
        // subgroup (AMD's default) left lanes 32..63 inactive: a wave64 8x8x8 group lost rows 4..7.
        private const uint RdnaSubgroupSize = 32;
        private bool _canRequireComputeSubgroup32;
        private uint _maxComputeWorkgroupSubgroups;

        private bool RequiresComputeSubgroup32(ComputeInputInfo input)
        {
            var invocations = (ulong)Math.Max(input.ThreadsX, 1) * Math.Max(input.ThreadsY, 1) * Math.Max(input.ThreadsZ, 1);
            return _canRequireComputeSubgroup32 &&
                   !(input.WaveSize == 64 && invocations == 64) &&
                   invocations <= (ulong)_maxComputeWorkgroupSubgroups * RdnaSubgroupSize;
        }

        bool IShaderPipelineHost.GraphicsSubgroupOperationsEnabled => GraphicsSubgroupOperationsEnabled;

        bool IShaderPipelineHost.SharedInt64AtomicsEnabled => SharedInt64AtomicsEnabled;
        bool IShaderPipelineHost.BufferInt64AtomicsEnabled => BufferInt64AtomicsEnabled;
        bool IShaderPipelineHost.Float16ConversionsEnabled => Float16ConversionsEnabled;
        bool IShaderPipelineHost.ShaderSignedZeroInfNanPreserveFloat32Supported =>
            _supportsShaderSignedZeroInfNanPreserveFloat32;
        // NVIDIA's compiler rejects the elided-EXEC wave64 compute module with NVVM error 3.
        bool IShaderPipelineHost.ExecGuardElisionEnabled => _physicalDeviceVendorId != NvidiaVendorId;
        bool IShaderPipelineHost.PerVertexPixelInputsSupported => _supportsPerVertexPixelInputs;
        bool IShaderPipelineHost.ClipDistanceEnabled => _supportsShaderClipDistance;

        RenderHostLimits IShaderPipelineHost.Limits => _renderHostLimits;

        SampleCountFlags IShaderPipelineHost.NoAttachmentSampleCounts => _noAttachmentSampleCounts;

        // A range the GPU wrote is downloaded first, so the word is what the guest CPU would read.
        private static readonly bool FlushBeforeTableReads = Environment.GetEnvironmentVariable("SHARPEMU_FLUSH_TABLE_READS") == "1";
        private int _flushedTableReads;
        private static readonly int SpeculativeTableReads = int.Parse(Environment.GetEnvironmentVariable("SHARPEMU_SPECULATIVE_TABLE_READS") ?? "0"); // [local] 1 = read, 2 = verify
        private long _speculativeReads, _speculativeMismatches, _speculativeReported; // [local]
        private uint _auditPre; private bool _auditArmed; // [local]
        private readonly Dictionary<ulong, (long Reads, long Changed)> _auditByShader = new(); // [local]
        private long _auditTotal;
        private void AuditTableRead(bool changed)
        {
            var shader = Diagnostics.GpuReadTrace.CurrentShader;
            var entry = _auditByShader.GetValueOrDefault(shader);
            _auditByShader[shader] = (entry.Reads + 1, entry.Changed + (changed ? 1 : 0));
            if (++_auditTotal % 3000 == 0)
                foreach (var (key, value) in _auditByShader.OrderByDescending(pair => pair.Value.Reads).Take(12))
                    Console.Error.WriteLine($"[TABLE_AUDIT] shader=0x{key:X16} dirty_reads={value.Reads} changed={value.Changed}");
        }

        private void ReportSpeculative()
        {
            if (_speculativeReads - _speculativeReported >= 2000)
            {
                _speculativeReported = _speculativeReads;
                Console.Error.WriteLine($"[SPEC_READ] reads={_speculativeReads} mismatches={_speculativeMismatches}");
            }
        }
        private static readonly bool NoSyncTableReads = Environment.GetEnvironmentVariable("SHARPEMU_NO_SYNC_TABLE_READS") == "1"; // [local]
        internal static readonly HashSet<ulong> WatchedBounds = new(); // [local]
        private int _watchLogs; // [local]
        // On the front thread the buffer and image caches belong to the render thread, so the
        // readers take guest memory as it is: a word the GPU is still writing is not observed
        // ([TEMP] measured none in GTA V; the cache then rebuilds the next time).
        // [local] A front thread that interprets ahead cannot see dirty state the render thread
        // has not recorded yet. A range the GPU has ever written is read on the render thread,
        // in stream order and through the synchronized path, so planning never uses stale data.
        [ThreadStatic] private static bool _backReading;
        private static readonly bool FrontSyncedReads = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SYNCED_READS") != "0";
        internal long FrontSyncedReadCount;
        private readonly Dictionary<string, long> _frontReadSites = new(); // [local]

        internal bool NeedsFrontCommandSync(ulong address, ulong size) => NeedsBackRead(address, size);

        // [local] Only the front thread may take the unsynchronized readers; the render thread
        // also runs with a front attached and must keep the synchronized ones.
        private bool OnFrontThread => _onPrepWorker || (_frontThread is { } front && front.ManagedThreadId == Environment.CurrentManagedThreadId);

        // [local] SHARPEMU_FRONT_PREP_WORKER: the worker prepares with the front's readers, but never waits on the render
        // thread (a read it would have to synchronize defers the preparation) and keeps no clean-page notes of its own.
        [ThreadStatic] private static bool _onPrepWorker;

        // [local experiment] SHARPEMU_DEFER_PATCHED=1: a preparation that reads bytes only the
        // pending-write overlay supplies moves to the render thread.
        private static readonly bool DeferPatched = Environment.GetEnvironmentVariable("SHARPEMU_DEFER_PATCHED") == "1";

        private void ApplyFrontOverlay(ulong address, Span<byte> destination)
        {
            // [local] The prep worker runs behind the front, so a pending write it sees may come after the draw.
            if (FrontOverlay.Apply(address, destination) != 0 && (_onPrepWorker || (DeferPatched && DeferFrontPreparation && _preparingOnFront)))
                throw new DeferPreparationException();
        }

        private bool NeedsBackRead(ulong address, ulong size)
        {
            if (!FrontSyncedReads || size == 0)
                return false;
            // [local] A read of bytes a queued WriteGuestNow has not applied yet (overlay mode 2):
            // it runs on the render thread after the write, also while preparing (no deferral).
            _backReadForPendingWrite = PendingWriteOverlay.Mode >= 2 && FrontOverlay.Overlaps(address, size);
            if (_backReadForPendingWrite || _bufferCache.EverGpuWritten(address, size))
                return true;
            if (_preparingOnFront) FrontPrepReads.Note(address, size); // [local] SHARPEMU_FRONT_PREP_RANGES: a direct read
            return false;
        }

        [ThreadStatic] private static bool _backReadForPendingWrite; // [local] per thread: the front and the prep worker

        // [local] SHARPEMU_FRONT_PREP_CHECK (on by default, =0 disables): the front reads guest memory directly only
        // where no GPU write has been recorded (NeedsBackRead), but it runs ahead of the render thread, so a write that
        // comes earlier in stream order may not be recorded yet when the front reads; most often the first write of a
        // range (the intro, new rings), and further ahead with SHARPEMU_FRONT_PENDING_LABELS, which lets a queue pass
        // its wait before the render thread has recorded the other queue's work. A front preparation is used only if
        // no range became GPU-written between its start and its execution; otherwise the render thread prepares the
        // draw or dispatch itself, in stream order and through the synchronized reads.
        private static readonly bool FrontPrepCheck = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_PREP_CHECK") != "0";
        internal long DiscardedFrontPreparations;

        internal long FrontPreparationVersion => _bufferCache.EverGpuWrittenVersion;

        internal bool FrontPreparationStillValid(long version)
        {
            if (!FrontPrepCheck || _bufferCache.EverGpuWrittenVersion == version)
                return true;
            DiscardedFrontPreparations++;
            return false;
        }

        // [local] SHARPEMU_FRONT_PREP_RANGES: as above, but a version change discards the preparation only when a range
        // first GPU-written since `version` overlaps a page it read directly. `verify` is set when the old rule would
        // have discarded it (mode 2: the caller prepares again and compares). Releases `pages`.
        internal bool FrontPreparationStillValid(long version, FrontPrepReads.Pages? pages, out bool verify)
        {
            verify = false;
            if (pages is null || !FrontSyncedReads)
                return FrontPreparationStillValid(version);
            try
            {
                if (!FrontPrepCheck || _bufferCache.EverGpuWrittenVersion == version)
                    return true;
                var overlap = pages.Overflow ? -1 : _bufferCache.FirstWritesSinceOverlap(version, pages);
                if (overlap != 0)
                {
                    if (overlap < 0) Interlocked.Increment(ref FrontPrepReads.Unknown);
                    else Interlocked.Increment(ref FrontPrepReads.Discarded);
                    DiscardedFrontPreparations++;
                    return false;
                }

                Interlocked.Increment(ref FrontPrepReads.Kept);
                verify = FrontPrepReads.Mode == 2;
                return true;
            }
            finally
            {
                FrontPrepReads.Release(pages);
            }
        }

        // [local] SHARPEMU_FRONT_PREP_RANGES: the pages the last front preparation of this thread read directly.
        [ThreadStatic] private static FrontPrepReads.Pages? _lastPrepPages;

        internal static FrontPrepReads.Pages? TakePrepPages()
        {
            var pages = _lastPrepPages;
            _lastPrepPages = null;
            return pages;
        }

        // [local] SHARPEMU_FRONT_STALE_CHECK=1 (diagnostic): the front notes each page it reads directly with the back
        // queue's item count at that moment; when the render thread records the first GPU write of a range, a page of it
        // the front read for an item still queued behind that write was read stale (the write comes first in stream
        // order, but the front read guest memory before the render thread had even recorded it).
        internal static readonly bool FrontStaleCheck = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_STALE_CHECK") == "1";
        private const int FrontReadSlots = 1 << 16;
        private readonly ulong[] _frontReadTags = FrontStaleCheck ? new ulong[FrontReadSlots] : [];
        private readonly long[] _frontReadSerials = FrontStaleCheck ? new long[FrontReadSlots] : [];
        private readonly ulong[] _frontReadAddresses = FrontStaleCheck ? new ulong[FrontReadSlots] : [];
        private readonly ulong[] _frontReadSizes = FrontStaleCheck ? new ulong[FrontReadSlots] : [];
        internal long FrontStalePages;
        internal long FrontStaleReads;
        private int _frontStaleLogs;

        private void NoteFrontDirectRead(ulong address, ulong size)
        {
            if (!FrontStaleCheck || _onPrepWorker || _back is not { } back || size == 0)
                return;
            var serial = Volatile.Read(ref back.Enqueued);
            for (var page = address >> 12; page <= (address + size - 1) >> 12; page++)
            {
                var slot = (int)(page & (FrontReadSlots - 1));
                Volatile.Write(ref _frontReadSerials[slot], serial);
                Volatile.Write(ref _frontReadAddresses[slot], address);
                Volatile.Write(ref _frontReadSizes[slot], size);
                Volatile.Write(ref _frontReadTags[slot], page + 1);
            }
        }

        // On the render thread, for a range the GPU writes for the first time.
        private void CheckFrontStaleReads(ulong address, ulong size)
        {
            if (_back is not { } back || size == 0)
                return;
            var executed = Volatile.Read(ref back.Executed);
            var first = address >> 12;
            var last = (address + size - 1) >> 12;
            if (last - first >= FrontReadSlots)
                last = first + FrontReadSlots - 1;
            for (var page = first; page <= last; page++)
            {
                var slot = (int)(page & (FrontReadSlots - 1));
                if (Volatile.Read(ref _frontReadTags[slot]) != page + 1)
                    continue;
                var serial = Volatile.Read(ref _frontReadSerials[slot]);
                if (serial <= executed)
                    continue;
                // The page's last front read: stale for certain when its bytes overlap the write.
                var readAddress = Volatile.Read(ref _frontReadAddresses[slot]);
                var readSize = Volatile.Read(ref _frontReadSizes[slot]);
                var exact = readAddress < address + size && address < readAddress + readSize;
                FrontStalePages++;
                if (exact)
                    FrontStaleReads++;
                if (_frontStaleLogs++ < 60)
                    Console.Error.WriteLine($"[FRONT_STALE_READ] {(exact ? "bytes" : "page")} read=0x{readAddress:X}+0x{readSize:X} write=0x{address:X}+0x{size:X} read_serial={serial} executed={executed} " +
                        $"enqueued={Volatile.Read(ref back.Enqueued)} tick={_scheduler.CurrentTick} pending_labels={FrontLabels.Count}");
            }
        }

        // [local] While the front prepares a program, a read of GPU-written data does not wait
        // for the render thread: the preparation is dropped and the render thread prepares the
        // program itself when it executes the draw or dispatch, in stream order.
        private sealed class DeferPreparationException : Exception;
        [ThreadStatic] private static bool _preparingOnFront;
        private static readonly bool DeferFrontPreparation = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_DEFER_PREP") != "0";
        internal long DeferredPreparations;

        // [local] SHARPEMU_FRONT_SKIP_DEFERRING (on by default, =0 disables): programs whose last front preparations
        // all came back empty (the render thread prepared them again, usually because they read GPU-written data) are
        // left to the render thread without trying; one attempt in SkipRetryEvery checks whether that still holds. The
        // render thread prepares a draw or dispatch the front did not, as it does for every preparation that came back
        // empty.
        private static readonly bool SkipDeferring = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_SKIP_DEFERRING") != "0";
        private const int SkipAfterEmpty = 4;
        private const int SkipRetryEvery = 32;
        private readonly Dictionary<ulong, int> _frontEmptyPreparations = new();
        internal long SkippedPreparations;

        private bool SkipFrontPreparation(ulong key)
        {
            if (!SkipDeferring)
                return false;
            lock (_frontEmptyPreparations) // [local] the prep worker notes results while the front asks
            {
                if (!_frontEmptyPreparations.TryGetValue(key, out var empty) || empty < SkipAfterEmpty)
                    return false;
                _frontEmptyPreparations[key] = empty + 1;
                if ((empty - SkipAfterEmpty) % SkipRetryEvery == SkipRetryEvery - 1)
                    return false;
            }

            SkippedPreparations++;
            return true;
        }

        private void NoteFrontPreparation(ulong key, bool empty)
        {
            if (!SkipDeferring)
                return;
            lock (_frontEmptyPreparations)
            {
                if (!empty)
                    _frontEmptyPreparations.Remove(key);
                else if (_frontEmptyPreparations.GetValueOrDefault(key) is var count && count < SkipAfterEmpty && (count != 0 || _frontEmptyPreparations.Count < 65536))
                    _frontEmptyPreparations[key] = count + 1;
            }
        }

        // [local] SHARPEMU_FRONT_PREP_WORKER=1: see FrontPrepWorker.
        internal FrontPrepWorker? PrepWorker;
        private Func<long, bool>? _frontPreparationStillValid;

        internal FrontPrepWorker.Job<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)>? SubmitPrepareOnWorker(FrontPrepWorker worker, ulong key, long version,
            Func<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)> prepare)
        {
            if (SkipFrontPreparation(key))
                return null;
            return worker.Submit(() =>
            {
                var programs = TryPrepareOnFront(prepare, out var inputs);
                NoteFrontPreparation(key, programs is null);
                return (programs, inputs);
            }, version);
        }

        // On the render thread: the worker's result, or nothing (the render thread prepares the draw).
        internal GraphicsPrograms? TakeWorkerPreparation(FrontPrepWorker worker, FrontPrepWorker.Job<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)>? job,
            out RenderExecutor.DrawProgramInputs inputs, Func<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)>? prepare = null)
        {
            worker.MaybeReport(Console.Error);
            inputs = default;
            if (job is null || !worker.TryTake(job, _frontPreparationStillValid ??= FrontPreparationStillValid, out var result, _tablesStillValid ??= TablesStillValid))
                return null;
            if (result.Programs is null)
            {
                Interlocked.Increment(ref worker.Empty);
                return null;
            }

            inputs = result.Inputs;
            return prepare is null ? result.Programs : VerifyGraphics(result.Programs, ref inputs, prepare);
        }

        // [local] SHARPEMU_FRONT_PREP_VERIFY: a preparation made ahead of the render thread (front or prep worker) is
        // prepared again here and compared; the render thread's result is used on a mismatch (and always with =2).
        internal static GraphicsPrograms? VerifyGraphics(GraphicsPrograms prepared, ref RenderExecutor.DrawProgramInputs inputs,
            Func<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)> prepare, bool force = false)
        {
            if (!FrontPrepVerify.Enabled && !force)
                return prepared;
            if (force) Interlocked.Increment(ref FrontPrepReads.Verified); // [local] SHARPEMU_FRONT_PREP_RANGES=2
            var (renderPrograms, renderInputs) = prepare();
            if (renderPrograms is null)
            {
                FrontPrepVerify.NoteRenderEmpty();
                inputs = default;
                return null;
            }

            Span<ulong> aheadParts = stackalloc ulong[FrontPrepVerify.Parts];
            Span<ulong> renderParts = stackalloc ulong[FrontPrepVerify.Parts];
            FrontPrepVerify.Fingerprint(prepared, in inputs, aheadParts);
            FrontPrepVerify.Fingerprint(renderPrograms, in renderInputs, renderParts);
            var same = FrontPrepVerify.Compare(aheadParts, renderParts, _onPrepWorker ? "draw_worker" : "draw", prepared.VertexInput.Stage.Program?.Hash ?? 0, prepared.PixelInput.Stage.Program?.Hash ?? 0);
            if (!same)
            {
                FrontPrepVerify.DumpStageDifference("draw_vertex", prepared.VertexInput.Stage.Program?.Hash ?? 0, prepared.VertexInput.Stage, renderPrograms.VertexInput.Stage);
                FrontPrepVerify.DumpStageDifference("draw_pixel", prepared.PixelInput.Stage.Program?.Hash ?? 0, prepared.PixelInput.Stage, renderPrograms.PixelInput.Stage);
            }

            if (same && !FrontPrepVerify.UseRender)
                return prepared;
            inputs = renderInputs;
            return renderPrograms;
        }

        internal static ComputeProgram? VerifyCompute(ComputeProgram prepared, Func<ComputeProgram?> prepare, bool force = false)
        {
            if (!FrontPrepVerify.Enabled && !force)
                return prepared;
            if (force) Interlocked.Increment(ref FrontPrepReads.Verified); // [local] SHARPEMU_FRONT_PREP_RANGES=2
            var renderProgram = prepare();
            if (renderProgram is null)
            {
                FrontPrepVerify.NoteRenderEmpty();
                return null;
            }

            Span<ulong> aheadParts = stackalloc ulong[FrontPrepVerify.Parts];
            Span<ulong> renderParts = stackalloc ulong[FrontPrepVerify.Parts];
            FrontPrepVerify.Fingerprint(prepared, aheadParts);
            FrontPrepVerify.Fingerprint(renderProgram, renderParts);
            var same = FrontPrepVerify.Compare(aheadParts, renderParts, "dispatch", prepared.Input.Stage.Program?.Hash ?? 0, 0);
            if (!same)
                FrontPrepVerify.DumpStageDifference("dispatch", prepared.Input.Stage.Program?.Hash ?? 0, prepared.Input.Stage, renderProgram.Input.Stage);
            return same && !FrontPrepVerify.UseRender ? prepared : renderProgram;
        }

        // [local] SHARPEMU_STORE_TABLE_APPLIED_CHECK (see DeviceStoreTablePlanner.AppliedCheck): a preparation that
        // evaluated a store table keeps the applied-write generation it started from, and is used only if no table
        // write was applied since.
        private Func<long, bool>? _tablesStillValid;
        internal long DiscardedTablePreparations;
        private long _tableDiscardReport = System.Diagnostics.Stopwatch.GetTimestamp();

        internal static long BeginTableMark()
        {
            SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.TakeUsedTables();
            return SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.AppliedGeneration;
        }

        internal static long EndTableMark(long mark) =>
            SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.TakeUsedTables() && SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.AppliedCheck ? mark : -1;

        internal bool TablesStillValid(long mark)
        {
            if (mark < 0 || SharpEmu.ShaderCompiler.Resources.DeviceStoreTablePlanner.AppliedGeneration == mark)
                return true;
            DiscardedTablePreparations++;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_tableDiscardReport).TotalSeconds >= 20)
            {
                _tableDiscardReport = System.Diagnostics.Stopwatch.GetTimestamp();
                Console.Error.WriteLine($"[STORE_TABLE_APPLIED] discarded_preparations={DiscardedTablePreparations}");
            }

            return false;
        }

        internal FrontPrepWorker.Job<ComputeProgram?>? SubmitComputeOnWorker(FrontPrepWorker worker, ulong key, long version, Func<ComputeProgram?> prepare)
        {
            if (SkipFrontPreparation(key))
                return null;
            return worker.Submit(() =>
            {
                var program = TryPrepareComputeOnFront(prepare);
                NoteFrontPreparation(key, program is null);
                return program;
            }, version);
        }

        internal ComputeProgram? TakeWorkerCompute(FrontPrepWorker worker, FrontPrepWorker.Job<ComputeProgram?>? job, Func<ComputeProgram?>? prepare = null)
        {
            if (job is null || !worker.TryTake(job, _frontPreparationStillValid ??= FrontPreparationStillValid, out var program, _tablesStillValid ??= TablesStillValid))
                return null;
            if (program is null)
            {
                Interlocked.Increment(ref worker.Empty);
                return null;
            }

            return prepare is null ? program : VerifyCompute(program, prepare);
        }

        internal static void EnterPrepWorkerThread()
        {
            _onPrepWorker = true;
            if (FrontPrepWorker.OnGuestLanes)
                SharpEmu.HLE.Host.HostLaneReservation.ApplyToHelperThread();
            else
                SharpEmu.HLE.Host.HostLaneReservation.ApplyToFrontWorkerThread();
        }

        internal GraphicsPrograms? TryPrepareOnFront(ulong key, Func<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)> prepare, out RenderExecutor.DrawProgramInputs inputs)
        {
            if (SkipFrontPreparation(key))
            {
                inputs = default;
                return null;
            }

            var result = TryPrepareOnFront(prepare, out inputs);
            NoteFrontPreparation(key, result is null);
            return result;
        }

        internal ComputeProgram? TryPrepareComputeOnFront(ulong key, Func<ComputeProgram?> prepare)
        {
            if (SkipFrontPreparation(key))
                return null;
            var result = TryPrepareComputeOnFront(prepare);
            NoteFrontPreparation(key, result is null);
            return result;
        }

        internal GraphicsPrograms? TryPrepareOnFront(Func<(GraphicsPrograms? Programs, RenderExecutor.DrawProgramInputs Inputs)> prepare, out RenderExecutor.DrawProgramInputs inputs)
        {
            _preparingOnFront = true;
            if (!_onPrepWorker) FrontPrepReads.Begin(); // [local] SHARPEMU_FRONT_PREP_RANGES
            var started = FrontProfile.LoopEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                var (programs, prepared) = prepare();
                inputs = prepared;
                if (FrontProfile.LoopEnabled && programs is null && !_onPrepWorker) FrontProfile.DeferredPrepTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                return programs;
            }
            catch (DeferPreparationException)
            {
                Interlocked.Increment(ref DeferredPreparations);
                inputs = default;
                return null;
            }
            finally
            {
                _preparingOnFront = false;
                FrontPrepReads.Release(_lastPrepPages); // [local] SHARPEMU_FRONT_PREP_RANGES: a previous one nobody took
                _lastPrepPages = FrontPrepReads.End();
            }
        }

        private static readonly bool FrontPrepLog = Environment.GetEnvironmentVariable("SHARPEMU_FRONT_PREP_LOG") == "1"; // [local]
        private static int _frontPrepLogs;

        internal ComputeProgram? TryPrepareComputeOnFront(Func<ComputeProgram?> prepare)
        {
            _preparingOnFront = true;
            if (!_onPrepWorker) FrontPrepReads.Begin(); // [local] SHARPEMU_FRONT_PREP_RANGES
            var started = FrontProfile.LoopEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                SharpEmu.ShaderCompiler.Resources.ResourceMaterializationCache.CurrentPlanHash = 0; // [local]
                var program = prepare();
                if (FrontProfile.LoopEnabled && program is null && !_onPrepWorker) FrontProfile.DeferredPrepTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                if (FrontPrepLog && Interlocked.Increment(ref _frontPrepLogs) <= 20000)
                    Console.Error.WriteLine($"[FRONT_PREP] hash=0x{SharpEmu.ShaderCompiler.Resources.ResourceMaterializationCache.CurrentPlanHash:X16} prepared={(program is null ? "no" : program.Available ? "yes" : "unavailable")} overlay={FrontOverlay.Count}");
                return program;
            }
            catch (DeferPreparationException)
            {
                Interlocked.Increment(ref DeferredPreparations);
                return null;
            }
            finally
            {
                _preparingOnFront = false;
                FrontPrepReads.Release(_lastPrepPages); // [local] SHARPEMU_FRONT_PREP_RANGES: a previous one nobody took
                _lastPrepPages = FrontPrepReads.End();
            }
        }

        private T RunBackRead<T>(Func<T> read)
        {
            if (_onPrepWorker) // [local] SHARPEMU_FRONT_PREP_WORKER: the render thread may be waiting for this preparation
            {
                throw new DeferPreparationException();
            }

            if (DeferFrontPreparation && _preparingOnFront && !(_backReadForPendingWrite && PendingWriteOverlay.Mode == 2)) // mode 3 defers like any synced read
            {
                throw new DeferPreparationException();
            }

            FrontSyncStats.Note(0);
            if ((Interlocked.Increment(ref FrontSyncedReadCount) & 63) == 0) // [local] sampled call sites
            {
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                var site = string.Join(" < ", frames.Take(5).Select(f => f.GetMethod() is { } m ? $"{m.DeclaringType?.Name}.{m.Name}" : "?"));
                lock (_frontReadSites)
                {
                    _frontReadSites[site] = _frontReadSites.GetValueOrDefault(site) + 64;
                    if (FrontSyncedReadCount % (64 * 64) == 0)
                        foreach (var (key, value) in _frontReadSites.OrderByDescending(pair => pair.Value).Take(6))
                            Console.Error.WriteLine($"[FRONT_SYNC_READ] total={FrontSyncedReadCount} count~{value} site={key}");
                }
            }
            return _back!.Run(() =>
            {
                _backReading = true;
                try
                {
                    return read();
                }
                finally
                {
                    _backReading = false;
                }
            });
        }

        private bool TryReadGuestWordDirect(ulong address, out uint word)
        {
            if (FrontStaleCheck) NoteFrontDirectRead(address, sizeof(uint)); // [local]
            // A page read before through its alias: no mapping check, no copy.
            if (IsCleanReadPage(address, sizeof(uint)) && TryGetAliasPointer(address, sizeof(uint), out var alias))
            {
                word = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(alias);
                return true;
            }

            word = 0;
            if (!_guestMemory.CanRead(address, sizeof(uint)))
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!_guestMemory.TryRead(address, bytes))
            {
                return false;
            }

            NoteFrontReadPage(address, sizeof(uint));
            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        // The front's clean-page note: the buffer cache's dirty state belongs to the render
        // thread, so only the mapping is checked.
        private void NoteFrontReadPage(ulong address, ulong size)
        {
            if (_onPrepWorker) // [local] the table belongs to the front thread
            {
                return;
            }

            if (!TryGetCleanReadPage(address, size, out var page, out var slot) || !_guestMemory.CanRead(page, CleanReadPageBytes))
            {
                return;
            }

            var pages = _frontCleanReadPages ??= new CleanReadPages();
            if (pages.Tags[slot] != page + 1)
            {
                pages.Tags[slot] = page + 1;
                pages.Aliases[slot] = 0;
                pages.Snapshots[slot] = null;
            }

            pages.Versions[slot] = _bufferCache.GpuModifiedVersion;
        }

        // [local] SHARPEMU_LAZY_BACK_READS (on by default; =0 disables): a lambda that captures a parameter makes the
        // compiler allocate its closure when the method is entered, taken branch or not. The guest readers below are
        // called per descriptor word (front and render thread) and only rarely go to the render thread, so the
        // closures now live in the helpers that run that branch. =0 allocates an object of the old closure's size on
        // entry instead, to measure the difference.
        internal static readonly bool LazyBackReads = Environment.GetEnvironmentVariable("SHARPEMU_LAZY_BACK_READS") != "0";

        internal sealed class EagerClosure(object owner, ulong address)
        {
            public readonly object Owner = owner;
            public readonly ulong Address = address;
        }

        private static object? _eagerClosure;

        internal static void AllocateEagerClosure(object owner, ulong address) => _eagerClosure = new EagerClosure(owner, address);

        private (bool Ok, uint Word) BackReadWord(ulong address) =>
            RunBackRead(() => (TryReadGuestWord(address, out var synced), synced));

        private bool BackReadResidentBytes(ulong address, Span<byte> destination)
        {
            var buffer = new byte[destination.Length];
            var ok = RunBackRead(() => TryReadResidentGuestBytes(address, buffer, false));
            buffer.CopyTo(destination);
            return ok;
        }

        public bool TryReadGuestWord(ulong address, out uint word)
        {
            if (!LazyBackReads) AllocateEagerClosure(this, address); // [local]
            using var profile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.GuestRead);
            using var frontProfile = FrontProfile.Measure(FrontProfile.Phase.WordRead);
            FrontProfile.NoteSite(FrontProfile.Phase.WordRead);
            var conservativeVerify = false; // [local] SHARPEMU_CONSERVATIVE_RECORDS
            var conservativePrecise = false;
            if (Gpu.Buffers.ConservativeRecords.Mode != 0 &&
                Gpu.Buffers.ConservativeRecords.Matches(Gpu.Buffers.ConservativeRecords.CurrentReader, SharpEmu.ShaderCompiler.Resources.SrtReadProbe.CurrentBaseAddress, address))
            {
                if (Gpu.Buffers.ConservativeRecords.Mode == 1)
                {
                    word = Gpu.Buffers.ConservativeRecords.Sentinel;
                    Gpu.Buffers.ConservativeRecords.NoteSentinel();
                    return true;
                }

                conservativeVerify = Gpu.Buffers.ConservativeRecords.Mode == 2;
                conservativePrecise = Gpu.Buffers.ConservativeRecords.Mode >= 3;
            }

            if (OnFrontThread)
            {
                if (NeedsBackRead(address, sizeof(uint)))
                {
                    var (ok, value) = BackReadWord(address);
                    word = value;
                    if (conservativeVerify && ok) Gpu.Buffers.ConservativeRecords.Verify(word); // [local]
                    return ok;
                }

                if (!TryReadGuestWordDirect(address, out word))
                    return false;
                if (!FrontOverlay.IsEmpty)
                {
                    Span<byte> patched = stackalloc byte[sizeof(uint)];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(patched, word);
                    ApplyFrontOverlay(address, patched);
                    word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(patched);
                }

                if (conservativeVerify) Gpu.Buffers.ConservativeRecords.Verify(word); // [local]
                return true;
            }

            word = 0;
            var synchronized = false;
            if (IsCleanReadPage(address, sizeof(uint)))
            {
                if (TryGetAliasPointer(address, sizeof(uint), out var alias))
                {
                    word = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(alias);
                    return true;
                }
            }
            else
            {
                // Resource planning can inspect a dynamic descriptor before the draw has
                // supplied a valid guest address.  Do not pass an invalid range to the
                // page tracker: it treats that as an emulator invariant violation and
                // terminates the process.  A failed read lets the materializer reject or
                // specialize the source normally.
                if (!_guestMemory.CanRead(address, sizeof(uint)))
                {
                    return false;
                }

                if (SpeculativeTableReads != 0 && _bufferCache.HasGpuDirtyBytes(address, sizeof(uint)) &&
                    !_bufferCache.HasPreciseGpuDirtyBytes(address, sizeof(uint))) // [local] dirty only through a broad binding
                {
                    Span<byte> cpu = stackalloc byte[sizeof(uint)];
                    if (_guestMemory.TryRead(address, cpu))
                    {
                        var cpuWord = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cpu);
                        _speculativeReads++;
                        if (SpeculativeTableReads == 1)
                        {
                            word = cpuWord;
                            ReportSpeculative();
                            return true;
                        }

                        if (_bufferCache.TrySynchronizeCpuRead(address, sizeof(uint), SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.ShaderResourceRead) &&
                            _guestMemory.TryRead(address, cpu) && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cpu) != cpuWord)
                        {
                            _speculativeMismatches++;
                            if (_speculativeMismatches < 20) Console.Error.WriteLine($"[SPEC_READ] mismatch address=0x{address:X} cpu=0x{cpuWord:X8} gpu=0x{System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(cpu):X8}");
                        }

                        ReportSpeculative();
                    }
                }

                if (SpeculativeTableReads == 3 && _bufferCache.HasGpuDirtyBytes(address, sizeof(uint))) // [local] mode 3: per-shader change audit
                {
                    Span<byte> pre = stackalloc byte[sizeof(uint)];
                    if (_guestMemory.TryRead(address, pre)) { _auditPre = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pre); _auditArmed = true; }
                }

                Gpu.Buffers.TlasHle.Instance?.NoteCpuRead(address, sizeof(uint), Gpu.Buffers.TableWordMemo.CurrentProgram); // [local] SHARPEMU_TLAS_HLE_CONSUMERS
                var memoPending = false; // [local] SHARPEMU_TABLE_WORD_MEMO
                var memoValue = 0u;
                if (!NoSyncTableReads && _bufferCache.HasGpuDirtyBytes(address, sizeof(uint)) && Gpu.Buffers.TableWordMemo.Enabled)
                {
                    if (Gpu.Buffers.TableWordMemo.TryGet(address, out memoValue))
                    {
                        Gpu.Buffers.TableWordMemo.NoteHit();
                        if (Gpu.Buffers.TableWordMemo.Mode == 1)
                        {
                            word = memoValue;
                            return true;
                        }

                        memoPending = true;
                    }
                    else
                    {
                        Gpu.Buffers.TableWordMemo.NoteMiss();
                    }
                }

                if (!NoSyncTableReads && _bufferCache.HasGpuDirtyBytes(address, sizeof(uint))) // [local] ceiling experiment: read the stale CPU copy instead of waiting
                {
                    if (Diagnostics.GpuReadTrace.Enabled)
                    {
                        Diagnostics.GpuReadTrace.Record(address, ResourceMaterializationCache.ReadingTable);
                    }

                    Span<byte> mirrored = stackalloc byte[sizeof(uint)]; // [local] SHARPEMU_READBACK_MIRROR
                    if (_bufferCache.TryReadMirrored(address, mirrored))
                    {
                        word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mirrored);
                        return true;
                    }

                    // [local experiment] the pending command buffer may hold the writes this word depends on
                    if (FlushBeforeTableReads) { _flushedTableReads++; _scheduler.FlushAndWait(); }
                    _bufferCache.NoteSyncTableRead(address, sizeof(uint)); // [local] SHARPEMU_EARLY_WRITER_SUBMIT
                    var census = Diagnostics.TableReadCensus.Enabled; // [local]
                    var censusTicks = default((ulong Own, ulong Window, ulong Precise, ulong Current, ulong Completed));
                    var censusPre = 0u;
                    var censusStarted = 0L;
                    var earlyPre = 0u; // [local] SHARPEMU_EARLY_WRITER_SUBMIT=2
                    if (Gpu.Buffers.GuestBufferCache.EarlyWriterMode == 2)
                    {
                        Span<byte> pre = stackalloc byte[sizeof(uint)];
                        earlyPre = _guestMemory.TryRead(address, pre) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pre) : 0;
                    }

                    var probe = Diagnostics.SrtReadProbeLog.Enabled; // [local] SHARPEMU_SRT_READ_PROBE
                    if (probe || census)
                    {
                        Span<byte> pre = stackalloc byte[sizeof(uint)];
                        censusPre = _guestMemory.TryRead(address, pre) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pre) : 0;
                        censusTicks = _bufferCache.CensusTicks(address, sizeof(uint));
                        censusStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    }

                    if (conservativePrecise) // [local] SHARPEMU_CONSERVATIVE_RECORDS=3/4: this read alone after its precise writer tick
                    {
                        _bufferCache.ForcePreciseWait = true;
                        _bufferCache.ForcePreciseVerify = Gpu.Buffers.ConservativeRecords.Mode == 4;
                    }

                    bool preciseSynced;
                    try
                    {
                        preciseSynced = _bufferCache.TrySynchronizeCpuRead(address, sizeof(uint),
                            SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.ShaderResourceRead);
                    }
                    finally
                    {
                        if (conservativePrecise)
                        {
                            _bufferCache.ForcePreciseWait = false;
                            _bufferCache.ForcePreciseVerify = false;
                            Gpu.Buffers.ConservativeRecords.NotePrecise(_bufferCache.PreciseWaitReads, _bufferCache.PreciseWaitFallbacks, _bufferCache.PreciseVerifyChecks, _bufferCache.PreciseVerifyMismatches);
                        }
                    }

                    if (!preciseSynced)
                    {
                        return false;
                    }

                    if (probe || census)
                    {
                        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(censusStarted).TotalMilliseconds;
                        Span<byte> post = stackalloc byte[sizeof(uint)];
                        var postWord = _guestMemory.TryRead(address, post) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(post) : censusPre;
                        if (probe)
                            Diagnostics.SrtReadProbeLog.Record(Diagnostics.GpuReadTrace.CurrentShader, address, ms, censusPre, postWord,
                                Gpu.Buffers.TableWordMemo.Enabled ? Gpu.Buffers.TableWordMemo.ProbeReason(address, postWord) : "off");
                        if (census)
                        Diagnostics.TableReadCensus.Record(address, postWord != censusPre, ms, censusTicks.Own, censusTicks.Window, censusTicks.Precise, censusTicks.Current, censusTicks.Completed);
                    }

                    if (Gpu.Buffers.GuestBufferCache.EarlyWriterMode == 2)
                    {
                        Span<byte> post = stackalloc byte[sizeof(uint)];
                        var postWord = _guestMemory.TryRead(address, post) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(post) : earlyPre;
                        _bufferCache.NoteSyncTableResult(address, postWord != earlyPre);
                    }

                    synchronized = true;
                    if (Gpu.Buffers.TableWordMemo.Enabled) // [local] SHARPEMU_TABLE_WORD_MEMO
                    {
                        Span<byte> synced = stackalloc byte[sizeof(uint)];
                        if (_guestMemory.TryRead(address, synced))
                        {
                            var syncedWord = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(synced);
                            if (memoPending)
                                Gpu.Buffers.TableWordMemo.Verify(address, memoValue, syncedWord);
                            Gpu.Buffers.TableWordMemo.Store(address, syncedWord);
                        }
                    }
                }
                else
                {
                    NoteCleanReadPage(address, sizeof(uint));
                }
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!_guestMemory.TryRead(address, bytes))
            {
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (_auditArmed) { _auditArmed = false; AuditTableRead(_auditPre != word); } // [local]
            if (WatchedBounds.Count != 0 && _watchLogs < 400) // [local] reads of the bounds 0xF1D1 writes
            {
                bool watched; lock (WatchedBounds) watched = WatchedBounds.Contains(address & ~0x1FUL) || WatchedBounds.Contains((address - 0x10) & ~0x1FUL);
                if (watched) { _watchLogs++; Console.Error.WriteLine($"[BOUNDS_READ] tick={_scheduler.CurrentTick} addr=0x{address:X} word=0x{word:X8} synchronized={synchronized} dirtyNow={_bufferCache.HasGpuDirtyBytes(address, 4)}"); }
            }
            if (Diagnostics.GpuReadTrace.Enabled && synchronized)
            {
                Diagnostics.GpuReadTrace.RecordValue(address, word);
            }

            if (conservativeVerify) Gpu.Buffers.ConservativeRecords.Verify(word); // [local]
            return true;
        }

        // Refused while a GPU buffer or image write may still own the range.
        public bool TryReadCleanGuestWord(ulong address, out uint word)
        {
            if (!LazyBackReads) AllocateEagerClosure(this, address); // [local]
            using var profile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.CleanGuestRead);
            using var frontProfile = FrontProfile.Measure(FrontProfile.Phase.WordRead);
            FrontProfile.NoteSite(FrontProfile.Phase.WordRead);
            if (OnFrontThread)
            {
                if (NeedsBackRead(address, sizeof(uint)))
                {
                    var (ok, value) = BackReadWord(address); // the front always answered clean reads; now with the synced value
                    word = value;
                    return ok;
                }

                if (!TryReadGuestWordDirect(address, out word))
                    return false;
                if (!FrontOverlay.IsEmpty) // [local]
                {
                    Span<byte> patched = stackalloc byte[sizeof(uint)];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(patched, word);
                    ApplyFrontOverlay(address, patched);
                    word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(patched);
                }

                return true;
            }

            word = 0;
            if (!_guestMemory.CanRead(address, sizeof(uint)))
            {
                return false;
            }

            if ((_bufferCache.MayHaveGpuDirtyPages(address, sizeof(uint)) && _bufferCache.HasGpuDirtyPages(address, sizeof(uint))) ||
                _bufferCache.HasGpuDirtyBytes(address, sizeof(uint)) ||
                _imageCache.HasGpuModifiedImageBytes(address, sizeof(uint)))
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!_guestMemory.TryRead(address, bytes))
            {
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        // The same ownership rules as the two word readers, checked once for a whole range.
        public bool TryReadResidentGuestBytes(ulong address, Span<byte> destination, bool clean)
        {
            if (!LazyBackReads) AllocateEagerClosure(this, address); // [local]
            var size = (ulong)destination.Length;
            using var frontProfile = FrontProfile.Measure(FrontProfile.Phase.ResidentRead);
            if (OnFrontThread && NeedsBackRead(address, size))
            {
                return BackReadResidentBytes(address, destination);
            }

            if (OnFrontThread)
            {
                if (FrontStaleCheck) NoteFrontDirectRead(address, size); // [local]
                if (IsCleanReadPage(address, size) && TryGetAliasPointer(address, size, out var frontAlias))
                {
                    new ReadOnlySpan<byte>(frontAlias, destination.Length).CopyTo(destination);
                    ApplyFrontOverlay(address, destination); // [local]
                    return true;
                }

                if (!_guestMemory.CanRead(address, size) || !_guestMemory.TryRead(address, destination))
                {
                    return false;
                }

                NoteFrontReadPage(address, size);
                ApplyFrontOverlay(address, destination); // [local]
                return true;
            }

            if (!ResidentReadAllowed(address, size, clean))
            {
                return false;
            }

            if (TryGetAliasPointer(address, size, out var alias))
            {
                new ReadOnlySpan<byte>(alias, destination.Length).CopyTo(destination);
                return true;
            }

            return _guestMemory.TryRead(address, destination);
        }

        // The render thread's ownership checks of TryReadResidentGuestBytes, shared with the in-place compare.
        private bool ResidentReadAllowed(ulong address, ulong size, bool clean)
        {
            if (clean || !IsCleanReadPage(address, size))
            {
                if (!_guestMemory.CanRead(address, size) ||
                    _bufferCache.HasGpuDirtyBytes(address, size) ||
                    (clean && (_bufferCache.HasGpuDirtyPages(address, size) || _imageCache.HasGpuModifiedImageBytes(address, size))))
                {
                    return false;
                }

                NoteCleanReadPage(address, size);
            }

            return true;
        }

        // [local] SHARPEMU_VALIDATE_IN_PLACE: what TryReadResidentGuestBytes would read, compared with expected where it
        // lies instead of copied out first. True with equal set when compared; false when the read would fail or take
        // another path (the front thread, memory outside a single backing view): the caller then reads as before.
        public bool TryCompareResidentGuestBytes(ulong address, ReadOnlySpan<byte> expected, bool clean, out bool equal)
        {
            equal = false;
            if (OnFrontThread || expected.IsEmpty)
            {
                return false;
            }

            if (!LazyBackReads) AllocateEagerClosure(this, address); // [local]
            var size = (ulong)expected.Length;
            if (!ResidentReadAllowed(address, size, clean))
            {
                return false;
            }

            if (TryGetAliasPointer(address, size, out var alias))
            {
                equal = new ReadOnlySpan<byte>(alias, expected.Length).SequenceEqual(expected);
                return true;
            }

            return _guestMemory.TryCompare(address, expected, out equal);
        }

        // [local] SHARPEMU_MATERIALIZE_SYNC_VALIDATE: on the render thread, the GPU-owned ranges among these are
        // synchronized as TryReadGuestWord would one word at a time, in one readback; the front never synchronizes.
        private readonly List<(ulong Address, ulong Size)> _syncRanges = new();

        // [local] SHARPEMU_STORE_TABLE_SYNC: what TryReadGuestWord does for each GPU-dirty word of the range (synchronize,
        // then read the backing), done once for the whole range; the readback waits for the range's writers on the
        // table readback queue, not behind the asynchronous copies of the shared one. The front never synchronizes.
        public bool TrySynchronizeTableRange(ReadOnlySpan<(ulong Address, int Length)> ranges)
        {
            if (OnFrontThread || ranges.Length != 1)
                return false;
            var (address, length) = ranges[0];
            var size = (ulong)length;
            if (!_guestMemory.CanRead(address, size))
                return false;
            return !_bufferCache.HasGpuDirtyBytes(address, size) ||
                _bufferCache.TrySynchronizeTableRead(address, size, SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.ShaderResourceRead);
        }

        public bool TrySynchronizeGuestRanges(ReadOnlySpan<(ulong Address, int Length)> ranges)
        {
            if (OnFrontThread || ranges.Length == 0 || !_guestMemory.CanRead(ranges[0].Address, (ulong)ranges[0].Length))
                return false;
            _syncRanges.Clear();
            foreach (var (address, length) in ranges)
            {
                if (_syncRanges.Count == 0 || (_guestMemory.CanRead(address, (ulong)length) && _bufferCache.HasGpuDirtyBytes(address, (ulong)length)))
                    _syncRanges.Add((address, (ulong)length));
            }

            return _bufferCache.TrySynchronizeCpuReads(_syncRanges, SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.ShaderResourceRead);
        }

        private const ulong CleanReadPageBytes = 0x1000;
        private const int CleanReadPageSlots = 64;
        private CleanReadPages? _cleanReadPages;
        // [local] The front thread's own clean-page notes. One shared table let each thread retag a slot under the
        // other (a tag of one page with the alias of another), and let the render thread trust a page the front
        // noted without the GPU-dirty check its own notes make.
        private CleanReadPages? _frontCleanReadPages;

        private CleanReadPages? ThreadCleanReadPages => _onPrepWorker ? null : OnFrontThread ? _frontCleanReadPages : _cleanReadPages;
        private bool _backingAliasAccess;

        private sealed class CleanReadPages
        {
            public readonly ulong[] Tags = new ulong[CleanReadPageSlots];
            public readonly long[] Versions = new long[CleanReadPageSlots];
            public readonly ulong[] Aliases = new ulong[CleanReadPageSlots];
            public readonly object?[] Snapshots = new object?[CleanReadPageSlots];
        }

        private bool TryGetAliasPointer(ulong address, ulong size, out byte* pointer)
        {
            pointer = null;
            if (!_backingAliasAccess || ThreadCleanReadPages is not { } pages ||
                !TryGetCleanReadPage(address, size, out var page, out var slot) || pages.Tags[slot] != page + 1 ||
                _guestBacking.BackingAliasSnapshot is not { } snapshot)
            {
                return false;
            }

            if (pages.Aliases[slot] == 0 || !ReferenceEquals(pages.Snapshots[slot], snapshot))
            {
                if (!_guestBacking.TryResolveBackingAlias(page, CleanReadPageBytes, out var resolved) || resolved == 0)
                {
                    return false;
                }

                pages.Aliases[slot] = resolved;
                pages.Snapshots[slot] = snapshot;
            }

            pointer = (byte*)(pages.Aliases[slot] + (address - page));
            return true;
        }

        private static bool TryGetCleanReadPage(ulong address, ulong size, out ulong page, out int slot)
        {
            page = address & ~(CleanReadPageBytes - 1);
            slot = (int)((page / CleanReadPageBytes) & (CleanReadPageSlots - 1));
            return size != 0 && address + size > address && ((address + size - 1) & ~(CleanReadPageBytes - 1)) == page;
        }

        private bool IsCleanReadPage(ulong address, ulong size) =>
            ThreadCleanReadPages is { } pages &&
            TryGetCleanReadPage(address, size, out var page, out var slot) &&
            pages.Tags[slot] == page + 1 &&
            pages.Versions[slot] == _bufferCache.GpuModifiedVersion;

        private void NoteCleanReadPage(ulong address, ulong size)
        {
            if (!TryGetCleanReadPage(address, size, out var page, out var slot))
            {
                return;
            }

            var version = _bufferCache.GpuModifiedVersion;
            if (!_guestMemory.CanRead(page, CleanReadPageBytes) || _bufferCache.HasGpuDirtyBytes(page, CleanReadPageBytes))
            {
                return;
            }

            var pages = _cleanReadPages ??= new CleanReadPages();
            if (pages.Tags[slot] != page + 1)
            {
                pages.Tags[slot] = page + 1;
                pages.Aliases[slot] = 0;
                pages.Snapshots[slot] = null;
            }

            pages.Versions[slot] = version;
        }

        // The front thread creates modules while the render thread creates pipelines from them.
        // Static: a test harness may build the presenter without running its initializers.
        private static readonly object _shaderModuleGate = new();

        public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramCompile);
            var module = CreateShaderModule(shader.Payload);
            SetDebugName(ObjectType.ShaderModule, module.Handle, $"SharpEmu {stage} 0x{hash:X16}");
            var identity = VulkanPipelineCacheStorage.CompiledShaderIdentity(shader.Payload);
            lock (_shaderModuleGate)
            {
                _shaderModules.Add(programId, module);
                _shaderModuleSpirvBytes[module.Handle] = shader.Payload.Length;
                _shaderModuleCacheIdentities[module.Handle] = identity;
            }

            if (stage == ShaderStage.Compute)
            {
                NoteRuntimeComputeModule(identity);
            }

            return module.Handle;
        }

        // The Metal shader compiler can spend seconds on one translated program and the
        // command stream cannot advance while it does, so a slow creation is reported with
        // the SPIR-V size that produced it.
        private const long SlowPipelineCreationMilliseconds = 250;

        private int SpirvBytesOf(ulong moduleHandle)
        {
            lock (_shaderModuleGate)
            {
                return _shaderModuleSpirvBytes.TryGetValue(moduleHandle, out var bytes) ? bytes : -1;
            }
        }

        private void ReportPipelineCreation(long elapsedMilliseconds, string kind, string stages, string spirv)
        {
            Interlocked.Add(ref _pipelineCreationMilliseconds, elapsedMilliseconds);
            if (Gpu.Pipelines.CompileLog.Enabled) // [local] SHARPEMU_COMPILE_LOG: the async compile ran on a compiler worker
                Gpu.Pipelines.CompileLog.Note(kind, elapsedMilliseconds, 0, $"{stages} spirv_bytes={spirv}".Replace(' ', ','),
                    kind == "compute-async" ? "compile_worker" : null);
            if (elapsedMilliseconds < SlowPipelineCreationMilliseconds)
            {
                return;
            }

            Console.Error.WriteLine(
                $"[GPU][WARN] Slow pipeline creation: kind={kind} {stages} spirv_bytes={spirv} " +
                $"ms={elapsedMilliseconds} total_s={Interlocked.Read(ref _pipelineCreationMilliseconds) / 1000.0:F1}");
        }

        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
                MarkPipelineCacheDirty();
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            if (!_supportsFragmentShaderBarycentric && RequiresFragmentShaderBarycentric(code))
            {
                throw new NotSupportedException(
                    "The shader requires the fragmentShaderBarycentric device feature.");
            }

            string? dumpPath = null;
            var dumpDirectory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
            if (!string.IsNullOrWhiteSpace(dumpDirectory))
            {
                Directory.CreateDirectory(dumpDirectory);

                var sequence = Interlocked.Increment(ref _shaderModuleDumpSequence);
                dumpPath = Path.Combine(dumpDirectory, $"{sequence:D4}.spv");
                File.WriteAllBytes(dumpPath, code);

                _pendingShaderModuleDumpPath = dumpPath;
            }

            try
            {
                fixed (byte* codePointer = code)
                {
                    var createInfo = new ShaderModuleCreateInfo
                    {
                        SType = StructureType.ShaderModuleCreateInfo,
                        CodeSize = (nuint)code.Length,
                        PCode = (uint*)codePointer,
                    };
                    Check(
                        _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                        "vkCreateShaderModule");
                    return module;
                }
            }
            finally
            {
                _pendingShaderModuleDumpPath = null;
            }
        }

        private static bool RequiresFragmentShaderBarycentric(ReadOnlySpan<byte> code)
        {
            for (var offset = 20; offset + 4 <= code.Length;)
            {
                var header = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(code[offset..]);
                var wordCount = (int)(header >> 16);
                if (wordCount == 0 || wordCount > (code.Length - offset) / 4)
                {
                    throw new InvalidOperationException("The shader contains an invalid SPIR-V instruction.");
                }

                if ((header & 0xFFFF) == (uint)SpirvOp.Capability && wordCount == 2 &&
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(code[(offset + 4)..]) ==
                        (uint)SpirvCapability.FragmentBarycentricKhr)
                {
                    return true;
                }
                offset += wordCount * 4;
            }
            return false;
        }

        // One layout binding per descriptor binding of the stage, at the stage's native binding numbers.
        private static void CollectLayoutBindings(List<DescriptorSetLayoutBinding> bindings, ShaderProgramInfo program, ShaderStage stage) =>
            CollectLayoutBindings(
                bindings,
                program.Bindings ?? throw SubmissionScheduler.Fatal($"The program has no binding layout: hash=0x{program.Hash:X16}."),
                stage);

        private static void CollectLayoutBindings(List<DescriptorSetLayoutBinding> bindings, BindingLayout layout, ShaderStage stage)
        {
            foreach (var binding in layout.Descriptors)
            {
                bindings.Add(new DescriptorSetLayoutBinding
                {
                    Binding = BindingLayout.NativeBindingIndex(stage, binding.Kind),
                    DescriptorType = DescriptorWriter.DescriptorType(binding.Kind),
                    DescriptorCount = DescriptorWriter.DescriptorCount(binding),
                    StageFlags = DescriptorWriter.ShaderStageFlag(stage),
                });
            }
        }

        // The set is pushed when its descriptors fit the device limit, else it comes from the heap.
        private DescriptorSetLayout CreateDescriptorSetLayout(List<DescriptorSetLayoutBinding> bindings, out bool usesPushDescriptors, out DescriptorSetDemand demand)
        {
            var descriptorCount = 0u;
            demand = default;
            foreach (var binding in bindings)
            {
                descriptorCount += binding.DescriptorCount;
                demand = demand.Add(DescriptorSetDemand.Of(binding.DescriptorType, binding.DescriptorCount));
            }

            usesPushDescriptors = descriptorCount <= _maxPushDescriptors;
            var bindingArray = bindings.ToArray();
            fixed (DescriptorSetLayoutBinding* bindingPointer = bindingArray)
            {
                var create = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    Flags = usesPushDescriptors ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0,
                    BindingCount = (uint)bindingArray.Length,
                    PBindings = bindingArray.Length == 0 ? null : bindingPointer,
                };
                Check(_vk.CreateDescriptorSetLayout(_device, &create, null, out var layout), "vkCreateDescriptorSetLayout");
                return layout;
            }
        }

        private PipelineLayout CreatePipelineLayout(DescriptorSetLayout setLayout, ShaderStageFlags pushStages)
        {
            var pushConstants = new PushConstantRange { StageFlags = pushStages, Offset = 0, Size = PushConstantBytes };
            var create = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstants,
            };
            Check(_vk.CreatePipelineLayout(_device, &create, null, out var layout), "vkCreatePipelineLayout");
            return layout;
        }

        // [local] What tells a graphics pipeline apart from the others of its program pair: the depth bounds and a
        // hash of the rest of the static state (bounds zeroed), the rendering formats and the vertex input.
        private static string DescribeKeyForProfile(GraphicsPipelineDescription? description)
        {
            if (description is null)
            {
                return string.Empty;
            }

            var parameters = description.StaticParameters;
            var rest = parameters.Clone();
            rest.DepthMinBounds = 0f;
            rest.DepthMaxBounds = 0f;
            var staticHash = System.IO.Hashing.XxHash64.HashToUInt64(rest.Bytes);
            return FormattableString.Invariant(
                $" dbt={(parameters.DepthBoundsTestEnable ? 1 : 0)} dbmin={parameters.DepthMinBounds:R} dbmax={parameters.DepthMaxBounds:R} static=0x{staticHash:X16} rendering=0x{(uint)description.Rendering.GetHashCode():X8} vertex_input=0x{(uint)description.VertexInput.GetHashCode():X8}");
        }

        private PipelineHandle RegisterPipeline(RenderPipelineEntry entry)
        {
            entry.Id = ++_nextPipelineId;
            _pipelineEntries.Add(entry.Id, entry);
            if (_gpuCommandProfile is { Dormant: false })
                Console.Error.WriteLine($"[PERF][GPU_PIPELINE] pipeline={entry.Id} vertex=0x{entry.ProfileVertexHash:X16} pixel=0x{entry.ProfilePixelHash:X16} compute=0x{entry.ProfileComputeHash:X16}{DescribeKeyForProfile(entry.Description)}");
            return new PipelineHandle(entry.Id, entry.Layout.Handle, entry.UsesPushDescriptors);
        }

        public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            if (description.Rendering.ColorCount > _maxColorAttachments)
            {
                throw SubmissionScheduler.Fatal($"The draw binds more color attachments than the device supports: count={description.Rendering.ColorCount} max={_maxColorAttachments}.");
            }

            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, description.VertexStage, ShaderStage.Vertex);
            if (description.PixelStage is { } pixelStage)
            {
                CollectLayoutBindings(bindings, pixelStage, ShaderStage.Pixel);
            }

            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out var demand);
            var entry = new RenderPipelineEntry
            {
                SetLayout = setLayout,
                Demand = demand,
                Layout = CreatePipelineLayout(setLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit),
                UsesPushDescriptors = usesPushDescriptors,
                Description = description,
                ProfileVertexHash = description.VertexStage.Hash,
                ProfilePixelHash = description.PixelStage?.Hash ?? 0,
            };
            // Rectangle draws bind a native fill or compatibility variant at draw time.
            if (!entry.RectangleList)
            {
                entry.Pipeline = CreateRenderPipeline(description, description.StaticParameters.Topology, entry.Layout);
            }

            return RegisterPipeline(entry);
        }

        private static StencilOpState ToVkStencilOpState(in StencilOperations operations) => new()
        {
            FailOp = operations.FailOperation,
            PassOp = operations.PassOperation,
            DepthFailOp = operations.DepthFailOperation,
            CompareOp = operations.Compare,
        };

        // The host format of each attribute, narrowed to the components the program fetches.
        private static void BuildVertexAttributes(
            GraphicsPipelineDescription description,
            VertexInputAttributeDescription[] attributes,
            VertexInputBindingDescription[] bindings)
        {
            var vertexInput = description.VertexInput;
            var info = description.VertexInfo;
            for (var binding = 0; binding < vertexInput.BindingCount; binding++)
            {
                bindings[binding] = new VertexInputBindingDescription
                {
                    Binding = (uint)binding,
                    Stride = vertexInput.Bindings[binding].Stride,
                    InputRate = vertexInput.Bindings[binding].Instance ? VertexInputRate.Instance : VertexInputRate.Vertex,
                };
            }

            for (var index = 0; index < vertexInput.AttributeCount; index++)
            {
                var resource = info.Attributes[index];
                var descriptor = resource.Descriptor;
                var compiledComponents = description.VertexStage.VertexFetchComponents[index];
                var usedComponents = compiledComponents > 0 ? compiledComponents : (uint)resource.RegisterCount;
                var format = VertexAttributeFormats.Resolve(in descriptor, usedComponents, out var attributeSize);
                if (descriptor.OutOfBounds != 0 && Interlocked.Exchange(ref _vertexOutOfBoundsLines, 1) == 0)
                {
                    Console.Error.WriteLine($"[LOADER][INFO] vertex_input accepted the out-of-bounds mode {descriptor.OutOfBounds} of an attribute buffer");
                }

                if (descriptor.AddThreadId)
                {
                    throw SubmissionScheduler.Fatal($"A vertex attribute buffer adds the thread index: attribute={index} hash=0x{description.VertexStage.Hash:X16}.");
                }

                if (descriptor.SwizzleEnabled)
                {
                    throw SubmissionScheduler.Fatal($"A vertex attribute buffer is swizzled: attribute={index} hash=0x{description.VertexStage.Hash:X16}.");
                }

                CheckVertexSwizzle(in descriptor, (int)usedComponents, attributeSize, index);
                attributes[index] = new VertexInputAttributeDescription
                {
                    Location = (uint)index,
                    Binding = vertexInput.Attributes[index].Binding,
                    Format = format,
                    Offset = vertexInput.Attributes[index].Offset,
                };
            }
        }

        private static uint DestinationSelect(uint x, uint y = 0, uint z = 0, uint w = 0) => x | (y << 3) | (z << 6) | (w << 9);

        // A destination select the fixed-function fetch cannot apply is logged once and accepted.
        private static void CheckVertexSwizzle(in BufferDescriptorWords descriptor, int fetchedComponents, uint attributeSize, int index)
        {
            uint swizzle;
            uint expected;
            var supported = true;
            switch (fetchedComponents)
            {
                case 1:
                    swizzle = descriptor.DestinationSelectX;
                    expected = DestinationSelect(4);
                    supported = swizzle == expected;
                    break;
                case 2:
                    swizzle = descriptor.DestinationSelectXY;
                    expected = attributeSize == 1 ? DestinationSelect(4, 0) : DestinationSelect(4, 5);
                    supported = swizzle == expected;
                    break;
                case 3:
                    swizzle = descriptor.DestinationSelectXYZ;
                    expected = attributeSize switch
                    {
                        1 => DestinationSelect(4, 0, 0),
                        2 => DestinationSelect(4, 5, 0),
                        _ => DestinationSelect(4, 5, 6),
                    };
                    supported = swizzle == expected;
                    break;
                case 4:
                    swizzle = descriptor.DestinationSelectXYZW;
                    switch (attributeSize)
                    {
                        case 1:
                            expected = DestinationSelect(4, 0, 0, 1);
                            supported = swizzle == expected;
                            break;
                        case 2:
                            expected = DestinationSelect(4, 5, 0, 1);
                            supported = swizzle == expected;
                            break;
                        case 3:
                            expected = DestinationSelect(4, 5, 6, 1);
                            supported = swizzle == expected || swizzle == DestinationSelect(4, 5, 6, 0);
                            break;
                        default:
                            expected = DestinationSelect(4, 5, 6, 7);
                            supported = swizzle == expected || swizzle == DestinationSelect(4, 5, 6, 1) || swizzle == DestinationSelect(4, 5, 6, 0);
                            break;
                    }

                    break;
                default:
                    throw SubmissionScheduler.Fatal($"A vertex attribute fetch uses an invalid component count: attribute={index} components={fetchedComponents}.");
            }

            if (!supported && Interlocked.Exchange(ref _vertexSwizzleLines, 1) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] vertex_input accepted an unsupported destination select attribute={index} size={attributeSize} " +
                    $"components={fetchedComponents} swizzle=0x{swizzle:X3} expected=0x{expected:X3}");
            }
        }

        // One graphics pipeline for dynamic rendering: the attachment formats travel in the create info.
        private Pipeline CreateRenderPipeline(GraphicsPipelineDescription description, PrimitiveTopology topology, PipelineLayout layout,
            PolygonMode polygonMode = PolygonMode.Fill)
        {
            var parameters = description.StaticParameters;
            var rendering = description.Rendering;
            var vertexModule = new ShaderModule(description.VertexProgram.Module);
            var pixelModule = description.PixelStage is null ? default : new ShaderModule(description.PixelProgram.Module);
            if (vertexModule.Handle == 0 || (description.PixelStage is not null && pixelModule.Handle == 0))
            {
                throw SubmissionScheduler.Fatal($"A graphics pipeline stage has no module: vertex=0x{vertexModule.Handle:X} pixel=0x{pixelModule.Handle:X}.");
            }

            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                var stageCount = 1u;
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                if (pixelModule.Handle != 0)
                {
                    shaderStages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.FragmentBit,
                        Module = pixelModule,
                        PName = entryPoint,
                    };
                }

                var vertexBindings = new VertexInputBindingDescription[description.VertexInput.BindingCount];
                var vertexAttributes = new VertexInputAttributeDescription[description.VertexInput.AttributeCount];
                BuildVertexAttributes(description, vertexAttributes, vertexBindings);
                var colorCount = (int)parameters.ColorCount;
                var blends = new PipelineColorBlendAttachmentState[colorCount];
                for (var index = 0; index < colorCount; index++)
                {
                    var mask = parameters.GetColorMask(index);
                    if ((mask & ~0xFu) != 0)
                    {
                        throw SubmissionScheduler.Fatal($"A color write mask has unknown bits: attachment={index} mask=0x{mask:X}.");
                    }

                    var separateAlpha = parameters.GetSeparateAlphaBlend(index);
                    blends[index] = new PipelineColorBlendAttachmentState
                    {
                        ColorWriteMask = ToVkColorWriteMask(mask),
                        BlendEnable = parameters.GetBlendEnable(index) && !parameters.GetBlendBypass(index),
                        SrcColorBlendFactor = ToVkBlendFactor(parameters.GetColorSourceBlend(index)),
                        DstColorBlendFactor = ToVkBlendFactor(parameters.GetColorDestinationBlend(index)),
                        ColorBlendOp = ToVkBlendOp(parameters.GetColorBlendFunction(index)),
                        SrcAlphaBlendFactor = ToVkBlendFactor(separateAlpha ? parameters.GetAlphaSourceBlend(index) : parameters.GetColorSourceBlend(index)),
                        DstAlphaBlendFactor = ToVkBlendFactor(separateAlpha ? parameters.GetAlphaDestinationBlend(index) : parameters.GetColorDestinationBlend(index)),
                        AlphaBlendOp = ToVkBlendOp(separateAlpha ? parameters.GetAlphaBlendFunction(index) : parameters.GetColorBlendFunction(index)),
                    };
                }

                var colorFormats = new Format[colorCount];
                Array.Copy(rendering.ColorFormats, colorFormats, colorCount);
                var cullMode = CullModeFlags.None;
                if (parameters.CullBack)
                {
                    cullMode |= CullModeFlags.BackBit;
                }

                if (parameters.CullFront)
                {
                    cullMode |= CullModeFlags.FrontBit;
                }

                fixed (VertexInputBindingDescription* bindingPointer = vertexBindings)
                fixed (VertexInputAttributeDescription* attributePointer = vertexAttributes)
                fixed (PipelineColorBlendAttachmentState* blendPointer = blends)
                fixed (Format* colorFormatPointer = colorFormats)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vertexBindings.Length,
                        PVertexBindingDescriptions = vertexBindings.Length == 0 ? null : bindingPointer,
                        VertexAttributeDescriptionCount = (uint)vertexAttributes.Length,
                        PVertexAttributeDescriptions = vertexAttributes.Length == 0 ? null : attributePointer,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = topology,
                        PrimitiveRestartEnable = parameters.PrimitiveRestartEnable,
                    };
                    var depthClipControl = new PipelineViewportDepthClipControlCreateInfoEXT
                    {
                        SType = StructureType.PipelineViewportDepthClipControlCreateInfoExt,
                        NegativeOneToOne = parameters.NegativeOneToOne,
                    };
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        PNext = _supportsDepthClipControl ? &depthClipControl : null,
                        ViewportCount = 1,
                        ScissorCount = 1,
                    };
                    var depthClip = new PipelineRasterizationDepthClipStateCreateInfoEXT
                    {
                        SType = StructureType.PipelineRasterizationDepthClipStateCreateInfoExt,
                        DepthClipEnable = parameters.DepthClipEnable,
                    };
                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        PNext = _supportsDepthClipEnable ? &depthClip : null,
                        PolygonMode = polygonMode,
                        CullMode = cullMode,
                        FrontFace = parameters.FrontFaceClockwise ? FrontFace.Clockwise : FrontFace.CounterClockwise,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        SampleShadingEnable = parameters.SampleShadingEnable,
                        RasterizationSamples = ImageDescription.VulkanSampleCount(parameters.Samples),
                        MinSampleShading = 1f,
                    };
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        LogicOp = LogicOp.Copy,
                        AttachmentCount = (uint)blends.Length,
                        PAttachments = blends.Length == 0 ? null : blendPointer,
                    };
                    var depthStencil = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthBoundsTestEnable = _supportsDepthBounds && parameters.DepthBoundsTestEnable,
                        StencilTestEnable = parameters.StencilTestEnable,
                        Front = ToVkStencilOpState(parameters.StencilFront),
                        Back = ToVkStencilOpState(parameters.StencilBack),
                        MinDepthBounds = parameters.DepthMinBounds,
                        MaxDepthBounds = parameters.DepthMaxBounds,
                    };
                    var dynamicStates = stackalloc DynamicState[14];
                    dynamicStates[0] = DynamicState.Viewport;
                    dynamicStates[1] = DynamicState.Scissor;
                    dynamicStates[2] = DynamicState.LineWidth;
                    dynamicStates[3] = DynamicState.DepthTestEnableExt;
                    dynamicStates[4] = DynamicState.DepthWriteEnableExt;
                    dynamicStates[5] = DynamicState.DepthCompareOpExt;
                    dynamicStates[6] = DynamicState.DepthBiasEnableExt;
                    dynamicStates[7] = DynamicState.DepthBias;
                    dynamicStates[8] = DynamicState.StencilCompareMask;
                    dynamicStates[9] = DynamicState.StencilReference;
                    dynamicStates[10] = DynamicState.StencilWriteMask;
                    dynamicStates[11] = DynamicState.BlendConstants;
                    var dynamicStateCount = 12u;
                    if (DynamicDepthBounds)
                    {
                        dynamicStates[dynamicStateCount++] = DynamicState.DepthBounds; // [local] SHARPEMU_DEPTH_BOUNDS_KEY=2
                    }

                    // Last so a pipeline without color attachments can leave it out.
                    if (_colorWriteEnableApi is not null && colorCount != 0)
                    {
                        dynamicStates[dynamicStateCount++] = DynamicState.ColorWriteEnableExt;
                    }

                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = dynamicStateCount,
                        PDynamicStates = dynamicStates,
                    };
                    var renderingInfo = new PipelineRenderingCreateInfo
                    {
                        SType = StructureType.PipelineRenderingCreateInfo,
                        ColorAttachmentCount = (uint)colorCount,
                        PColorAttachmentFormats = colorCount == 0 ? null : colorFormatPointer,
                        DepthAttachmentFormat = rendering.DepthFormat,
                        StencilAttachmentFormat = rendering.StencilFormat,
                    };
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        PNext = &renderingInfo,
                        StageCount = stageCount,
                        PStages = shaderStages,
                        PVertexInputState = &vertexInput,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterization,
                        PMultisampleState = &multisample,
                        PDepthStencilState = parameters.WithDepth ? &depthStencil : null,
                        PColorBlendState = &colorBlend,
                        PDynamicState = &dynamicState,
                        Layout = layout,
                    };
                    var graphicsStart = Stopwatch.GetTimestamp();
                    var cache = GetGuestPipelineCache(GraphicsCacheKey(description.VertexStage.Hash,
                        description.PixelStage?.Hash ?? 0, vertexModule.Handle, pixelModule.Handle));
                    Check(_vk.CreateGraphicsPipelines(_device, cache, 1, &pipelineInfo, null, out var pipeline),
                        $"vkCreateGraphicsPipelines(rendering vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16})");
                    ReportPipelineCreation(
                        (long)Stopwatch.GetElapsedTime(graphicsStart).TotalMilliseconds,
                        "graphics",
                        $"vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16}",
                        string.Join(
                            '/',
                            Enumerable
                                .Range(0, (int)stageCount)
                                .Select(index => SpirvBytesOf(shaderStages[index].Module.Handle))));
                    MarkPipelineCacheDirty();
                    Interlocked.Increment(ref _perfPipelineCreations);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16} colors={colorCount}");
                    return pipeline;
                }
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
            }
        }

        public PipelineHandle CreateComputePipeline(ComputePipelineDescription description)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, description.Stage, ShaderStage.Compute);
            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out var demand);
            var layout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit);
            var computeModule = new ShaderModule(description.Program.Module);
            if (computeModule.Handle == 0)
            {
                throw SubmissionScheduler.Fatal($"The compute pipeline has no module: hash=0x{description.Stage.Hash:X16}.");
            }

            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            Pipeline pipeline;
            try
            {
                var requiredSubgroupSize = new PipelineShaderStageRequiredSubgroupSizeCreateInfo
                {
                    SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo,
                    RequiredSubgroupSize = RdnaSubgroupSize,
                };
                var stageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    PNext = RequiresComputeSubgroup32(description.Input) ? &requiredSubgroupSize : null,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Flags = PipelineStatsProbe.CreateFlags,
                    Stage = stageInfo,
                    Layout = layout,
                };
                var computeStart = Stopwatch.GetTimestamp();
                var cache = GetGuestPipelineCache(ComputeCacheKey(description.Stage.Hash, computeModule.Handle));
                Check(_vk.CreateComputePipelines(_device, cache, 1, &pipelineInfo, null, out pipeline), $"vkCreateComputePipelines(rendering) hash=0x{description.Stage.Hash:X16}");
                ReportPipelineCreation(
                    (long)Stopwatch.GetElapsedTime(computeStart).TotalMilliseconds,
                    "compute",
                    $"cs=0x{description.Stage.Hash:X16}",
                    SpirvBytesOf(computeModule.Handle).ToString());
                MarkPipelineCacheDirty();
                Interlocked.Increment(ref _perfPipelineCreations);
                SetDebugName(ObjectType.Pipeline, pipeline.Handle, $"SharpEmu compute cs=0x{description.Stage.Hash:X16}");
                PipelineStatsProbe.Report(_vk, _device, pipeline, $"cs=0x{description.Stage.Hash:X16}");
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
            }

            var entry = new RenderPipelineEntry
            {
                Pipeline = pipeline,
                Layout = layout,
                SetLayout = setLayout,
                Demand = demand,
                UsesPushDescriptors = usesPushDescriptors,
                ProfileComputeHash = description.Stage.Hash,
            };
            return RegisterPipeline(entry);
        }

        // One compute pipeline whose vkCreateComputePipelines call runs on a worker
        // thread. Everything the command stream owns (descriptor and pipeline layout,
        // the module handle) is prepared before the task starts. The worker loads
        // its optional driver-cache shard and creates the native pipeline.
        private sealed class PendingComputePipeline
        {
            public required DescriptorSetLayout SetLayout;
            public required PipelineLayout Layout;
            public required DescriptorSetDemand Demand;
            public required bool UsesPushDescriptors;
            public required ulong Hash;
            public required int SpirvBytes;
            public Task<Pipeline> Compile = Task.FromResult(default(Pipeline));
            public long StartTimestamp;
            public long CompileMilliseconds;
        }

        private readonly Dictionary<ulong, PendingComputePipeline> _pendingComputePipelines = new();
        private static readonly SemaphoreSlim _computeCompileSlots =
            new(Math.Max(2, Environment.ProcessorCount / 2));

        // A host shader compiler can spend tens of seconds on one large translated
        // program. Running the compile on a worker thread keeps it out of the pipeline
        // cache's lock, so other queues can still create their own pipelines meanwhile;
        // SHARPEMU_ASYNC_COMPUTE_PIPELINES=0 compiles inline instead.
        private static readonly bool _asyncComputePipelines =
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_ASYNC_COMPUTE_PIPELINES"),
                "0",
                StringComparison.Ordinal);

        public bool TryCreateComputePipeline(ComputePipelineDescription description, out PipelineHandle handle)
        {
            if (!_asyncComputePipelines)
            {
                handle = CreateComputePipeline(description);
                return true;
            }

            handle = default;
            var key = description.Program.Id;
            if (_pendingComputePipelines.TryGetValue(key, out var pending))
            {
                if (!pending.Compile.IsCompleted)
                {
                    return false;
                }

                _pendingComputePipelines.Remove(key);
                var compiled = pending.Compile.GetAwaiter().GetResult();
                ReportPipelineCreation(
                    Interlocked.Read(ref pending.CompileMilliseconds),
                    "compute-async",
                    $"cs=0x{pending.Hash:X16} waited_ms={(long)Stopwatch.GetElapsedTime(pending.StartTimestamp).TotalMilliseconds}",
                    pending.SpirvBytes.ToString());
                MarkPipelineCacheDirty();
                Interlocked.Increment(ref _perfPipelineCreations);
                SetDebugName(ObjectType.Pipeline, compiled.Handle, $"SharpEmu compute cs=0x{pending.Hash:X16}");
                handle = RegisterPipeline(new RenderPipelineEntry
                {
                    Pipeline = compiled,
                    Layout = pending.Layout,
                    SetLayout = pending.SetLayout,
                    Demand = pending.Demand,
                    UsesPushDescriptors = pending.UsesPushDescriptors,
                    ProfileComputeHash = pending.Hash,
                });
                return true;
            }

            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, description.Stage, ShaderStage.Compute);
            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out var demand);
            var layout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit);
            var computeModule = new ShaderModule(description.Program.Module);
            if (computeModule.Handle == 0)
            {
                throw SubmissionScheduler.Fatal($"The compute pipeline has no module: hash=0x{description.Stage.Hash:X16}.");
            }

            var device = _device;
            var cacheSource = GetGuestPipelineCacheSource(ComputeCacheKey(description.Stage.Hash, computeModule.Handle));
            var vk = _vk;
            var started = new PendingComputePipeline
            {
                SetLayout = setLayout,
                Layout = layout,
                Demand = demand,
                UsesPushDescriptors = usesPushDescriptors,
                Hash = description.Stage.Hash,
                SpirvBytes = SpirvBytesOf(computeModule.Handle),
                StartTimestamp = Stopwatch.GetTimestamp(),
            };
            started.Compile = Task.Factory.StartNew(
                () =>
                {
                    // Each compile blocks its thread inside the Metal compiler service,
                    // so the number in flight is bounded instead of one thread per
                    // program the frame happens to touch.
                    _computeCompileSlots.Wait();
                    var compileStart = Stopwatch.GetTimestamp();
                    try
                    {
                        // Importing a MoltenVK cache compiles its MSL libraries.
                        // Keep that work inside the same bounded compiler slot.
                        var cache = ResolveGuestPipelineCache(cacheSource);
                        return CompileComputePipeline(vk, device, cache, computeModule, layout, started.Hash);
                    }
                    finally
                    {
                        Interlocked.Exchange(
                            ref started.CompileMilliseconds,
                            (long)Stopwatch.GetElapsedTime(compileStart).TotalMilliseconds);
                        _computeCompileSlots.Release();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            _pendingComputePipelines.Add(key, started);
            return false;
        }

        // vkCreateComputePipelines is the only call here; Vulkan synchronises host
        // access to the pipeline cache internally, so several may run at once.
        private static Pipeline CompileComputePipeline(
            Vk vk,
            Device device,
            PipelineCache cache,
            ShaderModule module,
            PipelineLayout layout,
            ulong hash)
        {
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Flags = PipelineStatsProbe.CreateFlags,
                    Stage = stageInfo,
                    Layout = layout,
                };
                var result = vk.CreateComputePipelines(device, cache, 1, &pipelineInfo, null, out var pipeline);
                if (result != Result.Success)
                {
                    throw SubmissionScheduler.Fatal($"vkCreateComputePipelines(async) failed: {result}.");
                }

                PipelineStatsProbe.Report(vk, device, pipeline, $"cs=0x{hash:X16}");

                return pipeline;
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
            }
        }

        private void DrainPendingComputePipelines()
        {
            foreach (var pending in _pendingComputePipelines.Values)
            {
                try
                {
                    var pipeline = pending.Compile.GetAwaiter().GetResult();
                    if (pipeline.Handle != 0) _vk.DestroyPipeline(_device, pipeline, null);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Pending compute pipeline failed during shutdown: {exception.Message}");
                }
                _vk.DestroyPipelineLayout(_device, pending.Layout, null);
                _vk.DestroyDescriptorSetLayout(_device, pending.SetLayout, null);
            }
            _pendingComputePipelines.Clear();
        }

        private void DestroyRenderPipelines()
        {
            foreach (var entry in _pipelineEntries.Values)
            {
                foreach (var pipeline in new[] { entry.Pipeline, entry.StripVariant, entry.ListVariant, entry.RectangleVariant })
                {
                    if (pipeline.Handle != 0)
                    {
                        _vk.DestroyPipeline(_device, pipeline, null);
                    }
                }

                _vk.DestroyPipelineLayout(_device, entry.Layout, null);
                _vk.DestroyDescriptorSetLayout(_device, entry.SetLayout, null);
            }

            _pipelineEntries.Clear();
            foreach (var module in _shaderModules.Values)
            {
                _vk.DestroyShaderModule(_device, module, null);
            }

            _shaderModules.Clear();
            _shaderModuleCacheIdentities.Clear();
        }
    }
}
