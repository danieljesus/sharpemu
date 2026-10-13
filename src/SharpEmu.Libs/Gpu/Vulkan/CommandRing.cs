// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// [local] SHARPEMU_RECORD_RING (on by default, =0 disables; plan-grabador-lineal-2026-10-12.md §4): the recording thread appends compact
// commands for the scheduler's command buffers to a linear stream of blocks, and a replay thread issues the real
// vkBegin/vkCmd*/vkEnd calls in order. Every vkCmd* of the codebase goes through VkRecord, which appends when the
// command buffer is the one the ring is recording and calls the driver otherwise. The submit thread waits for the
// replay of a buffer's end before it submits that buffer.
internal sealed unsafe partial class CommandRing : IDisposable
{
    // A/B of 2026-10-12, 3+3: 16.96 -> 18.29 FPS, 57.2 -> 53.3 ms/frame, same image, 0 drains per frame.
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_RECORD_RING") != "0";

    // SHARPEMU_RECORD_RING_MERGE=1: the replay thread merges a run of barriers into one vkCmdPipelineBarrier2 when
    // no barrier of the run could chain with an earlier one (its source stages miss their destination stages) and
    // no image or buffer appears twice.
    internal static readonly bool MergeBarriers = Environment.GetEnvironmentVariable("SHARPEMU_RECORD_RING_MERGE") == "1";

    // SHARPEMU_RECORD_RING_CHECK=1: every VkRecord call checks that a record for the ring's buffer comes from the
    // recording thread, and that no command reaches a buffer the ring owns while that buffer is not the one being
    // recorded (a record after its end, or before its begin). A direct vk.Cmd* call that bypasses VkRecord cannot be
    // seen here; the source scan in CommandRingSourceTests covers that.
    internal static readonly bool Check = Environment.GetEnvironmentVariable("SHARPEMU_RECORD_RING_CHECK") == "1";

    // The ring the wrappers append to. One device, one ring.
    internal static CommandRing? Active;

    internal const int BlockSize = 256 * 1024;
    private const int MaxBlocks = 128;
    internal const int MaxPayload = BlockSize / 4;
    // SHARPEMU_RECORD_RING_WAKE: records appended before a parked replay thread is woken (sync points wake it at
    // once). SHARPEMU_RECORD_RING_SPIN_US: how long the replay thread spins on an empty ring before it parks.
    private static readonly int WakeEvery = Math.Max(1, EnvInt("SHARPEMU_RECORD_RING_WAKE", 1));
    private static readonly long SpinTicks = EnvInt("SHARPEMU_RECORD_RING_SPIN_US", 1000) * Stopwatch.Frequency / 1_000_000;
    // [local] SHARPEMU_RECORD_RING_HYBRID=1: the replay thread spins only while the producer is appending (it found
    // records within the last SHARPEMU_RECORD_RING_ACTIVE_US, default 200 µs, and never longer than the spin above);
    // once a park times out with the ring still empty it parks again without spinning. The default spins up to the
    // full spin after every empty ring and again after every 1 ms park, ~75-89 % of a logical CPU in GTA V.
    private static readonly bool Hybrid = Environment.GetEnvironmentVariable("SHARPEMU_RECORD_RING_HYBRID") == "1";
    private static readonly long ActiveTicks = EnvInt("SHARPEMU_RECORD_RING_ACTIVE_US", 200) * Stopwatch.Frequency / 1_000_000;
    private long _lastData;
    private bool _idleSincePark;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;

    private sealed class Block
    {
        public readonly byte* Data = (byte*)NativeMemory.AlignedAlloc(BlockSize, 64);
        public int Published;
        public Block? Next;
    }

    private readonly Vk _vk;
    private readonly ConcurrentQueue<Block> _free = new();
    private readonly List<Block> _all = new();
    private readonly Thread _thread;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _waitLock = new();

    // Producer state.
    private Block _write;
    private int _writePos;
    private int _pendingSize;
    private long _produced;
    private int _sinceWake;
    internal nint ProducerBuffer;
    private int _producerThread;

