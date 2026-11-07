// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_FRONT_RESOLVE_POOL's scan of the back queue: an item is offered only once the one before it ran, a
// refusal stops the scan until the refused item ran, no more than SHARPEMU_FRONT_RESOLVE_AHEAD items wait offered, and
// an item is offered as the next to run only when nothing before it waits offered.
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

        public bool Offer(Action item, bool next)
        {
            var name = Names[item];
            Log.Add("offer " + name + (next ? " next" : ""));
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
            "run D1", "offer D2 next", "offer X",
            "run D2",
            "run X", "offer D3 next", "offer D4",
            "run D3",
            "run D4",
        }, script.Log);
    }

    [Fact]
    public void OnlyTheFirstItemAfterAStopIsOfferedAsNext()
    {
        var script = new Script();
        var queue = new VulkanVideoPresenter.BackQueue(static () => { }, 256) { Lookahead = script.Offer };
        const int count = 40;
        for (var index = 0; index < count; index++)
            queue.Enqueue(script.Item("D" + index));

        queue.RunPending(long.MaxValue);

        Assert.Equal(new[] { "offer D1 next" }, script.Log.Where(entry => entry.EndsWith(" next", StringComparison.Ordinal)));
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
        var leastAhead = int.MaxValue;
        foreach (var entry in script.Log)
        {
            var parts = entry.Split(' ');
            var name = parts[1];
            if (parts[0] == "offer")
            {
                Assert.True(offered.Add(name), "offered twice: " + name);
                waiting++;
                Assert.InRange(waiting, 1, VulkanVideoPresenter.FrontResolveAhead);
            }
            else if (offered.Contains(name))
            {
                waiting--;
                if (int.Parse(name[1..]) < count - VulkanVideoPresenter.FrontResolveAhead)
                    leastAhead = Math.Min(leastAhead, waiting);
            }
        }

        Assert.Equal(count - 1, offered.Count);
        Assert.Equal(0, waiting);
        // Topped up once half the offered items ran: while the queue holds enough, at least half the depth waits ahead.
        Assert.True(leastAhead >= VulkanVideoPresenter.FrontResolveAhead / 2 - 1, $"least ahead {leastAhead}");
    }

    [Fact]
    public void ASerialPairRunsInlineForItsSkipsThenTriesThePoolAgain()
    {
        var skips = new Dictionary<(ulong Vertex, ulong Pixel), int> { [(1, 2)] = 3 };

        Assert.False(AgcExports.CommandStreamTranslation.TakeFrontSerialSkip(skips, (1, 3)));
        Assert.True(AgcExports.CommandStreamTranslation.TakeFrontSerialSkip(skips, (1, 2)));
        Assert.True(AgcExports.CommandStreamTranslation.TakeFrontSerialSkip(skips, (1, 2)));
        Assert.True(AgcExports.CommandStreamTranslation.TakeFrontSerialSkip(skips, (1, 2)));
        Assert.Empty(skips);
        Assert.False(AgcExports.CommandStreamTranslation.TakeFrontSerialSkip(skips, (1, 2)));
    }
}
