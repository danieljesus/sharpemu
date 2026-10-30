// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] SHARPEMU_RT_RAYQUERY=1/2: acceleration structures for the BLASes 0x8395 walks.
//
// A BLAS the shader walked natively for want of an entry comes back in the map. Its header, then the whole blob, are read
// back; RayQueryBlas.Extract turns its pair records into an index buffer. Before the next 0x8395 dispatch the host builds
// a BLAS from the guest's own buffer (vertex stride 4, the vertices in place) and a TLAS of one identity instance over it,
// then writes (key, TLAS address, first pair) into the table of the fault buffer's ray query area.
// Phase 1 builds static blobs only: a blob any guest program writes (a compute binding or a planned device-address range
// marked written) loses its entry before the next 0x8395 dispatch and is read again; one written in two frames or more
// (the refit chain F43B/E45A/5981/AACC writes 7 of them every frame) stays on the native loop.
internal sealed unsafe partial class RayQueryHle
{
    internal enum AsState
    {
        None,
        Reading,
        Parsed,
        Built,
        Failed,
        Dynamic,
    }

    internal sealed class BuiltAs
    {
        public required GpuBuffer BlasStorage;
        public required AccelerationStructureKHR Blas;
        public required GpuBuffer TlasStorage;
        public required AccelerationStructureKHR Tlas;
        public ulong TlasAddress;
        public uint Slot;
    }

    private const ulong ScratchBytes = 128UL << 20;
    private const int MaxBuildsPerFrame = 24;
    private const uint MaxPrimitivesPerFrame = 2_000_000;
    private const int MaxReadsPerFrame = 8;

    private GpuBuffer? _buildScratch;
    private readonly uint[] _tableKeys = new uint[DeviceAddressPaging.RqTableSlots];
    private readonly List<Blob> _unpublish = new();
    private long _writeSeq;
    private long _builds, _buildPrimitives, _unpublished, _failed, _dynamic, _blasBytes;
    private readonly Dictionary<string, long> _failReasons = new();
    private long _queryLanes, _fallbackLanes, _counterDispatches;
    private readonly long[] _verify = new long[32];
    private int _sampleLogs;

    private static KhrAccelerationStructure Api => RayQueryDevice.Api!;

    // Under _lock.
    private void NoteBlobWritten(Blob blob)
    {
        if (DeviceAddressPaging.RayQueryMode == 0)
            return;
        blob.LastWriteSeq = ++_writeSeq;
        if (blob.LastWriteFrame != _frame)
        {
            blob.LastWriteFrame = _frame;
            blob.WriteFrames++;
        }

        if (blob.State == AsState.Built)
            _unpublish.Add(blob);
        if (blob.State is not (AsState.Failed or AsState.Dynamic))
            blob.State = blob.WriteFrames >= 2 ? AsState.Dynamic : AsState.None;
        if (blob.State == AsState.Dynamic && blob.Built is null)
            _dynamic++;
    }

    // After a 0x8395 dispatch: whole-blob reads of blobs whose header is known.
    private void RequestBlobReads()
    {
        List<Blob> wanted;
        lock (_lock)
            wanted = _blobs.Values.Where(blob => blob.State == AsState.None && blob.Size != 0).Take(MaxReadsPerFrame).ToList();
        foreach (var blob in wanted)
        {
            long seq;
            lock (_lock)
            {
                blob.State = AsState.Reading;
                seq = _writeSeq;
            }

            Read(blob.Base, blob.Size, bytes =>
            {
                var geometry = RayQueryBlas.Extract(bytes, out var reason);
                lock (_lock)
                {
                    if (blob.State != AsState.Reading)
                        return; // written meanwhile
                    if (geometry is null)
                    {
                        blob.State = AsState.Failed;
                        blob.FailReason = reason;
                        _failed++;
                        _failReasons[reason] = _failReasons.GetValueOrDefault(reason) + 1;
                        return;
                    }

                    blob.Geometry = geometry;
                    blob.ReadWriteSeq = seq;
                    blob.State = AsState.Parsed;
                }
            });
        }
    }

