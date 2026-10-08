// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Only collection capacity survives a lease. Results and graph references do not.
internal sealed class RuntimeEvaluationScratch : IDisposable
{
    [ThreadStatic]
    private static RuntimeEvaluationScratch? _available;

    private RuntimeEvaluationScratch? _nextAvailable;
    private bool _rented;

    internal ScalarValueCache Values { get; } = new();
    internal CompiledValueCache CompiledValues { get; } = new();
    internal List<ScalarValue> Visiting { get; } = [];
    // The roots of one batched evaluation.
    internal List<int> Roots { get; } = [];
    internal Stack<int> PendingBranches { get; } = new();
    private bool[] _visitedBranches = [];

    internal bool[] PrepareBranches(int count)
    {
        if (_visitedBranches.Length < count) _visitedBranches = new bool[count];
        else Array.Clear(_visitedBranches, 0, count);
        PendingBranches.Clear();
        return _visitedBranches;
    }

    internal static RuntimeEvaluationScratch Rent()
    {
        var scratch = _available;
        if (scratch is null)
            scratch = new RuntimeEvaluationScratch();
        else
            _available = scratch._nextAvailable;

        scratch._nextAvailable = null;
        scratch._rented = true;
        return scratch;
    }

    public void Dispose()
    {
        if (!_rented) return;
        Values.Reset();
        CompiledValues.Reset();
        Visiting.Clear();
        Roots.Clear();
        PendingBranches.Clear();
        _rented = false;
        _nextAvailable = _available;
        _available = this;
    }
}

// Plan-local indices avoid hashing graph nodes on every dependency evaluation. The marks and
// the stack carry one evaluation pass over the nodes a draw needs.
internal sealed class CompiledValueCache
{
    private ulong[] _values = [];
    private uint[] _stamps = [];
    private uint[] _marks = [];
    private int[] _stack = new int[64];
    private uint _generation = 1;
    private uint _pass = 1;
    private int _passDepth;
    private int _stackDepth;

    internal ulong[] Values => _values;

    internal int StackDepth => _stackDepth;

    internal void EnsureCapacity(int count)
    {
        if (_values.Length >= count) return;
        Array.Resize(ref _values, count);
        Array.Resize(ref _stamps, count);
        Array.Resize(ref _marks, count);
    }

    internal bool Contains(int index) => _stamps[index] == _generation;

    internal bool TryGet(int index, out ulong value)
    {
        if (_stamps[index] == _generation)
        {
            value = _values[index];
            return true;
        }

        value = 0;
        return false;
    }

    internal void Store(int index, ulong value)
    {
        _values[index] = value;
        _stamps[index] = _generation;
    }

    // A pass marks the nodes it has started; a pass that a running node's callback starts
    // shares those marks, so the nodes in progress stay in progress.
    internal void BeginPass()
    {
        if (_passDepth++ != 0 || ++_pass != 0) return;
        Array.Clear(_marks);
        _pass = 1;
    }

    internal void EndPass() => _passDepth--;

    internal bool IsInProgress(int index) => _marks[index] == _pass;

    internal void MarkInProgress(int index) => _marks[index] = _pass;

    internal void Push(int entry)
    {
        if (_stackDepth == _stack.Length) Array.Resize(ref _stack, _stackDepth * 2);
        _stack[_stackDepth++] = entry;
    }

    internal int Pop() => _stack[--_stackDepth];

    internal void Truncate(int depth) => _stackDepth = depth;

    internal void Reset()
    {
        _generation++;
        if (_generation == 0)
        {
            Array.Clear(_stamps);
            _generation = 1;
        }

        _stackDepth = 0;
        _passDepth = 0;
    }
}
