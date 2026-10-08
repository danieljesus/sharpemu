// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// Compile the immutable SRT graph once, not the draw's pointers or memory contents.
// Each node is a case of a small method that computes it from the dense values of its
// operands. A draw marks the nodes its roots need, skipping the ones its cache already
// holds, and runs them in dependency order without recursion; small methods bound JIT cost.
internal sealed class CompiledResourceEvaluator
{
    private const int NodesPerMethod = 256;
    private delegate bool NodeFunction(RuntimeValueEvaluator evaluator, int index, ulong[] values, out ulong result);
    private readonly Dictionary<ScalarValue, int> _indices;
    private readonly NodeFunction[] _functions;
    // The operand indices of each node, as the marking walks them: a node's dependencies are
    // _dependencies[_dependencyStart[node].._dependencyStart[node + 1]).
    private readonly int[] _dependencyStart;
    private readonly int[] _dependencies;
    // For a select, the indices of its condition and its first arm (-1 when constant): under
    // an active-lane mask the select takes the first arm without evaluating the others.
    private readonly int[] _selectCondition;
    private readonly int[] _selectArm;
    // For a resource table word, its slot and the index of the table word it aliases (-1 when
    // the slot is out of range); a clean slot is read by the clean evaluator instead.
    private readonly int[] _tableSlot;
    private readonly int[] _tableWord;
    internal ScalarValue[] Values { get; }
    internal int[][] SourceWords { get; }
    internal int[] TableWords { get; }
    internal int Count => Values.Length;

    private static readonly Dictionary<string, MethodInfo> Helpers = new[]
    {
        nameof(RuntimeValueEvaluator.EvaluateCompiledSpecial), nameof(RuntimeValueEvaluator.IsCompiledActiveMask),
        nameof(RuntimeValueEvaluator.EvaluateCompiledTableWord), nameof(RuntimeValueEvaluator.ReadUserData),
        nameof(RuntimeValueEvaluator.ReadShaderBase), nameof(RuntimeValueEvaluator.ReadRawWord),
    }.ToDictionary(name => name, name => typeof(RuntimeValueEvaluator).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!);
    private static readonly MethodInfo EvaluateOperation = typeof(ScalarOperationSemantics)
        .GetMethod(nameof(ScalarOperationSemantics.TryEvaluateFixed), BindingFlags.NonPublic | BindingFlags.Static)!;

    private CompiledResourceEvaluator(ShaderResourcePlan plan, List<ScalarValue> values, Dictionary<ScalarValue, int> indices,
        Dictionary<ScalarValue, ScalarValue?> phis)
    {
        Values = [.. values];
        _indices = indices;
        SourceWords = plan.DescriptorSources.Select(source => source.Dwords.Select(word => indices[word]).ToArray()).ToArray();
        TableWords = plan.TableReads.Select(read => indices[read.Value]).ToArray();
        _dependencyStart = new int[Count + 1];
        _selectCondition = new int[Count];
        _selectArm = new int[Count];
        _tableSlot = new int[Count];
        _tableWord = new int[Count];
        var dependencies = new List<int>();
        for (var index = 0; index < Count; index++)
        {
            _dependencyStart[index] = dependencies.Count;
            _selectCondition[index] = _selectArm[index] = _tableSlot[index] = _tableWord[index] = -1;
            CollectDependencies(plan, index, phis, dependencies);
        }

        _dependencyStart[Count] = dependencies.Count;
        _dependencies = [.. dependencies];
        _functions = new NodeFunction[(Count + NodesPerMethod - 1) / NodesPerMethod];
        for (var chunk = 0; chunk < _functions.Length; chunk++)
            _functions[chunk] = Compile(plan, chunk * NodesPerMethod, phis);
    }

    private int IndexOf(ScalarValue value) => value.IsConstant ? -1 : _indices.TryGetValue(value, out var index) ? index : -1;

