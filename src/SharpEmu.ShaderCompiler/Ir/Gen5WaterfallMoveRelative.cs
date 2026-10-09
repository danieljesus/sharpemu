// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Ir;

// Compilers index a per-lane array kept in VGPRs with a waterfall loop around V_MOVREL*:
//
//   loop: s_x = v_readfirstlane v_index
//         v_cmpx_eq_u32 s_x, v_index          ; EXEC = the lanes with that index
//         s_mov_b32 m0, s_x
//         v_movrels / v_movreld ...            ; relative to M0
//         s_andn2_b64 s_left, s_left, exec     ; retire those lanes
//         s_mov_b64 exec, s_left
//         s_cbranch_scc1 loop
//
// In the translated shader every invocation is one lane with its own scalar registers, so
// the loop is the same as each lane setting M0 to its own index once. Dozens of these small
// loops in a row made NVIDIA's compiler take minutes on one GTA V compute shader.
//
// A loop is collapsed in place (same PCs) only when nothing can tell the difference:
// s_left equals EXEC on entry, EXEC is overwritten right after the loop, and neither the
// readfirstlane destination nor M0 is read before being written again.
public static class Gen5WaterfallMoveRelative
{
    // Writes VGPR source 0 of the lane to M0. Only produced by this pass.
    public const string IndexToM0 = "VWaterfallIndexToM0";

    private const uint M0 = 124;
    private const uint Exec = 126;

    public static Gen5ShaderProgram Collapse(Gen5ShaderProgram program)
    {
        var instructions = program.Instructions;
        if (!instructions.Any(static instruction => instruction.Opcode is "VMovrelsB32" or "VMovreldB32"))
        {
            return program;
        }

        var targets = BranchTargets(instructions);
        List<Gen5ShaderInstruction>? rewritten = null;
        for (var head = 0; head < instructions.Count; head++)
        {
            if (TryMatch(instructions, targets, head, out var loop))
            {
                rewritten ??= [.. instructions];
                Rewrite(rewritten, loop);
                head = loop.Branch;
            }
        }

        return rewritten is null ? program : program with { Instructions = rewritten };
    }

    private readonly record struct Loop(int Head, int Branch, uint Lane, uint Index, uint Left, int Retire);

    private static bool TryMatch(IReadOnlyList<Gen5ShaderInstruction> code, HashSet<uint> targets, int head, out Loop loop)
    {
        loop = default;
        if (head + 6 >= code.Count ||
            code[head].Opcode != "VReadfirstlaneB32" ||
            !IsScalar(code[head].Destinations, out var lane) ||
            code[head].Sources.Count == 0 || code[head].Sources[0].Kind != Gen5OperandKind.VectorRegister)
        {
            return false;
        }

        var index = code[head].Sources[0].Value;
        var compare = code[head + 1];
        if (compare.Opcode != "VCmpxEqU32" || compare.Sources.Count != 2 ||
            !compare.Sources.Contains(Gen5Operand.Scalar(lane)) || !compare.Sources.Contains(Gen5Operand.Vector(index)))
        {
            return false;
        }

        var setM0 = code[head + 2];
        if (setM0.Opcode != "SMovB32" || !IsScalar(setM0.Destinations, out var m0) || m0 != M0 ||
            setM0.Sources.Count != 1 || setM0.Sources[0] != Gen5Operand.Scalar(lane))
        {
            return false;
        }

        // The body: relative moves plus one s_andn2 retiring the lanes, then EXEC = s_left
        // and the backward branch to the head.
        var retire = -1;
        var left = 0u;
        var position = head + 3;
        for (; position < code.Count; position++)
        {
            var instruction = code[position];
            if (targets.Contains(instruction.Pc))
            {
                return false;
            }

            if (instruction.Opcode is "VMovrelsB32" or "VMovreldB32")
            {
                if (instruction.Sources.Any(operand => operand == Gen5Operand.Scalar(lane)))
                {
                    return false;
                }

                continue;
            }

            // A wait between the M0 copy and the move (GTA V's BVH refit does this) changes
            // nothing the loop computes.
            if (instruction.Opcode is "SWaitcnt" or "SNop")
            {
                continue;
            }

            if (instruction.Opcode == "SAndn2B64" && retire < 0 && IsScalar(instruction.Destinations, out left) &&
                instruction.Sources.Count == 2 &&
                instruction.Sources[0] == Gen5Operand.Scalar(left) && instruction.Sources[1] == Gen5Operand.Scalar(Exec))
            {
                retire = position;
                continue;
            }

            break;
        }

        if (retire < 0 || position + 1 >= code.Count)
        {
            return false;
        }

        var restore = code[position];
        var branch = code[position + 1];
        if (restore.Opcode != "SMovB64" || !IsScalar(restore.Destinations, out var restored) || restored != Exec ||
            restore.Sources.Count != 1 || restore.Sources[0] != Gen5Operand.Scalar(left) ||
            branch.Opcode != "SCbranchScc1" || BranchTarget(branch) != code[head].Pc ||
            left == lane || left is M0 or Exec || lane is M0 or Exec)
        {
            return false;
        }

        loop = new Loop(head, position + 1, lane, index, left, retire);
        return LeftEqualsExecOnEntry(code, targets, head, left) && ExecOverwrittenAfter(code, loop.Branch + 1) &&
            IsDeadAfter(code, loop.Branch + 1, lane) && IsDeadAfter(code, loop.Branch + 1, M0);
    }

