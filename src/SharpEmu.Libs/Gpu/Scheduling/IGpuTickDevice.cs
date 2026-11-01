// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// Provide the scheduler with one timeline semaphore, one command pool, and one queue.
public interface IGpuTickDevice : IDisposable
{
    object QueueGate { get; }

    ulong TimelineHandle { get; }

    ulong ReadTimeline();

    bool TryWaitTimeline(ulong tick, out string failure);

    nint[] AllocateBuffers(int count);

    void BeginBuffer(nint buffer);

    void EndBuffer(nint buffer);

    bool TrySubmit(nint buffer, SubmitBundle bundle, out string failure);

    // [local] The buffer has ended and will be submitted; it is reused only after its tick completes. Runs on the
    // recording thread, also when a submit thread does the submit.
    void MarkSealed(nint buffer)
    {
    }

    // [local] Position of the last ended buffer in the device's record ring (0 without one), and a wait until the
    // ring has replayed up to a position. A buffer is submitted only after its end has been replayed.
    long RecordedPosition => 0;

    void WaitRecorded(long position)
    {
    }

    // [local] SHARPEMU_SUBMIT_BATCH: whether the ring has already replayed up to a position (never blocks).
    bool IsRecorded(long position) => true;

    // [local] SHARPEMU_SUBMIT_BATCH: submit several sealed buffers in order with one queue call. Each keeps its own
    // waits and signals; the default submits them one by one, which is the same work in the same order.
    bool TrySubmitBatch(ReadOnlySpan<nint> buffers, ReadOnlySpan<SubmitBundle> bundles, out string failure)
    {
        failure = "";
        for (var index = 0; index < buffers.Length; index++)
        {
            if (!TrySubmit(buffers[index], bundles[index], out failure))
            {
                return false;
            }
        }

        return true;
    }

    // [local] For GpuStallReport: the raw timeline value and the record ring's positions.
    string DescribeStall() => "";
}
