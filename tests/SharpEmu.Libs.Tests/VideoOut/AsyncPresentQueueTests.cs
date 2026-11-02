// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_ASYNC_PRESENT's hand-off, with a fake present call: each test owns its queue, and the switch is
// overridden per async flow.
public sealed class AsyncPresentQueueTests
{
    [Fact]
    public void PresentsOnItsOwnThreadAndReportsAfterTheCall()
    {
        var events = new List<string>();
        var renderThread = Environment.CurrentManagedThreadId;
        var presentThread = 0;
        using var queue = new AsyncPresentQueue(
            job =>
            {
                presentThread = Environment.CurrentManagedThreadId;
                lock (events) events.Add($"present {job.Serial} tick={job.Tick} image={job.ImageIndex}");
                return 0;
            },
            (job, result) =>
            {
                lock (events) events.Add($"completed {job.Serial} flip={job.FlipRequestId} result={result}");
            });

        var submitted = queue.Submit(tick: 7, imageIndex: 2, flipRequestId: 11);
        Assert.True(queue.TryFinish(out var finished, out var result));
        Assert.Equal(submitted, finished);
        Assert.Equal(0, result);
        Assert.NotEqual(renderThread, presentThread);
        Assert.Equal(["present 1 tick=7 image=2", "completed 1 flip=11 result=0"], events);
        Assert.False(queue.TryFinish(out _, out _));
        Assert.Equal(0, queue.Violations);
    }

    [Fact]
    public void KeepsTheHandOffOrderWithoutViolations()
    {
        var serials = new List<long>();
        using var queue = new AsyncPresentQueue(job =>
        {
            Thread.SpinWait(200);
            serials.Add(job.Serial);
            return job.Serial % 3 == 0 ? 1000001003 : 0;
        }, null);

        var results = new List<int>();
        for (var frame = 0; frame < 200; frame++)
        {
            queue.Submit((ulong)frame, (uint)(frame % 3), 0);
            queue.NoteAcquireAfterFinish(results);
        }

        Assert.Equal(Enumerable.Range(1, 200).Select(value => (long)value), serials);
        Assert.Equal(200, results.Count);
        Assert.Equal(66, results.Count(result => result != 0));
        Assert.Equal(0, queue.Violations);
        Assert.Equal(200, queue.Presents);
    }

    [Fact]
    public void CountsAnAcquireDuringAPresentAndAHandOffOverAnUnhandledResult()
    {
        using var release = new ManualResetEventSlim();
        using var queue = new AsyncPresentQueue(_ =>
        {
            release.Wait();
            return 0;
        }, null);

        queue.Submit(1, 0, 0);
        queue.NoteAcquire();
        Assert.Equal(1, queue.Violations);
        release.Set();
        Assert.True(queue.TryFinish(out _, out _));

        queue.Submit(2, 0, 0);
        while (queue.Pending) Thread.Yield();
        queue.Submit(3, 0, 0); // the second result was never handled
        Assert.Equal(2, queue.Violations);
        Assert.True(queue.TryFinish(out var last, out _));
        Assert.Equal(3, last.Serial);
    }

    [Fact]
    public void RethrowsAFailedPresentOnTheRenderThread()
    {
        using var queue = new AsyncPresentQueue(_ => throw new InvalidOperationException("lost"), null);
        queue.Submit(1, 0, 0);
        var failure = Assert.Throws<InvalidOperationException>(() => queue.TryFinish(out _, out _));
        Assert.Contains("lost", failure.Message);
        Assert.False(queue.TryFinish(out _, out _));
    }

    [Fact]
    public async Task ModeIsOverriddenPerAsyncFlow()
    {
        var outside = AsyncPresentQueue.Mode;
        await Task.Run(() =>
        {
            AsyncPresentQueue.Mode = 2;
            Assert.Equal(2, AsyncPresentQueue.Mode);
        });
        Assert.Equal(outside, AsyncPresentQueue.Mode);
    }
}

internal static class AsyncPresentQueueTestExtensions
{
    // What the render thread does before its next acquire: wait for the present and take its result.
    internal static void NoteAcquireAfterFinish(this AsyncPresentQueue queue, List<int> results)
    {
        if (queue.TryFinish(out _, out var result)) results.Add(result);
        queue.NoteAcquire();
    }
}
