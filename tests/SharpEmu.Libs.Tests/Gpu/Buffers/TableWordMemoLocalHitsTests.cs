// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// [local] SHARPEMU_TABLE_WORD_MEMO_LOCAL_HITS: a hit the locked path found is answered again from the thread's copy until
// any change of the word's state (value, presence, speculated) bumps its stripe, on any thread; the answers, the hit and
// audit sequence equal the locked path's, and =2 catches a change that skipped its bump.
[Collection("TableWordMemo")]
public sealed class TableWordMemoLocalHitsTests
{
    private const ulong Word = 0x6000_0048;
    private const ulong Writer = 0x4481C2A89BEA7646;

    private sealed class Settings : IDisposable
    {
        private readonly int _local = TableWordMemo.LocalHits;
        private readonly int _learn = TableWordMemo.LearnThreshold;
        private readonly int _audit = TableWordMemo.AuditRate;
        private readonly bool _updates = TableWordMemo.CommandUpdates;

        public Settings(int local, int learn = 8, int audit = 64)
        {
            TableWordMemo.ResetForTests();
            TableWordMemo.LocalHits = local;
            TableWordMemo.LearnThreshold = learn;
            TableWordMemo.AuditRate = audit;
            TableWordMemo.CommandUpdates = true;
        }

        public void Dispose()
        {
            TableWordMemo.LocalHits = _local;
            TableWordMemo.LearnThreshold = _learn;
            TableWordMemo.AuditRate = _audit;
            TableWordMemo.CommandUpdates = _updates;
            TableWordMemo.ResetForTests();
        }
    }

    // The reader's hit path: the local answer, else the locked one (TryGet, NoteHit, NoteAnsweredHit).
    private static (bool Hit, uint Value, bool Audit, bool Speculated, bool Local) Read(ulong address)
    {
        if (TableWordMemo.LocalHits != 0 && TableWordMemo.TryAnswerLocal(address, out var local, out var localAudit, out var localSpeculated))
            return (true, local, localAudit, localSpeculated, true);
        if (!TableWordMemo.TryGet(address, out var value))
            return (false, 0, false, false, false);
        TableWordMemo.NoteHit();
        var audit = TableWordMemo.NoteAnsweredHit(address, out var speculated);
        return (true, value, audit, speculated, false);
    }

    [Fact]
    public void ALockedHitIsAnsweredAgainLocally()
    {
        using var settings = new Settings(local: 1);
        TableWordMemo.Store(Word, 7);
        var first = Read(Word);
        Assert.True(first.Hit);
        Assert.False(first.Local);
        Assert.Equal(7u, first.Value);
        var again = Read(Word);
        Assert.True(again.Local);
        Assert.Equal(7u, again.Value);
    }

    [Fact]
    public void WithoutTheSwitchNothingIsAnsweredLocally()
    {
        using var settings = new Settings(local: 0);
        TableWordMemo.Store(Word, 7);
        Read(Word);
        Assert.False(TableWordMemo.TryAnswerLocal(Word, out _, out _, out _));
    }

    [Fact]
    public void ANewValueMakesTheCopyStale()
    {
        using var settings = new Settings(local: 1);
        TableWordMemo.Store(Word, 7);
        Read(Word);
        TableWordMemo.Store(Word, 9);
        var read = Read(Word);
        Assert.False(read.Local);
        Assert.Equal(9u, read.Value);
        Assert.True(Read(Word).Local);
    }

    [Fact]
    public void AnUntrustedGpuWriteDropsTheCopy()
    {
        using var settings = new Settings(local: 1);
        TableWordMemo.Store(Word, 7);
        Read(Word);
        using (TableWordMemo.BeginProgram(Writer))
            TableWordMemo.NoteGpuWrite(Word, 4);
        Assert.False(TableWordMemo.TryAnswerLocal(Word, out _, out _, out _));
        Assert.False(Read(Word).Hit);
    }

