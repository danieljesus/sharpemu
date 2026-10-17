// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

// SHARPEMU_STRIPED_ACCESS_COUNT: Dispose still waits for every in-flight access, including an alias access left on
// another thread, and single-view accesses work as before.
[Collection(GuestMemoryStateCollection.Name)]
public sealed class StripedAccessCountTests
{
    [Fact]
    public unsafe void DisposeWaitsForAliasAccessLeftOnAnotherThread()
    {
        if (!Supported) return;
        var previous = SharedBackingViews.StripedAccessCountDefault;
        SharedBackingViews.StripedAccessCountDefault = true;
        try
        {
            var host = HostViewMemory.Create();
            var store = new SharedBackingViews(host, BackingSize);
            var hole = HoleSize(host);
            var baseAddress = ReserveFreeHole(host, hole);
            Assert.True(host.SplitHole(baseAddress, Segment));
            Assert.True(store.TryMapReservedRange(baseAddress, Segment, Segment, HostPageProtection.ReadWrite, out _));
            Assert.True(store.TryWriteSingleView(baseAddress + 16, [1, 2, 3, 4]));
            var bytes = new byte[4];
            Assert.True(store.TryReadSingleView(baseAddress + 16, bytes));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
            Assert.True(store.TryCopySingleView(baseAddress + 64, baseAddress + 16, 4));
            Assert.True(store.TryReadBacking(baseAddress + 64, bytes));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);

            var entered = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                Assert.True(store.TryEnterAliasAccess());
                entered.Set();
            });
            holder.Start();
            entered.Wait();
            holder.Join();

            var disposed = Task.Run(store.Dispose);
            Assert.False(disposed.Wait(TimeSpan.FromMilliseconds(200)), "Dispose returned with an alias access in flight");
            var leaver = new Thread(store.ExitAliasAccess);
            leaver.Start();
            leaver.Join();
            Assert.True(disposed.Wait(TimeSpan.FromSeconds(5)), "Dispose did not see the alias access leave");
            Assert.False(store.TryWriteSingleView(baseAddress + 16, [5]));
            entered.Dispose();
            Assert.True(host.JoinHoles(baseAddress, hole));
            Assert.True(host.FreeHole(baseAddress, hole));
        }
        finally
        {
            SharedBackingViews.StripedAccessCountDefault = previous;
        }
    }
}
