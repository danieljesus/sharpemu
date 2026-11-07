// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_TABLE_WORD_MEMO: only the allowlisted refit's own written bindings keep a remembered word.
[Collection("TableWordMemo")]
public sealed class TableWordMemoTests
{
    private const ulong Refit = 0x5981037B07E391D5;
    private const ulong Word = 0x6000_0048;

    [Fact]
    public void TheRefitsBindingKeepsTheWord()
    {
        TableWordMemo.ResetForTests();
        TableWordMemo.Store(Word, 7);
        using (TableWordMemo.BeginProgram(Refit))
            TableWordMemo.NoteGpuWrite(Word - 0x48, 0x1000);

        Assert.True(TableWordMemo.TryGet(Word, out var value));
        Assert.Equal(7u, value);
    }

    [Fact]
    public void AFillCopyOrLabelAfterTheRefitDropsTheWord()
    {
        TableWordMemo.ResetForTests();
        TableWordMemo.Store(Word, 7);
        using (TableWordMemo.BeginProgram(Refit))
        {
        }

        // A command-processor fill, copy or label write marked through the written-buffer path, after the dispatch.
        TableWordMemo.NoteGpuWrite(Word, 4);

        Assert.False(TableWordMemo.TryGet(Word, out _));
    }

    [Fact]
    public void AnotherProgramsBindingDropsTheWord()
    {
        TableWordMemo.ResetForTests();
        TableWordMemo.Store(Word, 7);
        using (TableWordMemo.BeginProgram(0x4481C2A89BEA7646))
            TableWordMemo.NoteGpuWrite(Word, 4);

        Assert.False(TableWordMemo.TryGet(Word, out _));
    }

    [Fact]
    public void ACommandWriteUpdatesWhatItCoversAndDropsWhatItSplits()
    {
        TableWordMemo.ResetForTests();
        var updates = TableWordMemo.CommandUpdates;
        TableWordMemo.CommandUpdates = true;
        try
        {
            TableWordMemo.Store(Word, 7);
            TableWordMemo.Store(Word + 4, 8);
            TableWordMemo.NoteCommandWrite(Word, [1, 0, 0, 0, 9, 9]);

            Assert.True(TableWordMemo.TryGet(Word, out var value));
            Assert.Equal(1u, value);
            Assert.False(TableWordMemo.TryGet(Word + 4, out _));
        }
        finally
        {
            TableWordMemo.CommandUpdates = updates;
        }
    }

    // [local] SHARPEMU_TABLE_WORD_MEMO_CP_UPDATE=0: a command write drops what it touches and the probe names it.
    [Fact]
    public void WithoutCommandUpdatesACommandWriteDropsTheWord()
    {
        TableWordMemo.ResetForTests();
        var updates = TableWordMemo.CommandUpdates;
        TableWordMemo.CommandUpdates = false;
        try
        {
            TableWordMemo.Store(Word, 7);
            TableWordMemo.NoteCommandWrite(Word, [7, 0, 0, 0]);

            Assert.False(TableWordMemo.TryGet(Word, out _));
        }
        finally
        {
            TableWordMemo.CommandUpdates = updates;
        }
    }

    [Fact]
    public void AGpuWriteAfterTheCommandWriteStillDrops()
    {
        TableWordMemo.ResetForTests();
        var updates = TableWordMemo.CommandUpdates;
        TableWordMemo.CommandUpdates = true;
        try
        {
            TableWordMemo.Store(Word, 7);
            TableWordMemo.NoteCommandWrite(Word, [1, 0, 0, 0]);
            using (TableWordMemo.BeginProgram(0x0DA37182659C9032))
                TableWordMemo.NoteGpuWrite(Word, 4);

            Assert.False(TableWordMemo.TryGet(Word, out _));
        }
        finally
        {
            TableWordMemo.CommandUpdates = updates;
        }
    }

    // [local] SHARPEMU_TABLE_WORD_MEMO_LEARN: a program whose binding dropped the word k times in a row while the next
    // synchronized read found the same value stops dropping it; a different value restarts the run.
    private const ulong Builder = 0x5F7405747C3FE001;

    private static void DropAndResync(uint synchronized)
    {
        using (TableWordMemo.BeginProgram(Builder))
            TableWordMemo.NoteGpuWrite(Word - 0x28, 0x100);
        if (!TableWordMemo.TryGet(Word, out _))
            TableWordMemo.Store(Word, synchronized);
    }