    // Consumer state.
    private Block _read;
    private int _readPos;
    private long _consumed;
    private nint _current;

    private int _parked;
    private int _waiting;
    private volatile string? _failure;
    private volatile bool _stop;

    internal Silk.NET.Vulkan.Extensions.KHR.KhrPushDescriptor? PushApi;
    internal Silk.NET.Vulkan.Extensions.EXT.ExtColorWriteEnable? ColorWriteApi;

    public CommandRing(Vk vk)
    {
        _vk = vk;
        _write = NewBlock();
        _read = _write;
        _thread = new Thread(Run) { IsBackground = true, Name = "SharpEmu.RecordRing" };
        _thread.Start();
    }

    private Block NewBlock()
    {
        var block = new Block();
        lock (_all)
        {
            _all.Add(block);
        }

        return block;
    }

    // ---- Producer ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* Reserve(RingOp op, int payload)
    {
        var size = (8 + payload + 7) & ~7;
        if (_writePos + size + 8 > BlockSize)
        {
            NextBlock();
        }

        var header = _write.Data + _writePos;
        *(ushort*)header = (ushort)op;
        *(int*)(header + 4) = size;
        _pendingSize = size;
        return header + 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Commit()
    {
        _writePos += _pendingSize;
        _produced++;
        Volatile.Write(ref _write.Published, _writePos);
        if (Volatile.Read(ref _parked) != 0 && ++_sinceWake >= WakeEvery)
        {
            _sinceWake = 0;
            WakeConsumer();
        }
    }

    private void NextBlock()
    {
        var next = TakeBlock();
        _write.Next = next;
        var header = _write.Data + _writePos;
        *(ushort*)header = (ushort)RingOp.NextBlock;
        *(int*)(header + 4) = 8;
        _writePos += 8;
        _produced++;
        Volatile.Write(ref _write.Published, _writePos);
        _write = next;
        _writePos = 0;
    }

    private Block TakeBlock()
    {
        if (_free.TryDequeue(out var block))
        {
            block.Published = 0;
            block.Next = null;
            return block;
        }

        if (_all.Count < MaxBlocks)
        {
            return NewBlock();
        }

        var start = Stopwatch.GetTimestamp();
        WakeConsumerFenced();
        var spin = new SpinWait();
        while (!_free.TryDequeue(out block))
        {
            ThrowIfFailed();
            spin.SpinOnce(sleep1Threshold: -1);
        }

        RingStats.Backpressure(Stopwatch.GetTimestamp() - start);
        block.Published = 0;
        block.Next = null;
        return block;
    }

    internal long Produced => _produced;

    // Begin and end of the scheduler's command buffer, on the recording thread.
    public void BeginBuffer(nint buffer)
    {
        var thread = Environment.CurrentManagedThreadId;
        if (_producerThread != thread)
        {
            if (_producerThread != 0)
            {
                RingStats.ProducerThreadChanged();
            }

            _producerThread = thread;
        }

        var payload = Reserve(RingOp.Begin, sizeof(nint));
        *(nint*)payload = buffer;
        Commit();
        ProducerBuffer = buffer;
        if (Check)
        {
            lock (_owned)
            {
                _owned.Add(buffer);
            }
        }
    }

    private readonly HashSet<nint> _owned = new();

    // SHARPEMU_RECORD_RING_CHECK=1: a record for the ring's buffer from another thread.
    internal void CheckProducerThread()
    {
        if (Environment.CurrentManagedThreadId != _producerThread)
        {
            RingStats.CheckViolation($"a command for the ring's buffer from thread {Environment.CurrentManagedThreadId}, not the recording thread {_producerThread}");
        }
    }

    // SHARPEMU_RECORD_RING_CHECK=1: a command that would go straight to the driver on a buffer the ring records.
    internal void CheckDirect(nint buffer)
    {
        bool owned;
        lock (_owned)
        {
            owned = _owned.Contains(buffer);
        }

        if (owned)
        {
            RingStats.CheckViolation($"a command for ring buffer 0x{buffer:X} while it is not being recorded (recording 0x{ProducerBuffer:X})");
        }
    }

    public long EndBuffer(nint buffer)
    {
        ProducerBuffer = 0;
        var payload = Reserve(RingOp.End, sizeof(nint));
        *(nint*)payload = buffer;
        Commit();
        WakeConsumerFenced();
        return _produced;
    }

    // Wait until the replay thread has issued every record appended so far (the caller is about to touch the
    // command buffer or its pool directly).
    public void Drain(RingDrainReason reason)
    {
        if (Volatile.Read(ref _consumed) >= _produced)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        WaitReplayed(_produced);
        RingStats.Drained(reason, Stopwatch.GetTimestamp() - start);
    }

    // On the recording thread: wait until every record appended so far, including those of the buffer still being
    // recorded, has been issued. WaitAllSubmitted only covers sealed buffers, so teardown and swapchain recreation
    // call this before they destroy objects the open buffer may reference.
    public static void DrainActive(RingDrainReason reason) => Active?.Drain(reason);

    // Any thread: wait until the replay thread has issued records up to this position.
    public void WaitReplayed(long position)
    {
        if (Volatile.Read(ref _consumed) >= position)
        {
            return;
        }

        WakeConsumerFenced();
        var spin = new SpinWait();
        for (var index = 0; index < 64 && Volatile.Read(ref _consumed) < position; index++)
        {
            spin.SpinOnce(sleep1Threshold: -1);
        }

        if (Volatile.Read(ref _consumed) >= position)
        {
            ThrowIfFailed();
            return;
        }

        var waitStart = Stopwatch.GetTimestamp();
        var reports = 0;
        lock (_waitLock)
        {
            _waiting++;
            try
            {
                while (Volatile.Read(ref _consumed) < position && _failure == null)
                {
                    Monitor.Wait(_waitLock, 1);
                    if (Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds >= GpuStallReport.TimeoutMs * (reports + 1) &&
                        reports++ < GpuStallReport.MaxReportsPerWait)
                    {
                        GpuStallReport.Report($"ring replay wait over {GpuStallReport.TimeoutMs / 1000 * reports} s: wait_position={position}");
                    }
                }
            }
            finally
            {
                _waiting--;
            }
        }

        ThrowIfFailed();
    }

    // [local] GpuStallReport: records appended, records replayed, whether the replay thread is parked.
    internal string DescribeStall() =>
        $"ring_produced={_produced} ring_consumed={Volatile.Read(ref _consumed)} ring_parked={Volatile.Read(ref _parked)} ring_waiting={Volatile.Read(ref _waiting)} ring_failure={_failure ?? "none"}";

    private void ThrowIfFailed()
    {
        if (_failure is { } failure)
        {
            throw SubmissionScheduler.Fatal(failure);
        }
    }

    private void WakeConsumer()
    {
        if (Interlocked.Exchange(ref _parked, 0) == 1)
        {
            _wake.Release();
        }
    }

    private void WakeConsumerFenced()
    {
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _parked) != 0)
        {
            WakeConsumer();
        }
    }

    // ---- Consumer ----

    private void Run()
    {
        long consumed = 0;
        try
        {
            for (;;)
            {
                var block = _read;
                var published = Volatile.Read(ref block.Published);
                if (_readPos < published)
                {
                    var start = Stopwatch.GetTimestamp();
                    _idleSincePark = false;
                    var records = 0;
                    while (_readPos < published)
                    {
                        var header = block.Data + _readPos;
                        var op = (RingOp)(*(ushort*)header);
                        var size = *(int*)(header + 4);
                        if (op == RingOp.NextBlock)
                        {
                            var next = block.Next!;
                            _read = next;
                            _readPos = 0;
                            _free.Enqueue(block);
                            consumed++;
                            Volatile.Write(ref _consumed, consumed);
                            block = next;
                            published = Volatile.Read(ref block.Published);
                            continue;
                        }

                        if (op == RingOp.Barrier && MergeBarriers)
                        {
                            var merged = ReplayBarrierRun(block, published, out var run);
                            _readPos += merged;
                            consumed += run;
                            records += run;
                        }
                        else
                        {
                            Execute(op, header + 8);
                            _readPos += size;
                            consumed++;
                            records++;
                        }

                        Volatile.Write(ref _consumed, consumed);
                        if ((records & 63) == 0 && Volatile.Read(ref _waiting) != 0)
                        {
                            SignalWaiters();
                        }
                    }

                    _lastData = Stopwatch.GetTimestamp();
                    RingStats.Replayed(records, _lastData - start);
                    if (Volatile.Read(ref _waiting) != 0)
                    {
                        SignalWaiters();
                    }

                    continue;
                }

                if (_stop)
                {
                    return;
                }

                Idle(block);
            }
        }
        catch (Exception exception)
        {
            var message = $"The record ring replay failed: {exception.Message}";
            _failure = message;
            SignalWaiters();
            try
            {
                SubmissionScheduler.Fatal(message);
            }
            catch
            {
            }
        }
    }

    private void Idle(Block block)
    {
        var spinStart = Stopwatch.GetTimestamp();
        // Hybrid: spin until ActiveTicks after the last records (capped by the spin), not at all after an idle park.
        var spinTicks = !Hybrid ? SpinTicks : _idleSincePark ? 0 : Math.Min(SpinTicks, Math.Max(0, _lastData + ActiveTicks - spinStart));
        do
        {
            for (var index = 0; index < 16; index++)
            {
                if (Volatile.Read(ref block.Published) > _readPos || _stop)
                {
                    return;
                }

                Thread.SpinWait(8);
            }
        }
        while (Stopwatch.GetTimestamp() - spinStart < spinTicks);

        var start = Stopwatch.GetTimestamp();
        Volatile.Write(ref _parked, 1);
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref block.Published) > _readPos || _stop)
        {
            if (Interlocked.Exchange(ref _parked, 0) == 0)
            {
                _wake.Wait();
            }

            return;
        }

        if (!_wake.Wait(1))
        {
            _idleSincePark = true;
            if (Interlocked.Exchange(ref _parked, 0) == 0)
            {
                _wake.Wait();
            }
        }

        RingStats.Parked(Stopwatch.GetTimestamp() - start);
    }

    private void SignalWaiters()
    {
        lock (_waitLock)
        {
            Monitor.PulseAll(_waitLock);
        }
    }

    private void Require(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}.");
        }
    }

    public void Dispose()
    {
        if (Active == this)
        {
            Active = null;
        }

        _stop = true;
        WakeConsumerFenced();
        _wake.Release();
        _thread.Join();
        lock (_all)
        {
            foreach (var block in _all)
            {
                NativeMemory.AlignedFree(block.Data);
            }

            _all.Clear();
        }
    }

    public static IEnumerable<string> TakeReport(double seconds, long frames) => RingStats.TakeReport(seconds, frames);
}

