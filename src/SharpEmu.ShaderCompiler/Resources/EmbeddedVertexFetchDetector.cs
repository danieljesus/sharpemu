// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler.Resources;

// One buffer load of a vertex prolog that reads an attribute's stream.
public sealed record EmbeddedVertexFetchLoad(uint Pc, int AttributeId, uint Components, IReadOnlyList<uint> PrologLoads);

// The vertex prolog's fetch loads and the user registers that carry the draw offsets.
public sealed class EmbeddedVertexFetchPlan
{
    public List<EmbeddedVertexFetchLoad> Loads { get; } = [];
    public int VertexOffsetScalarRegister { get; set; } = ShaderResourceInfo.NoScalarRegister;
    public int InstanceOffsetScalarRegister { get; set; } = ShaderResourceInfo.NoScalarRegister;

    private const int ScalarSlotCount = 128;
    private const int SccSlot = ScalarSlotCount;
    private const int VectorSlot = SccSlot + 1;
    private const int SlotCount = VectorSlot + 256;
    private const int MaxTrackedTableLoads = 64;
    private const uint VccLowRegister = 106;
    private const uint M0Register = 124;
    private const uint ExecLowRegister = 126;

    // Fixed-function attributes replace these table loads. Keep instruction addresses
    // unchanged so branch targets and vertex-input bindings still refer to the same code.
    // A table load can be shared with code the detector does not recognize as a fetch
    // (another buffer walk, a branch, an export), so it is only removed when the
    // replaced fetches are the only consumers of everything it loads.
    public Gen5ShaderProgram RemoveReplacedTableLoads(Gen5ShaderProgram program)
    {
        var replacedLoads = ReplaceableTableLoads(program);
        return new Gen5ShaderProgram(program.Address, program.Instructions.Select(instruction =>
            replacedLoads.Contains(instruction.Pc) && instruction.Control is Gen5ScalarMemoryControl
                ? instruction with { Encoding = Gen5ShaderEncoding.Sopp, Opcode = "SNop", Sources = [], Destinations = [], Control = null }
                : instruction).ToArray());
    }

