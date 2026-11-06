// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HotFlagsCollection
{
    public const string Name = "HotFlags";
}

// [local] SHARPEMU_HOTFLAGS_FILE: the control file's parser, its change detection and the switches it changes.
[Collection(HotFlagsCollection.Name)]
public sealed class HotFlagsTests
{
    private sealed class Fake
    {
        public int Value;
        public int Sets;
    }

    private static (HotFlagSet Set, Fake Mode, Fake Count) FakeSet()
    {
        var mode = new Fake();
        var count = new Fake { Value = 4 };
        var set = new HotFlagSet(
        [
            new HotFlag("MODE", 0, 2, false, () => mode.Value, value => { mode.Value = value; mode.Sets++; }),
            new HotFlag("COUNT", 0, 32, true, () => count.Value, value => { count.Value = value; count.Sets++; }),
        ]);
        return (set, mode, count);
    }

    [Fact]
    public void ParserTakesBothNameFormsSkipsCommentsAndKeepsTheLastValue()
    {
        var entries = HotFlagSet.Parse("# header\n\nSHARPEMU_MODE=1\r\n count = 7  # inline\nnoequals\n=3\nmode=2\n");

        Assert.Equal(
        [
            new KeyValuePair<string, string>("COUNT", "7"),
            new KeyValuePair<string, string>("MODE", "2"),
        ], entries);
    }

    [Fact]
    public void AnAppliedChangeIsLoggedWithTheFrameAndNotAppliedAgain()
    {
        var (set, mode, count) = FakeSet();
        var log = new List<string>();

        Assert.Equal(2, set.Apply("MODE=1\nCOUNT=99\n", 120, log.Add));
        Assert.Equal(1, mode.Value);
        Assert.Equal(32, count.Value);
        Assert.Equal(["[HOTFLAG] MODE=0->1 frame=120", "[HOTFLAG] COUNT=4->32 frame=120"], log);

        log.Clear();
        Assert.Equal(0, set.Apply("MODE=1\nCOUNT=99\n", 130, log.Add));
        Assert.Equal(1, mode.Sets);
        Assert.Empty(log);

        Assert.Equal(1, set.Apply("MODE=0\nCOUNT=99\n", 140, log.Add));
        Assert.Equal(["[HOTFLAG] MODE=1->0 frame=140"], log);
    }

    [Fact]
    public void UnknownNamesAndInvalidValuesAreRejected()
    {
        var (set, mode, _) = FakeSet();
        var log = new List<string>();

        Assert.Equal(0, set.Apply("SHARPEMU_OTHER=1\nMODE=3\nMODE2=x\n", 1, log.Add));
        Assert.Equal(0, mode.Sets);
        Assert.Equal(
        [
            "[HOTFLAG] rejected OTHER (not hot-safe)",
            "[HOTFLAG] rejected MODE=3 (invalid, expected 0..2)",
            "[HOTFLAG] rejected MODE2 (not hot-safe)",
        ], log);

        log.Clear();
        set.Apply("SHARPEMU_OTHER=1\nMODE=3\n", 2, log.Add);
        Assert.Empty(log);
    }

    [Fact]
    public void TheFileIsReadAgainOnlyWhenItsWriteTimeOrSizeChanges()
    {
        var (set, mode, _) = FakeSet();
        var log = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"hotflags-{Guid.NewGuid():N}.txt");
        try
        {
            Assert.False(set.PollFile(path, 0, log.Add));

            File.WriteAllText(path, "MODE=1\n");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.True(set.PollFile(path, 10, log.Add));
            Assert.Equal(1, mode.Value);
            Assert.False(set.PollFile(path, 11, log.Add));

            File.WriteAllText(path, "MODE=2\n");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.False(set.PollFile(path, 12, log.Add));
            Assert.Equal(1, mode.Value);

            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));
            Assert.True(set.PollFile(path, 13, log.Add));
            Assert.Equal(2, mode.Value);
            Assert.Equal(["[HOTFLAG] MODE=0->1 frame=10", "[HOTFLAG] MODE=1->2 frame=13"], log);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheRegistryChangesTheSwitchItNames()
    {
        var registry = HotFlags.Registry();
        Assert.Equal(
        [
            "STAMP_ON_CHANGE", "BUFFER_SOURCE_MEMO", "NATIVE_STATE_REUSE", "FRONT_SPECULATE", "SUBMIT_BATCH",
            "TEXEL_CLEAN_SKIP", "IDLE_FLUSH", "STAGE_MEMO", "TARGET_MEMO_DCC", "ASYNC_GRAPHICS_PIPELINES",
        ], registry.Select(flag => flag.Name));
        Assert.Equal(["STAMP_ON_CHANGE", "NATIVE_STATE_REUSE", "STAGE_MEMO"], registry.Where(flag => flag.Resets).Select(flag => flag.Name));

        var before = VulkanVideoPresenter.IdleFlushDraws;
        try
        {
            var set = new HotFlagSet(registry);
            var log = new List<string>();
            set.Apply($"SHARPEMU_IDLE_FLUSH={before + 3}\n", 7, log.Add);
            Assert.Equal(before + 3, VulkanVideoPresenter.IdleFlushDraws);
            Assert.Equal([$"[HOTFLAG] IDLE_FLUSH={before}->{before + 3} frame=7"], log);
        }
        finally
        {
            VulkanVideoPresenter.IdleFlushDraws = before;
        }
    }

    [Fact]
    public void ChangingStampOnChangeMakesEveryBankForgetItsShadows()
    {
        var banks = new RegisterBanks(static message => new InvalidOperationException(message));
        const uint offset = 0x100;
        Assert.NotEqual(0u, banks.ContextBitsChanged(offset, [5u]));
        Assert.Equal(0u, banks.ContextBitsChanged(offset, [5u]));

        var flag = HotFlags.Registry().Single(entry => entry.Name == "STAMP_ON_CHANGE");
        flag.Set(flag.Get());

        Assert.NotEqual(0u, banks.ContextBitsChanged(offset, [5u]));
        Assert.Equal(0u, banks.ContextBitsChanged(offset, [5u]));
    }

    [Fact]
    public void ChangingStageMemoMovesTheMemoGeneration()
    {
        var generation = VulkanVideoPresenter.StageMemoGeneration;
        var flag = HotFlags.Registry().Single(entry => entry.Name == "STAGE_MEMO");
        flag.Set(flag.Get());
        Assert.NotEqual(generation, VulkanVideoPresenter.StageMemoGeneration);
    }

    [Fact]
    public void WithoutTheFileAPresentChangesNothing()
    {
        Assert.Null(Environment.GetEnvironmentVariable("SHARPEMU_HOTFLAGS_FILE"));
        Assert.False(HotFlags.Enabled);
        var described = new HotFlagSet(HotFlags.Registry()).Describe();
        var frames = HotFlags.PresentedFrames;

        HotFlags.FramePresented();

        Assert.Equal(frames + 1, HotFlags.PresentedFrames);
        Assert.Equal(described, new HotFlagSet(HotFlags.Registry()).Describe());
    }
}
