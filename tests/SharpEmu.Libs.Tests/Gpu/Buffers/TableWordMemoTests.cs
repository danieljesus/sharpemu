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
}