    // Follows every value derived from each table load forward through the program. A load
    // stays when any of those values reaches an instruction other than a replaced fetch
    // that does more than compute registers; a load that stays then becomes such a
    // consumer for the loads its own address depends on, so repeat until nothing changes.
    private HashSet<uint> ReplaceableTableLoads(Gen5ShaderProgram program)
    {
        var tableLoads = Loads.SelectMany(load => load.PrologLoads).Distinct().ToArray();
        if (tableLoads.Length == 0 || tableLoads.Length > MaxTrackedTableLoads)
        {
            return [];
        }

        var fetches = Loads.Select(load => load.Pc).ToHashSet();
        var instructions = program.Instructions;
        var forwardTargets = new uint?[instructions.Count];
        var straightLine = true;
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (Gen5IrBranchResolver.IsTerminator(instruction))
            {
                continue;
            }

            if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) && target > instruction.Pc)
            {
                forwardTargets[index] = target;
            }
            else if (Gen5IrBranchResolver.Instance.IsBranch(instruction) || WritesProgramCounter(instruction.Opcode))
            {
                straightLine = false;
            }
        }

        ulong kept = 0;
        while (true)
        {
            var foreign = TraceForeignConsumers(instructions, tableLoads, kept, fetches, forwardTargets, straightLine);
            if ((foreign & ~kept) == 0)
            {
                break;
            }

            kept |= foreign;
        }

        return tableLoads.Where((_, index) => (kept & (1ul << index)) == 0).ToHashSet();
    }

    // Each register slot holds one bit per table load whose value it may derive from.
    // A forward branch carries its state to the target, where it merges with the
    // fall-through path. With a backward or unresolved branch no write clears a slot
    // and the walk repeats until it stops growing.
    private static ulong TraceForeignConsumers(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        uint[] tableLoads,
        ulong kept,
        HashSet<uint> fetches,
        uint?[] forwardTargets,
        bool straightLine)
    {
        var taint = new ulong[SlotCount];
        var branched = new Dictionary<uint, ulong[]>();
        ulong foreign = 0;
        bool changed;
        do
        {
            changed = false;
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (branched.Remove(instruction.Pc, out var incoming))
                {
                    for (var slot = 0; slot < SlotCount; slot++)
                    {
                        taint[slot] |= incoming[slot];
                    }
                }

                var read = ReadTaint(instruction, taint);
                var tableLoad = Array.IndexOf(tableLoads, instruction.Pc);
                ulong written;
                if (fetches.Contains(instruction.Pc))
                {
                    written = 0;
                }
                else if (tableLoad >= 0 && (kept & (1ul << tableLoad)) == 0 && instruction.Control is Gen5ScalarMemoryControl)
                {
                    written = read | (1ul << tableLoad);
                }
                else
                {
                    if (read != 0 && HasEffectsBeyondRegisters(instruction))
                    {
                        foreign |= read;
                    }

                    written = read;
                }

                changed |= WriteTaint(instruction, taint, written, straightLine);
                if (straightLine && forwardTargets[index] is { } target)
                {
                    if (branched.TryGetValue(target, out var pending))
                    {
                        for (var slot = 0; slot < SlotCount; slot++)
                        {
                            pending[slot] |= taint[slot];
                        }
                    }
                    else
                    {
                        branched[target] = (ulong[])taint.Clone();
                    }
                }
            }
        }
        while (!straightLine && changed);

        return foreign;
    }

    private static ulong ReadTaint(Gen5ShaderInstruction instruction, ulong[] taint)
    {
        ulong read = 0;
        void Read(Gen5Operand operand, int count)
        {
            var slot = Slot(operand);
            var end = slot < ScalarSlotCount ? ScalarSlotCount : SlotCount;
            for (var offset = 0; offset < count && slot >= 0 && slot + offset < end; offset++)
            {
                read |= taint[slot + offset];
            }
        }

        // Lane transfers decode an unused third VOP3 source; only the value and the lane are read.
        var opcode = instruction.Opcode;
        var width = IsWide(opcode) ? 2 : 1;
        var sources = opcode is "VReadlaneB32" or "VWritelaneB32" ? instruction.Sources.Take(2) : instruction.Sources;
        foreach (var source in sources)
        {
            Read(source, width);
        }

        switch (instruction.Control)
        {
            case Gen5ScalarMemoryControl scalarLoad:
                if (instruction.Sources.Count > 0)
                {
                    Read(instruction.Sources[0], opcode.StartsWith("SBuffer", StringComparison.Ordinal) ? 4 : 2);
                }

                if (scalarLoad.DynamicOffsetRegister is { } offsetRegister)
                {
                    Read(Gen5Operand.Scalar(offsetRegister), 1);
                }

                break;
            case Gen5BufferMemoryControl buffer:
                Read(Gen5Operand.Scalar(buffer.ScalarResource), 4);
                Read(Gen5Operand.Vector(buffer.VectorAddress), 2);
                break;
            case Gen5ImageControl image:
                Read(Gen5Operand.Scalar(image.ScalarResource), 8);
                Read(Gen5Operand.Scalar(image.ScalarSampler), 4);
                Read(Gen5Operand.Vector(image.VectorAddress), 4);
                foreach (var address in image.AddressRegisters)
                {
                    Read(Gen5Operand.Vector(address), 1);
                }

                break;
            case Gen5GlobalMemoryControl global:
                Read(Gen5Operand.Scalar(global.ScalarAddress), 2);
                Read(Gen5Operand.Vector(global.VectorAddress), 2);
                break;
        }

        if (opcode.StartsWith("SCselect", StringComparison.Ordinal) || opcode.StartsWith("SCbranchScc", StringComparison.Ordinal) ||
            opcode.StartsWith("SCmov", StringComparison.Ordinal) || opcode.StartsWith("SAddc", StringComparison.Ordinal) ||
            opcode.StartsWith("SSubb", StringComparison.Ordinal))
        {
            read |= taint[SccSlot];
        }

        if (opcode.Contains("Cndmask", StringComparison.Ordinal) || opcode.Contains("Addc", StringComparison.Ordinal) ||
            opcode.Contains("Subb", StringComparison.Ordinal) || opcode.Contains("Vcc", StringComparison.Ordinal))
        {
            Read(Gen5Operand.Scalar(VccLowRegister), 2);
        }

        if (opcode.Contains("Exec", StringComparison.Ordinal) || opcode.Contains("exec", StringComparison.Ordinal))
        {
            Read(Gen5Operand.Scalar(ExecLowRegister), 2);
        }

        return read;
    }

    // Exact writes replace the slot; writes whose extent is only inferred from the
    // opcode, or that touch part of a register, can merge but never clear.
    private static bool WriteTaint(Gen5ShaderInstruction instruction, ulong[] taint, ulong value, bool replace)
    {
        var changed = false;
        void Write(int slot, bool exact)
        {
            if (slot < 0 || slot >= SlotCount)
            {
                return;
            }

            var next = exact && replace ? value : taint[slot] | value;
            changed |= (next & ~taint[slot]) != 0;
            taint[slot] = next;
        }

        var opcode = instruction.Opcode;
        var merges = opcode == "VWritelaneB32" || opcode.Contains("Movrel", StringComparison.Ordinal);
        var wide = IsWide(opcode);
        foreach (var destination in instruction.Destinations)
        {
            var slot = Slot(destination);
            Write(slot, !merges);
            if (wide && slot >= 0 && (slot < ScalarSlotCount ? slot + 1 < ScalarSlotCount : slot + 1 < SlotCount))
            {
                Write(slot + 1, false);
            }
        }

        if (instruction.Control is Gen5ScalarMemoryControl scalarLoad && instruction.Destinations.Count != 0 &&
            Slot(instruction.Destinations[0]) is var first and >= 0 and < ScalarSlotCount)
        {
            for (var component = 1; component < scalarLoad.DestinationCount && first + component < ScalarSlotCount; component++)
            {
                Write(first + component, false);
            }
        }

        var scalarDestination = instruction.Control switch
        {
            Gen5Vop3Control vop3 => vop3.ScalarDestination,
            Gen5SdwaControl sdwa => sdwa.ScalarDestination,
            _ => null,
        };
        if (scalarDestination is { } carry and < ScalarSlotCount)
        {
            Write((int)carry, true);
            Write((int)carry + 1, false);
        }

        if (instruction.Encoding == Gen5ShaderEncoding.Vopc && instruction.Destinations.Count == 0)
        {
            var mask = opcode.StartsWith("VCmpx", StringComparison.Ordinal) ? ExecLowRegister : VccLowRegister;
            Write((int)mask, true);
            Write((int)mask + 1, false);
        }
        else if (instruction.Encoding == Gen5ShaderEncoding.Vop2 &&
                 (opcode.Contains("Addc", StringComparison.Ordinal) || opcode.Contains("Subb", StringComparison.Ordinal) ||
                  opcode.Contains("Co", StringComparison.Ordinal)))
        {
            Write((int)VccLowRegister, false);
            Write((int)VccLowRegister + 1, false);
        }

        if (instruction.Encoding is Gen5ShaderEncoding.Sop1 or Gen5ShaderEncoding.Sop2 or Gen5ShaderEncoding.Sopc or Gen5ShaderEncoding.Sopk)
        {
            Write(SccSlot, WritesScc(opcode));
        }

        return changed;
    }

    // Register-only ALU work is fine to feed with a removed load's garbage as long as the
    // result is itself unused; memory accesses, exports, control flow and writes to EXEC,
    // M0 or hardware state turn a derived value into an observable one.
    private static bool HasEffectsBeyondRegisters(Gen5ShaderInstruction instruction)
    {
        var opcode = instruction.Opcode;
        if (instruction.Encoding is not (Gen5ShaderEncoding.Sop1 or Gen5ShaderEncoding.Sop2 or Gen5ShaderEncoding.Sopc or
            Gen5ShaderEncoding.Sopk or Gen5ShaderEncoding.Vop1 or Gen5ShaderEncoding.Vop2 or Gen5ShaderEncoding.Vopc or
            Gen5ShaderEncoding.Vop3 or Gen5ShaderEncoding.Vop3p))
        {
            return true;
        }

        if (WritesProgramCounter(opcode) || opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
            opcode.Contains("Exec", StringComparison.Ordinal) || opcode.Contains("exec", StringComparison.Ordinal) ||
            opcode.Contains("Movrel", StringComparison.Ordinal) || opcode.StartsWith("SSetreg", StringComparison.Ordinal))
        {
            return true;
        }

        return instruction.Destinations.Any(destination =>
            destination is { Kind: Gen5OperandKind.ScalarRegister, Value: M0Register or ExecLowRegister or ExecLowRegister + 1 });
    }

    private static int Slot(Gen5Operand operand) => operand.Kind switch
    {
        Gen5OperandKind.ScalarRegister when operand.Value < ScalarSlotCount => (int)operand.Value,
        Gen5OperandKind.VectorRegister when operand.Value < SlotCount - VectorSlot => VectorSlot + (int)operand.Value,
        _ => -1,
    };

    private static bool IsWide(string opcode) =>
        opcode.Contains("B64", StringComparison.Ordinal) || opcode.Contains("U64", StringComparison.Ordinal) ||
        opcode.Contains("I64", StringComparison.Ordinal) || opcode.Contains("F64", StringComparison.Ordinal);

    private static bool WritesProgramCounter(string opcode) =>
        opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64" or "SCbranchJoin";

    private static bool WritesScc(string opcode) =>
        opcode.StartsWith("SCmp", StringComparison.Ordinal) || opcode.StartsWith("SAdd", StringComparison.Ordinal) ||
        opcode.StartsWith("SSub", StringComparison.Ordinal) || opcode.StartsWith("SAnd", StringComparison.Ordinal) ||
        opcode.StartsWith("SOr", StringComparison.Ordinal) || opcode.StartsWith("SXor", StringComparison.Ordinal) ||
        opcode.StartsWith("SNand", StringComparison.Ordinal) || opcode.StartsWith("SNor", StringComparison.Ordinal) ||
        opcode.StartsWith("SXnor", StringComparison.Ordinal) || opcode.StartsWith("SLshl", StringComparison.Ordinal) ||
        opcode.StartsWith("SLshr", StringComparison.Ordinal) || opcode.StartsWith("SAshr", StringComparison.Ordinal) ||
        opcode.StartsWith("SBfe", StringComparison.Ordinal) || opcode.StartsWith("SNot", StringComparison.Ordinal);
}

