// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// [local] SHARPEMU_READBACK_SUBMIT_GUARD: a readback-queue copy that waits on the main timeline is submitted only once
// the submit thread has handed the tick it waits for to the main queue.
[Collection(SchedulingStateCollection.Name)]
public sealed class ReadbackSubmitGuardTests
{
    private readonly FakeTickDevice _device = new();

    private SubmissionScheduler NewScheduler(bool submitThread)
    {
        var scheduler = new SubmissionScheduler(_device, new RecordingRenderingState(), submitThread: submitThread);
        scheduler.Begin(new SubmissionContext());
        return scheduler;
    }

    [Fact]
    public async Task WithTheGuardTheCopyWaitsUntilItsTickIsOnTheMainQueue()
    {
        using var gate = new ManualResetEventSlim(false);
        _device.SubmitGate = gate;
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(submitThread: true);
        var tick = scheduler.Flush();

        var prepare = Task.Run(() => VulkanAsyncReadback.PrepareMainQueueWait(scheduler, tick, guard: true));
        Assert.False(await CompletesWithin(prepare, 100));
        Assert.Equal(0UL, scheduler.SubmittedTick);

        gate.Set();

        Assert.True(await CompletesWithin(prepare, 5000));
        Assert.True(prepare.Result);
        Assert.True(scheduler.SubmittedTick >= tick);
    }

    [Fact]
    public void WithoutTheGuardTheCopyIsSubmittedBeforeItsSignal()
    {
        using var gate = new ManualResetEventSlim(false);
        _device.SubmitGate = gate;
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(submitThread: true);
        var tick = scheduler.Flush();

        Assert.True(VulkanAsyncReadback.PrepareMainQueueWait(scheduler, tick, guard: false));
        Assert.Equal(0UL, scheduler.SubmittedTick);

        gate.Set();
        scheduler.WaitAllSubmitted();
    }

    [Fact]
    public void AWaitForTheTickBeingRecordedIsRefused()
    {
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(submitThread: false);
        var sealedTick = scheduler.Flush();

        Assert.False(VulkanAsyncReadback.PrepareMainQueueWait(scheduler, scheduler.CurrentTick, guard: true));
        Assert.True(VulkanAsyncReadback.PrepareMainQueueWait(scheduler, sealedTick, guard: true));
        Assert.True(VulkanAsyncReadback.PrepareMainQueueWait(scheduler, 0, guard: true));
    }
}