    // Before a 0x8395 dispatch: entries of written blobs removed, parsed blobs built and published.
    private bool Before8395()
    {
        List<Blob> unpublish;
        List<Blob> build;
        lock (_lock)
        {
            unpublish = [.. _unpublish.Distinct()];
            _unpublish.Clear();
            build = [];
            var primitives = 0u;
            foreach (var blob in _blobs.Values)
            {
                if (blob.State != AsState.Parsed || blob.Geometry is null)
                    continue;
                if (blob.LastWriteSeq > blob.ReadWriteSeq)
                {
                    blob.State = AsState.None;
                    continue;
                }

                if (build.Count >= MaxBuildsPerFrame || primitives + blob.Geometry.PrimitiveCount > MaxPrimitivesPerFrame)
                    break;
                primitives += blob.Geometry.PrimitiveCount;
                build.Add(blob);
            }
        }

        if (unpublish.Count == 0 && build.Count == 0)
            return false;
        _scheduler.EndRendering();
        foreach (var blob in unpublish)
            Unpublish(blob);
        if (build.Count != 0)
            BuildAll(build);
        return true;
    }

    private void Unpublish(Blob blob)
    {
        if (blob.Built is not { } built)
            return;
        blob.Built = null;
        WriteTableEntry(built.Slot, _tableKeys[built.Slot], 0, 0);
        _unpublished++;
        var device = _device.Device;
        _scheduler.QueueCompletionAction(() =>
        {
            Api.DestroyAccelerationStructure(device, built.Tlas, null);
            Api.DestroyAccelerationStructure(device, built.Blas, null);
            built.TlasStorage.Dispose();
            built.BlasStorage.Dispose();
        });
    }

    private uint TableSlot(uint key)
    {
        var slot = DeviceAddressPaging.RqHashSlot(key);
        for (var step = 0; step < DeviceAddressPaging.RqProbeLimit; step++)
        {
            if (_tableKeys[slot] == key || _tableKeys[slot] == 0)
                return slot;
            slot = (slot + 1) & (DeviceAddressPaging.RqTableSlots - 1);
        }

        return uint.MaxValue;
    }

    private void WriteTableEntry(uint slot, uint key, ulong address, uint firstPair)
    {
        _tableKeys[slot] = key;
        Span<uint> entry = [key, (uint)address, (uint)(address >> 32), firstPair];
        var faults = _cache.FaultBuffer;
        var (staging, offset) = _cache.UploadStream(MemoryMarshal.AsBytes(entry), 16);
        faults.CopyFrom(_scheduler.Current, staging, offset,
            DeviceAddressPaging.RqAreaOffset + (ulong)slot * DeviceAddressPaging.RqSlotWords * 4, 16, AccessFlags.HostWriteBit);
    }

    private static GpuBuffer HostBuffer(GpuDeviceInfo device, Scheduling.SubmissionScheduler scheduler, ReadOnlySpan<byte> data)
    {
        var buffer = new GpuBuffer(device, scheduler, GpuBufferUsage.Upload, 0,
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.StorageBufferBit,
            (ulong)data.Length);
        data.CopyTo(buffer.Mapped);
        buffer.Flush(0, (ulong)data.Length);
        return buffer;
    }

    private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    private void Barrier(CommandBuffer command, PipelineStageFlags source, AccessFlags2 sourceAccess, PipelineStageFlags destination, AccessFlags2 destinationAccess)
    {
        var barrier = new MemoryBarrier2 { SType = StructureType.MemoryBarrier2, SrcAccessMask = sourceAccess, DstAccessMask = destinationAccess };
        VulkanSynchronization.PipelineBarrier(_device.Vk, command, source, destination, 0, 1, &barrier, 0, null, 0, null);
    }

    // vkCmd* calls that VkRecord does not carry go straight to the driver: the record ring's replay thread must have
    // issued every earlier record first, or both threads record into the same command buffer.
    private static void DrainRing(CommandBuffer command)
    {
        if (CommandRing.Active is { } ring && ring.ProducerBuffer == command.Handle)
            ring.Drain(RingDrainReason.Fallback);
    }

    private CommandBuffer DirectCommand() => new(_scheduler.Current.Handle);

