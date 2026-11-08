// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

// [local] SHARPEMU_DRAW_PLAN: the group hashes of the shadow's table write are those of the stamp lookup it replaced,
// and a bank copied with the hashes only (CopyFrom by default, SHARPEMU_PLAN_SHADOW_COPY=1 copies the dwords too)
// reads the hashes of its source after any sequence of writes, pushes, pops, clears and forgets.
public sealed class PlanRegisterShadowTests
{
    private const ulong PacketAddress = 0x1_0000_1000;

    private static RegisterBanks NewBanks() => new(static message => new InvalidOperationException(message));

    private static PacketContext Packet(uint opcode, int valueCount) =>
        new(PacketHeader.Make((uint)valueCount + 2, opcode), PacketAddress, 0, 100, 100);

    private static void WriteContext(RegisterBanks banks, uint offset, params uint[] values) =>
        RegisterWriteTable.WriteContextPacket(banks, Packet(PacketOpcode.SetContextRegister, values.Length), offset, values);

    // The shadow's write as it was before the group table (2fb5a2b4): mix first, then the stamp lookup per write.
    private sealed class StampLookupShadow
    {
        private readonly uint[] _values = new uint[RegisterBankLayout.ContextRegisterCount];
        private readonly ulong[] _known = new ulong[(RegisterBankLayout.ContextRegisterCount + 63) / 64];
        public PlanGroupHashes Hashes;

        public void Write(uint offset, ReadOnlySpan<uint> values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                Write(offset + (uint)index, values[index]);
            }
        }

        public void Write(uint register, uint value)
        {
            if (register >= _values.Length)
            {
                return;
            }

            var word = (int)(register >> 6);
            var bit = 1UL << (int)(register & 63);
            var delta = PlanRegisterShadow.Mix(register, value);
            if ((_known[word] & bit) != 0)
            {
                var old = _values[register];
                if (old == value)
                {
                    return;
                }

                delta ^= PlanRegisterShadow.Mix(register, old);
            }

            _known[word] |= bit;
            _values[register] = value;
            switch (RegisterStamps.ContextStampOf(register))
            {
                case RegisterStamp.ColorTargets:
                    Hashes.ColorTargets ^= delta;
                    if (!PlanRegisterShadow.IsTargetAddress(register)) Hashes.TargetFormats ^= delta;
                    break;
                case RegisterStamp.DepthTargets:
                    Hashes.DepthTargets ^= delta;
                    if (!PlanRegisterShadow.IsTargetAddress(register)) Hashes.TargetFormats ^= delta;
                    break;
                case RegisterStamp.ShaderInterface:
                    Hashes.ShaderInterface ^= delta;
                    break;
                case RegisterStamp.ContextDynamic:
                    Hashes.ContextDynamic ^= delta;
                    break;
                default:
                    Hashes.PipelineState ^= delta;
                    break;
            }
        }