internal enum RingDrainReason
{
    Allocate,
    InlineSubmit,
    Fallback,
    Shutdown,
    Recreate,
    Capture,
    Count,
}

internal static class RingStats
{
    private static long _records, _replayTicks, _parks, _parkTicks, _backpressure, _backpressureTicks, _threadChanges;
    private static readonly long[] _drains = new long[(int)RingDrainReason.Count];
    private static readonly long[] _drainTicks = new long[(int)RingDrainReason.Count];
    private static readonly long[] _fallbacks = new long[(int)RingOp.Count];
    private static long _merged, _mergedCalls;
    private static long _checkViolations;
    private static int _checkReported;

    public static void CheckViolation(string what)
    {
        Interlocked.Increment(ref _checkViolations);
        if (Interlocked.Increment(ref _checkReported) <= 8)
        {
            Console.Error.WriteLine($"[GPU][ERROR][RING_CHECK] {what} stack={Environment.StackTrace}");
        }
    }

    public static void Replayed(int records, long ticks)
    {
        Interlocked.Add(ref _records, records);
        Interlocked.Add(ref _replayTicks, ticks);
    }

    public static void Parked(long ticks)
    {
        Interlocked.Increment(ref _parks);
        Interlocked.Add(ref _parkTicks, ticks);
    }