    private void BuildAll(List<Blob> blobs)
    {
        _buildScratch ??= new GpuBuffer(_device, _scheduler, GpuBufferUsage.DeviceLocal, 0,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, ScratchBytes);
        var device = _device.Device;
        var scratchBase = Align(_buildScratch.DeviceAddress, RayQueryDevice.ScratchAlignment);
        var scratchEnd = _buildScratch.DeviceAddress + ScratchBytes;
        var temporaries = new List<GpuBuffer>();
        var built = new List<(Blob Blob, BuiltAs As, AccelerationStructureBuildGeometryInfoKHR Info, AccelerationStructureGeometryKHR Geometry, uint Primitives)>();
        var scratch = scratchBase;
        // The BLASes.
        foreach (var blob in blobs)
        {
            var geometryData = blob.Geometry!;
            var key = (uint)(blob.Base >> 8);
            var slot = TableSlot(key);
            if (slot == uint.MaxValue)
            {
                Fail(blob, "table_full");
                continue;
            }

            var (guest, guestOffset) = _cache.ObtainBuffer(blob.Base, blob.Size, isWritten: false);
            var indices = HostBuffer(_device, _scheduler, MemoryMarshal.AsBytes<uint>(geometryData.Indices));
            temporaries.Add(indices);
            var geometry = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR
                {
                    Triangles = new AccelerationStructureGeometryTrianglesDataKHR
                    {
                        SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                        VertexFormat = Format.R32G32B32Sfloat,
                        VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = guest.DeviceAddress + guestOffset },
                        VertexStride = 4,
                        MaxVertex = geometryData.MaxVertex,
                        IndexType = IndexType.Uint32,
                        IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = indices.DeviceAddress },
                    },
                },
            };
            var info = new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = 1,
            };
            var primitives = geometryData.PrimitiveCount;
            var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
            info.PGeometries = &geometry;
            Api.GetAccelerationStructureBuildSizes(device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, &primitives, &sizes);
            if (scratch + sizes.BuildScratchSize > scratchEnd)
                break;
            var blasStorage = new GpuBuffer(_device, _scheduler, GpuBufferUsage.DeviceLocal, 0,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit, sizes.AccelerationStructureSize);
            var createInfo = new AccelerationStructureCreateInfoKHR
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = blasStorage.Handle,
                Size = sizes.AccelerationStructureSize,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            };
            AccelerationStructureKHR blas;
            if (Api.CreateAccelerationStructure(device, &createInfo, null, &blas) != Result.Success)
            {
                blasStorage.Dispose();
                Fail(blob, "create_blas");
                continue;
            }

            info.DstAccelerationStructure = blas;
            info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratch };
            scratch = Align(scratch + sizes.BuildScratchSize, RayQueryDevice.ScratchAlignment);
            _blasBytes += (long)sizes.AccelerationStructureSize;
            built.Add((blob, new BuiltAs { BlasStorage = blasStorage, Blas = blas, TlasStorage = null!, Tlas = default, Slot = slot }, info, geometry, primitives));
        }

        if (built.Count == 0)
        {
            DisposeLater(temporaries);
            return;
        }

        var command = DirectCommand();
        Barrier(command, PipelineStageFlags.AllCommandsBit, AccessFlags2.MemoryWriteBit,
            PipelineStageFlags.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr | AccessFlags2.ShaderReadBit);
        var infos = new AccelerationStructureBuildGeometryInfoKHR[built.Count];
        var geometries = new AccelerationStructureGeometryKHR[built.Count];
        var ranges = new AccelerationStructureBuildRangeInfoKHR[built.Count];
        for (var index = 0; index < built.Count; index++)
        {
            geometries[index] = built[index].Geometry;
            infos[index] = built[index].Info;
            ranges[index] = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = built[index].Primitives };
        }

        fixed (AccelerationStructureGeometryKHR* geometryPointer = geometries)
        fixed (AccelerationStructureBuildGeometryInfoKHR* infoPointer = infos)
        fixed (AccelerationStructureBuildRangeInfoKHR* rangePointer = ranges)
        {
            var rangePointers = stackalloc AccelerationStructureBuildRangeInfoKHR*[built.Count];
            for (var index = 0; index < built.Count; index++)
            {
                infoPointer[index].PGeometries = geometryPointer + index;
                rangePointers[index] = rangePointer + index;
            }

            DrainRing(command); // the barrier above is a ring record: issue it before the direct call
            Api.CmdBuildAccelerationStructures(command, (uint)built.Count, infoPointer, rangePointers);
        }

        Barrier(command, PipelineStageFlags.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureWriteBitKhr,
            PipelineStageFlags.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr);

        // One TLAS of one identity instance per BLAS (no face culling, mask 0xFF).
        scratch = scratchBase;
        var tlasInfos = new AccelerationStructureBuildGeometryInfoKHR[built.Count];
        var tlasGeometries = new AccelerationStructureGeometryKHR[built.Count];
        var tlasRanges = new AccelerationStructureBuildRangeInfoKHR[built.Count];
        var count = 0;
        foreach (var (blob, entry, _, _, primitives) in built)
        {
            var addressInfo = new AccelerationStructureDeviceAddressInfoKHR { SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = entry.Blas };
            var blasAddress = Api.GetAccelerationStructureDeviceAddress(device, &addressInfo);
            var instance = new AccelerationStructureInstanceKHR
            {
                Transform = new TransformMatrixKHR(),
                InstanceCustomIndex = 0,
                Mask = 0xFF,
                InstanceShaderBindingTableRecordOffset = 0,
                Flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr,
                AccelerationStructureReference = blasAddress,
            };
            instance.Transform.Matrix[0] = 1f;
            instance.Transform.Matrix[5] = 1f;
            instance.Transform.Matrix[10] = 1f;
            var instances = HostBuffer(_device, _scheduler, new ReadOnlySpan<byte>(&instance, sizeof(AccelerationStructureInstanceKHR)));
            temporaries.Add(instances);
            var geometry = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.InstancesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR
                {
                    Instances = new AccelerationStructureGeometryInstancesDataKHR
                    {
                        SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                        ArrayOfPointers = false,
                        Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instances.DeviceAddress },
                    },
                },
            };
            var info = new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.TopLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = 1,
                PGeometries = &geometry,
            };
            var one = 1u;
            var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
            Api.GetAccelerationStructureBuildSizes(device, AccelerationStructureBuildTypeKHR.DeviceKhr, &info, &one, &sizes);
            var tlasStorage = new GpuBuffer(_device, _scheduler, GpuBufferUsage.DeviceLocal, 0,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit, sizes.AccelerationStructureSize);
            var createInfo = new AccelerationStructureCreateInfoKHR
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = tlasStorage.Handle,
                Size = sizes.AccelerationStructureSize,
                Type = AccelerationStructureTypeKHR.TopLevelKhr,
            };
            AccelerationStructureKHR tlas;
            if (Api.CreateAccelerationStructure(device, &createInfo, null, &tlas) != Result.Success)
            {
                tlasStorage.Dispose();
                Fail(blob, "create_tlas");
                continue;
            }

            info.DstAccelerationStructure = tlas;
            info.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratch };
            scratch = Align(scratch + sizes.BuildScratchSize, RayQueryDevice.ScratchAlignment);
            entry.TlasStorage = tlasStorage;
            entry.Tlas = tlas;
            var tlasAddressInfo = new AccelerationStructureDeviceAddressInfoKHR { SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = tlas };
            entry.TlasAddress = Api.GetAccelerationStructureDeviceAddress(device, &tlasAddressInfo);
            tlasGeometries[count] = geometry;
            tlasInfos[count] = info;
            tlasRanges[count] = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = 1 };
            count++;
            _ = primitives;
        }

        fixed (AccelerationStructureGeometryKHR* geometryPointer = tlasGeometries)
        fixed (AccelerationStructureBuildGeometryInfoKHR* infoPointer = tlasInfos)
        fixed (AccelerationStructureBuildRangeInfoKHR* rangePointer = tlasRanges)
        {
            var rangePointers = stackalloc AccelerationStructureBuildRangeInfoKHR*[Math.Max(count, 1)];
            for (var index = 0; index < count; index++)
            {
                infoPointer[index].PGeometries = geometryPointer + index;
                rangePointers[index] = rangePointer + index;
            }

            if (count != 0)
            {
                DrainRing(command);
                Api.CmdBuildAccelerationStructures(command, (uint)count, infoPointer, rangePointers);
            }
        }

        Barrier(command, PipelineStageFlags.AccelerationStructureBuildBitKhr, AccessFlags2.AccelerationStructureWriteBitKhr,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.AccelerationStructureBuildBitKhr,
            AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.ShaderReadBit);

        foreach (var (blob, entry, _, _, primitives) in built)
        {
            if (entry.Tlas.Handle == 0)
            {
                DestroyLater(entry);
                continue;
            }

            lock (_lock)
            {
                blob.Built = entry;
                blob.State = AsState.Built;
                _builds++;
                _buildPrimitives += primitives;
            }

            WriteTableEntry(entry.Slot, (uint)(blob.Base >> 8), entry.TlasAddress, blob.Geometry!.FirstPair);
        }

        DisposeLater(temporaries);
    }

    private void DestroyLater(BuiltAs entry)
    {
        var device = _device.Device;
        _scheduler.QueueCompletionAction(() =>
        {
            Api.DestroyAccelerationStructure(device, entry.Blas, null);
            entry.BlasStorage.Dispose();
        });
    }

    private void DisposeLater(List<GpuBuffer> buffers)
    {
        if (buffers.Count != 0)
            _scheduler.QueueCompletionAction(() => buffers.ForEach(buffer => buffer.Dispose()));
    }

    private void Fail(Blob blob, string reason)
    {
        lock (_lock)
        {
            blob.State = AsState.Failed;
            blob.FailReason = reason;
            _failed++;
            _failReasons[reason] = _failReasons.GetValueOrDefault(reason) + 1;
        }
    }

    // The counters of one dispatch (under _lock): query and fallback lanes, the verify record ([GPU][RQ_VERIFY]).
    private void NoteQueryCounters(ReadOnlySpan<uint> counters, long frame)
    {
        if (DeviceAddressPaging.RayQueryMode == 0)
            return;
        _counterDispatches++;
        _queryLanes += counters[8];
        _fallbackLanes += counters[9];
        for (var index = 0; index < _verify.Length; index++)
            _verify[index] += counters[10 + index];
        if (counters[64] != 0 && _sampleLogs++ < 8)
        {
            var w = counters.Slice(65, 16).ToArray();
            static float F(uint value) => BitConverter.UInt32BitsToSingle(value);
            Console.Error.WriteLine($"[GPU][RQ_VERIFY_SAMPLE] frame={frame} blas=0x{(ulong)w[0] << 8:X} hit query/native={w[1]}/{w[2]} t={F(w[3])}/{F(w[6])} flags=0x{w[4]:X}/0x{w[7]:X} node=0x{w[5]:X}/0x{w[8]:X} tmax_in={F(w[9])} origin=({F(w[10])},{F(w[11])},{F(w[12])}) dir=({F(w[13])},{F(w[14])},{F(w[15])})");
        }

        if (_counterDispatches % 64 != 0)
            return;
        var total = _queryLanes + _fallbackLanes;
        Console.Error.WriteLine($"[GPU][RQ] frame={frame} query_lanes={(total == 0 ? 0 : 100.0 * _queryLanes / total):F1}% lanes_per_dispatch={total / 64} built={_builds} primitives={_buildPrimitives} blas_mib={_blasBytes / 1048576.0:F1} unpublished={_unpublished} dynamic={_dynamic} failed={_failed} reasons=[{string.Join(",", _failReasons.Select(pair => $"{pair.Key}:{pair.Value}"))}] known={_blobs.Count}");
        if (DeviceAddressPaging.RayQueryMode == 2)
        {
            var v = _verify;
            Console.Error.WriteLine($"[GPU][RQ_VERIFY] frame={frame} compared={v[0]} both_miss={v[1]} both_hit={v[2]} query_only={v[3]} native_only={v[4]} same_prim={v[5]} other_prim={v[6]} other_prim_equal_t={v[7]} differ_with_drop={v[8]} native_capped={v[9]} dt_same=[0:{v[10]} <1e-7:{v[11]} <1e-6:{v[12]} <1e-5:{v[13]} <1e-4:{v[14]} more:{v[15]}] dij_same=[0:{v[16]} <1e-6:{v[17]} <1e-5:{v[18]} <1e-4:{v[19]} <1e-3:{v[20]} more:{v[21]}] dt_other=[<1e-6:{v[22]} <1e-4:{v[23]} more:{v[24]}] query_only_alpha={v[25]} native_only_alpha={v[26]} other_prim_alpha={v[27]} dropped_lanes={v[28]}");
            Array.Clear(_verify);
        }

        _queryLanes = _fallbackLanes = 0;
    }
}
