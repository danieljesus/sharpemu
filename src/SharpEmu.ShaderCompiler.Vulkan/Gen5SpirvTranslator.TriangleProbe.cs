// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_BVH_TRI_PROBE=1 (probe, off by default; needs the ray query area: SHARPEMU_RT_RAYQUERY or
// SHARPEMU_RQ_MAP): in GTA V's 0x8395 only.
//
// Triangle tests (counter words): 44 tests, 45 hits, 46 hits at t_denom == 0 and t_num == 0 (t = NaN in
// the guest), 47 hits at t_denom == 0 and t_num != 0, 48 hits with t_denom != 0 and a NaN or infinite numerator or
// denominator, 49 hits SHARPEMU_BVH_TRI_DEGENERATE rejects, 50-53 word 46 by node type 0-3, 54 tests at t_denom == 0,
// 55 other hits whose t_num / t_denom is NaN. With SHARPEMU_RT_RAYQUERY=2, the class of the triangle the native walk
// committed last (0 other, 1 = word 46, 2 = 47, 3 = 48): 56-59 lanes whose native t is NaN, 60-63 native-only hits.
//
// Index records (words 128-255, 16 slots of 8): in the alpha test, after the guest loaded the primitive's three
// indices (0x12A4/0x12E4/0x1324: v1, v2, v11, the guest weighs i with the second and j with the third), a lane
// claims a slot hashed from its ray and node and writes 1, the BLAS key, the triangle node pointer (v8), the pair
// flags (v9), the three indices and the node's triangle_id (dword 15). The host prints each distinct
// (key, node, type) once ([GPU][TRI_REC]); tri-order.py tests the vertex order of node types 2 and 3 against them.
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        internal const uint TriProbeFirstWord = 44;
        internal const uint TriRecordFirstWord = 128;
        internal const uint LeanVerifyWord = 120;
        internal const uint TriRecordSlots = 16;
        internal const uint TriRecordWords = 8;

        private bool? _triProbeActive;
        private uint _triClass;
        private uint _triCommitted;

        private bool TriProbeActive => _triProbeActive ??=
            Environment.GetEnvironmentVariable("SHARPEMU_BVH_TRI_PROBE") == "1" && _request.Hash == NativeBlasHash && RqAreaUsable;

        private void DeclareTriProbeState()
        {
            if (_triClass != 0)
                return;
            _triClass = NativeVariable("triProbeClass");
            _triCommitted = NativeVariable("triProbeCommitted");
            _triTests = NativeVariable("triProbeTests");
            _triHits = NativeVariable("triProbeHits");
            _triDenomZero = NativeVariable("triProbeDenomZero");
            _leanCompared = NativeVariable("leanVerifyCompared");
        }

        // The walk is lane-local (no subgroup operations): rare classes take an atomic each, the frequent ones are
        // summed per lane and flushed after the walk.
        private void AddLaneCount(uint word, uint condition) =>
            EmitConditional(condition, () => AtomicAdd(RqCounter(word), UInt(1)));

        private void AddPrivateCount(uint variable, uint condition) =>
            Store(variable, IAdd(Load(_uintType, variable), SelectU(condition, UInt(1), UInt(0))));

        private uint _triTests, _triHits, _triDenomZero, _leanCompared;

        // SHARPEMU_BVH_LEAN_SLABS=2: word 120 box nodes compared (flushed after each BLAS walk), 121 nodes whose children differ.
        private void CountLeanVerify(uint differs)
        {
            DeclareTriProbeState();
            AddPrivateCount(_leanCompared, _module.ConstantBool(true));
            AddLaneCount(LeanVerifyWord + 1, differs);
        }

        // At the end of EmitNativeBlasLoop.
        private void FlushTriProbe()
        {
            if (!TriProbeActive || _triClass == 0)
                return;
            foreach (var (variable, word) in new[] { (_triTests, TriProbeFirstWord), (_triHits, TriProbeFirstWord + 1), (_triDenomZero, TriProbeFirstWord + 10), (_leanCompared, LeanVerifyWord) })
            {
                var value = Load(_uintType, variable);
                EmitConditional(IsNotZero(value), () => AtomicAdd(RqCounter(word), value));
                Store(variable, UInt(0));
            }
        }

        private void EmitTriangleProbe(uint nodeType, uint missed, uint reject, uint tNum, uint tDenom, uint iNum, uint jNum)
        {
            DeclareTriProbeState();
            var zero = Float(0f);
            uint Bad(uint value) => LogicalOr(_module.AddInstruction(SpirvOp.IsNan, _boolType, value), _module.AddInstruction(SpirvOp.IsInf, _boolType, value));
            var hit = LogicalNot(missed);
            var denomZero = FCompare(SpirvOp.FOrdEqual, tDenom, zero);
            var numZero = FCompare(SpirvOp.FOrdEqual, tNum, zero);
            var classA = LogicalAnd(hit, LogicalAnd(denomZero, numZero));
            var classB = LogicalAnd(hit, LogicalAnd(denomZero, LogicalNot(numZero)));
            var classC = LogicalAnd(LogicalAnd(hit, LogicalNot(denomZero)), Any(Bad(tNum), Bad(tDenom), Bad(iNum), Bad(jNum)));
            var other = LogicalAnd(LogicalAnd(hit, LogicalNot(LogicalOr(LogicalOr(classA, classB), classC))),
                _module.AddInstruction(SpirvOp.IsNan, _boolType, FDiv(tNum, tDenom)));
            AddPrivateCount(_triTests, _module.ConstantBool(true));
            AddPrivateCount(_triHits, hit);
            AddLaneCount(TriProbeFirstWord + 2, classA);
            AddLaneCount(TriProbeFirstWord + 3, classB);
            AddLaneCount(TriProbeFirstWord + 4, classC);
            if (reject != 0)
                AddLaneCount(TriProbeFirstWord + 5, LogicalAnd(hit, reject));
            for (var type = 0u; type < 4; type++)
                AddLaneCount(TriProbeFirstWord + 6 + type, LogicalAnd(classA, _module.AddInstruction(SpirvOp.IEqual, _boolType, nodeType, UInt(type))));
            AddPrivateCount(_triDenomZero, denomZero);
            AddLaneCount(TriProbeFirstWord + 11, other);
            Store(_triClass, SelectU(classA, UInt(1), SelectU(classB, UInt(2), SelectU(classC, UInt(3), UInt(0)))));
        }

        // In the native walk's commit (0x1520-0x1538): the class of the triangle just tested.
        private void NoteTriProbeCommit()
        {
            if (TriProbeActive && _triClass != 0)
                Store(_triCommitted, Load(_uintType, _triClass));
        }

        private void ResetTriProbeCommit()
        {
            if (!TriProbeActive)
                return;
            DeclareTriProbeState();
            Store(_triCommitted, UInt(0));
        }

        // In EmitRqVerifyCompare.
        private void CountTriProbeVerify(uint nativeNan, uint nativeOnly)
        {
            if (!TriProbeActive || _triClass == 0)
                return;
            var committed = Load(_uintType, _triCommitted);
            for (var type = 0u; type < 4; type++)
            {
                var isClass = _module.AddInstruction(SpirvOp.IEqual, _boolType, committed, UInt(type));
                EmitConditional(LogicalAnd(nativeNan, isClass), () => AtomicAdd(RqCounter(TriProbeFirstWord + 12 + type), UInt(1)));
                EmitConditional(LogicalAnd(nativeOnly, isClass), () => AtomicAdd(RqCounter(TriProbeFirstWord + 16 + type), UInt(1)));
            }
        }

        // Before 0x133C (0x1338 is skipped) in the native walk's alpha test (per lane, inside its alpha-tested branch).
        private void EmitTriangleRecord(NativeBlasPlan plan)
        {
            if (!TriProbeActive)
                return;
            var ray = (Gen5RayIntersectControl)plan.Instructions[0x10E4].Control!;
            var triangleId = NativeVariable("triProbeId", InvalidNode);
            {
                var nodeLow = LoadV(8);
                var word0 = LoadS(ray.ScalarResource);
                var word1 = LoadS(ray.ScalarResource + 1);
                var bvhBase = ShiftLeftLogical64(Pair64(word0, BitwiseAnd(word1, UInt(0xFF))), ULong(8));
                var nodeAddress = IAdd64(bvhBase, ShiftLeftLogical64(And64(Widen(nodeLow), ULong(~7ul)), ULong(3)));
                var unit = ResolveNodeUnit(nodeAddress, 0);
                Store(triangleId, UInt(InvalidNode));
                EmitConditional(unit.Valid, () => Store(triangleId, LoadNodeDword(unit, TriangleIdDword)));
                var hash = _module.AddInstruction(SpirvOp.IMul, _uintType,
                    BitwiseXor(BitwiseXor(LoadV(36), ShiftLeftLogical(LoadV(37), UInt(7))), BitwiseXor(nodeLow, LoadV(39))), UInt(2654435761));
                var slot = ShiftRightLogical(hash, UInt(28));
                var first = IAdd(UInt(TriRecordFirstWord), _module.AddInstruction(SpirvOp.IMul, _uintType, slot, UInt(TriRecordWords)));
                uint Word(uint index) => BlockWordPointer(_faultBuffer, IAdd(UInt((uint)(DeviceAddressPaging.RqCounterOffset / 4)), IAdd(first, UInt(index))));
                var claim = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType, Word(0), UInt(1), UInt(0), UInt(0), UInt(1), UInt(0));
                EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, claim, UInt(0)), () =>
                {
                    uint[] values = [RqBlasKey(), nodeLow, LoadV(9), LoadV(1), LoadV(2), LoadV(11), Load(_uintType, triangleId)];
                    for (var index = 0; index < values.Length; index++)
                        Store(Word(1 + (uint)index), values[index]);
                });
            }
        }
    }
}
