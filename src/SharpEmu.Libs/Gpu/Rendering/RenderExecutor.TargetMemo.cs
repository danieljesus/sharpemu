// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_TARGET_MEMO: consecutive draws mostly render into the same targets. A target's image lookup is a
// pure function of its register words while no image is created, removed or replaced (the lookup generation), the
// scheduler tick (the image's last-access tick) and the image cache's collection tick (its recency) stay the same,
// so the draw reuses the image the previous lookup of the same words returned. DCC color targets always look up:
// the host reads their guest metadata on every lookup. 1 reuses; 2 verifies (always looks up, compares, reports
// [PERF][TARGET_MEMO]); 0 (default) off.
public sealed partial class RenderExecutor
{
    internal static readonly int TargetMemoMode =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TARGET_MEMO"), out var mode) && mode is >= 0 and <= 2 ? mode : 0;

    private struct ColorTargetMemo
    {
        public bool Valid;
        public ColorTargetWords Words;
        public uint Mask;
        public bool ExactFormat;
        public ResourceSlotIdentifier Image;
    }

    private readonly ColorTargetMemo[] _colorTargetMemos = new ColorTargetMemo[ContextRegisters.ColorTargetCount];
    private bool _depthTargetMemoValid;
    private DepthTargetWords _depthTargetMemoWords;
    private ResourceSlotIdentifier _depthTargetMemoImage;
    private TargetLookupStamp _targetMemoStamp;

    private static long _memoHits, _memoMisses, _memoDcc, _memoMismatches, _memoStampChanges;

    public static string? TakeTargetMemoReport()
    {
        if (TargetMemoMode == 0)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"[PERF][TARGET_MEMO] mode={TargetMemoMode} hits={Interlocked.Exchange(ref _memoHits, 0)} misses={Interlocked.Exchange(ref _memoMisses, 0)} " +
            $"dcc_excluded={Interlocked.Exchange(ref _memoDcc, 0)} mismatches={Interlocked.Exchange(ref _memoMismatches, 0)} " +
            $"stamp_changes={Interlocked.Exchange(ref _memoStampChanges, 0)}");
    }

    // Drops every remembered target when the stamp moved since they were taken.
    private void SyncTargetMemoStamp()
    {
        var stamp = _host.TargetLookupStamp;
        if (stamp != _targetMemoStamp)
        {
            _targetMemoStamp = stamp;
            Array.Clear(_colorTargetMemos);
            _depthTargetMemoValid = false;
            _memoStampChanges++;
        }
    }

    private ResourceSlotIdentifier FindColorTargetImage(ContextRegisters context, uint slot, in ColorTargetResolution resolution)
    {
        var request = resolution.Request;
        if (TargetMemoMode == 0)
        {
            return _host.FindImage(ref request, exactFormat: false);
        }

        if (request.Description.Metadata.Kind == MetadataKind.Dcc)
        {
            _memoDcc++;
            return _host.FindImage(ref request, exactFormat: false);
        }

        SyncTargetMemoStamp();
        ref var memo = ref _colorTargetMemos[slot];
        var words = context.ColorTargets[slot];
        var mask = context.RenderTargetMaskForSlot(slot);
        var hit = _targetMemoStamp.IsValid && memo.Valid && !memo.ExactFormat && memo.Mask == mask && memo.Words == words;
        if (hit)
        {
            _memoHits++;
            if (TargetMemoMode == 1)
            {
                return memo.Image;
            }
        }
        else
        {
            _memoMisses++;
        }

        var image = _host.FindImage(ref request, exactFormat: false);
        if (hit && image != memo.Image)
        {
            _memoMismatches++;
            ReportTargetMemoMismatch($"color slot={slot} memo={memo.Image} lookup={image}");
        }

        SyncTargetMemoStamp();
        memo = new ColorTargetMemo { Valid = true, Words = words, Mask = mask, ExactFormat = false, Image = image };
        return image;
    }

    private ResourceSlotIdentifier FindDepthTargetImage(ContextRegisters context, in DepthTargetState depthTarget)
    {
        var request = depthTarget.Target.Request;
        if (TargetMemoMode == 0)
        {
            return _host.FindImage(ref request, exactFormat: false);
        }

        if (request.Description.Metadata.Kind == MetadataKind.Dcc)
        {
            _memoDcc++;
            return _host.FindImage(ref request, exactFormat: false);
        }

        SyncTargetMemoStamp();
        var words = context.DepthTarget;
        var hit = _targetMemoStamp.IsValid && _depthTargetMemoValid && _depthTargetMemoWords == words;
        if (hit)
        {
            _memoHits++;
            if (TargetMemoMode == 1)
            {
                return _depthTargetMemoImage;
            }
        }
        else
        {
            _memoMisses++;
        }

        var image = _host.FindImage(ref request, exactFormat: false);
        if (hit && image != _depthTargetMemoImage)
        {
            _memoMismatches++;
            ReportTargetMemoMismatch($"depth memo={_depthTargetMemoImage} lookup={image}");
        }

        SyncTargetMemoStamp();
        _depthTargetMemoValid = true;
        _depthTargetMemoWords = words;
        _depthTargetMemoImage = image;
        return image;
    }

    private static int _memoMismatchReports;

    private static void ReportTargetMemoMismatch(string what)
    {
        if (Interlocked.Increment(ref _memoMismatchReports) <= 16)
        {
            Console.Error.WriteLine($"[GPU][WARN][TARGET_MEMO] mismatch {what}");
        }
    }
}
