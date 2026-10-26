// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// [local] SHARPEMU_FAULT_READ_EMULATE: BZHI of an emulated faulting load against the instruction's definition.
public sealed class FaultReadBzhiTests
{
    private const uint Carry = 0x1, Zero = 0x40, Sign = 0x80, Overflow = 0x800, Parity = 0x4;

    [Theory]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFUL, 0UL, 8, 0UL, Zero)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFUL, 12UL, 8, 0xFFFUL, 0u)]
    [InlineData(0x8000_0000_0000_0001UL, 63UL, 8, 1UL, 0u)]
    [InlineData(0x8000_0000_0000_0001UL, 64UL, 8, 0x8000_0000_0000_0001UL, Carry | Sign)]
    [InlineData(0x8000_0000_0000_0001UL, 0x13FUL, 8, 1UL, 0u)]
    [InlineData(0x8000_0001UL, 32UL, 4, 0x8000_0001UL, Carry | Sign)]
    [InlineData(0x8000_0001UL, 31UL, 4, 1UL, 0u)]
    [InlineData(0x1234_5678UL, 0xFF00UL, 4, 0UL, Zero)]
    public void BzhiClearsFromTheIndexAndSetsTheFlags(ulong source, ulong index, int size, ulong expected, uint expectedFlags)
    {
        var flags = Carry | Zero | Sign | Overflow | Parity;
        Assert.Equal(expected, DirectExecutionBackend.Bzhi(source, index, size, ref flags));
        Assert.Equal(expectedFlags | Parity, flags);
    }
}
