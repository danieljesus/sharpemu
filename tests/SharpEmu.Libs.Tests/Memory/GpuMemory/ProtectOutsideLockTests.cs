// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

// [local] SHARPEMU_PROTECT_OUTSIDE_LOCK: the deferred protection's kernel call runs without the region lock, and every
// CPU-watch change of the region still lands in the order of the region-lock sections that decided it.
public sealed class ProtectOutsideLockTests
{
    private const ulong Page = TrackerLayout.PageBytes;
    private const ulong Region = TrackerLayout.BlockBytes;
    private const ulong Base = 0x4000_0000;
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Done = TimeSpan.FromSeconds(10);

    // Records every protection call; the call made by BlockThread waits for Release.
    private sealed class BlockingSpace : IGuestAddressSpace, IDisposable
    {
        public void Dispose() => Release.Set();

        public ulong ProtectionPageSize => 4096;

        public List<(ulong Address, ulong Size, GuestPageProtection Protection)> Protects { get; } = new();

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int BlockThread { get; set; } = -1;

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
        {
            if (Environment.CurrentManagedThreadId == BlockThread)
            {
                Entered.Set();
                Release.Wait();
            }

            lock (Protects)
            {
                Protects.Add((address, size, protection));
            }

            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) => throw new NotSupportedException();

        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => throw new NotSupportedException();

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress) => throw new NotSupportedException();