// Finds the attribute and buffer table walks of an embedded vertex fetch and the
// buffer loads they end in, so the host can bind them as fixed-function attributes.
public static class EmbeddedVertexFetchDetector
{
    private const int ScalarRegisterCount = 108;
    private const int VectorRegisterCount = 256;
    private const uint VccLow = 106;
    private const uint VccHigh = 107;

    private enum ValueType : byte
    {
        Unknown,
        Constant,
        AttributeTable,
        Attribute,
        BufferTable,
        Buffer,
        Index,
    }

    private struct ScalarInfo
    {
        public ValueType Type;
        public int AttributeId;
        public uint Value;
        public List<uint>? PrologLoads;

        public readonly ScalarInfo Copy() => new()
        {
            Type = Type,
            AttributeId = AttributeId,
            Value = Value,
            PrologLoads = PrologLoads is null ? null : [.. PrologLoads],
        };
    }

    public static EmbeddedVertexFetchPlan Detect(
        Gen5ShaderProgram program,
        int attributeTableRegister,
        int bufferTableRegister,
        uint userDataBase,
        uint userDataCount,
        uint waveSize)
    {
        var plan = new EmbeddedVertexFetchPlan();
        var vertexOffsetCandidate = -1;
        var instanceOffsetCandidate = -1;
        var vertexOffsetConflict = false;
        var instanceOffsetConflict = false;
        var scalars = new ScalarInfo[ScalarRegisterCount];
        var vectors = new ValueType[VectorRegisterCount];
        var lanes = new Dictionary<(uint Register, uint Lane), ScalarInfo>();
        var trackLanes = !program.Instructions.Any(instruction => HasBranch(instruction.Opcode));

        void Mark(int register, ValueType type)
        {
            if (register >= 0 && register < ScalarRegisterCount)
            {
                scalars[register].Type = type;
            }
        }

        Mark(attributeTableRegister, ValueType.AttributeTable);
        Mark(attributeTableRegister + 1, ValueType.AttributeTable);
        Mark(bufferTableRegister, ValueType.BufferTable);
        Mark(bufferTableRegister + 1, ValueType.BufferTable);

        foreach (var instruction in program.Instructions)
        {
            var destination = instruction.Destinations.Count != 0 ? instruction.Destinations[0] : default(Gen5Operand?);
            var source0 = instruction.Sources.Count > 0 ? instruction.Sources[0] : default(Gen5Operand?);
            var source1 = instruction.Sources.Count > 1 ? instruction.Sources[1] : default(Gen5Operand?);
            var source2 = instruction.Sources.Count > 2 ? instruction.Sources[2] : default(Gen5Operand?);

            // Fetch shaders accumulate the draw's vertex offset in v0, or in v5 and the
            // instance offset in v8 under the user-data-at-s8 layout.
            var vertexIndexAccumulator = IsVector(destination) && (destination!.Value.Value == 0 || (userDataBase == 8 && destination.Value.Value == 5));
            var instanceIndexAccumulator = IsVector(destination) && destination!.Value.Value == (userDataBase == 8 ? 8u : 3u);
            var indexOffsetAdd = (vertexIndexAccumulator || instanceIndexAccumulator) && IsTrackedScalarRegister(source0) &&
                ((instruction.Opcode == "VAddI32" && IsVector(source1) && source1!.Value.Value == destination!.Value.Value) ||
                 (userDataBase == 8 && destination!.Value.Value is 5 or 8 && instruction.Opcode == "VSadU32" &&
                  IsVector(source2) && source2!.Value.Value == destination.Value.Value &&
                  TryConstant(scalars, source1, out var sadZero) && sadZero == 0));
            if (plan.Loads.Count == 0 && indexOffsetAdd)
            {
                var register = ScalarRegister(source0!.Value);
                if (register >= userDataBase && register - userDataBase < userDataCount)
                {
                    ref var candidate = ref (vertexIndexAccumulator ? ref vertexOffsetCandidate : ref instanceOffsetCandidate);
                    ref var conflict = ref (vertexIndexAccumulator ? ref vertexOffsetConflict : ref instanceOffsetConflict);
                    if (candidate >= 0 && candidate != (int)register)
                    {
                        conflict = true;
                    }
                    else
                    {
                        candidate = (int)register;
                    }
                }
            }

            switch (instruction.Opcode)
            {
                case "VWritelaneB32":
                    if (IsVector(destination) && destination!.Value.Value < VectorRegisterCount)
                    {
                        vectors[destination.Value.Value] = ValueType.Unknown;
                    }

                    if (trackLanes && IsVector(destination) && IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount &&
                        TryConstant(scalars, source1, out var lane))
                    {
                        lanes[(destination!.Value.Value, Lane(lane, waveSize))] = scalars[ScalarRegister(source0.Value)].Copy();
                    }
                    else if (IsVector(destination))
                    {
                        ClearLanes(lanes, destination!.Value.Value);
                    }

                    break;
                case "VReadlaneB32":
                    if (trackLanes && IsTrackedScalarRegister(destination) && ScalarRegister(destination!.Value) < ScalarRegisterCount && IsVector(source0) &&
                        TryConstant(scalars, source1, out var readLane))
                    {
                        scalars[ScalarRegister(destination.Value)] = lanes.TryGetValue((source0!.Value.Value, Lane(readLane, waveSize)), out var stored)
                            ? stored.Copy()
                            : default;
                    }
                    else if (IsTrackedScalarRegister(destination))
                    {
                        ClearScalars(scalars, destination!.Value, 1);
                    }

                    break;
                case "SMovB32":
                    if (IsTrackedScalarRegister(destination) && IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount)
                    {
                        scalars[ScalarRegister(destination!.Value)] = scalars[ScalarRegister(source0.Value)].Copy();
                    }
                    else if (IsTrackedScalarRegister(destination))
                    {
                        if (TryConstant(scalars, source0, out var value))
                        {
                            scalars[ScalarRegister(destination!.Value)] = new ScalarInfo { Type = ValueType.Constant, Value = value };
                        }
                        else
                        {
                            ClearScalars(scalars, destination!.Value, 1);
                        }
                    }

                    break;
                case "SMovkI32":
                    if (IsTrackedScalarRegister(destination))
                    {
                        scalars[ScalarRegister(destination!.Value)] = new ScalarInfo
                        {
                            Type = ValueType.Constant,
                            Value = unchecked((uint)(short)instruction.Sources[0].Value),
                        };
                    }

                    break;
                default:
                    if (instruction.Control is Gen5ScalarMemoryControl scalarLoad && instruction.Opcode.StartsWith("SLoadDword", StringComparison.Ordinal))
                    {
                        ApplyScalarLoad(instruction, scalarLoad, scalars);
                    }
                    else if (instruction.Opcode == "VCndmaskB32")
                    {
                        if (IsVector(destination) && destination!.Value.Value < VectorRegisterCount)
                        {
                            ClearLanes(lanes, destination.Value.Value);
                            if (IsVector(source0) && source0!.Value.Value == 8 && IsVector(source1) && source1!.Value.Value == 5)
                            {
                                vectors[destination.Value.Value] = ValueType.Index;
                            }
                        }
                    }
                    else if (IsAttributePropagationAlu(instruction.Opcode))
                    {
                        if (IsTrackedScalarRegister(destination) && IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount &&
                            scalars[ScalarRegister(source0.Value)].Type == ValueType.Attribute)
                        {
                            scalars[ScalarRegister(destination!.Value)] = scalars[ScalarRegister(source0.Value)].Copy();
                        }
                        else if (IsTrackedScalarRegister(destination))
                        {
                            if (TryConstant(scalars, source0, out var left) && TryConstant(scalars, source1, out var right))
                            {
                                scalars[ScalarRegister(destination!.Value)] = new ScalarInfo
                                {
                                    Type = ValueType.Constant,
                                    Value = instruction.Opcode switch
                                    {
                                        "SAndB32" => left & right,
                                        "SLshlB32" => left << (int)(right & 31),
                                        "SBfeU32" => left >> (int)(right & 31),
                                        _ => unchecked(left + right),
                                    },
                                };
                            }
                            else
                            {
                                ClearScalars(scalars, destination!.Value, 1);
                            }
                        }
                    }
                    else if (instruction.Control is Gen5BufferMemoryControl buffer && IsFetchBufferLoad(instruction.Opcode))
                    {
                        if (buffer.VectorAddress < VectorRegisterCount && vectors[buffer.VectorAddress] == ValueType.Index &&
                            buffer.ScalarResource < ScalarRegisterCount && scalars[buffer.ScalarResource].Type == ValueType.Buffer)
                        {
                            var table = scalars[buffer.ScalarResource];
                            if (plan.Loads.Count == 0)
                            {
                                if (!vertexOffsetConflict)
                                {
                                    plan.VertexOffsetScalarRegister = vertexOffsetCandidate;
                                }

                                if (!instanceOffsetConflict)
                                {
                                    plan.InstanceOffsetScalarRegister = instanceOffsetCandidate;
                                }
                            }

                            plan.Loads.Add(new EmbeddedVertexFetchLoad(instruction.Pc, table.AttributeId, Math.Max(buffer.DwordCount, 1u), table.PrologLoads is null ? [] : [.. table.PrologLoads]));
                        }
                    }

                    break;
            }

            if (instruction.Opcode == "VMovreldB32")
            {
                lanes.Clear();
            }
            else if (instruction.Opcode != "VWritelaneB32")
            {
                foreach (var written in instruction.Destinations)
                {
                    if (written.Kind == Gen5OperandKind.VectorRegister)
                    {
                        ClearLanes(lanes, written.Value);
                    }
                }
            }
        }

        return plan;
    }