    // The case that could make it inexact: a CPU write fault on a guest thread drops the word while the reading thread holds
    // a copy. The drop bumps the stripe under the lock before the write returns, so the reader that runs after it misses.
    [Fact]
    public void ACpuWriteOnAnotherThreadDropsTheCopy()
    {
        using var settings = new Settings(local: 1);
        TableWordMemo.Store(Word, 7);
        Read(Word);
        Assert.True(Read(Word).Local);
        var writer = new Thread(() => TableWordMemo.NoteOtherWrite(Word, 4));
        writer.Start();
        writer.Join();
        Assert.False(TableWordMemo.TryAnswerLocal(Word, out _, out _, out _));
        Assert.False(Read(Word).Hit);
    }

    [Fact]
    public void ACommandWriteOfTheSameValueKeepsTheCopyAndANewValueDropsIt()
    {
        using var settings = new Settings(local: 1);
        TableWordMemo.Store(Word, 7);
        Read(Word);
        TableWordMemo.NoteCommandWrite(Word, [7, 0, 0, 0]);
        Assert.True(Read(Word).Local);
        TableWordMemo.NoteCommandWrite(Word, [8, 0, 0, 0]);
        var read = Read(Word);
        Assert.False(read.Local);
        Assert.Equal(8u, read.Value);
    }

    // A trusted writer's mark keeps the word but makes it speculated: the copy taken before is stale, the next one carries it.
    [Fact]
    public void LearningThatKeepsAWordUpdatesItsSpeculatedState()
    {
        using var settings = new Settings(local: 1, learn: 1);
        TableWordMemo.Store(Word, 7);
        using (TableWordMemo.BeginProgram(Writer))
            TableWordMemo.NoteGpuWrite(Word, 4); // dropped, pending a survival
        TableWordMemo.Store(Word, 7); // the same value: one survival, learned with k=1
        var first = Read(Word);
        Assert.False(first.Speculated);
        Assert.True(Read(Word).Local);
        using (TableWordMemo.BeginProgram(Writer))
            TableWordMemo.NoteGpuWrite(Word, 4); // kept, now speculated
        var locked = Read(Word);
        Assert.False(locked.Local);
        Assert.True(locked.Speculated);
        var local = Read(Word);
        Assert.True(local.Local);
        Assert.True(local.Speculated);
    }

    // The audit sampling counter advances on local hits as on locked ones: the same hits are audited.
    [Fact]
    public void TheSameHitsAreAudited()
    {
        bool[] Sequence(int local)
        {
            using var settings = new Settings(local, audit: 4);
            TableWordMemo.Store(Word, 7);
            TableWordMemo.Store(Word + 4, 8);
            var audits = new bool[24];
            for (var index = 0; index < audits.Length; index++)
            {
                if (index == 10)
                    TableWordMemo.Store(Word, 11);
                var read = Read(index % 3 == 0 ? Word + 4 : Word);
                Assert.True(read.Hit);
                audits[index] = read.Audit;
            }

            return audits;
        }

        Assert.Equal(Sequence(0), Sequence(1));
    }

    [Fact]
    public void VerifyAnswersLockedAndCatchesAChangeWithoutItsBump()
    {
        using var settings = new Settings(local: 2);
        TableWordMemo.Store(Word, 7);
        Read(Word);
        var before = TableWordMemo.LocalWrongTotal;
        var correct = Read(Word);
        Assert.False(correct.Local);
        Assert.Equal(7u, correct.Value);
        Assert.Equal(before, TableWordMemo.LocalWrongTotal);

        TableWordMemo.OverwriteWithoutBumpForTests(Word, 9);
        var read = Read(Word);
        Assert.False(read.Local);
        Assert.Equal(9u, read.Value);
        Assert.Equal(before + 1, TableWordMemo.LocalWrongTotal);
    }

    [Fact]
    public void ResetMakesEveryCopyStale()
    {
        using var settings = new Settings(local: 1);
        TableWordMemo.Store(Word, 7);
        Read(Word);
        TableWordMemo.ResetForTests();
        Assert.False(TableWordMemo.TryAnswerLocal(Word, out _, out _, out _));
    }
}
