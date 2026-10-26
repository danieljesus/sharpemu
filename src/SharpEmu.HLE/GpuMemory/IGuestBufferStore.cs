// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGuestBufferStore
{
    // Returns true when recovery completes, even if a later operation adds a new watch.
    bool MarkCpuWrite(ulong address, ulong size);

    bool DownloadToCpu(ulong address, ulong size);

    // True for current CPU data or an untracked range; false when required recovery fails.
    bool TrySynchronizeCpuRead(ulong address, ulong size) => DownloadToCpu(address, size);

    // [local] SHARPEMU_FAULT_READ_EMULATE: the bytes of a faulting read when none of them is GPU-modified, so the backing
    // already holds what a download would leave there; false when a download is needed.
    bool TryReadUnmodified(ulong address, Span<byte> data) => false;

    // [local] SHARPEMU_FAULT_READ_EMULATE=1: as TryReadUnmodified for a repeated poll whose previous bytes are in data:
    // waits while the range still holds them, until a write touches it or the deadline (Stopwatch ticks).
    // [local] SHARPEMU_FAULT_READ_EMULATE=1: as TryReadUnmodified, only while a writer of the page's GPU-modified
    // ranges is still in flight (the ordinary download would wait for it); false otherwise, so the download runs and
    // leaves the page clean.
    bool TryReadUnmodifiedWhileWritten(ulong address, Span<byte> data) => false;

    bool TryReadUnmodifiedParked(ulong address, Span<byte> data, long deadline) => TryReadUnmodified(address, data);

    // [local] SHARPEMU_FAULT_READ_EMULATE=2: counts the writes that touch the range between begin and end (-1: none).
    int BeginWriteWatch(ulong address, ulong size) => -1;

    int EndWriteWatch(int watch) => -1;
}
