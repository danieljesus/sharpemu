// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public interface IGpuCommandBufferSource
{
    nint[] AllocateBuffers(int count);

    void BeginBuffer(nint buffer);

    void EndBuffer(nint buffer, bool hostRead);
}

// Provide the scheduler with one timeline semaphore, one queue, its own command pool and further
// command pools for other recording threads.
public interface IGpuTickDevice : IGpuCommandBufferSource, IDisposable
{
    object QueueGate { get; }

    ulong TimelineHandle { get; }

    ulong ReadTimeline();

    bool TryWaitTimeline(ulong tick, out string failure);

    IGpuCommandBufferSource CreateBufferSource();

    bool TrySubmit(ReadOnlySpan<nint> buffers, SubmitBundle bundle, out string failure);

    // [local] The device's own buffer has ended and will be submitted; it is reused only after its tick completes. Runs
    // on the recording thread, also when a submit thread does the submit. Buffers of CreateBufferSource are not marked.
    void MarkSealed(nint buffer)
    {
    }

    // [local] Position of the last ended buffer in the device's record ring (0 without one), and a wait until the
    // ring has replayed up to a position. A buffer is submitted only after its end has been replayed. Only the
    // device's own buffers go through the ring; buffers of CreateBufferSource are recorded directly by their thread.
    long RecordedPosition => 0;

    void WaitRecorded(long position)
    {
    }

    // [local] SHARPEMU_SUBMIT_BATCH: whether the ring has already replayed up to a position (never blocks).
    bool IsRecorded(long position) => true;

    // [local] SHARPEMU_SUBMIT_BATCH: submit several sealed submissions in order with one queue call. Each keeps its own
    // buffers, waits and signals (unlike TrySubmit, whose buffers share one bundle); the default submits them one by
    // one, which is the same work in the same order.
    bool TrySubmitBatch(ReadOnlySpan<nint[]> buffers, ReadOnlySpan<SubmitBundle> bundles, out string failure)
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