    // A scalar load through the attribute table yields attribute records; one through
    // the buffer table, or through a loaded attribute, yields buffer records.
    private static void ApplyScalarLoad(Gen5ShaderInstruction instruction, Gen5ScalarMemoryControl control, ScalarInfo[] scalars)
    {
        var destination = instruction.Destinations.Count != 0 ? instruction.Destinations[0] : default(Gen5Operand?);
        var source0 = instruction.Sources.Count > 0 ? instruction.Sources[0] : default(Gen5Operand?);
        var source1 = instruction.Sources.Count > 1 ? instruction.Sources[1] : default(Gen5Operand?);
        if (!IsTrackedScalarRegister(destination))
        {
            return;
        }

        var register = ScalarRegister(destination!.Value);
        var count = Math.Max(control.DestinationCount, 1u);
        bool TryOffset(out uint rawOffset)
        {
            rawOffset = 0;
            uint baseOffset = 0;
            if (source1 is { } offsetOperand && !TryConstant(scalars, offsetOperand, out baseOffset))
            {
                return false;
            }

            var value = (ulong)baseOffset + unchecked((uint)control.ImmediateOffsetBytes);
            if (value > uint.MaxValue)
            {
                return false;
            }

            rawOffset = (uint)value;
            return true;
        }

        if (IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount && scalars[ScalarRegister(source0.Value)].Type == ValueType.AttributeTable)
        {
            if (TryOffset(out var rawOffset))
            {
                var index = (int)(rawOffset / 4);
                for (uint component = 0; component < count && register + component < ScalarRegisterCount; component++)
                {
                    scalars[register + component] = new ScalarInfo
                    {
                        Type = ValueType.Attribute,
                        AttributeId = index + (int)component,
                        PrologLoads = [instruction.Pc],
                    };
                }
            }
            else
            {
                ClearScalars(scalars, destination.Value, count);
            }
        }
        else if (IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount && scalars[ScalarRegister(source0.Value)].Type == ValueType.BufferTable)
        {
            if (TryOffset(out var rawOffset))
            {
                for (uint component = 0; component < count && register + component < ScalarRegisterCount; component++)
                {
                    scalars[register + component] = new ScalarInfo
                    {
                        Type = ValueType.Buffer,
                        AttributeId = (int)((rawOffset + component * 4) / 16),
                        PrologLoads = [instruction.Pc],
                    };
                }
            }
            else if (source1 is { } attribute && IsTrackedScalarRegister(attribute) && ScalarRegister(attribute) < ScalarRegisterCount &&
                     scalars[ScalarRegister(attribute)].Type == ValueType.Attribute && (control.ImmediateOffsetBytes & 3) == 0)
            {
                var loaded = scalars[ScalarRegister(attribute)];
                for (uint component = 0; component < count && register + component < ScalarRegisterCount; component++)
                {
                    scalars[register + component] = new ScalarInfo
                    {
                        Type = ValueType.Buffer,
                        AttributeId = loaded.AttributeId,
                        PrologLoads = [.. loaded.PrologLoads ?? [], instruction.Pc],
                    };
                }
            }
            else
            {
                ClearScalars(scalars, destination.Value, count);
            }
        }
        else
        {
            ClearScalars(scalars, destination.Value, count);
        }
    }

