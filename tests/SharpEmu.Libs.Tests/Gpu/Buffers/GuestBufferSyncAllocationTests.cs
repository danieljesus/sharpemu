// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] Managed bytes per warm ObtainBuffer whose range the CPU rewrote (the dirty-page synchronization: page
// tracker visit, upload range collection, staging copy). GTA V spent ~650 KB/frame here before the visit state and
// the collection were reused; the uploads themselves stay, only their per-call objects go.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferSyncAllocationTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    // SHARPEMU_ALLOC_SURVEY_WAIT_S=<s>: waits before and after the count (for dotnet-trace) and counts 20 times longer.
    private static readonly int WaitSeconds = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ALLOC_SURVEY_WAIT_S"), out var wait) ? wait : 0;
    private static readonly int Rounds = WaitSeconds > 0 ? 1280 : 64;
    private const int PerRound = 32;
    // Larger than a caching page: a smaller read takes the stream ring instead of the synchronization.
    private const ulong Size = 0x8000;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DirtyObtainAllocatesLittle(bool written, bool texel)
    {
        if (fixture.Vulkan is not { } vulkan) return;
        using var harness = new CacheHarness(vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var payload = new byte[Size];
        long bytes = 0;
        long uploads = 0;
        for (var round = 0; round < Rounds * 2; round++)
        {
            if (WaitSeconds > 0 && round == Rounds) Thread.Sleep(WaitSeconds * 1000);
            var measured = harness.Worker.Run(() =>
            {
                long allocated = 0;
                for (var index = 0; index < PerRound; index++)
                {
                    payload[0]++;
                    harness.Store.MarkCpuWrite(address, Size);
                    harness.Memory.TryWrite(address, payload);
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    harness.Cache.ObtainBuffer(address, Size, isWritten: written, isTexelBuffer: texel);
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                }

                return allocated;
            });
            harness.Finish();
            if (round >= Rounds)
            {
                bytes += measured;
                uploads += PerRound;
            }
        }

        if (WaitSeconds > 0) Thread.Sleep(WaitSeconds * 1000);
        // The last obtain carried the CPU's bytes: the loop measured real uploads.
        payload[0] = 0xA5;
        Assert.True(harness.Store.MarkCpuWrite(address, Size));
        harness.Memory.TryWrite(address, payload);
        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, Size, isWritten: written, isTexelBuffer: texel));
        harness.Finish();
        Assert.Equal(payload, harness.ReadBack(buffer, offset, Size));

        var perCall = bytes / (double)uploads;
        output.WriteLine($"written={written} texel={texel}: {perCall:F1} B per dirty obtain");
        harness.Shutdown();
        // A texel read defers its upload: the job and the two lists it takes to the upload worker (~320 B) stay.
        Assert.True(perCall <= (texel ? 384 : 64), $"{perCall:F1} B per dirty obtain");
    }
}
