// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

// [local] SHARPEMU_INDIRECT_ARGS_PROBE: the GPU-side copy of every indirect dispatch's arguments (see IndirectArgsProbe).
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private GpuBuffer? _indirectArgsRing;
        private int _indirectArgsScanCountdown;

        // Records the copy of the 12 argument bytes at (buffer, offset) into the next ring slot, after the writes that
        // produced them. Returns where the dispatch takes its arguments from: the guest buffer, or the ring slot when
        // SHARPEMU_INDIRECT_ARGS_INJECT replaces this dispatch's X group count.
        private (VkBuffer Buffer, ulong Offset) RecordIndirectArgsProbe(CommandBuffer command, VkBuffer buffer, ulong offset, ulong address, bool threadUnits)
        {
            if (_indirectArgsRing is null)
            {
                _indirectArgsRing = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags,
                    (ulong)IndirectArgsProbe.Slots * IndirectArgsProbe.SlotBytes);
                _indirectArgsRing.Mapped.Clear();
                var ring = _indirectArgsRing;
                Gpu.Scheduling.GpuStallReport.Extra = () => IndirectArgsProbe.Ledger.DescribeLatest(192, slot => ReadIndirectArgsSlot(ring, slot));
            }

            Span<byte> cpu = stackalloc byte[12];
            if (!_guestMemory.TryRead(address, cpu))
                cpu.Clear();
            var shader = IndirectArgsProbe.CurrentShader;
            var (slot, sequence) = IndirectArgsProbe.Ledger.Reserve(shader, address, _scheduler.CurrentTick, cpu,
                _bufferCache.HasGpuDirtyBytes(address, 12));
            var ringHandle = _indirectArgsRing.Handle;
            var slotOffset = (ulong)slot * IndirectArgsProbe.SlotBytes;

            var before = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcAccessMask = AccessFlags2.ShaderWriteBit | AccessFlags2.TransferWriteBit | AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buffer,
                Offset = offset,
                Size = 3u * sizeof(uint),
            };
            VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 0, null, 1, &before, 0, null);
            var region = new BufferCopy { SrcOffset = offset, DstOffset = slotOffset, Size = 3u * sizeof(uint) };
            VkRecord.CmdCopyBuffer(_vk, command, buffer, ringHandle, 1, &region);
            VkRecord.CmdFillBuffer(_vk, command, ringHandle, slotOffset + 12, sizeof(uint), IndirectArgsLedger.SequenceWord(sequence));

            (VkBuffer, ulong) source = (buffer, offset);
            if (threadUnits && IndirectArgsProbe.ShouldInject(shader) && IndirectArgsProbe.Inject is { } inject)
            {
                var written = new BufferMemoryBarrier2
                {
                    SType = StructureType.BufferMemoryBarrier2,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstAccessMask = AccessFlags2.TransferWriteBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = ringHandle,
                    Offset = slotOffset,
                    Size = IndirectArgsProbe.SlotBytes,
                };
                VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 1, &written, 0, null);
                VkRecord.CmdFillBuffer(_vk, command, ringHandle, slotOffset, sizeof(uint), inject.Groups);
                Console.Error.WriteLine($"[GPU][INDIRECT_ARGS] inject seq={sequence} shader=0x{shader:X} address=0x{address:X} groups_x={inject.Groups} tick={_scheduler.CurrentTick}");
                source = (ringHandle, slotOffset);
            }

            var ready = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = AccessFlags2.IndirectCommandReadBit | AccessFlags2.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = ringHandle,
                Offset = slotOffset,
                Size = IndirectArgsProbe.SlotBytes,
            };
            VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.TransferBit, PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.HostBit, 0, 0, null, 1, &ready, 0, null);

            if (--_indirectArgsScanCountdown <= 0)
            {
                _indirectArgsScanCountdown = 256;
                CompareWriterInputs();
                var ring = _indirectArgsRing;
                foreach (var line in IndirectArgsProbe.Ledger.Scan(scanSlot => ReadIndirectArgsSlot(ring, scanSlot)))
                    Console.Error.WriteLine(line);
            }

            return source;
        }

        private int _prefixArgsLines;

        // 0x8039D8A00 (hash F6F44A551620914E) writes the thread counts of 0x8039D9800's indirect dispatches: for each of
        // its threads i < count, (P[i+1] - P[i], 1, 1), and (total - P[last], 1, 1) for the last, where the struct at
        // s[4:5] holds P's offset (+124), total (+140) and count (+152). A P that is not monotonic, or a total under
        // P[last], wraps to a ~4e9 count. This prints the CPU's view of those inputs (raw guest memory, no sync) and
        // whether the buffer store holds GPU-written bytes there: always for the first few, then every wrap.
        public void NoteIndirectArgsWriter(ulong shaderHash, ReadOnlySpan<uint> userScalars, ReadOnlySpan<uint> flattenedTable, uint threads)
        {
            if (shaderHash != 0xF6F44A551620914E || userScalars.Length < 6)
                return;
            var header = ((ulong)userScalars[5] << 32 | userScalars[4]) & 0xFFFF_FFFF_FFFFUL;
            Span<byte> fields = stackalloc byte[64];
            if (!_guestMemory.TryRead(header + 112, fields))
                return;
            var prefixOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fields[(124 - 112)..]);
            var total = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fields[(140 - 112)..]);
            var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(fields[(152 - 112)..]);
            var shown = (int)Math.Min(count, 64u);
            var prefixAddress = header + prefixOffset;
            Span<byte> prefixBytes = stackalloc byte[64 * 4];
            if (shown > 0 && !_guestMemory.TryRead(prefixAddress, prefixBytes[..(shown * 4)]))
                return;
            Span<uint> prefix = stackalloc uint[64];
            for (var index = 0; index < shown; index++)
                prefix[index] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(prefixBytes[(index * 4)..]);
            var (wrapped, text) = IndirectArgsLedger.DescribePrefix(prefix[..shown], count, total);
            QueueWriterInputCheck(header, fields, prefixAddress, prefix[..Math.Min(shown, WriterPrefixWords)]);
            // The shader takes P's offset and the count from the flattened resource table the host resolved (the total
            // it loads itself, only in the last thread): a table without the raw values means the materialization is
            // stale.
            var stale = !flattenedTable.Contains(prefixOffset) || !flattenedTable.Contains(count);
            if (!wrapped && !stale && _prefixArgsLines >= 8)
                return;
            if (_prefixArgsLines++ >= 200)
                return;
            Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[GPU][INDIRECT_ARGS] prefix{(wrapped ? "_wrap" : "")}{(stale ? "_stale_table" : "")} tick={_scheduler.CurrentTick} header=0x{header:X} threads={threads} count={count} total={total} " +
                $"p=0x{prefixAddress:X} start:diff=[{text}] header_gpu_dirty={_bufferCache.HasGpuDirtyBytes(header + 112, 64)} " +
                $"p_gpu_dirty={(shown > 0 && _bufferCache.HasGpuDirtyBytes(prefixAddress, (ulong)shown * 4))} " +
                $"table=[{string.Join(",", flattenedTable.Length > 16 ? flattenedTable[..16].ToArray() : flattenedTable.ToArray())}]"));
        }

        // The GPU's view of 0x8039D8A00's inputs: after the dispatch is recorded, the header words (+112..+176) and the
        // first prefix entries are copied from the buffer store into a host-visible ring, and later compared with the CPU
        // view taken at recording. The game cannot rewrite them before the GPU has run the dispatch, so a difference is
        // a stale upload (a CPU write the store missed), which a hang would need and which may show without one.
        private const int WriterSlots = 1024;
        private const int WriterSlotBytes = 256;
        private const int WriterPrefixWords = 16;
        private GpuBuffer? _writerRing;
        private ulong _writerSequence;
        private long _writerCompared, _writerMismatches;
        private int _writerLines;
        private readonly Queue<(ulong Sequence, int Slot, ulong Header, ulong Tick, byte[] Fields, uint[] Prefix)> _writerPending = new();

        private void QueueWriterInputCheck(ulong header, ReadOnlySpan<byte> fields, ulong prefixAddress, ReadOnlySpan<uint> prefix)
        {
            _writerRing ??= new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, (ulong)WriterSlots * WriterSlotBytes);
            CompareWriterInputs();
            if (_writerPending.Count >= WriterSlots / 2)
                return;
            var sequence = ++_writerSequence;
            var slot = (int)(sequence % WriterSlots);
            _writerPending.Enqueue((sequence, slot, header, _scheduler.CurrentTick, fields.ToArray(), prefix.ToArray()));
            var words = prefix.Length;
            Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => RecordWriterInputCopy(sequence, slot, header, prefixAddress, words));
        }

        private void RecordWriterInputCopy(ulong sequence, int slot, ulong header, ulong prefixAddress, int words)
        {
            var ring = _writerRing!;
            var command = BeginBatchedGuestCommands();
            var slotOffset = (ulong)slot * WriterSlotBytes;
            var (headerBuffer, headerOffset) = _bufferCache.ObtainBuffer(header + 112, 64, false);
            var barriers = stackalloc BufferMemoryBarrier2[2];
            var count = 1u;
            barriers[0] = ReadBarrier(headerBuffer.Handle, headerOffset, 64);
            (GpuBuffer Buffer, ulong Offset) prefixSource = default;
            if (words > 0)
            {
                prefixSource = _bufferCache.ObtainBuffer(prefixAddress, (ulong)words * 4, false);
                barriers[count++] = ReadBarrier(prefixSource.Buffer.Handle, prefixSource.Offset, (ulong)words * 4);
            }

            VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 0, null, count, barriers, 0, null);
            var headerCopy = new BufferCopy { SrcOffset = headerOffset, DstOffset = slotOffset, Size = 64 };
            VkRecord.CmdCopyBuffer(_vk, command, headerBuffer.Handle, ring.Handle, 1, &headerCopy);
            if (words > 0)
            {
                var prefixCopy = new BufferCopy { SrcOffset = prefixSource.Offset, DstOffset = slotOffset + 64, Size = (ulong)words * 4 };
                VkRecord.CmdCopyBuffer(_vk, command, prefixSource.Buffer.Handle, ring.Handle, 1, &prefixCopy);
            }

            VkRecord.CmdFillBuffer(_vk, command, ring.Handle, slotOffset + WriterSlotBytes - 4, sizeof(uint), IndirectArgsLedger.SequenceWord(sequence));
        }

        private static BufferMemoryBarrier2 ReadBarrier(VkBuffer buffer, ulong offset, ulong size) => new()
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcAccessMask = AccessFlags2.ShaderWriteBit | AccessFlags2.TransferWriteBit | AccessFlags2.MemoryWriteBit,
            DstAccessMask = AccessFlags2.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = offset,
            Size = size,
        };

        private void CompareWriterInputs()
        {
            var ring = _writerRing;
            if (ring is null)
                return;
            while (_writerPending.Count > 0)
            {
                var pending = _writerPending.Peek();
                var slot = ring.Mapped.Slice(pending.Slot * WriterSlotBytes, WriterSlotBytes);
                var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(slot);
                if (Volatile.Read(ref words[WriterSlotBytes / 4 - 1]) != IndirectArgsLedger.SequenceWord(pending.Sequence))
                    return;

                _writerPending.Dequeue();
                _writerCompared++;
                var gpuFields = slot[..64];
                var gpuPrefix = words.Slice(16, pending.Prefix.Length);
                if (!gpuFields.SequenceEqual(pending.Fields) || !gpuPrefix.SequenceEqual(pending.Prefix))
                {
                    _writerMismatches++;
                    if (_writerLines++ < 64)
                    {
                        var differing = new System.Text.StringBuilder();
                        for (var offset = 0; offset < 64; offset += 4)
                        {
                            var cpu = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pending.Fields.AsSpan(offset));
                            var gpu = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(gpuFields[offset..]);
                            if (cpu != gpu)
                                differing.Append(System.Globalization.CultureInfo.InvariantCulture, $"+{112 + offset}:cpu={cpu},gpu={gpu} ");
                        }

                        for (var index = 0; index < pending.Prefix.Length; index++)
                        {
                            if (pending.Prefix[index] != gpuPrefix[index])
                                differing.Append(System.Globalization.CultureInfo.InvariantCulture, $"P[{index}]:cpu={pending.Prefix[index]},gpu={gpuPrefix[index]} ");
                        }

                        Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"[GPU][INDIRECT_ARGS] writer_stale seq={pending.Sequence} tick={pending.Tick} header=0x{pending.Header:X} {differing}"));
                    }
                }

                if ((_writerCompared & 63) == 0)
                {
                    Console.Error.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"[GPU][INDIRECT_ARGS] writer_check compared={_writerCompared} mismatches={_writerMismatches} pending={_writerPending.Count}"));
                }
            }
        }

        private static (uint X, uint Y, uint Z, uint Sequence) ReadIndirectArgsSlot(GpuBuffer ring, int slot)
        {
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                ring.Mapped.Slice(slot * IndirectArgsProbe.SlotBytes, IndirectArgsProbe.SlotBytes));
            return (Volatile.Read(ref words[0]), Volatile.Read(ref words[1]), Volatile.Read(ref words[2]), Volatile.Read(ref words[3]));
        }
    }
}
