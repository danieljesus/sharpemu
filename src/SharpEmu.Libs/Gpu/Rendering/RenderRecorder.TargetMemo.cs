// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// [local] SHARPEMU_TARGET_MEMO on the recorder: after the upstream merge the executor only resolves a draw's targets
// and the recorder looks their images up (FindTargets). A target's image lookup is a pure function of its register
// words while no image is created, removed or replaced (the lookup generation), the scheduler tick and the image
// cache's collection tick stay the same (IRenderHost.TargetLookupStamp), so a draw whose registers repeat the previous
// lookup's reuses the image it found. DCC color targets always look up: the host reads their guest metadata on every
// lookup. 1 (default) reuses; 2 verifies (always looks up, compares, reports [PERF][TARGET_MEMO]); 0 off.
internal sealed partial class RenderRecorder
{
    private struct ColorTargetMemo
    {
        public bool Valid;
        public ColorTargetWords Words;
        public uint Mask;
        public ResourceSlotIdentifier Image;
    }

    private readonly ColorTargetMemo[] _colorTargetMemos = new ColorTargetMemo[ContextRegisters.ColorTargetCount];
    private bool _depthTargetMemoValid;
    private DepthTargetWords _depthTargetMemoWords;
    private ResourceSlotIdentifier _depthTargetMemoImage;
    private TargetLookupStamp _targetMemoStamp;

    // Drops every remembered target when the stamp moved since they were taken.
    private void SyncTargetMemoStamp()
    {
        var stamp = _host.TargetLookupStamp;
        if (stamp != _targetMemoStamp)
        {
            _targetMemoStamp = stamp;
            Array.Clear(_colorTargetMemos);
            _depthTargetMemoValid = false;
            RenderExecutor._memoStampChanges++;
        }
    }

    private ResourceSlotIdentifier FindColorTargetImage(ContextRegisters? context, uint slot, ref ImageRequest request)
    {
        if (RenderExecutor.TargetMemoMode == 0 || context is null || OnWorker || slot >= ContextRegisters.ColorTargetCount)
        {
            return _host.FindImage(ref request, exactFormat: false);
        }

        if (request.Description.Metadata.Kind == MetadataKind.Dcc)
        {
            RenderExecutor._memoDcc++;
            return _host.FindImage(ref request, exactFormat: false);
        }

        SyncTargetMemoStamp();
        ref var memo = ref _colorTargetMemos[slot];
        var words = context.ColorTargets[slot];
        var mask = context.RenderTargetMaskForSlot(slot);
        var hit = _targetMemoStamp.IsValid && memo.Valid && memo.Mask == mask && memo.Words == words;
        if (hit)
        {
            RenderExecutor._memoHits++;
            if (RenderExecutor.TargetMemoMode == 1)
            {
                return memo.Image;
            }
        }
        else
        {
            RenderExecutor._memoMisses++;
        }

        var image = _host.FindImage(ref request, exactFormat: false);
        if (hit && image != memo.Image)
        {
            RenderExecutor._memoMismatches++;
            RenderExecutor.ReportTargetMemoMismatch($"color slot={slot} memo={memo.Image} lookup={image}");
        }

        SyncTargetMemoStamp();
        memo = new ColorTargetMemo { Valid = true, Words = words, Mask = mask, Image = image };
        return image;
    }

    private ResourceSlotIdentifier FindDepthTargetImage(ContextRegisters? context, ref ImageRequest request)
    {
        if (RenderExecutor.TargetMemoMode == 0 || context is null || OnWorker)
        {
            return _host.FindImage(ref request, exactFormat: false);
        }

        if (request.Description.Metadata.Kind == MetadataKind.Dcc)
        {
            RenderExecutor._memoDcc++;
            return _host.FindImage(ref request, exactFormat: false);
        }

        SyncTargetMemoStamp();
        var words = context.DepthTarget;
        var hit = _targetMemoStamp.IsValid && _depthTargetMemoValid && _depthTargetMemoWords == words;
        if (hit)
        {
            RenderExecutor._memoHits++;
            if (RenderExecutor.TargetMemoMode == 1)
            {
                return _depthTargetMemoImage;
            }
        }
        else
        {
            RenderExecutor._memoMisses++;
        }

        var image = _host.FindImage(ref request, exactFormat: false);
        if (hit && image != _depthTargetMemoImage)
        {
            RenderExecutor._memoMismatches++;
            RenderExecutor.ReportTargetMemoMismatch($"depth memo={_depthTargetMemoImage} lookup={image}");
        }

        SyncTargetMemoStamp();
        _depthTargetMemoValid = true;
        _depthTargetMemoWords = words;
        _depthTargetMemoImage = image;
        return image;
    }
}
