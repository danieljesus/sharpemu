// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

// [local] SHARPEMU_GPU_RESOLVE_PROBE: the static origin of a descriptor's dwords and the gate's arithmetic.
public sealed class GpuResolveProbeTests
{
    private static GpuResolveProbe.Origin Of(ScalarValue value) => GpuResolveProbe.OriginOf(value, new Dictionary<ScalarValue, GpuResolveProbe.Origin>());

    [Fact]
    public void UserDataAndConstantsAreImmediate()
    {
        var word = ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32, ScalarValue.UserData(4), ScalarValue.ConstantOf(0x10u));
        Assert.Equal(GpuResolveProbe.Origin.Immediate, Of(word));
    }

    [Fact]
    public void AScalarLoadThroughAUserDataPointerIsMemory()
    {
        var pointer = ScalarValue.Handle(ScalarValueKind.AddressHandle, [ScalarValue.UserData(0), ScalarValue.UserData(1)]);
        var load = ScalarValue.MemoryRead(ScalarValueKind.ScalarAddressWord, pointer, ScalarValue.ConstantOf(0x20u), 0);
        var word = ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32, load, ScalarValue.UserData(2));
        Assert.Equal(GpuResolveProbe.Origin.Memory, Of(word));
    }

    [Fact]
    public void AFirstLaneAnywhereInTheChainIsAWaterfall()
    {
        var lane = ScalarValue.FirstLane(ScalarValue.UserData(3), ScalarValue.ConstantOf(1u));
        var pointer = ScalarValue.Handle(ScalarValueKind.AddressHandle, [lane, ScalarValue.UserData(1)]);
        var load = ScalarValue.MemoryRead(ScalarValueKind.ScalarAddressWord, pointer, ScalarValue.ConstantOf(0u), 0);
        Assert.Equal(GpuResolveProbe.Origin.Waterfall, Of(load));
    }

    [Fact]
    public void TheGateSavesEachBucketDownToItsFloor()
    {
        var steps = new double[(int)DrawCostProbe.Step.Count];
        steps[(int)DrawCostProbe.Step.ResBuffers] = 3.0; // V
        steps[(int)DrawCostProbe.Step.ResImages] = 2.0; // T
        steps[(int)DrawCostProbe.Step.CommitBuild] = 1.0; // D
        steps[(int)DrawCostProbe.Step.DrawAttachments] = 4.0; // stays
        var gate = GpuResolveProbe.Evaluate(0, [1.0, 0.5, -1, 1.0], steps, 10.0);
        // V saves 3.0 - 0.4, T half of 2.0 - 0.8, W nothing, D 1.0 - 0.2.
        var saved = 2.6 + 0.6 + 0.8;
        Assert.Equal(10.0 - saved, gate.ResidualUs, 6);
        Assert.Equal(10.0 - saved / 2, gate.ResidualCalibrated, 6);
        Assert.Equal((3.0 + 1.0 + 1.0) / 6.0, gate.TranslatableCost, 6);
    }
}
