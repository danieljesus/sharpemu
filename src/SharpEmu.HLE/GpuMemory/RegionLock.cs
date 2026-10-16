// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.GpuMemory;

// Spin lock that runs in the fault handler; it must not allocate or block in the kernel.
public sealed class RegionLock
{
    public enum Category
    {
        MemoryTracker,
        ImageCache,
    }

    private readonly Category _category;
    private int _held;
    private int _owner;
    private long _holdStarted;
    private GpuMemoryAccessProfile.Operation _holdOperation;

    public RegionLock(Category category = Category.MemoryTracker) => _category = category;

    // [local] SHARPEMU_PROFILE_RESOLVE=1: contended waits per category, the probe thread's (the render thread) apart
    // from the others. Only the contended path pays for it.
    public static readonly bool WaitProbe = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RESOLVE") == "1";
    [ThreadStatic] private static bool t_probeThread;
    private static readonly long[] ProbeWaits = new long[4];
    private static readonly long[] ProbeWaitTicks = new long[4];

    public static void MarkProbeThread() => t_probeThread = true;

    private static void RecordProbeWait(Category category, long ticks)
    {
        var slot = (int)category * 2 + (t_probeThread ? 0 : 1);
        Interlocked.Increment(ref ProbeWaits[slot]);
        Interlocked.Add(ref ProbeWaitTicks[slot], ticks);
    }

    // (waits, ticks) per category: [category * 2] = probe thread, [category * 2 + 1] = every other thread; resets them.
    public static void TakeProbeWaits(Span<long> waits, Span<long> ticks)
    {
        for (var index = 0; index < ProbeWaits.Length; index++)
        {
            waits[index] = Interlocked.Exchange(ref ProbeWaits[index], 0);
            ticks[index] = Interlocked.Exchange(ref ProbeWaitTicks[index], 0);
        }
    }

    public readonly ref struct Held
    {
        private readonly RegionLock _lock;

        public Held(RegionLock regionLock)
        {
            _lock = regionLock;
            regionLock.Enter();
        }

        public void Dispose() => _lock.Exit();
    }

    public Held Hold() => new(this);

    public void Enter()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _owner) == thread)
        {
            PageGuard.OnFatal("Cannot acquire the region lock twice on the same thread.");
            return;
        }

        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            var waitStarted = GpuMemoryAccessProfile.Enabled || WaitProbe ? Stopwatch.GetTimestamp() : 0;
            do
            {
                Thread.SpinWait(1);
            }
            while (Interlocked.CompareExchange(ref _held, 1, 0) != 0);
            if (GpuMemoryAccessProfile.Enabled)
                GpuMemoryAccessProfile.RecordLockWait(_category, Stopwatch.GetTimestamp() - waitStarted);
            if (WaitProbe)
                RecordProbeWait(_category, Stopwatch.GetTimestamp() - waitStarted);
        }

        Volatile.Write(ref _owner, thread);
        if (GpuMemoryAccessProfile.Enabled)
        {
            _holdOperation = GpuMemoryAccessProfile.GetLockOperation(_category, hold: true);
            _holdStarted = Stopwatch.GetTimestamp();
        }
    }

    public void Exit()
    {
        if (Volatile.Read(ref _owner) != Environment.CurrentManagedThreadId)
        {
            PageGuard.OnFatal("Only the thread that holds the region lock can release it.");
            return;
        }

        // Copy measurements before release; the next owner can then replace the lock's fields.
        var holdOperation = _holdOperation;
        var holdTicks = GpuMemoryAccessProfile.Enabled ? Stopwatch.GetTimestamp() - _holdStarted : 0;
        Volatile.Write(ref _owner, 0);
        Volatile.Write(ref _held, 0);
        if (GpuMemoryAccessProfile.Enabled)
            GpuMemoryAccessProfile.RecordLockHold(holdOperation, holdTicks);
    }
}
