// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_SLICES' hook in the back queue: a taken item goes to the slicer instead of running, an item it
// does not take runs only after the slicer joined what it took, and RunPending joins before it returns (queue empty or
// deadline passed).
public sealed class FrontSliceBackQueueTests
{
    private sealed class Slicer : VulkanVideoPresenter.BackQueue.ISlicer
    {
        public readonly List<string> Log = new();
        public readonly Dictionary<Action, string> Names = new();
        private int _open;

        public Action Item(string name)
        {
            Action item = () => Log.Add("run " + name);
            Names[item] = name;
            return item;
        }

        public bool Take(Action work)
        {
            var name = Names[work];
            if (name.StartsWith('D') || (name.StartsWith('B') && _open != 0))
            {
                return true;
            }

            Flush(FrontSliceFlush.Item);
            return false;
        }

        public void Run(Action work)
        {
            Log.Add("take " + Names[work]);
            _open++;
        }

        public void Flush(FrontSliceFlush reason)
        {
            if (_open == 0)
            {
                return;
            }

            Log.Add($"join {reason.ToString().ToLowerInvariant()} {_open}");
            _open = 0;
        }
    }

    [Fact]
    public void AnItemTheSlicerDoesNotTakeRunsAfterWhatItTookIsJoined()
    {
        var slicer = new Slicer();
        var queue = new VulkanVideoPresenter.BackQueue(static () => { }, 64) { Slicer = slicer };
        foreach (var name in new[] { "D1", "D2", "B1", "X", "B2", "D3" })
            queue.Enqueue(slicer.Item(name));

        Assert.Equal(6, queue.RunPending(long.MaxValue));

        Assert.Equal(new[]
        {
            "take D1", "take D2", "take B1",
            "join item 3", "run X",
            "run B2",
            "take D3",
            "join exit 1",
        }, slicer.Log);
    }

    [Fact]
    public void ADeadlineJoinsWhatWasTakenBeforeReturning()
    {
        var slicer = new Slicer();
        var queue = new VulkanVideoPresenter.BackQueue(static () => { }, 64) { Slicer = slicer };
        queue.Enqueue(slicer.Item("D1"));
        queue.Enqueue(slicer.Item("D2"));

        Assert.Equal(1, queue.RunPending(0));
        Assert.Equal(new[] { "take D1", "join exit 1" }, slicer.Log);

        Assert.Equal(1, queue.RunPending(long.MaxValue));
        Assert.Equal(new[] { "take D1", "join exit 1", "take D2", "join exit 1" }, slicer.Log);
    }

    [Fact]
    public void AnItemAfterItemsJoinsBeforeTheHookThatMayWait()
    {
        var slicer = new Slicer();
        var queue = new VulkanVideoPresenter.BackQueue(static () => { }, 64) { Slicer = slicer };
        queue.AfterItem = () => slicer.Log.Add("after");
        queue.Enqueue(slicer.Item("D1"));

        queue.RunPending(long.MaxValue);

        Assert.Equal(new[] { "take D1", "join after 1", "after" }, slicer.Log);
    }
}