    private static bool HasBranch(string opcode) =>
        opcode is "SSetpcB64" or "SBranch" or "SCbranchScc0" or "SCbranchScc1" or "SCbranchVccz" or "SCbranchVccnz" or
            "SCbranchExecz" or "SCbranchExecnz";

    private static bool IsFetchBufferLoad(string opcode) =>
        opcode is "BufferLoadFormatX" or "BufferLoadFormatXy" or "BufferLoadFormatXyz" or "BufferLoadFormatXyzw";

    private static bool IsAttributePropagationAlu(string opcode) =>
        opcode is "SBfeU32" or "SAndB32" or "SAddI32" or "SAddU32" or "SLshlB32";

    // Track general scalar registers and VCC. M0 and EXEC remain shader state,
    // but cannot carry table records in this analysis.
    private static bool IsTrackedScalarRegister(Gen5Operand? operand) =>
        operand is { Kind: Gen5OperandKind.ScalarRegister, Value: < ScalarRegisterCount };

    private static bool IsVector(Gen5Operand? operand) => operand is { Kind: Gen5OperandKind.VectorRegister };

    private static uint ScalarRegister(Gen5Operand operand) => operand.Value;

    private static uint Lane(uint lane, uint waveSize) => waveSize is 32 or 64 ? lane % waveSize : lane;