        public bool TryEnsureRangeCommitted(ulong address, ulong size) => throw new NotSupportedException();

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) => throw new NotSupportedException();

        public bool TryFreeGuestMemory(ulong address) => throw new NotSupportedException();
    }

    // The guard is not disposed: the tests leave pages watched, which Dispose rejects.
    private static (PageGuard Guard, GuestPageTracker Tracker) Create(IGuestAddressSpace space, int mode)
    {
        var guard = new PageGuard(space);
        guard.Permissions.Set(Base, Region, ReadWrite);
        GuestPageTracker.ProtectOutsideLock = mode;
        return (guard, new GuestPageTracker(guard));
    }

    // A read upload of the range with deferred protection: the pages are clean and still writable afterwards.
    private static List<TrackedRegion> DeferredUpload(GuestPageTracker tracker, ulong address, ulong size)
    {
        var pending = new List<TrackedRegion>();
        tracker.ForEachUploadRange(address, size, false, static (_, _) => { }, static () => { }, preserveCpuWriteHotPages: false, pending);
        return pending;
    }

    private static Thread StartProtecting(BlockingSpace space, List<TrackedRegion> pending)
    {
        var thread = new Thread(() => GuestPageTracker.ApplyDeferredProtection(pending)) { IsBackground = true };
        space.BlockThread = thread.ManagedThreadId;
        thread.Start();
        Assert.True(space.Entered.Wait(Done));
        return thread;
    }

    [Fact]
    public void TheRegionLockIsFreeDuringTheKernelCall()
    {
        var space = new BlockingSpace();
        var (_, tracker) = Create(space, mode: 1);
        using var __ = space;
        var pending = DeferredUpload(tracker, Base, Page * 2);
        Assert.Single(pending);
        Assert.Empty(space.Protects);
        var protecting = StartProtecting(space, pending);

        // A query under the region lock completes while the protection call is still blocked.
        var query = Task.Run(() => tracker.HasCpuDirtyPages(Base, Page * 2));
        Assert.True(query.Wait(Done));
        Assert.False(query.Result);

        space.Release.Set();
        Assert.True(protecting.Join(Done));
        Assert.Equal([(Base, Page * 2, GuestPageProtection.Read)], space.Protects);
    }

    [Fact]
    public void WithoutTheSwitchTheRegionLockIsHeldDuringTheKernelCall()
    {
        var space = new BlockingSpace();
        var (_, tracker) = Create(space, mode: 0);
        using var __ = space;
        var pending = DeferredUpload(tracker, Base, Page * 2);
        var protecting = StartProtecting(space, pending);

        var query = Task.Run(() => tracker.HasCpuDirtyPages(Base, Page * 2));
        Assert.False(query.Wait(Blocked));

        space.Release.Set();
        Assert.True(protecting.Join(Done));
        Assert.True(query.Wait(Done));
        Assert.False(query.Result);
    }

    [Fact]
    public void AnotherProtectionOfTheRegionWaitsForTheCallInFlight()
    {
        var space = new BlockingSpace();
        var (_, tracker) = Create(space, mode: 1);
        using var __ = space;
        var first = DeferredUpload(tracker, Base, Page);
        var second = DeferredUpload(tracker, Base + Page, Page);
        var protecting = StartProtecting(space, first);

        // The first call protects both pages; the second job has nothing left to protect, but it must not copy its
        // page before that call has landed.
        var other = Task.Run(() => GuestPageTracker.ApplyDeferredProtection(second));
        Assert.False(other.Wait(Blocked));

        space.Release.Set();
        Assert.True(protecting.Join(Done));
        Assert.True(other.Wait(Done));
        Assert.Equal([(Base, Page * 2, GuestPageProtection.Read)], space.Protects);
    }

    [Fact]
    public void APageDirtiedWhileItsProtectionIsInFlightIsUnprotectedAfterIt()
    {
        var space = new BlockingSpace();
        var (_, tracker) = Create(space, mode: 1);
        using var __ = space;
        var pending = DeferredUpload(tracker, Base, Page * 2);
        var protecting = StartProtecting(space, pending);

        var write = Task.Run(() => tracker.MarkCpuDirtyPages(Base + 16, 8));
        Assert.False(write.Wait(Blocked));

        space.Release.Set();
        Assert.True(protecting.Join(Done));
        Assert.True(write.Wait(Done));
        Assert.True(write.Result);
        Assert.Equal(
            [(Base, Page * 2, GuestPageProtection.Read), (Base, Page, ReadWrite)],
            space.Protects);
        Assert.True(tracker.HasCpuDirtyPages(Base, Page));
        Assert.False(tracker.HasCpuDirtyPages(Base + Page, Page));
    }

    [Fact]
    public void TheWatchSequenceMatchesTheLockedPath()
    {
        Assert.Equal(Sequence(mode: 0), Sequence(mode: 1));
        Assert.NotEmpty(Sequence(mode: 1));
    }

    // [local] SHARPEMU_DEFERRED_HOT_PRESERVE: a deferred upload that preserves hot pages lists them for the copy but
    // leaves them dirty and writable, so it makes no protection change for them.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void APreservedHotPageIsCopiedButNotProtected(int mode)
    {
        var space = new BlockingSpace();
        var (_, tracker) = Create(space, mode);
        using var __ = space;
        for (var round = 0; round < 2; round++)
        {
            GuestPageTracker.ApplyDeferredProtection(DeferredUpload(tracker, Base, Page * 2));
            tracker.MarkCpuDirtyPages(Base + 16, 8);
        }

        var protects = space.Protects.Count;
        var uploads = new List<(ulong, ulong)>();
        var pending = new List<TrackedRegion>();
        tracker.ForEachUploadRange(Base, Page * 2, false, (address, size) => uploads.Add((address, size)), static () => { },
            preserveCpuWriteHotPages: true, pending);
        GuestPageTracker.ApplyDeferredProtection(pending);

        Assert.Equal([(Base, Page)], uploads);
        Assert.Equal(protects, space.Protects.Count);
        Assert.True(tracker.HasCpuDirtyPages(Base, Page));
    }

    private static List<(ulong Address, ulong Size, GuestPageProtection Protection)> Sequence(int mode)
    {
        var space = new BlockingSpace();
        var (_, tracker) = Create(space, mode);
        var first = DeferredUpload(tracker, Base, Page * 4);
        tracker.MarkCpuDirtyPages(Base + Page, 8);
        var second = DeferredUpload(tracker, Base + Page * 6, Page * 2);
        GuestPageTracker.ApplyDeferredProtection(first);
        tracker.MarkCpuDirtyPages(Base + Page * 7, 8);
        GuestPageTracker.ApplyDeferredProtection(second);
        tracker.ForEachUploadRange(Base, Page * 8, false, static (_, _) => { }, static () => { }, preserveCpuWriteHotPages: false);
        tracker.MarkCpuDirtyPages(Base + Page * 2, Page * 2);
        var third = DeferredUpload(tracker, Base + Page * 2, Page * 2);
        GuestPageTracker.ApplyDeferredProtection(third);
        return space.Protects;
    }
}
