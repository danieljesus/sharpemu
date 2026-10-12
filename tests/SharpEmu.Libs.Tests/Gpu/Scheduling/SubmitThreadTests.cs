// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

// [local] SHARPEMU_SUBMIT_THREAD: the recording thread seals ticks, a submit thread submits them in order.
[Collection(SchedulingStateCollection.Name)]
public sealed class SubmitThreadTests
{
    private readonly FakeTickDevice _device = new();

    private SubmissionScheduler NewScheduler(bool submitThread, Action<ulong>? completed = null)
    {
        var scheduler = new SubmissionScheduler(_device, new RecordingRenderingState(), completed: completed,
            submitThread: submitThread);
        scheduler.Begin(new SubmissionContext());
        return scheduler;
    }

    [Fact]
    public async Task SealedTicksAreSubmittedInOrderAndWaitSubmittedBlocksUntilThen()
    {
        using var gate = new ManualResetEventSlim(false);
        _device.SubmitGate = gate;
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(submitThread: true);
        Assert.True(scheduler.UsesSubmitThread);

        var ticks = new[] { scheduler.Flush(), scheduler.Flush(), scheduler.Flush() };

        Assert.Equal(new ulong[] { 1, 2, 3 }, ticks);
        Assert.Equal(4UL, scheduler.CurrentTick);
        Assert.Equal(0UL, scheduler.SubmittedTick);
        var waiter = Task.Run(() => scheduler.WaitSubmitted(3));
        Assert.False(await CompletesWithin(waiter, 100));

        gate.Set();

        Assert.True(await CompletesWithin(waiter, 5000));
        Assert.Equal(3UL, scheduler.SubmittedTick);
        Assert.Equal(ticks, _device.Submits.Select(submit => submit.Tick));
        Assert.Equal(new[] { "b0", "b1", "b2" }, _device.Submits.Select(submit => _device.Name(submit.Buffer)));
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, _device.SubmitThreads);
    }

    [Fact]
    public void TheQueuePartJoinsTheTargetCapturedWhenTheBufferWasSealed()
    {
        using var gate = new ManualResetEventSlim(false);
        _device.SubmitGate = gate;
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(submitThread: true);
        var enqueued = 0L;
        var joined = new List<(long Target, int Thread)>();
        scheduler.CaptureSubmitTarget = () => ++enqueued;
        scheduler.WaitSubmitTarget = target =>
        {
            lock (joined)
            {
                joined.Add((target, Environment.CurrentManagedThreadId));
            }
        };

        scheduler.Flush();
        enqueued += 10;
        scheduler.Flush();
        gate.Set();
        scheduler.WaitAllSubmitted();

        Assert.Equal(new long[] { 1, 12 }, joined.Select(entry => entry.Target));
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, joined.Select(entry => entry.Thread));
    }

    [Fact]
    public void WithoutTheThreadTheQueuePartRunsInlineAndEverySealedTickIsSubmitted()
    {
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(submitThread: false);
        var joined = new List<long>();
        scheduler.CaptureSubmitTarget = () => 7;
        scheduler.WaitSubmitTarget = target =>
        {
            Assert.Equal(joined.Count, _device.Submits.Count);
            joined.Add(target);
        };

        Assert.False(scheduler.UsesSubmitThread);
        Assert.Equal(1UL, scheduler.Flush());
        scheduler.WaitSubmitted(1);

        Assert.Equal(1UL, scheduler.SubmittedTick);
        Assert.Equal(new long[] { 7 }, joined);
        Assert.Single(_device.Submits);
        Assert.Equal(new[] { Environment.CurrentManagedThreadId }, _device.SubmitThreads);
    }

    [Fact]
    public void WaitAndFinishSubmitThroughTheThread()
    {
        _device.CompleteOnSubmit = true;
        var completed = new List<ulong>();
        using var scheduler = NewScheduler(submitThread: true, completed: tick =>
        {
            lock (completed)
            {
                completed.Add(tick);
            }
        });

        scheduler.Wait(1);
        scheduler.Finish();
        scheduler.FlushAndWait();
        scheduler.WaitForAllPriorityOperations();

        Assert.Equal(new ulong[] { 1, 2, 3 }, _device.Submits.Select(submit => submit.Tick));
        Assert.Equal(new ulong[] { 1, 2, 3 }, completed);
        Assert.True(scheduler.IsTickComplete(3));
    }

    [Fact]
    public void ASubmitFailureSurfacesOnTheRecordingThread()
    {
        using var fatal = new FatalScope();
        _device.FailSubmit = true;
        // The failed tick never signals: the scheduler is left undisposed, as the process would end here.
        var scheduler = NewScheduler(submitThread: true);
        scheduler.Current.SetDebugInfo((uint)RecordedOperation.EopInterrupt, 99, 1, 2, 3, 4, 0xABCD);

        Assert.Equal(1UL, scheduler.Submit());
        var error = Assert.Throws<SchedulerFatalException>(() => scheduler.WaitSubmitted(1));

        Assert.Equal(
            "vkQueueSubmit failed: VK_ERROR_DEVICE_LOST, tick=1 debug_op=4 debug_submit=99 args=1,2,3,4,0x000000000000ABCD",
            error.Message);
        Assert.Throws<SchedulerFatalException>(() => scheduler.Submit());
        Assert.Equal(0UL, scheduler.SubmittedTick);
    }

    [Fact]
    public async Task ShutdownSubmitsEverySealedTickBeforeItCloses()
    {
        using var gate = new ManualResetEventSlim(false);
        _device.SubmitGate = gate;
        _device.CompleteOnSubmit = true;
        var scheduler = NewScheduler(submitThread: true);
        scheduler.Flush();
        scheduler.Flush();

        var shutdown = Task.Run(scheduler.Dispose);
        Assert.False(await CompletesWithin(shutdown, 100));
        gate.Set();

        Assert.True(await CompletesWithin(shutdown, 5000));
        Assert.Equal(new ulong[] { 1, 2, 3 }, _device.Submits.Select(submit => submit.Tick));
        Assert.False(scheduler.PriorityWorkerAlive);
        Assert.Equal("dispose", _device.Log[^1]);
    }
}
