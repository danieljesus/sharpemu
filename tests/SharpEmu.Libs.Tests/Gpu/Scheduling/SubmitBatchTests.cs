// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

// [local] SHARPEMU_SUBMIT_BATCH: the submit thread joins the oldest sealed buffer, then submits it together with the
// following ones whose replay and uploads are already done, in order, with one queue call.
[Collection(SchedulingStateCollection.Name)]
public sealed class SubmitBatchTests
{
    private readonly FakeTickDevice _device = new();

    private SubmissionScheduler NewScheduler(int limit)
    {
        var scheduler = new SubmissionScheduler(_device, new RecordingRenderingState(), submitThread: true)
        {
            SubmitBatchLimit = limit,
        };
        scheduler.Begin(new SubmissionContext());
        return scheduler;
    }

    // Seals one buffer per position; the listed positions are not replayed yet.
    private ulong[] SealAt(SubmissionScheduler scheduler, long[] positions, params long[] unreplayed)
    {
        lock (_device.UnreplayedPositions)
        {
            foreach (var position in unreplayed)
            {
                _device.UnreplayedPositions.Add(position);
            }
        }

        var ticks = new ulong[positions.Length];
        for (var index = 0; index < positions.Length; index++)
        {
            _device.NextRecordedPosition = positions[index];
            ticks[index] = scheduler.Flush();
        }

        return ticks;
    }

    private void Replay(long position)
    {
        lock (_device.UnreplayedPositions)
        {
            _device.UnreplayedPositions.Remove(position);
        }
    }

    private void WaitForBatches(int count)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            lock (_device.BatchSizes)
            {
                if (_device.BatchSizes.Count >= count)
                {
                    return;
                }
            }

            Thread.Sleep(1);
        }
    }

    [Fact]
    public void ReadyBuffersBehindTheJoinedOneGoInTheSameCallInOrder()
    {
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(limit: 8);
        var ticks = SealAt(scheduler, [1, 2, 3], unreplayed: 1);

        Replay(1);
        scheduler.WaitSubmitted(ticks[^1]);

        Assert.Equal(new[] { 3 }, _device.BatchSizes);
        Assert.Equal(ticks, _device.Submits.Select(submit => submit.Tick));
        Assert.Equal(new[] { "b0", "b1", "b2" }, _device.Submits.Select(submit => _device.Name(submit.Buffer)));
        Assert.Equal(ticks[^1], scheduler.SubmittedTick);
    }

    [Fact]
    public void ABufferNotReplayedYetEndsTheBatch()
    {
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(limit: 8);
        var ticks = SealAt(scheduler, [1, 2, 3], unreplayed: [1, 2]);

        Replay(1);
        WaitForBatches(1);
        Assert.Equal(ticks[0], scheduler.SubmittedTick);
        Replay(2);
        scheduler.WaitSubmitted(ticks[^1]);

        Assert.Equal(new[] { 1, 2 }, _device.BatchSizes);
        Assert.Equal(ticks, _device.Submits.Select(submit => submit.Tick));
    }

    [Fact]
    public void ABufferWhoseUploadsAreNotDoneEndsTheBatch()
    {
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(limit: 8);
        var enqueued = 0L;
        var joined = new List<long>();
        scheduler.CaptureSubmitTarget = () => ++enqueued;
        scheduler.WaitSubmitTarget = target =>
        {
            lock (joined)
            {
                joined.Add(target);
            }
        };
        scheduler.SubmitTargetDone = target => target <= 2;
        var ticks = SealAt(scheduler, [1, 2, 3], unreplayed: 1);

        Replay(1);
        scheduler.WaitSubmitted(ticks[^1]);

        Assert.Equal(new[] { 2, 1 }, _device.BatchSizes);
        Assert.Equal(new long[] { 1, 3 }, joined);
        Assert.Equal(ticks, _device.Submits.Select(submit => submit.Tick));
    }

    [Fact]
    public void WithoutATargetCheckOnlyBuffersThatNeedNoJoinAreTaken()
    {
        _device.CompleteOnSubmit = true;
        using var scheduler = NewScheduler(limit: 8);
        scheduler.WaitSubmitTarget = _ => { };
        var ticks = SealAt(scheduler, [1, 2, 3], unreplayed: 1);

        Replay(1);
        scheduler.WaitSubmitted(ticks[^1]);

        Assert.Equal(new[] { 1, 1, 1 }, _device.BatchSizes);
        Assert.Equal(ticks, _device.Submits.Select(submit => submit.Tick));
    }

    [Fact]
    public void TheLimitCapsTheBatchAndOffSubmitsOneByOne()
    {
        _device.CompleteOnSubmit = true;
        using (var scheduler = NewScheduler(limit: 2))
        {
            var ticks = SealAt(scheduler, [1, 2, 3], unreplayed: 1);
            Replay(1);
            scheduler.WaitSubmitted(ticks[^1]);
            Assert.Equal(new[] { 2, 1 }, _device.BatchSizes);
        }

        var off = new FakeTickDevice { CompleteOnSubmit = true };
        using var plain = new SubmissionScheduler(off, new RecordingRenderingState(), submitThread: true) { SubmitBatchLimit = 0 };
        plain.Begin(new SubmissionContext());
        var plainTicks = new[] { plain.Flush(), plain.Flush() };
        plain.WaitSubmitted(plainTicks[^1]);
        Assert.Empty(off.BatchSizes);
        Assert.Equal(plainTicks, off.Submits.Select(submit => submit.Tick));
    }
}
