// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_RESOLVE_POOL's scan of the back queue: an item is offered only once the one before it ran, a
// refusal stops the scan until the refused item ran, and no more than SHARPEMU_FRONT_RESOLVE_AHEAD items wait offered.
public sealed class BackQueueLookaheadTests
{
    private sealed class Script
    {
        public readonly List<string> Log = new();
        public readonly Dictionary<Action, string> Names = new();
        public readonly HashSet<string> Refused = new();

        public Action Item(string name)
        {
            Action item = () => Log.Add("run " + name);
            Names[item] = name;
            return item;
        }

        public bool Offer(Action item)
        {
            var name = Names[item];
            Log.Add("offer " + name);
            return !Refused.Contains(name);
        }
    }

    [Fact]
    public void ARefusedItemStopsTheScanUntilItRan()
    {
        var script = new Script();
        script.Refused.Add("X");
        var queue = new VulkanVideoPresenter.BackQueue(static () => { }, 64) { Lookahead = script.Offer };
        foreach (var name in new[] { "D1", "D2", "X", "D3", "D4" })
            queue.Enqueue(script.Item(name));

        Assert.Equal(5, queue.RunPending(long.MaxValue));

        Assert.Equal(new[]
        {
            "run D1", "offer D2", "offer X",
            "run D2",
            "run X", "offer D3", "offer D4",
            "run D3",
            "run D4",
        }, script.Log);
    }

    [Fact]
    public void NoMoreThanTheDepthWaitsOffered()
    {
        var script = new Script();
        var queue = new VulkanVideoPresenter.BackQueue(static () => { }, 256) { Lookahead = script.Offer };
        const int count = 40;
        for (var index = 0; index < count; index++)
            queue.Enqueue(script.Item("D" + index));

        queue.RunPending(long.MaxValue);

        var offered = new HashSet<string>();
        var waiting = 0;
        foreach (var entry in script.Log)
        {
            var name = entry[(entry.IndexOf(' ') + 1)..];
            if (entry.StartsWith("offer ", StringComparison.Ordinal))
            {
                Assert.True(offered.Add(name), "offered twice: " + name);
                waiting++;
                Assert.InRange(waiting, 1, VulkanVideoPresenter.FrontResolveAhead);
            }
            else if (offered.Contains(name))
            {
                waiting--;
            }
        }

        Assert.Equal(count - 1, offered.Count);
        Assert.Equal(0, waiting);
    }
}