    private static void Rewrite(List<Gen5ShaderInstruction> code, Loop loop)
    {
        var head = code[loop.Head];
        code[loop.Head] = head with
        {
            Opcode = IndexToM0,
            Sources = [Gen5Operand.Vector(loop.Index)],
            Destinations = [Gen5Operand.Scalar(M0)],
            Control = null,
        };
        code[loop.Head + 1] = Nop(code[loop.Head + 1]);
        code[loop.Head + 2] = Nop(code[loop.Head + 2]);
        // The loop leaves s_left and SCC at zero; s_and_b64 with two zeros does the same.
        code[loop.Retire] = code[loop.Retire] with
        {
            Encoding = Gen5ShaderEncoding.Sop2,
            Opcode = "SAndB64",
            Sources = [Gen5Operand.Source(128), Gen5Operand.Source(128)],
            Destinations = [Gen5Operand.Scalar(loop.Left)],
            Control = null,
        };
        code[loop.Branch - 1] = Nop(code[loop.Branch - 1]);
        code[loop.Branch] = Nop(code[loop.Branch]);
    }

    private static Gen5ShaderInstruction Nop(Gen5ShaderInstruction instruction) =>
        instruction with
        {
            Encoding = Gen5ShaderEncoding.Sopp,
            Opcode = "SNop",
            Words = [0xBF800000],
            Sources = [],
            Destinations = [],
            Control = null,
        };

    // Walking back from the head within the block: s_left and EXEC must be copies of each
    // other, or both copies of one register that was not rewritten between the two copies.
    private static bool LeftEqualsExecOnEntry(IReadOnlyList<Gen5ShaderInstruction> code, HashSet<uint> targets, int head, uint left)
    {
        Gen5Operand? firstSource = null;
        var firstIsLeft = false;
        for (var position = head - 1; position >= 0; position--)
        {
            var instruction = code[position];
            if (IsBranch(instruction))
            {
                return false;
            }

            var writesLeft = WritesPair(instruction, left);
            var writesExec = WritesPair(instruction, Exec) || WritesExecImplicitly(instruction);
            if (firstSource is { } shared && shared.Kind == Gen5OperandKind.ScalarRegister &&
                WritesPair(instruction, shared.Value) && !(writesLeft || writesExec))
            {
                return false;
            }

            if (writesLeft || writesExec)
            {
                if (writesLeft && writesExec || instruction.Opcode != "SMovB64" || instruction.Sources.Count != 1 ||
                    instruction.Sources[0].Kind != Gen5OperandKind.ScalarRegister)
                {
                    return false;
                }

                var source = instruction.Sources[0];
                if (firstSource is null)
                {
                    // The copy nearest the head: one of them may simply copy the other.
                    if (writesLeft && source == Gen5Operand.Scalar(Exec) || writesExec && source == Gen5Operand.Scalar(left))
                    {
                        return true;
                    }

                    firstSource = source;
                    firstIsLeft = writesLeft;
                }
                else if (writesLeft == firstIsLeft)
                {
                    return false;
                }
                else
                {
                    return source == firstSource;
                }
            }

            if (targets.Contains(instruction.Pc))
            {
                return false;
            }
        }

        return false;
    }

    private static bool ExecOverwrittenAfter(IReadOnlyList<Gen5ShaderInstruction> code, int position) =>
        position < code.Count && code[position] is { Opcode: "SMovB64" } restore &&
        IsScalar(restore.Destinations, out var register) && register == Exec &&
        restore.Sources.Count == 1 && restore.Sources[0].Kind == Gen5OperandKind.ScalarRegister;