    public static void Backpressure(long ticks)
    {
        Interlocked.Increment(ref _backpressure);
        Interlocked.Add(ref _backpressureTicks, ticks);
    }

    public static void ProducerThreadChanged() => Interlocked.Increment(ref _threadChanges);

    public static void Drained(RingDrainReason reason, long ticks)
    {
        Interlocked.Increment(ref _drains[(int)reason]);
        Interlocked.Add(ref _drainTicks[(int)reason], ticks);
    }

    public static void Fallback(RingOp op) => Interlocked.Increment(ref _fallbacks[(int)op]);

    public static void Merged(int barriers)
    {
        Interlocked.Add(ref _merged, barriers);
        Interlocked.Increment(ref _mergedCalls);
    }

    public static IEnumerable<string> TakeReport(double seconds, long frames)
    {
        if (CommandRing.Active == null)
        {
            yield break;
        }

        var perFrame = (double)(frames > 0 ? frames : 1);
        var ms = 1000.0 / Stopwatch.Frequency / perFrame;
        var line = new StringBuilder();
        line.Append(string.Create(CultureInfo.InvariantCulture,
            $"[PERF][RING] window_s={seconds:F1} frames={frames} records_per_frame={Interlocked.Exchange(ref _records, 0) / perFrame:F0} " +
            $"replay_ms_per_frame={Interlocked.Exchange(ref _replayTicks, 0) * ms:F2} parks_per_frame={Interlocked.Exchange(ref _parks, 0) / perFrame:F1} " +
            $"park_ms_per_frame={Interlocked.Exchange(ref _parkTicks, 0) * ms:F2} backpressure={Interlocked.Exchange(ref _backpressure, 0)} " +
            $"backpressure_ms_per_frame={Interlocked.Exchange(ref _backpressureTicks, 0) * ms:F2} producer_thread_changes={Interlocked.Exchange(ref _threadChanges, 0)}"));
        long drains = 0;
        double drainMs = 0;
        for (var index = 0; index < (int)RingDrainReason.Count; index++)
        {
            var count = Interlocked.Exchange(ref _drains[index], 0);
            var ticks = Interlocked.Exchange(ref _drainTicks[index], 0);
            drains += count;
            drainMs += ticks * ms;
            if (count != 0)
            {
                line.Append(string.Create(CultureInfo.InvariantCulture,
                    $" drain_{(RingDrainReason)index}={count / perFrame:F2}/{ticks * ms:F3}ms"));
            }
        }

        line.Append(string.Create(CultureInfo.InvariantCulture, $" drains_per_frame={drains / perFrame:F2} drain_ms_per_frame={drainMs:F3}"));
        for (var index = 0; index < (int)RingOp.Count; index++)
        {
            var count = Interlocked.Exchange(ref _fallbacks[index], 0);
            if (count != 0)
            {
                line.Append(string.Create(CultureInfo.InvariantCulture, $" fallback_{(RingOp)index}={count / perFrame:F2}"));
            }
        }

        if (CommandRing.Check)
        {
            line.Append(string.Create(CultureInfo.InvariantCulture, $" check_violations={Interlocked.Exchange(ref _checkViolations, 0)}"));
        }

        var merged = Interlocked.Exchange(ref _merged, 0);
        var mergedCalls = Interlocked.Exchange(ref _mergedCalls, 0);
        if (mergedCalls != 0)
        {
            line.Append(string.Create(CultureInfo.InvariantCulture,
                $" merged_barriers_per_frame={merged / perFrame:F0} merged_calls_per_frame={mergedCalls / perFrame:F0}"));
        }

        yield return line.ToString();
    }
}
