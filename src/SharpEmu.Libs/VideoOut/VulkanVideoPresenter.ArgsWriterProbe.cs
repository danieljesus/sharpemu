// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

// [local] SHARPEMU_ARGS_WRITER_PROBE: the GPU-side copies of 0x8039D8A00's inputs and outputs and of 0x8039D9800's
// arguments (see ArgsWriterProbe).
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private const int ArgsWriterSlots = 256;
        private const int ArgsWriterSlotBytes = 2048;
        private const int ArgsWriterEntryOffset = 0;
        private const int ArgsWriterHeaderOffset = 64;
        private const int ArgsWriterPrefixOffset = 128;
        private const int ArgsWriterOutputOffset = 512;
        private const int ArgsConsumerSlots = 1024;
        private const int ArgsConsumerSlotBytes = 16;

        private GpuBuffer? _argsWriterRing, _argsConsumerRing;
        private readonly ArgsWriterLedger _argsWriterLedger = new();

        // SHARPEMU_ARGS_WRITER_INJECT=<nth>: before the nth writer dispatch, the +140 word behind the page table becomes the
        // CPU's total + 2^24, so the last count of that writer is ~1.7e7 (~0.2 s of early-exiting groups): proves the
        // report end to end (StaleHeader and Big on the writer, consumer_big matched to it) without the real stall.
        private static readonly int ArgsWriterInject = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ARGS_WRITER_INJECT"), out var nth) ? nth : 0;
        private int _argsWritersSeen;

        public void BeforeArgsWriterDispatch(ulong shaderHash, ReadOnlySpan<uint> userScalars)
        {
            if (shaderHash != ArgsWriterProbe.WriterHash || ArgsWriterInject <= 0 || userScalars.Length < 6 || ++_argsWritersSeen != ArgsWriterInject)
                return;
            var header = ((ulong)userScalars[5] << 32 | userScalars[4]) & 0xFFFF_FFFF_FFFFUL;
            Span<byte> total = stackalloc byte[4];
            if (!_guestMemory.TryRead(header + 140, total) || _bufferCache.RegisteredBufferAt(header + 140, sizeof(uint)) is not { } buffer)
            {
                Console.Error.WriteLine($"[GPU][ARGS_WRITER] inject skipped header=0x{header:X}");
                return;
            }

            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(total) + (1u << 24);
            buffer.Fill(header + 140 - buffer.CpuAddress, sizeof(uint), value);
            Console.Error.WriteLine($"[GPU][ARGS_WRITER] inject nth={ArgsWriterInject} header=0x{header:X} total={value} tick={_scheduler.CurrentTick}");
        }

        public void NoteArgsWriterRecorded(ulong shaderHash, IPreparedBindings bindings, ReadOnlySpan<uint> userScalars, ulong submitId, uint threads)
        {
            if (shaderHash != ArgsWriterProbe.WriterHash || bindings is not PreparedStageBindings prepared || userScalars.Length < 6)
                return;
            var sources = prepared.BufferSources;
            var views = prepared.Descriptors.Buffers;
            var table = prepared.Stage.Resources.FlattenedResourceTable;
            var info = prepared.Resources.Info;
            if (sources.Length < 2 || views.Length < 2 || table.Length < 2 || info.Buffers.Count < 2)
                return;
            var outputIndex = info.Buffers[0].Written ? 0 : 1;
            var prefixIndex = 1 - outputIndex;

            var header = ((ulong)userScalars[5] << 32 | userScalars[4]) & 0xFFFF_FFFF_FFFFUL;
            var headerBytes = new byte[ArgsWriterProbe.HeaderBytes];
            if (!_guestMemory.TryRead(header + ArgsWriterProbe.HeaderBase, headerBytes))
                return;
            var count = table[1];
            var words = (int)Math.Min(count, (uint)ArgsWriterProbe.MaxPrefixWords);
            var prefixAddress = sources[prefixIndex].Descriptor.Address;
            var prefix = new uint[words];
            if (words > 0 && !_guestMemory.TryRead(prefixAddress, System.Runtime.InteropServices.MemoryMarshal.AsBytes(prefix.AsSpan())))
                return;

            _argsWriterRing ??= CreateArgsWriterRing((ulong)ArgsWriterSlots * ArgsWriterSlotBytes);
            var ring = _argsWriterRing;
            var totalAddress = header + 140;
            var entryOffset = (totalAddress >> PageOwnerTable.PageBits) * sizeof(ulong);
            var registered = _bufferCache.RegisteredBufferAt(header + ArgsWriterProbe.HeaderBase, ArgsWriterProbe.HeaderBytes);
            var expectedEntry = 0UL;
            if (_bufferCache.RegisteredBufferAt(totalAddress, sizeof(uint)) is { } totalBuffer)
                expectedEntry = totalBuffer.DeviceAddress + (((totalAddress >> PageOwnerTable.PageBits) << PageOwnerTable.PageBits) - totalBuffer.CpuAddress);
            var backingHeader = new byte[ArgsWriterProbe.HeaderBytes];
            var backingPrefix = new uint[words];
            var backingRead = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TryReadBacking(header + ArgsWriterProbe.HeaderBase, backingHeader) &&
                (words == 0 || SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TryReadBacking(prefixAddress, System.Runtime.InteropServices.MemoryMarshal.AsBytes(backingPrefix.AsSpan())));
            var cpu = new ArgsWriterProbe.WriterCpu(header, sources[outputIndex].Descriptor.Address, threads, table[0], count, headerBytes, prefix,
                expectedEntry, registered is not null, backingRead ? backingHeader : null, backingRead ? backingPrefix : null);

            ulong sequence;
            int slot;
            lock (_argsWriterLedger)
                sequence = _argsWriterLedger.ReserveWriter(ArgsWriterSlots, _scheduler.CurrentTick, submitId, cpu, out slot);
            if (sequence == 0)
                return;

            var command = BeginBatchedGuestCommands();
            var slotOffset = (ulong)slot * ArgsWriterSlotBytes;
            RecordArgsProbeReadBarrier(command);
            var regions = stackalloc BufferCopy[1];
            var pageTable = _bufferCache.BdaPageTableBuffer;
            if (entryOffset + sizeof(ulong) <= pageTable.Size)
                CopyArgsProbeBytes(command, pageTable.Handle, entryOffset, ring.Handle, slotOffset + ArgsWriterEntryOffset, sizeof(ulong));
            if (registered is not null)
                CopyArgsProbeBytes(command, registered.Handle, header + ArgsWriterProbe.HeaderBase - registered.CpuAddress, ring.Handle,
                    slotOffset + ArgsWriterHeaderOffset, ArgsWriterProbe.HeaderBytes);
            var memoryOffsets = prepared.ShaderData;
            var layout = prepared.Layout;
            var prefixView = views[prefixIndex];
            var prefixShift = MemoryOffsetOf(memoryOffsets, layout.MemoryOffsetDword, prefixIndex);
            var prefixBytes = (ulong)words * sizeof(uint);
            if (words > 0 && prefixView.Buffer.Handle != 0 && prefixShift + prefixBytes <= prefixView.Range)
                CopyArgsProbeBytes(command, prefixView.Buffer, prefixView.Offset + prefixShift, ring.Handle, slotOffset + ArgsWriterPrefixOffset, prefixBytes);
            var outputView = views[outputIndex];
            var outputShift = MemoryOffsetOf(memoryOffsets, layout.MemoryOffsetDword, outputIndex);
            var outputs = (ulong)Math.Min(count, (uint)ArgsWriterProbe.MaxOutputs) * ArgsWriterProbe.OutputStride;
            if (outputView.Range > outputShift)
                outputs = Math.Min(outputs, outputView.Range - outputShift);
            if (outputs > 0 && outputView.Buffer.Handle != 0 && outputShift < outputView.Range)
                CopyArgsProbeBytes(command, outputView.Buffer, outputView.Offset + outputShift, ring.Handle, slotOffset + ArgsWriterOutputOffset, outputs);
            VkRecord.CmdFillBuffer(_vk, command, ring.Handle, slotOffset + ArgsWriterSlotBytes - sizeof(uint), sizeof(uint), IndirectArgsLedger.SequenceWord(sequence));
            RecordArgsProbeHostBarrier(command);
            DrainArgsWriterProbe();
        }

        // Records the copy of a 0x8039D9800 dispatch's 12 argument bytes, after the barrier that made them readable.
        private void RecordArgsConsumerProbe(CommandBuffer command, VkBuffer buffer, ulong offset, ulong address)
        {
            _argsConsumerRing ??= CreateArgsWriterRing((ulong)ArgsConsumerSlots * ArgsConsumerSlotBytes);
            ulong sequence;
            int slot;
            lock (_argsWriterLedger)
                sequence = _argsWriterLedger.ReserveConsumer(ArgsConsumerSlots, _scheduler.CurrentTick, _currentSubmitId, address, out slot);
            if (sequence == 0)
                return;
            var slotOffset = (ulong)slot * ArgsConsumerSlotBytes;
            RecordArgsProbeReadBarrier(command);
            CopyArgsProbeBytes(command, buffer, offset, _argsConsumerRing.Handle, slotOffset, 3u * sizeof(uint));
            VkRecord.CmdFillBuffer(_vk, command, _argsConsumerRing.Handle, slotOffset + 12, sizeof(uint), IndirectArgsLedger.SequenceWord(sequence));
            RecordArgsProbeHostBarrier(command);
            DrainArgsWriterProbe();
        }

        private GpuBuffer CreateArgsWriterRing(ulong size)
        {
            var ring = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, size);
            ring.Mapped.Clear();
            if (_argsWriterRing is null && _argsConsumerRing is null)
            {
                var previous = Gpu.Scheduling.GpuStallReport.Extra;
                Gpu.Scheduling.GpuStallReport.Extra = () =>
                {
                    var lines = previous is null ? new List<string>() : previous().ToList();
                    lock (_argsWriterLedger)
                        lines.AddRange(_argsWriterLedger.DescribeLatest(8));
                    return lines;
                };
            }

            return ring;
        }

        private static uint MemoryOffsetOf(uint[] shaderData, uint memoryOffsetDword, int index)
        {
            var dword = (int)memoryOffsetDword + index / 4;
            return dword < shaderData.Length ? (shaderData[dword] >> (index % 4 * 8)) & 0xFF : 0;
        }

        private void RecordArgsProbeReadBarrier(CommandBuffer command)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit,
            };
            VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
        }

        private void RecordArgsProbeHostBarrier(CommandBuffer command)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = AccessFlags2.HostReadBit,
            };
            VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        }

        private void CopyArgsProbeBytes(CommandBuffer command, VkBuffer source, ulong sourceOffset, VkBuffer destination, ulong destinationOffset, ulong size)
        {
            var region = new BufferCopy { SrcOffset = sourceOffset, DstOffset = destinationOffset, Size = size };
            Gpu.Vulkan.ScopeBarrierTracker.BeginSelfOrdered();
            VkRecord.CmdCopyBuffer(_vk, command, source, destination, 1, &region);
            Gpu.Vulkan.ScopeBarrierTracker.EndSelfOrdered();
        }

        private void DrainArgsWriterProbe()
        {
            List<string> lines;
            lock (_argsWriterLedger)
                lines = _argsWriterLedger.Drain(ReadArgsWriterSlot, ReadArgsConsumerSlot);
            foreach (var line in lines)
                Console.Error.WriteLine(line);
        }

        private ArgsWriterProbe.WriterGpu? ReadArgsWriterSlot(ArgsWriterLedger.Pending pending)
        {
            var slot = _argsWriterRing!.Mapped.Slice(pending.Slot * ArgsWriterSlotBytes, ArgsWriterSlotBytes);
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(slot);
            if (Volatile.Read(ref words[ArgsWriterSlotBytes / 4 - 1]) != IndirectArgsLedger.SequenceWord(pending.Sequence))
                return null;
            var cpu = pending.Cpu;
            var entry = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(slot[ArgsWriterEntryOffset..]);
            var header = cpu.HeaderCopied ? slot.Slice(ArgsWriterHeaderOffset, ArgsWriterProbe.HeaderBytes).ToArray() : null;
            var prefix = words.Slice(ArgsWriterPrefixOffset / 4, cpu.Prefix.Length).ToArray();
            var outputs = new uint[ArgsWriterProbe.Outputs(cpu.TableCount, cpu.Prefix.Length) * 3];
            for (var index = 0; index < outputs.Length / 3; index++)
            {
                var first = (ArgsWriterOutputOffset + index * ArgsWriterProbe.OutputStride) / 4;
                outputs[index * 3] = words[first];
                outputs[index * 3 + 1] = words[first + 1];
                outputs[index * 3 + 2] = words[first + 2];
            }

            return new ArgsWriterProbe.WriterGpu(entry, cpu.ExpectedEntry, header, prefix, outputs);
        }

        private (uint X, uint Y, uint Z)? ReadArgsConsumerSlot(ArgsWriterLedger.Consumer consumer)
        {
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                _argsConsumerRing!.Mapped.Slice(consumer.Slot * ArgsConsumerSlotBytes, ArgsConsumerSlotBytes));
            if (Volatile.Read(ref words[3]) != IndirectArgsLedger.SequenceWord(consumer.Sequence))
                return null;
            return (words[0], words[1], words[2]);
        }
    }
}
