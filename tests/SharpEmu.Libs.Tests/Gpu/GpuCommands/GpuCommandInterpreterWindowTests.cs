// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterWindowTests
{
    private const ulong Command = StreamRunner.CommandAddress;

    private static uint[] InstanceCount(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static uint[] WriteData(ulong destination, params uint[] values)
    {
        var payload = new uint[3 + values.Length];
        payload[0] = 5u << 8;
        payload[1] = StreamRunner.Low(destination);
        payload[2] = StreamRunner.High(destination);
        values.CopyTo(payload, 3);
        return StreamRunner.Packet(PacketOpcode.WriteData, payload);
    }

    private static IEnumerable<ulong> StreamReads(StreamRunner runner) =>
        runner.Host.GuestReads.Where(address => address >= Command && address < Command + 0x1000);

    [Fact]
    public void ConsecutivePackets_ComeFromOneHostRead()
    {
        var runner = new StreamRunner();

        var progress = runner.Run(InstanceCount(1), InstanceCount(2), InstanceCount(3));

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal(3u, runner.Interpreter.InstanceCount);
        Assert.Equal([Command], StreamReads(runner));
    }

    [Fact]
    public void APacketThatWritesMemory_DropsTheWindow()
    {
        var runner = new StreamRunner();
        // The write lands on the count of the packet after it (two dwords, then five, then the count).
        var patched = Command + (2 + 5 + 1) * sizeof(uint);

        var progress = runner.Run(InstanceCount(1), WriteData(patched, 9), InstanceCount(2));

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal(9u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void AHostCommandThatRan_DropsTheWindow()
    {
        var runner = new StreamRunner();
        runner.Load(InstanceCount(1), InstanceCount(2), InstanceCount(3));
        // Queued while the window is filled: it runs before the next packet and changes the last count.
        runner.Host.BeforeGuestRead = address =>
        {
            if (address == Command)
                runner.Host.PendingCommands.Enqueue(() => runner.Host.WriteDword(Command + 5 * sizeof(uint), 7));
        };

        var progress = runner.Run();

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal(7u, runner.Interpreter.InstanceCount);
        Assert.Equal(2, StreamReads(runner).Count());
    }

    [Fact]
    public void ANestedBuffer_KeepsTheParentsWindow()
    {
        const ulong nested = Command + 0x800;
        var runner = new StreamRunner();
        runner.Load(nested, StreamRunner.Concat(InstanceCount(5), InstanceCount(6)));
        var indirect = StreamRunner.Packet(PacketOpcode.IndirectBuffer, StreamRunner.Low(nested), StreamRunner.High(nested) & 0xFFFF, 4);

        var progress = runner.Run(InstanceCount(1), indirect, InstanceCount(2));

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal(2u, runner.Interpreter.InstanceCount);
        Assert.Equal([Command, nested], StreamReads(runner));
    }

    [Fact]
    public void APacketLongerThanTheWindow_IsReadDirectly()
    {
        var runner = new StreamRunner();
        var longNop = StreamRunner.Packet(PacketOpcode.Nop, new uint[300]);

        var progress = runner.Run(longNop, InstanceCount(4));

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal(4u, runner.Interpreter.InstanceCount);
        Assert.Contains(Command + sizeof(uint), StreamReads(runner));
        Assert.Contains(Command + 301 * sizeof(uint), StreamReads(runner));
    }
}
