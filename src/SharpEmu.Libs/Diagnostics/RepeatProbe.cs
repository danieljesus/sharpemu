// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_REPEAT_PROBE=1 (off by default): how many of the draws and dispatches the recording thread records
// come back in the next frame with the same inputs, the gate for a cross-frame reuse that would emit again what an
// item recorded instead of resolving, binding and recording it again. Each draw (ResolvedDrawKind.Draw, hashed in
// RecordDrawCommands once its mesh parameters are known and before its bindings) and each dispatch that reaches its
// preparation (hashed before BeginPreparation, after the consume hooks) gets eight part hashes of what its recorded
// commands are made from:
//
//   program     the stage programs (ShaderProgramInfo.Hash, the compiled module Id/Module), the shader bases, the
//               GraphicsPrograms flags (blending off, unwritten targets dropped, solid clear, mesh, tessellation) and
//               the bound graphics pipeline handle (noted after TryResolvePipeline). The pipeline cache keys a handle
//               by value (GraphicsPipelineKey: rendering formats, vertex layout, every static context register the
//               pipeline is built from), so the handle stands for the context registers the commands depend on. A
//               dispatch: the compute module and the ComputeInputInfo fields (its pipeline key is the module id).
//   targets     per color target its slot, the resolution (base address, backing size, extent, mip, layer, samples,
//               export mapping, metadata clear support, clear value) and the request's ImageDescription and view;
//               the depth target's resolution (addresses, sizes, format, flags, request) and DepthStencilState.
//   descriptors per stage the T# and S# words, the V# words of buffers the program writes (or uses atomically), the
//               V# words of read-only buffers with the base address cleared, the device-address ranges (written ones
//               with their base, read-only ones without), the flattened resource table when the layout uploads it,
//               the call record ranges; the vertex buffers' stride/records/rate and the attributes' words with the
//               base cleared, the vertex fetch registers; the index buffer's size, type and enable.
//   user        the user data registers the binding layout pushes (BindingLayout.UserDataRegisters, as
//               AssembleShaderData reads them; the others, including descriptor table pointers whose descriptors are
//               hashed above, are left out), the dispatch thread limits, mesh draw parameters, tessellation data and
//               call arguments/targets/records; a dispatch's user scalars when a hook kept them.
//   arguments   the draw kind, operation, counts, first instance, emission (indexed, offsets, indirect address),
//               topology, primitive type and restart, tessellation index fields, whether the indices were converted
//               on the CPU; a dispatch's group counts, indirect address, indirect threads, storage writes and the fill
//               and copy candidates it still carries.
//   dynamic     DynamicDrawState (viewport, scissor, blend constants, depth/stencil dynamic state, color writes),
//               WritesMemory, WriteStages and ResetBindings.
//   ring        the base addresses cleared above: read-only V#s, read-only device-address ranges, vertex buffers and
//               attributes, the index buffer (the constant rings and streamed geometry move every draw).
//   content     a hash of the CPU view of the bytes at those addresses: min(footprint, the program's MaxByteExtent
//               when it has one, SHARPEMU_REPEAT_CONTENT_BYTES (1024 by default)) bytes each, read raw from guest
//               memory (no publication, no readback: bytes the GPU wrote and the CPU has not seen read as the CPU
//               sees them); the CPU-converted indices when the draw has them.
//
// Left out: the record's stamps (ContextStamp, DynamicStamp, TargetVersion, Sequence: monotonic, they never repeat),
// the submit id, packet address and debug information (not in the commands' effect), the resolved image and view
// handles (looked up from the requests hashed above), the mip statistics (statistics only), the snapshot's Origin.
//
//   full    = program, targets, descriptors, user, arguments, dynamic, ring
//   noring  = full without ring: the item would repeat if the moved read-only bases were patched (dynamic offsets)
//   content = noring with content: and the bytes behind those bases are the same as last frame's
//
// A frame is the items between two guest flips (CaptureFlip), so repeat presents do not split one. At each flip the
// frame is compared with the previous one: each hashed item is paired with an unpaired item of the previous frame
// with the same hash (the k-th occurrence with the k-th, a multiset intersection), then the pairs that keep their
// order (a longest increasing subsequence of the paired positions) and the runs (consecutive items paired with
// consecutive items, the chunks a replay of command buffer pieces could take whole). lag2 pairs full against the
// frame before the previous one (rings that come back every second frame). The parts are paired one by one too.
//
// Times are the items' own (DrawCostProbe's Begin to End, hashing excluded); items that were not hashed (skipped
// draws, clears, resolves, copies, consumed or empty dispatches) count in the totals and never repeat. One
// [PERF][REPEAT] line per SHARPEMU_REPEAT_REPORT_S (5 by default) window and kind:
//
//   [PERF][REPEAT] window_s=5.0 frames=30 compared=29 content_pairs=29 analyze_ms=1.20 unattributed=0 kind=draw
//       items_per_frame=4850 ms_per_frame=119.80 hashed=97.0 masked_per_item=7.10 hash_us=1.10
//       full=31.0/35.20/29.4 noring=62.0/70.10/58.5 content=41.0/46.30/38.6 lag2_full=55.0/60.00/50.1
//       inorder=full:29.0/27.9,noring:...,content:... runs=full:25.0/18.0/16.2/812,noring:...,content:...
//       parts=program:97.0,targets:99.0,descriptors:60.0,user:70.0,arguments:90.0,dynamic:95.0,ring:30.0,content:45.0
//
// <variant>=<% of the kind's items that repeat>/<ms per frame of those items>/<% of the kind's ms>; inorder
// <% items>/<% ms>; runs <% items in runs of 2 or more>/<% items in runs of 8 or more>/<% ms in runs of 8 or
// more>/<longest run>; parts <% items whose part repeats>. Percentages are over every item of the kind in the
// compared frames. content and the content part only count frame pairs whose both frames read contents
// (SHARPEMU_REPEAT_CONTENT_EVERY=N reads them on two consecutive frames out of N, 1 by default: every frame).
// hash_us is the probe's own cost per hashed item, analyze_ms its cost per compared frame at the flip;
// unattributed counts hashes made outside a timed item (deferred records), which are dropped.
//
// The gate: if content's % ms (and runs' for a replay of pieces) is under 50 % of the draw time, a cross-frame reuse
// keyed on these inputs cannot pay for its own lookup. The pipeline handle and the read-only masking make these upper
// bounds for a key built from raw registers and descriptor tables.
internal static class RepeatProbe
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_REPEAT_PROBE") == "1";

    private static readonly long ReportTicks = (long)((double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_REPEAT_REPORT_S"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 5.0) * Stopwatch.Frequency);

    private static readonly int ContentBytes =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_REPEAT_CONTENT_BYTES"), out var bytes) && bytes >= 0 ? Math.Min(bytes, 1 << 16) : 1024;

    private static readonly int ContentEvery =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_REPEAT_CONTENT_EVERY"), out var every) && every > 0 ? every : 1;

    internal delegate bool GuestReader(ulong address, Span<byte> destination);

    // The raw guest memory reader (ICpuMemory.TryRead), set by the presenter while the probe is on.
    internal static GuestReader? Reader;

    internal const int PartCount = 8;

    private static readonly string[] PartNames = ["program", "targets", "descriptors", "user", "arguments", "dynamic", "ring", "content"];

    private const byte DrawKind = 0;
    private const byte DispatchKind = 1;
    private const int Kinds = 2;

    // The part hashes of one item. Each note adds to them in order, so an item noted twice (a draw recorded in two
    // pieces) hashes both.
    internal struct Parts
    {
        public ulong Program;
        public ulong Targets;
        public ulong Descriptors;
        public ulong User;
        public ulong Arguments;
        public ulong Dynamic;
        public ulong Ring;
        public ulong Content;
        public int Masked;

        public readonly ulong this[int part] => part switch
        {
            0 => Program,
            1 => Targets,
            2 => Descriptors,
            3 => User,
            4 => Arguments,
            5 => Dynamic,
            6 => Ring,
            _ => Content,
        };

        public readonly ulong NoRing(byte kind)
        {
            var hash = Mix(kind + 1UL);
            Add(ref hash, Program);
            Add(ref hash, Targets);
            Add(ref hash, Descriptors);
            Add(ref hash, User);
            Add(ref hash, Arguments);
            Add(ref hash, Dynamic);
            return hash;
        }

        public readonly ulong Full(byte kind)
        {
            var hash = NoRing(kind);
            Add(ref hash, Ring);
            return hash;
        }

        public readonly ulong WithContent(byte kind)
        {
            var hash = NoRing(kind);
            Add(ref hash, Content);
            return hash;
        }
    }

    // Reads the bounded contents behind the masked addresses; Cap 0 or no reader hashes nothing.
    internal readonly struct ContentSource(GuestReader? reader, int cap)
    {
        public bool Active => reader is not null && cap > 0;

        public ulong Hash(ulong address, ulong size)
        {
            if (reader is null || cap <= 0 || address == 0 || size == 0)
            {
                return 0;
            }

            Span<byte> bytes = stackalloc byte[cap];
            var length = (int)Math.Min(size, (ulong)cap);
            var window = bytes[..length];
            return reader(address, window) ? XxHash3.HashToUInt64(window) ^ (ulong)length : 0xFFFF_FFFF_0000_0000UL | (uint)length;
        }

        public ulong Hash(ReadOnlySpan<byte> data) =>
            reader is null || cap <= 0 ? 0 : XxHash3.HashToUInt64(data[..Math.Min(data.Length, cap)]) ^ (ulong)data.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Add(ref ulong hash, ulong value) => hash = Mix(hash ^ Mix(value + 0x9E3779B97F4A7C15UL));

    private static void Add(ref ulong hash, bool value) => Add(ref hash, value ? 1UL : 2UL);

    private static ulong Words(ReadOnlySpan<uint> words) => XxHash3.HashToUInt64(MemoryMarshal.AsBytes(words)) ^ (ulong)words.Length;

    // The bytes of a value type without references; the padding of a struct the runtime zeroed is zero.
    internal static ulong Bytes<T>(in T value) where T : unmanaged =>
        XxHash3.HashToUInt64(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    // One stage: its program, shader base and everything its bindings are made from.
    internal static void HashStage(ref Parts parts, in ShaderStageResources stage, ShaderProgram module, in ContentSource content)
    {
        var program = stage.Program;
        Add(ref parts.Program, program?.Hash ?? 0);
        Add(ref parts.Program, module.Id);
        Add(ref parts.Program, module.Module);
        Add(ref parts.Program, stage.ShaderBase);
        var snapshot = stage.Resources;
        if (program is null || snapshot is null)
        {
            return;
        }

        var buffers = program.Resources?.Info.Buffers;
        Add(ref parts.Descriptors, (ulong)snapshot.Buffers.Length);
        for (var index = 0; index < snapshot.Buffers.Length; index++)
        {
            var words = snapshot.Buffers[index];
            var resource = buffers is not null && index < buffers.Count ? buffers[index] : null;
            if (words.Length < 4 || resource is not null && (resource.Written || resource.Atomic))
            {
                Add(ref parts.Descriptors, Words(words));
                continue;
            }

            var descriptor = BufferDescriptorWords.From(words);
            var masked = descriptor.WithAddress(0);
            Add(ref parts.Descriptors, masked.Word0 | ((ulong)masked.Word1 << 32));
            Add(ref parts.Descriptors, masked.Word2 | ((ulong)masked.Word3 << 32));
            if (words.Length > 4)
            {
                Add(ref parts.Descriptors, Words(words.AsSpan(4)));
            }

            Add(ref parts.Ring, descriptor.Address);
            var footprint = descriptor.Footprint() ?? ulong.MaxValue;
            var extent = resource?.MaxByteExtent ?? 0;
            Add(ref parts.Content, content.Hash(descriptor.Address, extent != 0 ? Math.Min(footprint, extent) : footprint));
            parts.Masked++;
        }

        Add(ref parts.Descriptors, (ulong)snapshot.Images.Length);
        foreach (var words in snapshot.Images)
        {
            Add(ref parts.Descriptors, Words(words));
        }

        Add(ref parts.Descriptors, (ulong)snapshot.Samplers.Length);
        foreach (var words in snapshot.Samplers)
        {
            Add(ref parts.Descriptors, Words(words));
        }

        Add(ref parts.Descriptors, (ulong)snapshot.DeviceAddressRanges.Length);
        foreach (var range in snapshot.DeviceAddressRanges)
        {
            Add(ref parts.Descriptors, range.Size);
            Add(ref parts.Descriptors, (range.Planned ? 1UL : 0) | (range.Written ? 2UL : 0) | (range.Conservative ? 4UL : 0));
            if (range.Written)
            {
                Add(ref parts.Descriptors, range.Base);
                continue;
            }

            Add(ref parts.Ring, range.Base);
            Add(ref parts.Content, content.Hash(range.Base, range.Size));
            parts.Masked++;
        }

        if (program.Bindings is { } layout)
        {
            var registers = layout.UserDataRegisters;
            for (var index = 0; index < registers.Count; index++)
            {
                var register = registers[index];
                var user = (long)register - program.UserDataBase;
                var value = user >= 0 && user < snapshot.UserData.Length ? snapshot.UserData[user] : 0xDEAD_0000u | register;
                Add(ref parts.User, ((ulong)register << 32) | value);
            }

            if (layout.UsesFlattenedTable)
            {
                Add(ref parts.Descriptors, Words(snapshot.FlattenedResourceTable));
            }
        }

        if (stage.ThreadLimits is { } limits)
        {
            Add(ref parts.User, limits.X | ((ulong)limits.Y << 32));
            Add(ref parts.User, limits.Z);
        }

        if (stage.MeshDraw is { } mesh)
        {
            Add(ref parts.User, Bytes(in mesh));
        }

        if (stage.TessellationData is { } tessellation)
        {
            Add(ref parts.User, Words(tessellation));
        }

        if (stage.CallArguments is { } arguments)
        {
            Add(ref parts.User, XxHash3.HashToUInt64(MemoryMarshal.AsBytes(arguments.AsSpan())));
        }

        if (stage.CallTargets is { } targets)
        {
            Add(ref parts.User, XxHash3.HashToUInt64(MemoryMarshal.AsBytes(targets.AsSpan())));
        }

        if (stage.CallRecords is { } records)
        {
            Add(ref parts.User, XxHash3.HashToUInt64(MemoryMarshal.AsBytes(records.AsSpan())));
        }

        if (stage.CallRecordRanges is { } callRanges)
        {
            foreach (var range in callRanges)
            {
                Add(ref parts.Descriptors, range.Base);
                Add(ref parts.Descriptors, range.Size);
            }
        }
    }

    private static void HashImageRequest(ref ulong hash, in Gpu.Images.ImageRequest request)
    {
        Add(ref hash, Bytes(in request.Description));
        Add(ref hash, Bytes(in request.View));
        Add(ref hash, (ulong)request.Role);
    }

    internal static void HashTargets(ref Parts parts, ref DrawState state)
    {
        Add(ref parts.Targets, state.ColorCount | (state.PixelActive ? 1UL << 32 : 0) | (state.DepthResolved ? 1UL << 33 : 0));
        foreach (ref readonly var color in DrawState.BoundColors(ref state))
        {
            ref readonly var resolution = ref color.Resolution;
            Add(ref parts.Targets, color.Slot);
            Add(ref parts.Targets, resolution.BaseAddress);
            Add(ref parts.Targets, resolution.BackingSize);
            Add(ref parts.Targets, resolution.Extent.Width | ((ulong)resolution.Extent.Height << 32));
            Add(ref parts.Targets, resolution.BaseMipLevel | ((ulong)resolution.BaseArrayLayer << 32));
            Add(ref parts.Targets, resolution.Samples | ((ulong)resolution.ExportMapping.Packed << 32) |
                (resolution.MetadataClearSupported ? 1UL << 40 : 0) | (resolution.MetadataFixedClearSupported ? 1UL << 41 : 0));
            var clear = resolution.ColorClearValue;
            Add(ref parts.Targets, Bytes(in clear));
            HashImageRequest(ref parts.Targets, resolution.Request);
        }

        if (!state.DepthResolved)
        {
            return;
        }

        var depth = state.Depth.Target.Target;
        Add(ref parts.Targets, (ulong)depth.Format | ((ulong)depth.Samples << 32));
        Add(ref parts.Targets, depth.Width | ((ulong)depth.Height << 32));
        Add(ref parts.Targets, depth.DepthAddress);
        Add(ref parts.Targets, depth.DepthSize);
        Add(ref parts.Targets, depth.StencilAddress);
        Add(ref parts.Targets, depth.StencilSize);
        Add(ref parts.Targets, depth.HtileAddress);
        Add(ref parts.Targets, depth.HtileSize);
        Add(ref parts.Targets, (depth.HasStencil ? 1UL : 0) | (depth.HasHtile ? 2UL : 0) | (depth.DepthClearEnabled ? 4UL : 0) |
            (depth.StencilClearEnabled ? 8UL : 0) | (depth.DepthWriteDisabled ? 16UL : 0) | (depth.StencilWriteDisabled ? 32UL : 0) |
            (state.Depth.MetadataClear ? 64UL : 0));
        HashImageRequest(ref parts.Targets, depth.Request);
        var depthState = state.Depth.Target.State;
        Add(ref parts.Targets, Bytes(in depthState));
    }

    // A draw before its bindings: the vertex input is the one the draw binds (with its mesh parameters).
    internal static void HashDraw(ref Parts parts, ResolvedDraw record, ref DrawState state, VertexInputInfo vertexInput, PixelInputInfo? pixelInput,
        in ContentSource content)
    {
        var programs = state.Programs;
        Add(ref parts.Program, (programs.DisableBlending ? 1UL : 0) | (programs.DropUnwrittenColorTargets ? 2UL : 0) |
            (programs.SolidClear is not null ? 4UL : 0) | (programs.MeshInput is not null ? 8UL : 0) | (programs.Tessellation is not null ? 16UL : 0));
        HashStage(ref parts, vertexInput.Stage, programs.Vertex, in content);
        if (pixelInput is not null)
        {
            HashStage(ref parts, pixelInput.Stage, programs.Pixel, in content);
        }

        Add(ref parts.Descriptors, (ulong)vertexInput.Buffers.Length);
        foreach (var buffer in vertexInput.Buffers)
        {
            Add(ref parts.Descriptors, buffer.Stride | ((ulong)buffer.RecordCount << 32));
            Add(ref parts.Descriptors, buffer.PerInstance);
            Add(ref parts.Ring, buffer.Address);
            Add(ref parts.Content, content.Hash(buffer.Address, buffer.Size));
            parts.Masked++;
        }

        Add(ref parts.Descriptors, (ulong)vertexInput.Attributes.Length);
        foreach (var attribute in vertexInput.Attributes)
        {
            var masked = attribute.Descriptor.WithAddress(0);
            Add(ref parts.Descriptors, masked.Word0 | ((ulong)masked.Word1 << 32));
            Add(ref parts.Descriptors, masked.Word2 | ((ulong)masked.Word3 << 32));
            Add(ref parts.Descriptors, (uint)attribute.RegisterStart | ((ulong)(uint)attribute.RegisterCount << 32));
            Add(ref parts.Descriptors, (uint)attribute.AttributeId | ((ulong)attribute.FetchIndex << 32));
            Add(ref parts.Descriptors, (uint)attribute.BufferIndex | ((ulong)attribute.OffsetBytes << 32));
            Add(ref parts.Ring, attribute.Descriptor.Address);
        }

        Add(ref parts.Descriptors, (vertexInput.FetchEmbedded ? 1UL : 0) | ((ulong)(uint)vertexInput.FetchAttributeRegister << 8) |
            ((ulong)(uint)vertexInput.FetchBufferRegister << 24) | ((ulong)(uint)vertexInput.StartInstanceUserData << 40));

        ref readonly var index = ref record.Index;
        Add(ref parts.Descriptors, (index.Enabled ? 1UL : 0) | ((ulong)index.Type << 8));
        Add(ref parts.Descriptors, index.Size);
        if (index.Enabled)
        {
            Add(ref parts.Ring, index.Address);
            Add(ref parts.Content, index.HostData is { } host ? content.Hash(host.AsSpan(0, (int)Math.Min(index.Size, (ulong)host.Length))) : content.Hash(index.Address, index.Size));
            parts.Masked++;
        }

        HashTargets(ref parts, ref state);

        ref readonly var draw = ref record.Draw;
        ref readonly var emission = ref record.Emission;
        Add(ref parts.Arguments, (ulong)record.Kind | ((ulong)draw.Operation << 8) | ((ulong)record.Topology << 24) | ((ulong)record.PrimitiveType << 40) |
            (record.PrimitiveRestart ? 1UL << 56 : 0) | (index.HostData is not null ? 1UL << 57 : 0));
        Add(ref parts.Arguments, draw.Count | ((ulong)draw.InstanceCount << 32));
        Add(ref parts.Arguments, draw.FirstInstance | ((ulong)emission.FirstInstance << 32));
        Add(ref parts.Arguments, (uint)emission.VertexOffset | ((ulong)emission.FirstVertex << 32));
        Add(ref parts.Arguments, emission.IndirectArgumentsAddress ^ (emission.Indexed ? 1UL << 63 : 0));
        Add(ref parts.Arguments, record.TessellationIndexAddress);
        Add(ref parts.Arguments, record.TessellationIndexSize | ((ulong)(uint)record.TessellationVertexOffset << 32));

        var dynamicState = record.Dynamic;
        Add(ref parts.Dynamic, Bytes(in dynamicState));
        Add(ref parts.Dynamic, (ulong)record.WriteStages | (record.WritesMemory ? 1UL << 40 : 0) | (record.ResetBindings ? 1UL << 41 : 0));
    }

    // A dispatch before its preparation.
    internal static void HashDispatch(ref Parts parts, ResolvedDispatch record, ComputeProgram computeProgram, in ContentSource content)
    {
        var input = computeProgram.Input;
        Add(ref parts.Program, record.ShaderAddress);
        Add(ref parts.Program, input.ThreadsX | ((ulong)input.ThreadsY << 32));
        Add(ref parts.Program, input.ThreadsZ | ((ulong)input.WaveSize << 32));
        Add(ref parts.Program, input.DispatchThreadsX | ((ulong)input.DispatchThreadsY << 32));
        Add(ref parts.Program, input.DispatchThreadsZ | ((ulong)input.LocalDataShareDwords << 32));
        Add(ref parts.Program, input.ScratchDwords | ((ulong)(uint)input.ThreadIdCount << 32));
        Add(ref parts.Program, (input.DispatchThreadDimensions ? 1UL : 0) | (input.GroupIdX ? 2UL : 0) | (input.GroupIdY ? 4UL : 0) |
            (input.GroupIdZ ? 8UL : 0) | (input.ThreadGroupSizeEnabled ? 16UL : 0) | (input.IeeeMode ? 32UL : 0) |
            (input.NeedsLocalDataShareBarriers ? 64UL : 0) | ((ulong)(uint)input.WorkgroupRegister << 32));
        HashStage(ref parts, input.Stage, computeProgram.Program, in content);

        Add(ref parts.Arguments, record.GroupsX | ((ulong)record.GroupsY << 32));
        Add(ref parts.Arguments, record.GroupsZ);
        Add(ref parts.Arguments, record.IndirectArgumentsAddress);
        Add(ref parts.Arguments, (record.IndirectThreads ? 1UL : 0) | (record.HasStorageWrites ? 2UL : 0) | (record.MetadataClear is not null ? 4UL : 0) |
            (record.ConstantFill is not null ? 8UL : 0) | (record.BoundedFill is not null ? 16UL : 0) | (record.BoundedCopy is not null ? 32UL : 0) |
            (record.ImageClear is not null ? 64UL : 0) | (record.RecordFill is not null ? 128UL : 0));
        if (record.UserScalars is { } scalars)
        {
            Add(ref parts.User, Words(scalars));
        }
    }

    // ---- The recording thread's items and frames.

    // One frame's items in recording order; an item that was not hashed never pairs.
    internal sealed class Frame
    {
        public int Count;
        public bool ContentRead;
        public ulong[] Full = new ulong[256];
        public ulong[] NoRing = new ulong[256];
        public ulong[] Content = new ulong[256];
        public ulong[][] PartHashes = NewParts(256);
        public long[] Ticks = new long[256];
        public byte[] Kinds = new byte[256];
        public bool[] Hashed = new bool[256];
        public int[] Masked = new int[256];
        public long[] HashTicks = new long[256];

        private static ulong[][] NewParts(int capacity)
        {
            var parts = new ulong[PartCount][];
            for (var part = 0; part < PartCount; part++)
            {
                parts[part] = new ulong[capacity];
            }

            return parts;
        }

        public void Clear()
        {
            Count = 0;
            ContentRead = false;
        }

        public void Add(byte kind, bool hashed, in Parts parts, long ticks, long hashTicks = 0)
        {
            if (Count == Full.Length)
            {
                var capacity = Count * 2;
                Array.Resize(ref Full, capacity);
                Array.Resize(ref NoRing, capacity);
                Array.Resize(ref Content, capacity);
                for (var part = 0; part < PartCount; part++)
                {
                    Array.Resize(ref PartHashes[part], capacity);
                }

                Array.Resize(ref Ticks, capacity);
                Array.Resize(ref Kinds, capacity);
                Array.Resize(ref Hashed, capacity);
                Array.Resize(ref Masked, capacity);
                Array.Resize(ref HashTicks, capacity);
            }

            var index = Count++;
            Kinds[index] = kind;
            Hashed[index] = hashed;
            Ticks[index] = ticks;
            Masked[index] = parts.Masked;
            HashTicks[index] = hashTicks;
            Full[index] = hashed ? parts.Full(kind) : 0;
            NoRing[index] = hashed ? parts.NoRing(kind) : 0;
            Content[index] = hashed ? parts.WithContent(kind) : 0;
            for (var part = 0; part < PartCount; part++)
            {
                PartHashes[part][index] = hashed ? Mix(parts[part] + kind) : 0;
            }
        }
    }

    // Pairs and orders the items of two frames; one per recording thread, its scratch reused.
    internal sealed class Matcher
    {
        private readonly Dictionary<ulong, int> _heads = new();
        private readonly Dictionary<ulong, int> _chainEnds = new();
        private int[] _next = new int[256];
        private int[] _tails = new int[256];
        private int[] _predecessors = new int[256];

        // Pairs each valid current item with the first unpaired valid previous item of the same hash (the k-th
        // occurrence with the k-th); match[i] is its previous position or -1. Returns the pairs.
        public int Match(ReadOnlySpan<ulong> previous, ReadOnlySpan<bool> previousValid, ReadOnlySpan<ulong> current, ReadOnlySpan<bool> currentValid,
            Span<int> match)
        {
            if (_next.Length < previous.Length)
            {
                _next = new int[Math.Max(previous.Length, _next.Length * 2)];
            }

            // Each hash keeps its first unpaired position; the chain runs through its later ones in order.
            _heads.Clear();
            _chainEnds.Clear();
            for (var index = 0; index < previous.Length; index++)
            {
                if (!previousValid[index])
                {
                    continue;
                }

                _next[index] = -1;
                var hash = previous[index];
                if (_chainEnds.TryGetValue(hash, out var tail))
                {
                    _next[tail] = index;
                }
                else
                {
                    _heads[hash] = index;
                }

                _chainEnds[hash] = index;
            }

            var pairs = 0;
            for (var index = 0; index < current.Length; index++)
            {
                match[index] = -1;
                if (!currentValid[index])
                {
                    continue;
                }

                ref var head = ref CollectionsMarshal.GetValueRefOrNullRef(_heads, current[index]);
                if (Unsafe.IsNullRef(ref head) || head < 0)
                {
                    continue;
                }

                match[index] = head;
                head = _next[head];
                pairs++;
            }

            return pairs;
        }

        // Marks the items of one longest strictly increasing subsequence of the paired previous positions: the pairs
        // that keep their order. Returns its length.
        public int InOrder(ReadOnlySpan<int> match, Span<bool> inOrder)
        {
            if (_tails.Length < match.Length)
            {
                _tails = new int[Math.Max(match.Length, _tails.Length * 2)];
                _predecessors = new int[_tails.Length];
            }

            inOrder.Clear();
            var length = 0;
            for (var index = 0; index < match.Length; index++)
            {
                var value = match[index];
                if (value < 0)
                {
                    continue;
                }

                int low = 0, high = length;
                while (low < high)
                {
                    var middle = (low + high) >>> 1;
                    if (match[_tails[middle]] < value)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                _predecessors[index] = low > 0 ? _tails[low - 1] : -1;
                _tails[low] = index;
                if (low == length)
                {
                    length++;
                }
            }

            for (var item = length > 0 ? _tails[length - 1] : -1; item >= 0; item = _predecessors[item])
            {
                inOrder[item] = true;
            }

            return length;
        }

        // The length of the run each item is in: consecutive items paired with consecutive previous items (0 for an
        // unpaired item). Returns the longest.
        public static int Runs(ReadOnlySpan<int> match, Span<int> runLength)
        {
            var longest = 0;
            var start = 0;
            while (start < match.Length)
            {
                if (match[start] < 0)
                {
                    runLength[start++] = 0;
                    continue;
                }

                var end = start;
                while (end + 1 < match.Length && match[end + 1] >= 0 && match[end + 1] == match[end] + 1)
                {
                    end++;
                }

                var length = end - start + 1;
                runLength.Slice(start, length).Fill(length);
                longest = Math.Max(longest, length);
                start = end + 1;
            }

            return longest;
        }
    }

    private enum Variant
    {
        Full,
        NoRing,
        Content,
        Count,
    }

    private static readonly string[] VariantNames = ["full", "noring", "content"];

    // The window's sums, per kind.
    internal sealed class Window
    {
        public long Frames;
        public long Compared;
        public long ContentPairs;
        public long AnalyzeTicks;
        public readonly long[] Items = new long[Kinds];
        public readonly long[] Ticks = new long[Kinds];
        public readonly long[] Hashed = new long[Kinds];
        public readonly long[] Masked = new long[Kinds];
        public readonly long[] HashTicks = new long[Kinds];
        public readonly long[] ContentItems = new long[Kinds];
        public readonly long[] ContentTicks = new long[Kinds];
        public readonly long[] RepeatItems = new long[Kinds * (int)Variant.Count];
        public readonly long[] RepeatTicks = new long[Kinds * (int)Variant.Count];
        public readonly long[] OrderItems = new long[Kinds * (int)Variant.Count];
        public readonly long[] OrderTicks = new long[Kinds * (int)Variant.Count];
        public readonly long[] Run2Items = new long[Kinds * (int)Variant.Count];
        public readonly long[] Run8Items = new long[Kinds * (int)Variant.Count];
        public readonly long[] Run8Ticks = new long[Kinds * (int)Variant.Count];
        public readonly int[] Longest = new int[Kinds * (int)Variant.Count];
        public readonly long[] Lag2Items = new long[Kinds];
        public readonly long[] Lag2Ticks = new long[Kinds];
        public readonly long[] Lag2Base = new long[Kinds];
        public readonly long[] Lag2BaseTicks = new long[Kinds];
        public readonly long[] PartItems = new long[Kinds * PartCount];

        public void Clear()
        {
            Frames = Compared = ContentPairs = AnalyzeTicks = 0;
            foreach (var array in new[] { Items, Ticks, Hashed, Masked, HashTicks, ContentItems, ContentTicks, RepeatItems, RepeatTicks, OrderItems,
                         OrderTicks, Run2Items, Run8Items, Run8Ticks, Lag2Items, Lag2Ticks, Lag2Base, Lag2BaseTicks, PartItems })
            {
                Array.Clear(array);
            }

            Array.Clear(Longest);
        }
    }

    // Compares a frame with the previous one (and full with the one before when there is one) into the window.
    internal sealed class Analyzer
    {
        private readonly Matcher _matcher = new();
        private int[] _match = new int[256];
        private int[] _runs = new int[256];
        private bool[] _inOrder = new bool[256];

        private void Ensure(int current)
        {
            if (_match.Length < current)
            {
                var capacity = Math.Max(current, _match.Length * 2);
                _match = new int[capacity];
                _runs = new int[capacity];
                _inOrder = new bool[capacity];
            }
        }

        public void Compare(Frame previous, Frame current, Frame? beforePrevious, Window window)
        {
            Ensure(current.Count);
            window.Compared++;
            var count = current.Count;
            var match = _match.AsSpan(0, count);
            var currentHashed = current.Hashed.AsSpan(0, count);
            var previousHashed = previous.Hashed.AsSpan(0, previous.Count);
            for (var index = 0; index < count; index++)
            {
                var kind = current.Kinds[index];
                window.Items[kind]++;
                window.Ticks[kind] += current.Ticks[index];
                if (current.Hashed[index])
                {
                    window.Hashed[kind]++;
                    window.Masked[kind] += current.Masked[index];
                    window.HashTicks[kind] += current.HashTicks[index];
                }
            }

            var contentPair = current.ContentRead && previous.ContentRead;
            if (contentPair)
            {
                window.ContentPairs++;
                for (var index = 0; index < count; index++)
                {
                    var kind = current.Kinds[index];
                    window.ContentItems[kind]++;
                    window.ContentTicks[kind] += current.Ticks[index];
                }
            }

            for (var variant = 0; variant < (int)Variant.Count; variant++)
            {
                if (variant == (int)Variant.Content && !contentPair)
                {
                    continue;
                }

                var (previousHashes, currentHashes) = variant switch
                {
                    (int)Variant.Full => (previous.Full, current.Full),
                    (int)Variant.NoRing => (previous.NoRing, current.NoRing),
                    _ => (previous.Content, current.Content),
                };
                _matcher.Match(previousHashes.AsSpan(0, previous.Count), previousHashed, currentHashes.AsSpan(0, count), currentHashed, match);
                _matcher.InOrder(match, _inOrder.AsSpan(0, count));
                Matcher.Runs(match, _runs.AsSpan(0, count));
                for (var index = 0; index < count; index++)
                {
                    if (match[index] < 0)
                    {
                        continue;
                    }

                    var kind = current.Kinds[index];
                    var slot = kind * (int)Variant.Count + variant;
                    var ticks = current.Ticks[index];
                    window.RepeatItems[slot]++;
                    window.RepeatTicks[slot] += ticks;
                    if (_inOrder[index])
                    {
                        window.OrderItems[slot]++;
                        window.OrderTicks[slot] += ticks;
                    }

                    if (_runs[index] >= 2)
                    {
                        window.Run2Items[slot]++;
                    }

                    if (_runs[index] >= 8)
                    {
                        window.Run8Items[slot]++;
                        window.Run8Ticks[slot] += ticks;
                    }

                    window.Longest[slot] = Math.Max(window.Longest[slot], _runs[index]);
                }
            }

            for (var part = 0; part < PartCount; part++)
            {
                if (part == PartCount - 1 && !contentPair)
                {
                    continue;
                }

                _matcher.Match(previous.PartHashes[part].AsSpan(0, previous.Count), previousHashed, current.PartHashes[part].AsSpan(0, count), currentHashed, match);
                for (var index = 0; index < count; index++)
                {
                    if (match[index] >= 0)
                    {
                        window.PartItems[current.Kinds[index] * PartCount + part]++;
                    }
                }
            }

            if (beforePrevious is null || beforePrevious.Count == 0)
            {
                return;
            }

            _matcher.Match(beforePrevious.Full.AsSpan(0, beforePrevious.Count), beforePrevious.Hashed.AsSpan(0, beforePrevious.Count),
                current.Full.AsSpan(0, count), currentHashed, match);
            for (var index = 0; index < count; index++)
            {
                var kind = current.Kinds[index];
                window.Lag2Base[kind]++;
                window.Lag2BaseTicks[kind] += current.Ticks[index];
                if (match[index] >= 0)
                {
                    window.Lag2Items[kind]++;
                    window.Lag2Ticks[kind] += current.Ticks[index];
                }
            }
        }
    }

    private static int _thread;
    private static int _depth;
    private static bool _open;
    private static bool _hashed;
    private static byte _kind;
    private static long _itemStart;
    private static long _itemHashTicks;
    private static Parts _parts;
    private static int _flips;
    private static int _flipsSeen;
    private static long _framesClosed;
    private static long _windowStart;
    private static long _unattributed;
    private static Frame _current = new();
    private static Frame _previous = new();
    private static Frame _beforePrevious = new();
    private static readonly Analyzer _analyzer = new();
    private static readonly Window _window = new();

    private static bool ContentThisFrame => ContentEvery <= 1 || _framesClosed % ContentEvery < 2;

    // A guest flip, from any thread: the recording thread closes the frame before its next item.
    internal static void NoteFlip()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _flips);
        }
    }

    internal static void Begin(bool dispatch)
    {
        var thread = Environment.CurrentManagedThreadId;
        if (_thread == 0)
        {
            _thread = thread;
            _windowStart = Stopwatch.GetTimestamp();
            _flipsSeen = Volatile.Read(ref _flips);
        }

        if (thread != _thread || _depth++ != 0)
        {
            return;
        }

        var flips = Volatile.Read(ref _flips);
        if (flips != _flipsSeen)
        {
            _flipsSeen = flips;
            CloseFrame();
        }

        _open = true;
        _hashed = false;
        _kind = dispatch ? DispatchKind : DrawKind;
        _parts = default;
        _itemHashTicks = 0;
        _itemStart = Stopwatch.GetTimestamp();
    }

    internal static void End()
    {
        if (Environment.CurrentManagedThreadId != _thread || _depth == 0 || --_depth != 0 || !_open)
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - _itemStart - _itemHashTicks;
        _open = false;
        _current.Add(_kind, _hashed, in _parts, ticks, _itemHashTicks);
    }

    // True when a note on this thread belongs to the open item; counts the ones that do not.
    private static bool Owns()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            return false;
        }

        if (!_open)
        {
            _unattributed++;
            return false;
        }

        return true;
    }

    private static ContentSource Content => new(ContentThisFrame ? Reader : null, ContentBytes);

    internal static void NoteDraw(ResolvedDraw record, ref DrawState state, VertexInputInfo vertexInput, PixelInputInfo? pixelInput)
    {
        if (!Owns())
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var content = Content;
        HashDraw(ref _parts, record, ref state, vertexInput, pixelInput, in content);
        _hashed = true;
        _itemHashTicks += Stopwatch.GetTimestamp() - started;
    }

    internal static void NotePipeline(ulong pipeline)
    {
        if (Environment.CurrentManagedThreadId == _thread && _open && _hashed)
        {
            Add(ref _parts.Program, pipeline);
        }
    }

    internal static void NoteDispatch(ResolvedDispatch record, ComputeProgram computeProgram)
    {
        if (!Owns())
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var content = Content;
        HashDispatch(ref _parts, record, computeProgram, in content);
        _hashed = true;
        _itemHashTicks += Stopwatch.GetTimestamp() - started;
    }

    // [local] For tests: an item hashed from given parts.
    internal static void NoteForTests(in Parts parts)
    {
        if (Owns())
        {
            _parts = parts;
            _hashed = true;
        }
    }

    private static void CloseFrame()
    {
        var frame = _current;
        if (frame.Count == 0)
        {
            return;
        }

        frame.ContentRead = ContentThisFrame && Reader is not null && ContentBytes > 0;
        var started = Stopwatch.GetTimestamp();
        _window.Frames++;
        if (_previous.Count != 0)
        {
            _analyzer.Compare(_previous, frame, _beforePrevious.Count != 0 ? _beforePrevious : null, _window);
        }

        var now = Stopwatch.GetTimestamp();
        _window.AnalyzeTicks += now - started;
        var recycled = _beforePrevious;
        _beforePrevious = _previous;
        _previous = frame;
        recycled.Clear();
        _current = recycled;
        _framesClosed++;
        if (now - _windowStart >= ReportTicks)
        {
            foreach (var line in Report(_window, (now - _windowStart) / (double)Stopwatch.Frequency, _unattributed))
            {
                Console.Error.WriteLine(line);
            }

            _window.Clear();
            _unattributed = 0;
            _windowStart = Stopwatch.GetTimestamp();
        }
    }

    internal static List<string> Report(Window window, double seconds, long unattributed)
    {
        var lines = new List<string>(Kinds);
        var toMs = 1000.0 / Stopwatch.Frequency;
        var compared = Math.Max(window.Compared, 1);
        for (var kind = 0; kind < Kinds; kind++)
        {
            var items = window.Items[kind];
            if (items == 0)
            {
                continue;
            }

            var hashed = window.Hashed[kind];
            var line = new StringBuilder(1024);
            line.Append(FormattableString.Invariant(
                $"[PERF][REPEAT] window_s={seconds:F1} frames={window.Frames} compared={window.Compared} content_pairs={window.ContentPairs} "));
            line.Append(FormattableString.Invariant(
                $"analyze_ms={window.AnalyzeTicks * toMs / compared:F2} unattributed={unattributed} kind={(kind == DrawKind ? "draw" : "dispatch")} "));
            line.Append(FormattableString.Invariant(
                $"items_per_frame={items / (double)compared:F0} ms_per_frame={window.Ticks[kind] * toMs / compared:F2} "));
            line.Append(FormattableString.Invariant(
                $"hashed={Percent(hashed, items):F1} masked_per_item={(hashed == 0 ? 0 : window.Masked[kind] / (double)hashed):F2} "));
            line.Append(FormattableString.Invariant($"hash_us={(hashed == 0 ? 0 : window.HashTicks[kind] * toMs * 1000 / hashed):F2}"));
            for (var variant = 0; variant < (int)Variant.Count; variant++)
            {
                var slot = kind * (int)Variant.Count + variant;
                var (baseItems, baseTicks, pairs) = variant == (int)Variant.Content
                    ? (window.ContentItems[kind], window.ContentTicks[kind], Math.Max(window.ContentPairs, 1))
                    : (items, window.Ticks[kind], compared);
                line.Append(FormattableString.Invariant(
                    $" {VariantNames[variant]}={Percent(window.RepeatItems[slot], baseItems):F1}/{window.RepeatTicks[slot] * toMs / pairs:F2}/{Percent(window.RepeatTicks[slot], baseTicks):F1}"));
            }

            line.Append(FormattableString.Invariant(
                $" lag2_full={Percent(window.Lag2Items[kind], window.Lag2Base[kind]):F1}/{window.Lag2Ticks[kind] * toMs / compared:F2}/{Percent(window.Lag2Ticks[kind], window.Lag2BaseTicks[kind]):F1}"));
            line.Append(" inorder=");
            for (var variant = 0; variant < (int)Variant.Count; variant++)
            {
                var slot = kind * (int)Variant.Count + variant;
                var (baseItems, baseTicks) = variant == (int)Variant.Content ? (window.ContentItems[kind], window.ContentTicks[kind]) : (items, window.Ticks[kind]);
                line.Append(FormattableString.Invariant(
                    $"{(variant == 0 ? "" : ",")}{VariantNames[variant]}:{Percent(window.OrderItems[slot], baseItems):F1}/{Percent(window.OrderTicks[slot], baseTicks):F1}"));
            }

            line.Append(" runs=");
            for (var variant = 0; variant < (int)Variant.Count; variant++)
            {
                var slot = kind * (int)Variant.Count + variant;
                var (baseItems, baseTicks) = variant == (int)Variant.Content ? (window.ContentItems[kind], window.ContentTicks[kind]) : (items, window.Ticks[kind]);
                line.Append(FormattableString.Invariant(
                    $"{(variant == 0 ? "" : ",")}{VariantNames[variant]}:{Percent(window.Run2Items[slot], baseItems):F1}/{Percent(window.Run8Items[slot], baseItems):F1}/{Percent(window.Run8Ticks[slot], baseTicks):F1}/{window.Longest[slot]}"));
            }

            line.Append(" parts=");
            for (var part = 0; part < PartCount; part++)
            {
                var baseItems = part == PartCount - 1 ? window.ContentItems[kind] : items;
                line.Append(FormattableString.Invariant(
                    $"{(part == 0 ? "" : ",")}{PartNames[part]}:{Percent(window.PartItems[kind * PartCount + part], baseItems):F1}"));
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    private static double Percent(long part, long whole) => whole <= 0 ? 0 : part * 100.0 / whole;

    // [local] For tests: clears the thread ownership, the frames and the window.
    internal static void ResetForTests()
    {
        _thread = 0;
        _depth = 0;
        _open = false;
        _hashed = false;
        _flips = 0;
        _flipsSeen = 0;
        _framesClosed = 0;
        _unattributed = 0;
        _current = new Frame();
        _previous = new Frame();
        _beforePrevious = new Frame();
        _window.Clear();
    }

    internal static Window WindowForTests => _window;

    internal static void CloseFrameForTests()
    {
        _flipsSeen = Volatile.Read(ref _flips);
        CloseFrame();
    }
}
