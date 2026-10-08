// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// How a plan uses each user-data register: as a value that reaches a descriptor, a table word,
// a predicate or any other output; as the base address of raw reads; or as an input of a buffer
// descriptor dword computed from user data and constants alone. Registers of the last two kinds
// can change between draws without changing what the plan materializes beyond those dwords:
// games allocate their descriptor tables and constant buffers per draw, so the pointers change
// on nearly every draw while the tables repeat.
//
// The roots are the values the materializer evaluates (the same list the compiled evaluator
// compiles). Everything reachable from a root counts as a value use, except the two registers of
// a handle that is the direct (low, high) pair of a raw read, visited as an address use, and the
// registers under a pure buffer dword, visited as a dword use. A register with any value use is a
// value register.
public sealed class UserDataUseAnalysis
{
    public const int AbsoluteBase = -1;
    public const int UnknownBase = -2;

    private const int ValueMode = 0;
    private const int DwordMode = 1;

    private readonly bool[] _valueRegisters;
    private readonly bool[] _addressRegisters;
    private readonly bool[] _dwordRegisters;

    private UserDataUseAnalysis(bool[] valueRegisters, bool[] addressRegisters, bool[] dwordRegisters,
        RebasableDword[] rebasableDwords)
    {
        _valueRegisters = valueRegisters;
        _addressRegisters = addressRegisters;
        _dwordRegisters = dwordRegisters;
        RebasableDwords = rebasableDwords;
        for (var index = 0; index < addressRegisters.Length; index++)
        {
            AnyAddressOnly |= (addressRegisters[index] || dwordRegisters[index]) && !valueRegisters[index];
        }
    }

    // A buffer descriptor dword that is a function of user data and constants only, with the
    // buffer it belongs to in the snapshot's Buffers.
    public readonly record struct RebasableDword(int Buffer, int Dword, ScalarValue Value);

    public uint UserDataBase { get; private init; }

    public bool AnyAddressOnly { get; }

    // Every buffer dword that depends on a register which is not a value register.
    public RebasableDword[] RebasableDwords { get; }

    // A register outside the analysed range is compared like a value, which is the safe choice.
    public bool IsValueRegister(int index) => (uint)index >= (uint)_valueRegisters.Length || _valueRegisters[index];

    // The user-data register a raw read's handle starts at when the handle is the direct
    // (register, register + 1) pair, or AbsoluteBase when the address does not come from such a pair.
    public static int BaseRegisterOf(ScalarValue handle)
    {
        if (handle.Operands.Length < 2 ||
            handle.Operands[0].Kind != ScalarValueKind.UserData ||
            handle.Operands[1].Kind != ScalarValueKind.UserData ||
            handle.Operands[1].UserDataRegister != handle.Operands[0].UserDataRegister + 1)
        {
            return AbsoluteBase;
        }

        return (int)handle.Operands[0].UserDataRegister;
    }

    // Evaluates a value made of user data, constants and uniform operations for one draw's
    // user data, the way the runtime evaluator does.
    public bool TryEvaluatePure(ScalarValue value, IReadOnlyList<uint> userData, out ulong result)
    {
        result = 0;
        switch (value.Kind)
        {
            case ScalarValueKind.Constant:
                result = value.Payload;
                return true;
            case ScalarValueKind.UserData:
            {
                var register = value.UserDataRegister;
                if (register < UserDataBase || register - UserDataBase >= (uint)userData.Count)
                {
                    return false;
                }

                result = userData[(int)(register - UserDataBase)];
                return true;
            }
            case ScalarValueKind.Operation:
            {
                if (!RuntimeValueValidator.IsUniformOperation(value.Operation) || value.Operands.Length > 4)
                {
                    return false;
                }

                Span<ulong> operands = stackalloc ulong[value.Operands.Length];
                for (var index = 0; index < operands.Length; index++)
                {
                    if (!TryEvaluatePure(value.Operands[index], userData, out operands[index]))
                    {
                        return false;
                    }
                }

                return ScalarOperationSemantics.TryEvaluate(value.Operation, operands, out result);
            }
            default:
                return false;
        }
    }