    // Every path out of the loop must write the register before anything that could read
    // it; M0 is also read implicitly by relative moves, LDS/GDS and a few others. Forward
    // branches are followed on both sides; a backward or indirect branch may reach a read.
    private static bool IsDeadAfter(IReadOnlyList<Gen5ShaderInstruction> code, int start, uint register)
    {
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            for (var position = pending.Pop(); position < code.Count && visited.Add(position); position++)
            {
                var instruction = code[position];
                if (instruction.Opcode == "SEndpgm")
                {
                    break;
                }

                if (IsBranch(instruction))
                {
                    if (instruction.Opcode is not "SBranch" && !instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var target = IndexOfPc(code, BranchTarget(instruction));
                    if (target < start)
                    {
                        // Back before the loop's exit: the code there may read the register.
                        return false;
                    }

                    if (target > position)
                    {
                        pending.Push(target);
                    }
                    // A branch back to code this walk already passed without a read or a
                    // write of the register cannot reach one by repeating it.

                    if (instruction.Opcode == "SBranch")
                    {
                        break;
                    }

                    continue;
                }

                if (instruction.Sources.Any(operand => operand.Kind == Gen5OperandKind.ScalarRegister && operand.Value == register) ||
                    register == M0 && ReadsM0Implicitly(instruction))
                {
                    return false;
                }

                if (instruction.Destinations.Any(operand => operand.Kind == Gen5OperandKind.ScalarRegister && operand.Value == register))
                {
                    break;
                }
            }
        }

        return true;
    }

    private static int IndexOfPc(IReadOnlyList<Gen5ShaderInstruction> code, uint pc)
    {
        for (var index = 0; index < code.Count; index++)
        {
            if (code[index].Pc == pc)
            {
                return index;
            }
        }

        return -1;
    }

    // On GFX10 only the GWS and ordered-count LDS instructions read M0, and a buffer
    // instruction only when it targets LDS (the LDS bit of its first word).
    private const uint BufferLdsBit = 1u << 16;

    private static bool ReadsM0Implicitly(Gen5ShaderInstruction instruction) =>
        instruction.Opcode.Contains("Movrel", StringComparison.Ordinal) ||
        instruction.Opcode.StartsWith("DsGws", StringComparison.Ordinal) ||
        instruction.Opcode.StartsWith("DsOrderedCount", StringComparison.Ordinal) ||
        instruction.Opcode.StartsWith("VInterp", StringComparison.Ordinal) ||
        instruction.Opcode.StartsWith("SSendmsg", StringComparison.Ordinal) ||
        instruction.Opcode.StartsWith("Buffer", StringComparison.Ordinal) && (instruction.Words.Count == 0 || (instruction.Words[0] & BufferLdsBit) != 0) ||
        instruction.Opcode.StartsWith("Global", StringComparison.Ordinal) ||
        instruction.Opcode.StartsWith("Flat", StringComparison.Ordinal) ||
        instruction.Opcode.StartsWith("Scratch", StringComparison.Ordinal);

    private static bool WritesPair(Gen5ShaderInstruction instruction, uint low) =>
        instruction.Destinations.Any(operand =>
            operand.Kind == Gen5OperandKind.ScalarRegister && (operand.Value == low || operand.Value == low + 1));

    private static bool WritesExecImplicitly(Gen5ShaderInstruction instruction) =>
        instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
        instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
        instruction.Opcode.Contains("Wrexec", StringComparison.Ordinal);

    private static bool IsScalar(IReadOnlyList<Gen5Operand> operands, out uint register)
    {
        register = 0;
        if (operands.Count != 1 || operands[0].Kind != Gen5OperandKind.ScalarRegister)
        {
            return false;
        }

        register = operands[0].Value;
        return true;
    }

    private static bool IsBranch(Gen5ShaderInstruction instruction) =>
        instruction.Opcode is "SBranch" or "SSetpcB64" or "SSwappcB64" or "SRfeB64" ||
        instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal);

    private static uint BranchTarget(Gen5ShaderInstruction branch) =>
        unchecked((uint)(branch.Pc + 4 + (short)(branch.Words[0] & 0xFFFF) * 4));

    private static HashSet<uint> BranchTargets(IReadOnlyList<Gen5ShaderInstruction> code)
    {
        var targets = new HashSet<uint>();
        foreach (var instruction in code)
        {
            if (instruction.Opcode == "SBranch" || instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                targets.Add(BranchTarget(instruction));
            }
        }

        return targets;
    }
}
