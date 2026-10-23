// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_PREP_WORKER: the worker runs preparations in submission order, and the render thread gets a
// result only when it is finished and still valid; otherwise it prepares the draw itself.
public sealed class FrontPrepWorkerTests
{
    private static readonly Func<long, bool> AlwaysValid = static _ => true;

    [Fact]
    public void RunsPreparationsInSubmissionOrderAndDeliversEachResult()
    {
        var order = new List<int>();
        using var worker = new FrontPrepWorker();
        var jobs = new List<FrontPrepWorker.Job<int>>();
        for (var index = 0; index < 500; index++)
        {
            var value = index;
            jobs.Add(worker.Submit(() =>
            {
                lock (order)
                    order.Add(value);
                return value * 3;
            }, version: 7));
        }

        for (var index = 0; index < jobs.Count; index++)
        {
            while (Volatile.Read(ref jobs[index].State) is not (FrontPrepWorker.Job.Done or FrontPrepWorker.Job.Running))
                Thread.Yield();
            Assert.True(worker.TryTake(jobs[index], AlwaysValid, out var result));
            Assert.Equal(index * 3, result);
        }

        Assert.Equal(Enumerable.Range(0, 500), order);
        Assert.Equal(500, worker.Taken);
        Assert.Equal(0, worker.Late);
    }

    [Fact]
    public void APreparationNotStartedWhenTheDrawExecutesIsAbandonedAndNeverRuns()
    {
        using var gate = new ManualResetEventSlim();
        using var worker = new FrontPrepWorker();
        var first = worker.Submit(() => { gate.Wait(TimeSpan.FromSeconds(10)); return 1; }, version: 0);
        var secondRan = false;
        var second = worker.Submit(() => { secondRan = true; return 2; }, version: 0);

        Assert.False(worker.TryTake(second, AlwaysValid, out var late));
        Assert.Equal(0, late);
        gate.Set();
        Assert.True(worker.TryTake(first, AlwaysValid, out var result));
        Assert.Equal(1, result);
        worker.Dispose();

        Assert.False(secondRan);
        Assert.Equal(FrontPrepWorker.Job.Abandoned, second.State);
        Assert.Equal(1, worker.Skipped);
        Assert.Equal(1, worker.Late);
    }

    [Fact]
    public async Task APreparationRunningWhenTheDrawExecutesIsWaitedFor()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        using var worker = new FrontPrepWorker();
        var job = worker.Submit(() => { started.Set(); gate.Wait(TimeSpan.FromSeconds(10)); return 42; }, version: 0);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var release = Task.Run(async () => { await Task.Delay(50); gate.Set(); });

        Assert.True(worker.TryTake(job, AlwaysValid, out var result));
        Assert.Equal(42, result);
        Assert.Equal(1, worker.Waited);
        await release;
    }

    [Fact]
    public void AStaleResultIsDiscarded()
    {
        using var worker = new FrontPrepWorker();
        var job = worker.Submit(() => 5, version: 11);
        while (Volatile.Read(ref job.State) != FrontPrepWorker.Job.Done)
            Thread.Yield();

        long checkedVersion = -1;
        Assert.False(worker.TryTake(job, version => { checkedVersion = version; return version == 12; }, out var result));
        Assert.Equal(0, result);
        Assert.Equal(11, checkedVersion);
        Assert.Equal(1, worker.Stale);
        Assert.Equal(0, worker.Taken);
    }

    [Fact]
    public void AFailedPreparationLeavesTheDrawToTheRenderThread()
    {
        using var worker = new FrontPrepWorker();
        var job = worker.Submit<int>(() => throw new InvalidOperationException("boom"), version: 0);
        var next = worker.Submit(() => 9, version: 0);
        while (Volatile.Read(ref next.State) != FrontPrepWorker.Job.Done)
            Thread.Yield();

        Assert.False(worker.TryTake(job, AlwaysValid, out _));
        Assert.True(worker.TryTake(next, AlwaysValid, out var result));
        Assert.Equal(9, result);
        Assert.Equal(1, worker.Failures);
    }

    [Fact]
    public void AResultThatReadStoreTablesIsDiscardedWhenATableWriteWasAppliedSince()
    {
        var applied = 5L;
        var usedTables = true;
        using var worker = new FrontPrepWorker(beginMark: () => Volatile.Read(ref applied), endMark: mark => usedTables ? mark : -1);
        Func<long, bool> tablesStillValid = mark => mark == Volatile.Read(ref applied);

        var unchanged = worker.Submit(() => 1, version: 0);
        while (Volatile.Read(ref unchanged.State) != FrontPrepWorker.Job.Done)
            Thread.Yield();
        Assert.True(worker.TryTake(unchanged, AlwaysValid, out var kept, tablesStillValid));
        Assert.Equal(1, kept);

        var written = worker.Submit(() => 2, version: 0);
        while (Volatile.Read(ref written.State) != FrontPrepWorker.Job.Done)
            Thread.Yield();
        Volatile.Write(ref applied, 6);
        Assert.False(worker.TryTake(written, AlwaysValid, out _, tablesStillValid));
        Assert.Equal(1, worker.Stale);

        usedTables = false;
        var noTables = worker.Submit(() => 3, version: 0);
        while (Volatile.Read(ref noTables.State) != FrontPrepWorker.Job.Done)
            Thread.Yield();
        Volatile.Write(ref applied, 7);
        Assert.True(worker.TryTake(noTables, AlwaysValid, out var free, tablesStillValid));
        Assert.Equal(3, free);
    }
}