        public void Clear()
        {
            Array.Clear(_known);
            Hashes = default;
        }
    }

    [Fact]
    public void EveryRegisterHashesIntoTheGroupOfItsStamp()
    {
        for (var register = 0u; register < RegisterBankLayout.ContextRegisterCount + 4; register++)
        {
            var shadow = new PlanRegisterShadow();
            var reference = new StampLookupShadow();
            foreach (var value in new[] { 0u, 0x1234_5678u, 0u, 0xFFFF_FFFFu, 0xFFFF_FFFFu })
            {
                shadow.Write(register, value);
                reference.Write(register, value);
                Assert.True(reference.Hashes.Equals(shadow.Hashes), $"register=0x{register:X} value=0x{value:X8}");
            }
        }
    }

    [Fact]
    public void RandomWritesHashAsTheStampLookupDid()
    {
        var random = new System.Random(0x5EED);
        var shadow = new PlanRegisterShadow();
        var reference = new StampLookupShadow();
        var values = new uint[16];
        for (var step = 0; step < 200_000; step++)
        {
            // Few distinct values per register, so repeats and A, B, A sequences are common.
            var register = (uint)random.Next((int)RegisterBankLayout.ContextRegisterCount + 8);
            var count = random.Next(8) == 0 ? random.Next(1, values.Length) : 1;
            for (var index = 0; index < count; index++)
            {
                values[index] = (uint)random.Next(4) * 0x0101_0101u;
            }

            var span = values.AsSpan(0, count);
            if (count == 1 && random.Next(2) == 0)
                shadow.Write(register, span[0]);
            else
                shadow.Write(register, span);
            reference.Write(register, span);
            if (random.Next(5000) == 0)
            {
                shadow.Clear();
                reference.Clear();
            }

            if (!reference.Hashes.Equals(shadow.Hashes))
            {
                Assert.Fail($"step={step} register=0x{register:X} count={count}");
            }
        }

        Assert.NotEqual(default, shadow.Hashes);
    }

    [Fact]
    public void ACopyCopiedWithoutTheShadowStillMatchesTheOneCopiedWithIt()
    {
        var copy = NewBanks().Clone(); // a copied bank's own shadow from earlier draws, if the old path left one
        var withShadow = NewBanks();
        foreach (var copyPlanShadow in new[] { false, true })
        {
            var live = NewBanks();
            var otherQueue = NewBanks(); // never written: no shadow, as a queue without context writes
            var steps = 0;

            void Check(string step)
            {
                copy.CopyFrom(live, copyPlanShadow);
                Assert.True(live.PlanHashes.Equals(copy.PlanHashes), $"{step} copyPlanShadow={copyPlanShadow}");
                withShadow.CopyFrom(live, copyPlanShadow: true);
                Assert.True(withShadow.PlanHashes.Equals(copy.PlanHashes), $"{step} copyPlanShadow={copyPlanShadow} (dwords)");
                copy.CopyFrom(otherQueue, copyPlanShadow); // the pooled copy taken by the other queue in between
                Assert.True(otherQueue.PlanHashes.Equals(copy.PlanHashes), $"{step} copyPlanShadow={copyPlanShadow} (other queue)");
                steps++;
            }

            Check("new");
            WriteContext(live, DbDepthControl, 0x0000_0006);
            WriteContext(live, CbColor0Info, 0x10);
            RegisterWriteTable.WriteContextEntry(live, PaClViewportXScale, 0x3F80_0000, PacketAddress);
            Check("written");
            var first = live.PlanHashes;
            WriteContext(live, CbColor0Info, 0x10); // repeated
            Check("repeated");
            Assert.Equal(first, live.PlanHashes);
            WriteContext(live, CbColor0Info, 0x20);
            Check("changed");
            WriteContext(live, CbColor0Info, 0x10); // back
            Check("back");
            Assert.Equal(first, live.PlanHashes);
            live.ApplyContextState(ContextStateOperation.Push);
            RegisterWriteTable.WriteContextEntry(live, DbDepthControl, 0x0000_0002, PacketAddress);
            Check("pushed");
            live.ApplyContextState(ContextStateOperation.Pop);
            Check("popped");
            Assert.Equal(first, live.PlanHashes);
            live.ApplyContextState(ContextStateOperation.PushClear);
            Check("push-cleared");
            Assert.Equal(default, live.PlanHashes);
            WriteContext(live, CbColor0Info, 0x30);
            Check("push-cleared, written");
            live.ApplyContextState(ContextStateOperation.Pop);
            Check("popped again");
            Assert.Equal(first, live.PlanHashes);
            live.ForgetPlanHashes();
            Check("forgotten");
            WriteContext(live, CbColor0Info, 0x40);
            Check("forgotten, written");
            live.ApplyContextState(ContextStateOperation.Clear);
            Check("cleared");
            Assert.Equal(default, live.PlanHashes);
            WriteContext(live, CbColor0Info, 0x10);
            RegisterWriteTable.WriteContextEntry(live, PaClViewportXScale, 0x3F80_0000, PacketAddress);
            WriteContext(live, DbDepthControl, 0x0000_0006);
            Check("rewritten");
            Assert.Equal(first, live.PlanHashes); // the same values as before, in another order
            live.Reset();
            Check("reset");
            Assert.Equal(default, live.PlanHashes);
            var clone = live.Clone();
            WriteContext(clone, CbColor0Info, 0x10);
            WriteContext(live, CbColor0Info, 0x10);
            Assert.Equal(live.PlanHashes, clone.PlanHashes);
            Check("cloned");
            Assert.Equal(16, steps);
        }
    }

    [Fact]
    public void ABankCopiedWithTheHashesOnlyStartsFromUniqueHashesWhenWritten()
    {
        var live = NewBanks();
        WriteContext(live, CbColor0Info, 0x10);
        var copy = NewBanks();
        copy.CopyFrom(live, copyPlanShadow: false);
        WriteContext(live, DbDepthControl, 0x0000_0006);
        WriteContext(copy, DbDepthControl, 0x0000_0006);
        Assert.NotEqual(live.PlanHashes, copy.PlanHashes); // equal to no other state, as a forgotten bank
        Assert.NotEqual(default, copy.PlanHashes);

        // Nothing written in the source: the copy starts from the reset hashes, as the source does.
        var fresh = NewBanks();
        copy.CopyFrom(fresh, copyPlanShadow: false);
        WriteContext(fresh, DbDepthControl, 0x0000_0006);
        WriteContext(copy, DbDepthControl, 0x0000_0006);
        Assert.Equal(fresh.PlanHashes, copy.PlanHashes);
    }

    [Fact]
    public void CopyingTheHashesOnlyAllocatesNoShadow()
    {
        var live = NewBanks();
        WriteContext(live, CbColor0Info, 0x10);
        var otherQueue = NewBanks();
        var copy = NewBanks();

        long Allocated(bool copyPlanShadow)
        {
            for (var warm = 0; warm < 4; warm++)
            {
                copy.CopyFrom(live, copyPlanShadow);
                copy.CopyFrom(otherQueue, copyPlanShadow);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 100; index++)
            {
                copy.CopyFrom(live, copyPlanShadow);
                copy.CopyFrom(otherQueue, copyPlanShadow); // drops the copy's shadow on the dword path
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var dwords = Allocated(copyPlanShadow: true);
        var hashes = Allocated(copyPlanShadow: false);
        Assert.True(dwords >= 100 * RegisterBankLayout.ContextRegisterCount * sizeof(uint), $"dwords={dwords}");
        Assert.True(hashes <= dwords - 100 * RegisterBankLayout.ContextRegisterCount * sizeof(uint), $"hashes={hashes} dwords={dwords}");
    }
}
