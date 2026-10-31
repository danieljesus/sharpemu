// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_BLAS_CYCLE for GTA V's 0x8395 native BLAS loop: revisits of a box node.
//
// In a BVH that is a tree a walk enters each box node once (children dropped on a full stack only remove visits), so
// a walk that enters the same box twice is in a blob that is not a tree. From BlasCycleFrom iterations on (normal
// walks are ~5 iterations, 0.02 % pass 256), a lane keeps Brent's mark: the box node it entered at its 2^k-th box
// iteration; entering the mark again is a revisit. A cyclic walk is periodic once its stack is full, so the mark finds
// it within one period after 2^k exceeds the period, long before the 100k step guard.
//
// 1 (stop): a lane that revisits ends its walk after that iteration (a box iteration commits nothing), keeping its hit,
//   as the step guard would end it later.
// 2 (verify, also on with SHARPEMU_SLOW_MODE_PROBE=1): the same detection without stopping.
// Both count in the fault buffer's ray query area (DeviceAddressPaging.RqCounterOffset, words BlasCycleFirstWord..):
// walks checked, revisits, revisits that then reached the guard, revisits that ended by themselves, stops, long walks,
// guarded lanes, the longest walk, guarded TLAS waves, and one sample (key, steps, node, revisit step, ray, tmax). Lanes with a
// long, guarded or revisiting walk add their BLAS to the area's map (key, lanes, max steps, last node), whose blob the
// host reads and checks for cycles ([GPU][BLAS_CYCLE], [GPU][BLAS_GRAPH]).
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        internal const uint BlasCycleFirstWord = 84;
        internal const uint BlasCycleChecked = 0;
        internal const uint BlasCycleRevisits = 1;
        internal const uint BlasCycleRevisitGuarded = 2;
        internal const uint BlasCycleRevisitFinished = 3;
        internal const uint BlasCycleStopped = 4;
        internal const uint BlasCycleLong = 5;
        internal const uint BlasCycleGuarded = 6;
        internal const uint BlasCycleMaxSteps = 7;
        internal const uint BlasCycleTlasGuarded = 8;
        internal const uint BlasCycleTlasMax = 9;
        internal const uint BlasCycleSample = 10; // 10 claim, 11 key, 12 steps, 13 last node, 14 revisit step, 15 mark, 16-21 ray (v36-v41), 22 tmax (v23)
        // SHARPEMU_BLAS_CYCLE_LONG: the steps above which a walk is long (4096; healthy walks stay under ~650).
        internal static readonly uint BlasCycleLongSteps =
            uint.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_BLAS_CYCLE_LONG"), out var longSteps) && longSteps > 0 ? longSteps : 4096;

        private enum BlasCycleMode
        {
            Off,
            Stop,
            Verify,
        }

        private static readonly uint BlasCycleFrom =
            uint.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_BLAS_CYCLE_FROM"), out var from) && from > 0 ? from : 256;

        private BlasCycleMode? _blasCycleMode;
        private uint _cycleMark;
        private uint _cycleBoxSteps;
        private uint _cycleSeen;

        private BlasCycleMode CycleMode => _blasCycleMode ??= _request.Hash != NativeBlasHash
            ? BlasCycleMode.Off
            : DeviceAddressPaging.ReadBlasCycleMode() switch
            {
                1 => BlasCycleMode.Stop,
                2 => BlasCycleMode.Verify,
                _ => BlasCycleMode.Off,
            };

        private bool CycleCounting => CycleMode != BlasCycleMode.Off && RqAreaUsable;

        private uint CycleCounter(uint word) => RqCounter(BlasCycleFirstWord + word);

        // At the start of each walk.
        private void BeginBlasCycleWalk()
        {
            if (CycleMode == BlasCycleMode.Off)
            {
                return;
            }

            if (_cycleMark == 0)
            {
                _cycleMark = NativeVariable("blasCycleMark", uint.MaxValue);
                _cycleBoxSteps = NativeVariable("blasCycleBoxSteps");
                _cycleSeen = NativeVariable("blasCycleSeen");
            }

            Store(_cycleMark, UInt(uint.MaxValue));
            Store(_cycleBoxSteps, UInt(0));
            Store(_cycleSeen, UInt(0));
        }

        // At the start of each iteration, before the node test (v8 = the node this iteration enters).
        private void EmitBlasCycleCheck()
        {
            if (CycleMode == BlasCycleMode.Off)
            {
                return;
            }

            var steps = Load(_uintType, _nativeSteps);
            EmitConditional(_module.AddInstruction(SpirvOp.UGreaterThanEqual, _boolType, steps, UInt(BlasCycleFrom)), () =>
            {
                var node = LoadV(8);
                var box = _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, ISubU(BitwiseAnd(node, UInt(7)), UInt(4)), UInt(1));
                EmitConditional(box, () =>
                {
                    var revisit = LogicalAnd(UEqual(node, Load(_uintType, _cycleMark)), UEqual(Load(_uintType, _cycleSeen), UInt(0)));
                    EmitConditional(revisit, () => Store(_cycleSeen, IAdd(Load(_uintType, _nativeSteps), UInt(1))));
                    var count = IAdd(Load(_uintType, _cycleBoxSteps), UInt(1));
                    Store(_cycleBoxSteps, count);
                    EmitConditional(UEqual(BitwiseAnd(count, ISubU(count, UInt(1))), UInt(0)), () => Store(_cycleMark, node));
                });
            });
        }

        // Added to the loop condition: in stop mode a lane that revisited leaves.
        private uint BlasCycleContinue(uint again) =>
            CycleMode == BlasCycleMode.Stop ? LogicalAnd(again, UEqual(Load(_uintType, _cycleSeen), UInt(0))) : again;

        // After the loop: alive = the lane was still walking (guarded, or stopped in stop mode).
        private void EndBlasCycleWalk()
        {
            if (!CycleCounting)
            {
                return;
            }

            var steps = Load(_uintType, _nativeSteps);
            var alive = IsNotZero(Load(_uintType, _nativeAlive));
            var seen = Load(_uintType, _cycleSeen);
            var revisited = IsNotZero(seen);
            var guarded = _maxDispatcherSteps > 0
                ? _module.AddInstruction(SpirvOp.UGreaterThanEqual, _boolType, steps, UInt((uint)_maxDispatcherSteps))
                : _module.ConstantBool(false);
            var longWalk = _module.AddInstruction(SpirvOp.UGreaterThan, _boolType, steps, UInt(BlasCycleLongSteps));
            EmitConditional(_module.AddInstruction(SpirvOp.UGreaterThanEqual, _boolType, steps, UInt(BlasCycleFrom)), () =>
            {
                AtomicAdd(CycleCounter(BlasCycleChecked), UInt(1));
                EmitConditional(revisited, () =>
                {
                    AtomicAdd(CycleCounter(BlasCycleRevisits), UInt(1));
                    if (CycleMode == BlasCycleMode.Stop)
                    {
                        AtomicAdd(CycleCounter(BlasCycleStopped), SelectU(alive, UInt(1), UInt(0)));
                    }
                    else
                    {
                        AtomicAdd(CycleCounter(BlasCycleRevisitGuarded), SelectU(guarded, UInt(1), UInt(0)));
                        AtomicAdd(CycleCounter(BlasCycleRevisitFinished), SelectU(alive, UInt(0), UInt(1)));
                    }
                });
                EmitConditional(longWalk, () => AtomicAdd(CycleCounter(BlasCycleLong), UInt(1)));
                EmitConditional(guarded, () => AtomicAdd(CycleCounter(BlasCycleGuarded), UInt(1)));
                _module.AddInstruction(SpirvOp.AtomicUMax, _uintType, CycleCounter(BlasCycleMaxSteps), UInt(1), UInt(0), steps);
                var notable = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, _module.AddInstruction(SpirvOp.LogicalOr, _boolType, revisited, guarded), longWalk);
                EmitConditional(notable, () =>
                {
                    var claim = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType, CycleCounter(BlasCycleSample), UInt(1), UInt(0), UInt(0), UInt(1), UInt(0));
                    EmitConditional(UEqual(claim, UInt(0)), () =>
                    {
                        Store(CycleCounter(BlasCycleSample + 1), RqBlasKey());
                        Store(CycleCounter(BlasCycleSample + 2), steps);
                        Store(CycleCounter(BlasCycleSample + 3), LoadV(8));
                        Store(CycleCounter(BlasCycleSample + 4), seen);
                        Store(CycleCounter(BlasCycleSample + 5), Load(_uintType, _cycleMark));
                        for (var register = 0u; register < 6; register++)
                        {
                            Store(CycleCounter(BlasCycleSample + 6 + register), LoadV(36 + register));
                        }

                        Store(CycleCounter(BlasCycleSample + 12), LoadV(23));
                    });
                });
                EmitRqMapInsert(notable, countRoots: false, maxValue: steps);
            });
        }

        // After the TLAS loop (wave-uniform): waves that reached the guard there.
        private void EndBlasCycleTlas()
        {
            if (!CycleCounting)
            {
                return;
            }

            var elected = _module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3));
            EmitConditional(elected, () =>
            {
                AtomicAdd(CycleCounter(BlasCycleTlasGuarded), SelectU(IsNotZero(Load(_uintType, _tlasCapped)), UInt(1), UInt(0)));
                _module.AddInstruction(SpirvOp.AtomicUMax, _uintType, CycleCounter(BlasCycleTlasMax), UInt(1), UInt(0), Load(_uintType, _iterationGuard));
            });
        }
    }
}
