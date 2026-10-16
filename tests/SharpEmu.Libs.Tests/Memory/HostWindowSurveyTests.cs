// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory;

public sealed class HostWindowSurveyTests
{
    private sealed class ScriptedHostMemory : IHostMemory
    {
        private readonly List<(ulong Base, ulong AllocationBase, ulong Size, HostRegionState State)> _regions = new();
        public List<(ulong Address, ulong Size)> Reserves { get; } = new();
        public List<ulong> Frees { get; } = new();
        public Func<ulong, ulong>? ReserveResult { get; init; }

        public ScriptedHostMemory Add(ulong address, ulong allocationBase, ulong size, HostRegionState state)
        {
            _regions.Add((address, allocationBase, size, state));
            _regions.Sort((a, b) => a.Base.CompareTo(b.Base));
            return this;
        }

        public ulong Allocate(ulong address, ulong size, HostPageProtection protection) => 0;

        public ulong Reserve(ulong address, ulong size, HostPageProtection protection)
        {
            Reserves.Add((address, size));
            return ReserveResult?.Invoke(address) ?? address;
        }

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address)
        {
            Frees.Add(address);
            return true;
        }

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint previous)
        {
            previous = 0;
            return false;
        }

        public bool ProtectRaw(ulong address, ulong size, uint protection, out uint previous)
        {
            previous = 0;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            var next = ulong.MaxValue;
            foreach (var region in _regions)
            {
                if (address >= region.Base && address < region.Base + region.Size)
                {
                    info = new HostRegionInfo(region.Base, region.AllocationBase, region.Size, region.State,
                        0, HostPageProtection.ReadWrite, 0, 0);
                    return true;
                }

                if (region.Base > address)
                {
                    next = Math.Min(next, region.Base);
                }
            }

            info = new HostRegionInfo(address, 0, next - address, HostRegionState.Free, 0x10000,
                HostPageProtection.NoAccess, 0, 0);
            return true;
        }
    }

    private const ulong Start = 0x10_0000_0000;
    private const ulong End = 0x20_0000_0000;

    // Heap-like block (3 runs) and a thread stack (3 runs) at a random base, as in the startup cluster.
    private static ScriptedHostMemory Cluster(ulong at) => new ScriptedHostMemory()
        .Add(at, at, 0x1000, HostRegionState.Reserved)
        .Add(at + 0x1000, at, 0x11000, HostRegionState.Committed)
        .Add(at + 0x12000, at, 0x1EE000, HostRegionState.Reserved)
        .Add(at + 0x200000, at + 0x200000, 0x17A000, HostRegionState.Reserved)
        .Add(at + 0x37A000, at + 0x200000, 0x3000, HostRegionState.Committed)
        .Add(at + 0x37D000, at + 0x200000, 0x3000, HostRegionState.Committed);

    [Fact]
    public void SurveyGroupsForeignRunsByAllocationBase()
    {
        const ulong at = 0x14_CC40_0000;
        var host = Cluster(at).Add(0x18_0000_0000, 0x18_0000_0000, 0x10000, HostRegionState.Reserved);

        var survey = HostWindowSurvey.Survey(host, Start, End,
            (address, _) => address == 0x18_0000_0000, _ => false);

        Assert.Equal(6, survey.Runs);
        Assert.Equal(0x380000UL, survey.Bytes);
        Assert.Equal(0x17000UL, survey.CommittedBytes);
        Assert.Equal(2, survey.Allocations.Count);
        Assert.Equal(new HostAllocationSummary(at, at, at + 0x200000, 0x200000, 0x11000, 3, false), survey.Allocations[0]);
        Assert.Equal(new HostAllocationSummary(at + 0x200000, at + 0x200000, at + 0x380000, 0x180000, 0x6000, 3, false),
            survey.Allocations[1]);
        var text = HostWindowSurvey.Format(survey);
        Assert.Contains("foreign_regions=6 foreign_bytes=0x380000 foreign_allocations=2 foreign_committed=0x17000", text);
        Assert.Contains($"0x{at:X}@0x{at:X}+0x200000:b0x200000:c0x11000:r3", text);
    }

    [Fact]
    public void SurveyListsEveryAllocationWithoutACap()
    {
        var host = new ScriptedHostMemory();
        for (ulong i = 0; i < 40; i++)
        {
            host.Add(Start + i * 0x100000, Start + i * 0x100000, 0x10000, HostRegionState.Reserved);
        }

        var survey = HostWindowSurvey.Survey(host, Start, End, (_, _) => false, _ => false);

        Assert.Equal(40, survey.Allocations.Count);
        var text = HostWindowSurvey.Format(survey);
        Assert.Contains($"0x{Start + 39 * 0x100000:X}@", text);
        Assert.DoesNotContain("...", text);
    }

    [Fact]
    public void SurveyCountsOnlyBytesOutsideTheBaselineFootprint()
    {
        const ulong at = 0x30_0000_0000;
        var startup = HostWindowSurvey.Survey(Cluster(at), Start, 0x40_0000_0000, (_, _) => false, _ => false);
        var baseline = startup.Allocations.Select(a => (a.Start, a.End)).ToList();
        // A different allocation reuses a freed stack slot, and a new 64 KiB stack lands right after the cluster.
        var later = new ScriptedHostMemory()
            .Add(at, at, 0x200000, HostRegionState.Committed)
            .Add(at + 0x200000, at + 0x200000, 0x180000, HostRegionState.Reserved)
            .Add(at + 0x380000, at + 0x380000, 0x10000, HostRegionState.Reserved);

        var survey = HostWindowSurvey.Survey(later, Start, 0x40_0000_0000, (_, _) => false, _ => false, baseline);

        Assert.Equal(0x10000UL, survey.BytesOutsideBaseline);
        Assert.Equal(0x390000UL, survey.Bytes);
    }

    [Fact]
    public void SurveyMarksSharpEmuAllocations()
    {
        var host = new ScriptedHostMemory().Add(Start, Start, 0x10000, HostRegionState.Committed);
        var survey = HostWindowSurvey.Survey(host, Start, End, (_, _) => false, allocationBase => allocationBase == Start);
        Assert.True(survey.Allocations.Single().Tracked);
        Assert.EndsWith(":sharpemu]", HostWindowSurvey.Format(survey));
    }

    [Theory]
    [InlineData("0x14CC400000", 0x14CC400000UL, 0x200000UL, 0UL)]
    [InlineData("0x14CC400000:0x800000", 0x14CC400000UL, 0x800000UL, 0UL)]
    [InlineData(" 0x14_CC40_0000 : 0x200000 : 0x10000 ", 0x14CC400000UL, 0x200000UL, 0x10000UL)]
    [InlineData("88250000000:65536", 88250000000UL, 0x10000UL, 0UL)]
    public void BlockerParsesAddressSizeAndCommittedHead(string text, ulong address, ulong size, ulong commit)
    {
        Assert.True(HostWindowSurvey.TryParseBlockers(text, out var blockers));
        Assert.Equal((address, size, commit), Assert.Single(blockers));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("junk")]
    [InlineData("0")]
    [InlineData("0x1000:0")]
    [InlineData("0x1000:0x1000:0x2000")]
    [InlineData("0x1000:0x1000:0x10:0x10")]
    [InlineData("0x1000,bad")]
    public void BlockerRejectsMalformedValues(string? text)
    {
        Assert.False(HostWindowSurvey.TryParseBlockers(text, out var blockers));
        Assert.Empty(blockers);
    }

    [Fact]
    public void BlockerParsesSeveralEntries()
    {
        Assert.True(HostWindowSurvey.TryParseBlockers("0x14CC400000:0x200000,0x14E9600000:0x100000:0x10000", out var blockers));
        Assert.Equal(new List<(ulong, ulong, ulong)> { (0x14CC400000, 0x200000, 0), (0x14E9600000, 0x100000, 0x10000) }, blockers);
    }

    [Fact]
    public void BlockerThatMissesItsAddressIsFreedAndReported()
    {
        var host = new ScriptedHostMemory { ReserveResult = address => address == 0x14CC400000 ? address : 0x7000_0000 };
        var log = new List<string>();

        var placed = HostWindowSurvey.ApplyTestHostBlockers(host, "0x14CC400000:0x200000:0x10000,0x14E9600000", log.Add);

        Assert.Equal((0x14CC400000UL, 0x200000UL), Assert.Single(placed));
        Assert.Equal(0x7000_0000UL, Assert.Single(host.Frees));
        Assert.Contains(log, line => line.Contains("placed 0x14CC400000+0x200000 committed_head=0x10000"));
        Assert.Contains(log, line => line.StartsWith("[LOADER][ERROR]") && line.Contains("0x14E9600000"));
    }

    [Fact]
    public void UnparsableBlockerIsReportedAndPlacesNothing()
    {
        var host = new ScriptedHostMemory();
        var log = new List<string>();
        Assert.Empty(HostWindowSurvey.ApplyTestHostBlockers(host, "nope", log.Add));
        Assert.Empty(host.Reserves);
        Assert.Contains("cannot parse", Assert.Single(log));
        Assert.Empty(HostWindowSurvey.ApplyTestHostBlockers(host, null, log.Add));
        Assert.Single(log);
    }

    [Theory]
    [InlineData(null, 0x1000UL, 0x1000UL, false)]
    [InlineData("", 0x1000UL, 0x1000UL, false)]
    [InlineData("off", 0x1000UL, 0x1000UL, false)]
    [InlineData("fail", 0x1000UL, 0UL, false)]
    [InlineData("fail", 0x1000UL, 0x1000UL, true)]
    [InlineData(" FAIL ", 0x1000UL, 0x1000UL, true)]
    [InlineData("fail-any", 0x1000UL, 0UL, true)]
    [InlineData("fail-any", 0UL, 0UL, false)]
    public void FixedMapConflictFailsOnlyWhenTheModeCoversTheForeignMemory(string? mode, ulong bytes, ulong committed, bool fail)
    {
        Assert.Equal(fail, KernelMemoryCompatExports.ShouldFailOnForeignHostMemory(mode, new ForeignHostMemory(bytes, committed)));
    }

    [Fact]
    public void ChildAddressSpaceIsUnchangedWithoutTheToggle()
    {
        Assert.False(MitigatedChildAddressSpace.Resolve(_ => null).IsActive);
        Assert.False(MitigatedChildAddressSpace.Resolve(Env(("SHARPEMU_CHILD_ASLR", "high"))).IsActive);
        Assert.Equal(default, MitigatedChildAddressSpace.Resolve(Env(("SHARPEMU_CHILD_GC_REGION_RANGE", "0x1000"))));
    }

    [Fact]
    public void ChildAddressSpaceLowTurnsOffBottomUpAndHighEntropyAslr()
    {
        var plan = MitigatedChildAddressSpace.Resolve(Env(("SHARPEMU_CHILD_ASLR", "Low")));
        Assert.True(plan.IsActive);
        Assert.Equal(0x220000UL, plan.Policy1Bits);
        Assert.Equal(MitigatedChildAddressSpace.DefaultGcRegionRange, plan.GcRegionRange);
    }

    [Theory]
    [InlineData(null, "0x40000000", "0x40000000")]
    [InlineData(null, "1073741824", "0x40000000")]
    [InlineData(null, "keep", null)]
    [InlineData(null, "junk", MitigatedChildAddressSpace.DefaultGcRegionRange)]
    [InlineData("0x2000000000", "0x40000000", null)]
    public void ChildAddressSpaceGcRangeRespectsExplicitSettings(string? dotnet, string? configured, string? expected)
    {
        var plan = MitigatedChildAddressSpace.Resolve(Env(
            ("SHARPEMU_CHILD_ASLR", "low"), ("DOTNET_GCRegionRange", dotnet), ("SHARPEMU_CHILD_GC_REGION_RANGE", configured)));
        Assert.Equal(expected, plan.GcRegionRange);
        Assert.Equal(0x220000UL, plan.Policy1Bits);
    }

    // A plain host reservation (never a placeholder) inside the probe range is foreign to the guest space.
    [Fact]
    public void ForeignHostMemoryReportsAPlainReservationAndItsCommittedHead()
    {
        if (!OperatingSystem.IsWindows()) return;
        var views = HostViewMemory.Create();
        var memoryHost = PlatformMemory;
        using var memory = new PhysicalVirtualMemory(memoryHost, views, BackingSize);
        var granularity = views.Granularity;
        var address = ProbeGuestAddress(views, 4 * granularity);
        var placed = HostWindowSurvey.ApplyTestHostBlockers(memoryHost,
            $"0x{address:X}:0x{2 * granularity:X}:0x{granularity:X}", _ => { });
        Assert.Equal((address, 2 * granularity), Assert.Single(placed));
        try
        {
            Assert.Equal(new ForeignHostMemory(2 * granularity, granularity), memory.ForeignHostMemoryIn(address, 4 * granularity));
            var description = memory.DescribeRangeForDiagnostics(address, 4 * granularity);
            Assert.Contains($"0x{address:X}+0x{granularity:X}:Committed:alloc=0x{address:X}", description);
            Assert.Contains("owned=False:sharpemu=False", description);
            Assert.Equal(default, memory.ForeignHostMemoryIn(address + 2 * granularity, 2 * granularity));
        }
        finally
        {
            Assert.True(memoryHost.Free(address));
        }
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "low", true)]
    [InlineData("0", "low", false)]
    [InlineData("1", null, true)]
    [InlineData("yes", "low", false)]
    public void EbootGuardFollowsTheLowAslrToggleUnlessSetExplicitly(string? guard, string? aslr, bool expected)
    {
        Assert.Equal(expected, HostWindowSurvey.ShouldGuardEbootBand(Env(("SHARPEMU_EBOOT_GUARD", guard), ("SHARPEMU_CHILD_ASLR", aslr))));
    }

    [Fact]
    public void EbootGuardCoversTheBandBetweenTheEbootBaseAndTheWindow()
    {
        var host = new ScriptedHostMemory();
        var log = new List<string>();
        Assert.Equal(new HostAddressRange(0x8_0000_0000, 0x8_0000_0000), HostWindowSurvey.ReserveEbootGuard(host, log.Add));
        Assert.Equal((0x8_0000_0000UL, 0x8_0000_0000UL), Assert.Single(host.Reserves));
        Assert.StartsWith("[LOADER][INFO] Eboot guard reserved", Assert.Single(log));
    }

    [Fact]
    public void EbootGuardThatCannotTakeTheBandNamesTheOccupant()
    {
        var host = new ScriptedHostMemory { ReserveResult = _ => 0x9_0000_0000 }
            .Add(0x4_7FFF_0000, 0x4_7FFF_0000, 0x4_1C00_0000, HostRegionState.Committed);
        var log = new List<string>();
        Assert.Null(HostWindowSurvey.ReserveEbootGuard(host, log.Add));
        Assert.Equal(0x9_0000_0000UL, Assert.Single(host.Frees));
        Assert.Contains("alloc=0x47FFF0000", Assert.Single(log));
    }

    private static Func<string, string?> Env(params (string Name, string? Value)[] values) =>
        name => values.FirstOrDefault(value => value.Name == name).Value;
}
