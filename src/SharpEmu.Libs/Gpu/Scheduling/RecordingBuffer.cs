// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public enum RecordedOperation : uint
{
    DispatchDirect,
    DrawIndex,
    DrawIndexAuto,
    EopWrite,
    EopInterrupt,
    EopWriteBack,
    EopFlip,
    EopWriteBackFlip,
    EopOnlyFlip,
    Unknown,
}

public sealed class RecordingBuffer
{
    private readonly IGpuTickDevice _device;
    private readonly IRenderingState _rendering;
    private SubmissionContext? _context;
    private bool _handleWasRequested;

    internal nint Buffer;

    internal RecordingBuffer(SubmissionScheduler owner, IGpuTickDevice device, IRenderingState rendering)
    {
        Owner = owner;
        _device = device;
        _rendering = rendering;
    }

    public SubmissionScheduler Owner { get; }

    public uint DebugOp { get; private set; }

    public ulong DebugSubmitId { get; private set; }

    public uint DebugArg0 { get; private set; }

    public uint DebugArg1 { get; private set; }

    public uint DebugArg2 { get; private set; }

    public uint DebugArg3 { get; private set; }

    public ulong DebugArg4 { get; private set; }

    public bool IsInvalid => Buffer == 0;

    internal bool HasPendingCommands => _handleWasRequested || _rendering.IsRendering;

    // The native handle stays valid only until the next submission.
    public nint Handle
    {
        get
        {
            if (IsInvalid)
            {
                throw SubmissionScheduler.Fatal("The command buffer is not recording.");
            }

            // A caller can record native commands after it obtains the handle.
            _handleWasRequested = true;
            if (Vulkan.VkCallProbe.Enabled) // [local]
            {
                Vulkan.VkCallProbe.CountHandle();
            }

            return Buffer;
        }
    }

    public SubmissionContext Context => _context ?? throw SubmissionScheduler.Fatal("The command buffer has no submission context.");

    public void SetDebugInfo(uint op, ulong submitId, uint arg0 = 0, uint arg1 = 0, uint arg2 = 0, uint arg3 = 0, ulong arg4 = 0)
    {
        DebugOp = op;
        DebugSubmitId = submitId;
        DebugArg0 = arg0;
        DebugArg1 = arg1;
        DebugArg2 = arg2;
        DebugArg3 = arg3;
        DebugArg4 = arg4;
        _opHistory[(int)(_opHistoryNext++ & (OpHistorySize - 1))] = new RecordedOp(Owner.CurrentTick, op, arg4, arg0, arg1, arg2); // [local]
    }

    // [local] GpuStallReport: the last operations recorded and the tick each went into. Written by the recording
    // thread only; a report reads it racily, which is enough to name the shaders in a tick the GPU does not finish.
    private const int OpHistorySize = 8192;
    private readonly record struct RecordedOp(ulong Tick, uint Op, ulong Shader, uint Arg0, uint Arg1, uint Arg2);
    private readonly RecordedOp[] _opHistory = new RecordedOp[OpHistorySize];
    private long _opHistoryNext;

    internal string DescribeTick(ulong tick)
    {
        var counts = new Dictionary<(uint Op, ulong Shader), (int Count, uint Arg0, uint Arg1, uint Arg2)>();
        var seen = 0;
        foreach (var entry in _opHistory)
        {
            if (entry.Tick != tick)
            {
                continue;
            }

            seen++;
            var key = (entry.Op, entry.Shader);
            var known = counts.GetValueOrDefault(key);
            counts[key] = (known.Count + 1, entry.Arg0, entry.Arg1, entry.Arg2);
        }

        return $"ops_in_tick_{tick}={seen} " + string.Join(" ", counts.OrderByDescending(pair => pair.Value.Count).Take(24).Select(pair =>
            $"{(RecordedOperation)pair.Key.Op}:0x{pair.Key.Shader:X}x{pair.Value.Count}(last={pair.Value.Arg0},{pair.Value.Arg1},{pair.Value.Arg2})"));
    }

    public void EndRendering()
    {
        if (_rendering.IsRendering)
        {
            _rendering.EndRendering();
        }
    }

    internal void Bind(SubmissionContext context) => _context = context;

    internal void Begin()
    {
        if (_rendering.IsRendering || IsInvalid)
        {
            throw SubmissionScheduler.Fatal("Cannot start recording while rendering is active or the command buffer is missing.");
        }

        _device.BeginBuffer(Buffer);
        _handleWasRequested = false;
        SetDebugInfo((uint)RecordedOperation.Unknown, 0);
    }

    internal void End()
    {
        EndRendering();
        _device.EndBuffer(Handle);
    }
}