    public static UserDataUseAnalysis Of(ShaderResourcePlan plan)
    {
        var graph = plan.Graph;
        var count = (int)graph.UserDataCount;
        var valueRegisters = new bool[count];
        var addressRegisters = new bool[count];
        var dwordRegisters = new bool[count];
        var visited = new HashSet<(ScalarValue, int)>();
        var pending = new Stack<(ScalarValue Value, int Mode)>();
        void Root(ScalarValue? value, int mode = ValueMode)
        {
            if (value is not null)
            {
                pending.Push((value, mode));
            }
        }

        // Sources shared with an image or a sampler keep their dwords as values: only a buffer
        // descriptor is reassembled on a hit.
        var bufferOnlySources = new bool[plan.DescriptorSources.Count];
        foreach (var buffer in plan.Info.Buffers)
        {
            if (buffer.Source < bufferOnlySources.Length)
            {
                bufferOnlySources[buffer.Source] = true;
            }
        }

        foreach (var image in plan.Info.Images)
        {
            if (image.Source < bufferOnlySources.Length)
            {
                bufferOnlySources[image.Source] = false;
            }
        }

        foreach (var sampler in plan.Info.Samplers)
        {
            if (sampler.Source < bufferOnlySources.Length)
            {
                bufferOnlySources[sampler.Source] = false;
            }
        }

        var rebasable = new List<RebasableDword>();
        for (var sourceIndex = 0; sourceIndex < plan.DescriptorSources.Count; sourceIndex++)
        {
            var dwords = plan.DescriptorSources[sourceIndex].Dwords;
            var buffer = bufferOnlySources[sourceIndex] ? BufferOfSource(plan, sourceIndex) : -1;
            for (var dword = 0; dword < dwords.Length; dword++)
            {
                if (buffer >= 0 && IsPure(dwords[dword], out var readsUserData))
                {
                    if (readsUserData)
                    {
                        rebasable.Add(new RebasableDword(buffer, dword, dwords[dword]));
                        Root(dwords[dword], DwordMode);
                    }
                }
                else
                {
                    Root(dwords[dword]);
                }
            }
        }

        foreach (var read in plan.TableReads)
        {
            Root(read.Value);
        }

        foreach (var read in plan.DynamicReads)
        {
            Root(read);
        }

        foreach (var branch in plan.ResourceBranches)
        {
            Root(branch.Condition);
        }

        foreach (var range in plan.DeviceAddressRanges)
        {
            Root(range.BaseLow);
            Root(range.BaseHigh);
        }

        foreach (var image in plan.IndirectImages)
        {
            Root(image.Key);
        }

        foreach (var candidates in plan.BufferCandidateTables)
        {
            Root(candidates.Limit);
        }

        foreach (var source in plan.DescriptorSources)
        {
            if (source.IndirectImage?.SelectorValues is { } selector)
            {
                foreach (var value in selector.RuntimeValues())
                {
                    Root(value);
                }
            }
        }

        void MarkRegister(bool[] registers, uint register)
        {
            var index = (long)register - graph.UserDataBase;
            if (index >= 0 && index < registers.Length)
            {
                registers[index] = true;
            }
        }

        while (pending.TryPop(out var item))
        {
            if (!visited.Add(item))
            {
                continue;
            }

            var (value, mode) = item;
            if (value.Kind == ScalarValueKind.UserData)
            {
                MarkRegister(mode == DwordMode ? dwordRegisters : valueRegisters, value.UserDataRegister);
                continue;
            }

            if (value.Kind is ScalarValueKind.ScalarBufferWord or ScalarValueKind.ScalarAddressWord && value.Operands.Length >= 1)
            {
                var handle = value.Operands[0];
                var baseRegister = BaseRegisterOf(handle);
                if (baseRegister >= 0)
                {
                    MarkRegister(addressRegisters, (uint)baseRegister);
                    MarkRegister(addressRegisters, (uint)baseRegister + 1);
                    for (var index = 2; index < handle.Operands.Length; index++)
                    {
                        pending.Push((handle.Operands[index], ValueMode));
                    }
                }
                else
                {
                    pending.Push((handle, ValueMode));
                }

                for (var index = 1; index < value.Operands.Length; index++)
                {
                    pending.Push((value.Operands[index], ValueMode));
                }

                continue;
            }

            foreach (var operand in value.Operands)
            {
                pending.Push((operand, mode));
            }
        }

        // A dword whose registers are all values is rebuilt by the key, not by re-evaluation.
        rebasable.RemoveAll(dword => !DependsOnNonValueRegister(dword.Value, graph.UserDataBase, valueRegisters));
        return new UserDataUseAnalysis(valueRegisters, addressRegisters, dwordRegisters, [.. rebasable]) { UserDataBase = graph.UserDataBase };
    }

    private static int BufferOfSource(ShaderResourcePlan plan, int source)
    {
        for (var index = 0; index < plan.Info.Buffers.Count; index++)
        {
            if (plan.Info.Buffers[index].Source == source)
            {
                return index;
            }
        }

        return -1;
    }

    // A value computed from user data and constants through uniform operations only.
    private static bool IsPure(ScalarValue value, out bool readsUserData)
    {
        readsUserData = false;
        var pending = new Stack<ScalarValue>();
        var seen = new HashSet<ScalarValue>();
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            switch (current.Kind)
            {
                case ScalarValueKind.Constant:
                    continue;
                case ScalarValueKind.UserData:
                    readsUserData = true;
                    continue;
                case ScalarValueKind.Operation when RuntimeValueValidator.IsUniformOperation(current.Operation) && current.Operands.Length <= 4:
                    foreach (var operand in current.Operands)
                    {
                        pending.Push(operand);
                    }

                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool DependsOnNonValueRegister(ScalarValue value, uint userDataBase, bool[] valueRegisters)
    {
        var pending = new Stack<ScalarValue>();
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (current.Kind == ScalarValueKind.UserData)
            {
                var index = (long)current.UserDataRegister - userDataBase;
                if (index >= 0 && index < valueRegisters.Length && !valueRegisters[index])
                {
                    return true;
                }

                continue;
            }

            foreach (var operand in current.Operands)
            {
                pending.Push(operand);
            }
        }

        return false;
    }
}
