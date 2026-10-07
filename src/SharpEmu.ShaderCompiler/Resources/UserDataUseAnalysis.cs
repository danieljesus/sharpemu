// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// How a plan uses each user-data register: as a value that reaches a descriptor, a table word,
// a predicate or any other output, or only as the base address of its raw reads. A register of
// the second kind can change between draws without changing what the plan materializes, as
// long as the words read through it are the same; games allocate their descriptor tables per
// draw, so the pointers change on nearly every draw while the tables repeat.
//
// The walk is conservative: everything reachable from any root counts as a value use, except
// the two registers of a handle that is the direct (low, high) pair of a raw read, which are
// visited as an address use instead. A register with any value use is a value register.
public sealed class UserDataUseAnalysis
{
    public const int AbsoluteBase = -1;
    public const int UnknownBase = -2;

    private readonly bool[] _valueRegisters;
    private readonly bool[] _addressRegisters;

    private UserDataUseAnalysis(bool[] valueRegisters, bool[] addressRegisters)
    {
        _valueRegisters = valueRegisters;
        _addressRegisters = addressRegisters;
        for (var index = 0; index < addressRegisters.Length; index++)
        {
            AnyAddressOnly |= addressRegisters[index] && !valueRegisters[index];
        }
    }

    public uint UserDataBase { get; private init; }

    public bool AnyAddressOnly { get; }

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

    public static UserDataUseAnalysis Of(ShaderResourcePlan plan)
    {
        var graph = plan.Graph;
        var count = (int)graph.UserDataCount;
        var valueRegisters = new bool[count];
        var addressRegisters = new bool[count];
        var parented = new HashSet<ScalarValue>();
        foreach (var value in graph.Values)
        {
            foreach (var operand in value.Operands)
            {
                parented.Add(operand);
            }
        }

        var visited = new HashSet<ScalarValue>();
        var pending = new Stack<ScalarValue>();
        void Root(ScalarValue? value)
        {
            if (value is not null)
            {
                pending.Push(value);
            }
        }

        foreach (var value in graph.Values)
        {
            if (!parented.Contains(value))
            {
                Root(value);
            }
        }

        foreach (var source in plan.DescriptorSources)
        {
            foreach (var dword in source.Dwords)
            {
                Root(dword);
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

        foreach (var image in plan.IndirectImages)
        {
            Root(image.Key);
        }

        foreach (var image in plan.Info.Images)
        {
            if (image.Source < plan.DescriptorSources.Count &&
                plan.DescriptorSources[(int)image.Source].IndirectImage?.SelectorValues is { } selector)
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

        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value))
            {
                continue;
            }

            if (value.Kind == ScalarValueKind.UserData)
            {
                MarkRegister(valueRegisters, value.UserDataRegister);
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
                        pending.Push(handle.Operands[index]);
                    }
                }
                else
                {
                    pending.Push(handle);
                }

                for (var index = 1; index < value.Operands.Length; index++)
                {
                    pending.Push(value.Operands[index]);
                }

                continue;
            }

            foreach (var operand in value.Operands)
            {
                pending.Push(operand);
            }
        }

        return new UserDataUseAnalysis(valueRegisters, addressRegisters) { UserDataBase = graph.UserDataBase };
    }
}