    private static void WithLearning(int threshold, Action body)
    {
        var learn = TableWordMemo.LearnThreshold;
        TableWordMemo.LearnThreshold = threshold;
        TableWordMemo.ResetForTests();
        try
        {
            body();
        }
        finally
        {
            TableWordMemo.LearnThreshold = learn;
            TableWordMemo.ResetForTests();
        }
    }

    [Fact]
    public void ALearnedWriterKeepsTheWordAfterKSurvivals() => WithLearning(3, () =>
    {
        TableWordMemo.Store(Word, 7);
        DropAndResync(7);
        DropAndResync(7);
        Assert.False(TableWordMemo.IsTrusted(Word, Builder));
        DropAndResync(7);

        Assert.True(TableWordMemo.IsTrusted(Word, Builder));
        using (TableWordMemo.BeginProgram(Builder))
            TableWordMemo.NoteGpuWrite(Word - 0x28, 0x100);
        Assert.True(TableWordMemo.TryGet(Word, out var value));
        Assert.Equal(7u, value);
        TableWordMemo.NoteAnsweredHit(Word, out var speculated);
        Assert.True(speculated);
    });

    [Fact]
    public void ADifferentValueRestartsTheRun() => WithLearning(2, () =>
    {
        TableWordMemo.Store(Word, 7);
        DropAndResync(7);
        DropAndResync(8);
        DropAndResync(8);
        Assert.False(TableWordMemo.IsTrusted(Word, Builder));
        DropAndResync(8);
        Assert.True(TableWordMemo.IsTrusted(Word, Builder));
    });

    [Fact]
    public void LearningIsPerProgramAndPerWord() => WithLearning(1, () =>
    {
        TableWordMemo.Store(Word, 7);
        DropAndResync(7);
        Assert.True(TableWordMemo.IsTrusted(Word, Builder));
        Assert.False(TableWordMemo.IsTrusted(Word + 4, Builder));
        using (TableWordMemo.BeginProgram(0x4481C2A89BEA7646))
            TableWordMemo.NoteGpuWrite(Word, 4);
        Assert.False(TableWordMemo.TryGet(Word, out _));
    });

    [Fact]
    public void AnAuditMismatchBansTheWord() => WithLearning(1, () =>
    {
        TableWordMemo.Store(Word, 7);
        DropAndResync(7);
        Assert.True(TableWordMemo.IsTrusted(Word, Builder));
        TableWordMemo.NoteAudit(Word, 7, 9, speculated: true, checkedOnDevice: true);
        Assert.False(TableWordMemo.TryGet(Word, out _));
        Assert.False(TableWordMemo.IsTrusted(Word, Builder));
        TableWordMemo.Store(Word, 9);
        DropAndResync(9);
        Assert.False(TableWordMemo.IsTrusted(Word, Builder));
    });

    [Fact]
    public void WithoutLearningTheBindingStillDrops() => WithLearning(0, () =>
    {
        TableWordMemo.Store(Word, 7);
        for (var i = 0; i < 4; i++)
            DropAndResync(7);
        using (TableWordMemo.BeginProgram(Builder))
            TableWordMemo.NoteGpuWrite(Word - 0x28, 0x100);
        Assert.False(TableWordMemo.TryGet(Word, out _));
    });
    // [local] SHARPEMU_TABLE_WORD_MEMO_FAST_HIT: one hold answers what TryGet and NoteAnsweredHit answered, with the same
    // audit sampling sequence.
    [Fact]
    public void TryAnswerGivesTheSameValueAndAuditsAsTheTwoCalls()
    {
        var audits = new List<bool>();
        var speculations = new List<bool>();
        TableWordMemo.ResetForTests();
        TableWordMemo.Store(Word, 7);
        for (var hit = 0; hit < 3 * TableWordMemo.AuditRate + 5; hit++)
        {
            Assert.True(TableWordMemo.TryGet(Word, out var value));
            Assert.Equal(7u, value);
            audits.Add(TableWordMemo.NoteAnsweredHit(Word, out var speculated));
            speculations.Add(speculated);
        }

        TableWordMemo.ResetForTests();
        TableWordMemo.Store(Word, 7);
        for (var hit = 0; hit < audits.Count; hit++)
        {
            Assert.True(TableWordMemo.TryAnswer(Word, out var value, out var audit, out var speculated));
            Assert.Equal(7u, value);
            Assert.Equal(audits[hit], audit);
            Assert.Equal(speculations[hit], speculated);
        }

        Assert.False(TableWordMemo.TryAnswer(Word + 4, out _, out var missAudit, out _));
        Assert.False(missAudit);
    }
}