    // The operands a node reads through the dense values; constants are immediates.
    private void CollectDependencies(ShaderResourcePlan plan, int index, Dictionary<ScalarValue, ScalarValue?> phis, List<int> dependencies)
    {
        var value = Values[index];
        void Add(ScalarValue operand)
        {
            var operandIndex = IndexOf(operand);
            if (operandIndex >= 0) dependencies.Add(operandIndex);
        }

        switch (value.Kind)
        {
            case ScalarValueKind.Phi:
                if (phis[value] is { } invariant) Add(invariant);
                break;
            case ScalarValueKind.ResourceTableWord:
                // The clean evaluator or the aliased table word, decided per draw by the marking.
                if (value.Payload < (ulong)plan.TableReads.Count)
                {
                    _tableSlot[index] = (int)value.Payload;
                    _tableWord[index] = TableWords[(int)value.Payload];
                }

                break;
            case ScalarValueKind.Select:
                _selectCondition[index] = IndexOf(value.Operands[0]);
                _selectArm[index] = IndexOf(value.Operands[1]);
                // Ordinary selects are eager, including reads on the inactive arm.
                foreach (var operand in value.Operands) Add(operand);
                break;
            case ScalarValueKind.ScalarAddressWord:
            case ScalarValueKind.ScalarBufferWord:
                if ((uint)value.MemoryIndex >= (uint)plan.Memory.Count || value.Operands.Length < 2) break;
                foreach (var word in value.Operands[0].Operands) Add(word);
                Add(value.Operands[1]);
                break;
            case ScalarValueKind.Operation:
                if (RuntimeValueValidator.IsUniformOperation(value.Operation))
                    foreach (var operand in value.Operands) Add(operand);
                break;
            // A first-lane value is evaluated by its own evaluator under the active mask.
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<ShaderResourcePlan> Pending = new();
    private static readonly SemaphoreSlim PendingSignal = new(0);
    private static int _compilerStarted;

    internal static void Enqueue(ShaderResourcePlan plan)
    {
        Pending.Enqueue(plan);
        if (Interlocked.Exchange(ref _compilerStarted, 1) == 0)
        {
            new Thread(CompilePending) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "SRT compiler" }.Start();
        }

        PendingSignal.Release();
    }

    private static void CompilePending()
    {
        while (true)
        {
            PendingSignal.Wait();
            while (Pending.TryDequeue(out var plan)) plan.CompileEvaluatorNow();
        }
    }

    internal bool TryGetIndex(ScalarValue value, out int index) => _indices.TryGetValue(value, out index);

    // Evaluates every root the cache does not hold yet, each after its dependencies; false as
    // soon as one needed node cannot be evaluated, with the nodes computed so far kept. A node
    // whose dependency is still in progress is on a cycle, which fails like the interpreter's
    // visiting set. A pass started by a callback from a running node shares the marks of the
    // outer one, so the in-progress nodes stay in progress.
    internal bool Evaluate(RuntimeValueEvaluator evaluator, CompiledValueCache cache, ReadOnlySpan<int> roots)
    {
        cache.BeginPass();
        var stackBase = cache.StackDepth;
        var values = cache.Values;
        var succeeded = true;
        foreach (var root in roots)
        {
            if (cache.Contains(root)) continue;
            if (cache.IsInProgress(root))
            {
                succeeded = false;
                break;
            }

            cache.Push(root);
            while (succeeded && cache.StackDepth > stackBase)
            {
                var top = cache.Pop();
                if (top < 0)
                {
                    // Its dependencies ran: compute the node and keep its value.
                    var index = ~top;
                    if (_functions[index / NodesPerMethod](evaluator, index, values, out var result)) cache.Store(index, result);
                    else succeeded = false;
                    continue;
                }

                if (cache.Contains(top) || cache.IsInProgress(top)) continue;
                cache.MarkInProgress(top);
                cache.Push(~top);
                succeeded = PushDependencies(evaluator, cache, top);
            }

            if (!succeeded) break;
        }

        cache.Truncate(stackBase);
        cache.EndPass();
        return succeeded;
    }

    private bool PushDependencies(RuntimeValueEvaluator evaluator, CompiledValueCache cache, int index)
    {
        switch (Values[index].Kind)
        {
            case ScalarValueKind.Select when _selectCondition[index] >= 0 && evaluator.IsCompiledActiveMask(_selectCondition[index]):
                return _selectArm[index] < 0 || PushDependency(cache, _selectArm[index]);
            case ScalarValueKind.ResourceTableWord:
                return _tableWord[index] < 0 || evaluator.IsCleanSlot(_tableSlot[index]) || PushDependency(cache, _tableWord[index]);
            default:
                // Pushed last to first, so the first operand is evaluated first, as the interpreter does.
                for (var position = _dependencyStart[index + 1] - 1; position >= _dependencyStart[index]; position--)
                {
                    if (!PushDependency(cache, _dependencies[position])) return false;
                }

                return true;
        }
    }

    private static bool PushDependency(CompiledValueCache cache, int index)
    {
        if (cache.Contains(index)) return true;
        if (cache.IsInProgress(index)) return false;
        cache.Push(index);
        return true;
    }

    internal static CompiledResourceEvaluator? Build(ShaderResourcePlan plan)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported || Environment.GetEnvironmentVariable("SHARPEMU_SRT_NATIVE") == "0") return null;