    private static bool TryConstant(ScalarInfo[] scalars, Gen5Operand? operand, out uint value)
    {
        value = 0;
        if (operand is not { } source)
        {
            return false;
        }

        switch (source.Kind)
        {
            case Gen5OperandKind.LiteralConstant:
                value = source.Value;
                return true;
            case Gen5OperandKind.EncodedConstant:
                if (source.Value == 125)
                {
                    value = 0;
                    return true;
                }

                return Gen5InlineConstants.TryDecode(source.Value, out value);
            case Gen5OperandKind.ScalarRegister when source.Value < ScalarRegisterCount && scalars[source.Value].Type == ValueType.Constant:
                value = scalars[source.Value].Value;
                return true;
            default:
                return false;
        }
    }

    private static void ClearScalars(ScalarInfo[] scalars, Gen5Operand destination, uint count)
    {
        if (destination.Kind != Gen5OperandKind.ScalarRegister)
        {
            return;
        }

        for (uint index = 0; index < count && destination.Value + index < ScalarRegisterCount; index++)
        {
            scalars[destination.Value + index] = default;
        }
    }

    private static void ClearLanes(Dictionary<(uint Register, uint Lane), ScalarInfo> lanes, uint register)
    {
        foreach (var key in lanes.Keys.Where(key => key.Register == register).ToArray())
        {
            lanes.Remove(key);
        }
    }
}