        var values = new List<ScalarValue>();
        var indices = new Dictionary<ScalarValue, int>();
        var phis = new Dictionary<ScalarValue, ScalarValue?>();
        void Add(ScalarValue value)
        {
            if (indices.TryAdd(value, values.Count)) values.Add(value);
        }
        foreach (var source in plan.DescriptorSources)
            foreach (var word in source.Dwords) Add(word);
        foreach (var read in plan.TableReads) Add(read.Value);
        foreach (var read in plan.DynamicReads) Add(read);
        foreach (var branch in plan.ResourceBranches)
            if (branch.Condition is { } condition) Add(condition);
        foreach (var range in plan.DeviceAddressRanges)
        {
            Add(range.BaseLow);
            Add(range.BaseHigh);
        }
        foreach (var image in plan.IndirectImages) Add(image.Key);
        foreach (var candidates in plan.BufferCandidateTables)
            if (candidates.Limit is { } limit) Add(limit);

        // An iterative walk also admits cycles; the marking fails them at run time.
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (value.Kind == ScalarValueKind.Phi)
            {
                var invariant = plan.Graph.ResolveInvariantPhi(value);
                phis[value] = invariant;
                if (invariant is not null) Add(invariant);
            }
            else if (value.Kind == ScalarValueKind.FirstLane)
            {
                Add(value.Operands[0]);
            }
            else if (value.Kind == ScalarValueKind.ResourceTableWord)
            {
                if (value.Payload < (ulong)plan.TableReads.Count) Add(plan.TableReads[(int)value.Payload].Value);
            }
            else if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord)
            {
                if ((uint)value.MemoryIndex >= (uint)plan.Memory.Count || value.Operands.Length < 2) continue;
                foreach (var word in value.Operands[0].Operands) Add(word);
                Add(value.Operands[1]);
            }
            else if (value.Kind == ScalarValueKind.Select ||
                (value.Kind == ScalarValueKind.Operation && RuntimeValueValidator.IsUniformOperation(value.Operation)))
            {
                foreach (var operand in value.Operands) Add(operand);
            }
        }
        return values.Count == 0 ? null : new(plan, values, indices, phis);
    }

    private NodeFunction Compile(ShaderResourcePlan plan, int first, Dictionary<ScalarValue, ScalarValue?> phis)
    {
        var count = Math.Min(NodesPerMethod, Count - first);
        var method = new DynamicMethod($"Srt_{plan.Hash:X16}_{first}", typeof(bool),
            [typeof(RuntimeValueEvaluator), typeof(int), typeof(ulong[]), typeof(ulong).MakeByRefType()], typeof(CompiledResourceEvaluator).Module, true);
        var il = method.GetILGenerator();
        var result = il.DeclareLocal(typeof(ulong));
        var operands = Enumerable.Range(0, 5).Select(_ => il.DeclareLocal(typeof(ulong))).ToArray();
        var failed = il.DefineLabel();
        var store = il.DefineLabel();
        var cases = Enumerable.Range(0, count).Select(_ => il.DefineLabel()).ToArray();

        void Call(string helper) => il.Emit(OpCodes.Call, Helpers[helper]);
        void Constant(ulong value) => il.Emit(OpCodes.Ldc_I8, unchecked((long)value));
        // The operand's value onto the stack: an immediate, or the dense value its node left.
        bool PushOperand(ScalarValue value)
        {
            if (value.IsConstant)
            {
                Constant(value.Payload);
                return true;
            }
            if (!_indices.TryGetValue(value, out var index)) return false;
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldelem_I8);
            return true;
        }
        bool Operand(ScalarValue value, LocalBuilder destination)
        {
            if (!PushOperand(value)) return false;
            il.Emit(OpCodes.Stloc, destination);
            return true;
        }
        void Load(int index, bool narrow = false)
        {
            il.Emit(OpCodes.Ldloc, operands[index]);
            if (narrow) il.Emit(OpCodes.Conv_U4);
        }
        void Special(int index)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Ldloca, result);
            Call(nameof(RuntimeValueEvaluator.EvaluateCompiledSpecial));
            il.Emit(OpCodes.Brfalse, failed);
        }

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, first);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Switch, cases);
        il.Emit(OpCodes.Br, failed);

        for (var offset = 0; offset < count; offset++)
        {
            var index = first + offset;
            var value = Values[index];
            il.MarkLabel(cases[offset]);
            switch (value.Kind)
            {
                case ScalarValueKind.Constant:
                    Constant(value.Payload);
                    il.Emit(OpCodes.Stloc, result);
                    break;
                case ScalarValueKind.MemoryAperture:
                    Constant(Gen5InlineConstants.DecodeAperture64((uint)value.Payload) >> 32);
                    il.Emit(OpCodes.Stloc, result);
                    break;
                case ScalarValueKind.UserData:
                    if (value.UserDataRegister < plan.UserDataBase)
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, unchecked((int)(value.UserDataRegister - plan.UserDataBase)));
                    il.Emit(OpCodes.Ldloca, result);
                    Call(nameof(RuntimeValueEvaluator.ReadUserData));
                    il.Emit(OpCodes.Brfalse, failed);
                    break;
                case ScalarValueKind.ShaderBase:
                    il.Emit(OpCodes.Ldarg_0);
                    Call(nameof(RuntimeValueEvaluator.ReadShaderBase));
                    il.Emit(OpCodes.Stloc, result);
                    break;
                case ScalarValueKind.Phi:
                    if (phis[value] is not { } invariant || !Operand(invariant, result)) il.Emit(OpCodes.Br, failed);
                    break;
                case ScalarValueKind.FirstLane:
                    // Crosses evaluator contexts: the active-lane cache of its own evaluator.
                    Special(index);
                    break;
                case ScalarValueKind.ResourceTableWord:
                    if (_tableWord[index] < 0)
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, _tableSlot[index]);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldc_I4, _tableWord[index]);
                    il.Emit(OpCodes.Ldelem_I8);
                    il.Emit(OpCodes.Ldloca, result);
                    Call(nameof(RuntimeValueEvaluator.EvaluateCompiledTableWord));
                    il.Emit(OpCodes.Brfalse, failed);
                    break;
                case ScalarValueKind.Select:
                {
                    if (value.Operands.Length < 3 || value.Operands.Any(operand => IndexOf(operand) < 0 && !operand.IsConstant))
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    var ordinary = il.DefineLabel();
                    var chosen = il.DefineLabel();
                    if (_selectCondition[index] >= 0)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Ldc_I4, _selectCondition[index]);
                        Call(nameof(RuntimeValueEvaluator.IsCompiledActiveMask));
                        il.Emit(OpCodes.Brfalse, ordinary);
                        Operand(value.Operands[1], result);
                        il.Emit(OpCodes.Br, store);
                    }
                    il.MarkLabel(ordinary);
                    PushOperand(value.Operands[0]);
                    il.Emit(OpCodes.Brfalse, chosen);
                    Operand(value.Operands[1], result);
                    il.Emit(OpCodes.Br, store);
                    il.MarkLabel(chosen);
                    Operand(value.Operands[2], result);
                    break;
                }
                case ScalarValueKind.ScalarAddressWord:
                case ScalarValueKind.ScalarBufferWord:
                {
                    var buffer = value.Kind == ScalarValueKind.ScalarBufferWord;
                    if ((uint)value.MemoryIndex >= (uint)plan.Memory.Count || value.Operands.Length < 2 ||
                        value.Operands[0].Operands.Length < 2 || (buffer && value.Operands[0].Operands.Length != 4))
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    var handle = value.Operands[0];
                    if (!Operand(handle.Operands[0], operands[0]) || !Operand(handle.Operands[1], operands[1]) || !Operand(value.Operands[1], operands[2]) ||
                        (buffer && (!Operand(handle.Operands[2], operands[3]) || !Operand(handle.Operands[3], operands[4]))))
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldc_I4, (int)value.Kind);
                    Load(0);
                    Load(1);
                    Load(2, narrow: true);
                    if (buffer) Load(3);
                    else Constant(0);
                    Constant(unchecked((ulong)(long)(int)plan.Memory[value.MemoryIndex].Offset));
                    il.Emit(OpCodes.Ldloca, result);
                    Call(nameof(RuntimeValueEvaluator.ReadRawWord));
                    il.Emit(OpCodes.Brfalse, failed);
                    break;
                }
                case ScalarValueKind.Operation:
                    if (!RuntimeValueValidator.IsUniformOperation(value.Operation))
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    if (value.Operands.Length > 4)
                    {
                        Special(index);
                        break;
                    }
                    var missing = false;
                    for (var operand = 0; operand < 4; operand++)
                    {
                        if (operand < value.Operands.Length) missing |= !Operand(value.Operands[operand], operands[operand]);
                        else
                        {
                            Constant(0);
                            il.Emit(OpCodes.Stloc, operands[operand]);
                        }
                    }
                    if (missing)
                    {
                        il.Emit(OpCodes.Br, failed);
                        break;
                    }
                    if (EmitArithmetic(il, value.Operation, operands)) il.Emit(OpCodes.Stloc, result);
                    else
                    {
                        il.Emit(OpCodes.Ldc_I4, (int)value.Operation);
                        for (var operand = 0; operand < 4; operand++) Load(operand);
                        il.Emit(OpCodes.Ldloca, result);
                        il.Emit(OpCodes.Call, EvaluateOperation);
                        il.Emit(OpCodes.Brfalse, failed);
                    }
                    break;
                default:
                    il.Emit(OpCodes.Br, failed);
                    break;
            }
            il.Emit(OpCodes.Br, store);
        }

        il.MarkLabel(store);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Stind_I8);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(failed);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldc_I8, 0L);
        il.Emit(OpCodes.Stind_I8);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        var function = method.CreateDelegate<NodeFunction>();
        RuntimeHelpers.PrepareDelegate(function);
        return function;
    }

    private static bool EmitArithmetic(ILGenerator il, ScalarOperation operation, LocalBuilder[] operands)
    {
        if (operation is ScalarOperation.Construct64 or ScalarOperation.AddCarry32 or ScalarOperation.UMulHi32)
        {
            for (var operand = 0; operand < 2; operand++)
            {
                il.Emit(OpCodes.Ldloc, operands[operand]);
                il.Emit(OpCodes.Conv_U4);
                il.Emit(OpCodes.Conv_U8);
                if (operand == 1 && operation == ScalarOperation.Construct64)
                {
                    il.Emit(OpCodes.Ldc_I4, 32);
                    il.Emit(OpCodes.Shl);
                }
            }
            il.Emit(operation == ScalarOperation.Construct64 ? OpCodes.Or :
                operation == ScalarOperation.AddCarry32 ? OpCodes.Add : OpCodes.Mul);
            if (operation == ScalarOperation.UMulHi32)
            {
                il.Emit(OpCodes.Ldc_I4, 32);
                il.Emit(OpCodes.Shr_Un);
            }
            return true;
        }
        var narrow = operation is ScalarOperation.IAdd32 or ScalarOperation.ISub32 or ScalarOperation.IMul32 or
            ScalarOperation.And32 or ScalarOperation.Or32 or ScalarOperation.Xor32 or ScalarOperation.Not32 or
            ScalarOperation.ShiftLeft32 or ScalarOperation.ShiftRightLogical32 or ScalarOperation.ShiftRightArithmetic32;
        OpCode opcode;
        switch (operation)
        {
            case ScalarOperation.IAdd32: case ScalarOperation.IAdd64: opcode = OpCodes.Add; break;
            case ScalarOperation.ISub32: case ScalarOperation.ISub64: opcode = OpCodes.Sub; break;
            case ScalarOperation.IMul32: case ScalarOperation.IMul64: opcode = OpCodes.Mul; break;
            case ScalarOperation.And32: case ScalarOperation.And64: opcode = OpCodes.And; break;
            case ScalarOperation.Or32: opcode = OpCodes.Or; break;
            case ScalarOperation.Xor32: opcode = OpCodes.Xor; break;
            case ScalarOperation.Not32: opcode = OpCodes.Not; break;
            case ScalarOperation.ShiftLeft32: case ScalarOperation.ShiftLeft64: opcode = OpCodes.Shl; break;
            case ScalarOperation.ShiftRightLogical32: case ScalarOperation.ShiftRightLogical64: opcode = OpCodes.Shr_Un; break;
            case ScalarOperation.ShiftRightArithmetic32: case ScalarOperation.ShiftRightArithmetic64: opcode = OpCodes.Shr; break;
            default: return false;
        }
        il.Emit(OpCodes.Ldloc, operands[0]);
        if (narrow) il.Emit(OpCodes.Conv_U4);
        if (operation != ScalarOperation.Not32)
        {
            il.Emit(OpCodes.Ldloc, operands[1]);
            if (opcode == OpCodes.Shl || opcode == OpCodes.Shr || opcode == OpCodes.Shr_Un)
            {
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Ldc_I4, narrow ? 31 : 63);
                il.Emit(OpCodes.And);
            }
            else if (narrow) il.Emit(OpCodes.Conv_U4);
        }
        il.Emit(opcode);
        if (narrow) il.Emit(OpCodes.Conv_U4);
        il.Emit(OpCodes.Conv_U8);
        return true;
    }
}
