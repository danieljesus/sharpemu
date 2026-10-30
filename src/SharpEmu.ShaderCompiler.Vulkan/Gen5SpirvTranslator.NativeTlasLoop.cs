// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_NATIVE_TLAS_LOOP for GTA V's RT reflections pass (0x8395E43F382309DF).
//
// The TLAS walk (pc 0x0F8C-0x19C0, around the BLAS loop) is uniform over the wave: the node (s12),
// the stack depth (s96) and the stack (lanes of v32) are scalar state, and each box node is tested
// by every lane that still follows the walk (s0). After the test the wave picks the next node and
// the children to push from all lanes' results: the first lane (s_ff1) holding a third, second or
// first child leads, and children no earlier choice covers are added in lane order. Translated
// instruction by instruction, each of those ~40 ballots and readlanes is a wave64 exchange with a
// workgroup barrier, every TLAS iteration.
//
// The native loop keeps the same wave semantics with one exchange per box node: each 32-lane half
// summarises its lanes' results with subgroup ballots and shuffles (per child slot, the first
// lane's whole result, and the distinct child pointers in lane order), the halves meet once through
// LDS, and every invocation then derives the same decision. Lane order, the leader rules, the
// distinctness tests, the push order, the 60-entry stack clamp and the s0/EXEC masks are the
// guest's, so the walk is bit-exact; the BLAS of each instance runs in the native BLAS loop.
//
// 1 (or on): the native loop replaces the guest loop (needs the native BLAS loop on).
// 2 (or verify): the native loop runs first on a copy of the state; at the exit v23 v29 v31 v21
// v22 v6 v7 are compared with the guest's ([GPU][NATIVE_TLAS]).
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private const uint NativeTlasHeaderPc = 0x0F8C;
        private const uint NativeTlasLatchPc = 0x19C0;
        private const uint NativeTlasExitPc = 0x19C4;
        private const uint NativeTlasStackClamp = 60;
        // FNV-1a over "pc:opcode;" of the walk's own instructions (the BLAS loop has its own).
        private const ulong NativeTlasSignature = 0xF79C89A0C38E1B3BUL;
        // Exchange area in the guest LDS: two parities of two halves of 32 dwords. The guest only
        // uses LDS for the BLAS stack, which is empty whenever the walk exchanges.
        private const uint NativeTlasExchangeDwords = 128;

        private enum NativeTlasMode
        {
            Off,
            Native,
            Verify,
        }

        private sealed record NativeTlasPlan(int Header, int Latch, int Exit, SortedDictionary<uint, Gen5ShaderInstruction> Instructions);

        private static NativeTlasMode ReadNativeTlasMode() =>
            (Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_TLAS_LOOP") ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "" or "1" or "on" or "native" => NativeTlasMode.Native, // on by default (rt433: bit-exact; rt434)
                "2" or "verify" => NativeTlasMode.Verify,
                _ => NativeTlasMode.Off,
            };

        private static readonly uint[] NativeTlasLiveOuts = [23, 29, 31, 21, 22, 6, 7];

        // Instance entry: scalar instance record (0x0FB8-0x0FDC), ray to object space (0x0FE8-0x1050)
        // and the BLAS prologue (0x1054-0x10DC).
        private const uint NativeTlasInstanceFirst = 0x0FB8;
        private const uint NativeTlasInstanceScalarLast = 0x0FDC;
        private const uint NativeTlasTransformFirst = 0x0FE8;
        private const uint NativeTlasPrologueLast = 0x10DC;

        private NativeTlasMode _nativeTlasMode;
        private NativeTlasPlan? _nativeTlas;
        private uint _tlasStack;
        private uint _tlasExec;
        private uint _tlasInS0;
        private uint _tlasAnyS0;
        private uint _tlasAnyExec;
        private uint _tlasDone;
        private uint _tlasPop;
        private uint _tlasParity;
        private uint _tlasCapped;
        private uint _tlasVerifyArmed;
        private uint[]? _tlasResults;
        private readonly List<(uint Register, bool Vector, uint Shadow)> _tlasShadows = [];
        private readonly List<(uint Variable, uint Shadow)> _tlasVariableShadows = [];

        private NativeTlasPlan? PrepareNativeTlasLoop(IReadOnlyList<ShaderBlock> blocks)
        {
            if (_request.Hash != NativeBlasHash)
            {
                return null;
            }

            _nativeTlasMode = ReadNativeTlasMode();
            if (_nativeTlasMode == NativeTlasMode.Off)
            {
                return null;
            }

            var plan = TryBuildNativeTlasPlan(blocks, out var reason);
            Console.Error.WriteLine(plan is null
                ? $"[NATIVE_TLAS] hash=0x{_request.Hash:X16} not applied: {reason}"
                : $"[NATIVE_TLAS] hash=0x{_request.Hash:X16} mode={_nativeTlasMode} prepared: blocks={plan.Header}..{plan.Latch} exit={plan.Exit}");
            if (plan is null)
            {
                _nativeTlasMode = NativeTlasMode.Off;
            }

            return plan;
        }

        private NativeTlasPlan? TryBuildNativeTlasPlan(IReadOnlyList<ShaderBlock> blocks, out string reason)
        {
            reason = string.Empty;
            if (_nativeBlas is null)
            {
                reason = "the native BLAS loop is off";
                return null;
            }

            if (!TryFindBlock(blocks, NativeTlasHeaderPc, out var header) || !_loopLatchByHeader.TryGetValue(header, out var latch))
            {
                reason = "no loop at 0x0F8C";
                return null;
            }

            if (_program.Instructions[blocks[latch].EndIndex - 1].Pc != NativeTlasLatchPc ||
                latch + 1 >= blocks.Count || blocks[latch + 1].StartPc != NativeTlasExitPc)
            {
                reason = "the loop does not end at 0x19C0 with its exit at 0x19C4";
                return null;
            }

            if (_nativeBlas.Header <= header || _nativeBlas.Exit > latch)
            {
                reason = "the BLAS loop is not inside the TLAS loop";
                return null;
            }

            var instructions = new SortedDictionary<uint, Gen5ShaderInstruction>();
            var signature = 0xCBF29CE484222325UL;
            for (var index = blocks[header].StartIndex; index < blocks[latch].EndIndex; index++)
            {
                var instruction = _program.Instructions[index];
                instructions[instruction.Pc] = instruction;
                if (instruction.Pc is >= NativeBlasHeaderPc and <= NativeBlasLatchPc)
                {
                    continue;
                }

                foreach (var character in $"{instruction.Pc:X4}:{instruction.Opcode};")
                {
                    signature = (signature ^ character) * 0x100000001B3UL;
                }
            }

            if (signature != NativeTlasSignature)
            {
                reason = $"loop signature 0x{signature:X16} is not 0x{NativeTlasSignature:X16}";
                return null;
            }

            return new NativeTlasPlan(header, latch, latch + 1, instructions);
        }

        private bool NativeTlasSupported(out string reason)
        {
            reason = !_emulateWave64 || _subgroupInvocationIdInput == 0
                ? "the wave is not emulated as two 32-lane subgroups"
                : _lds == 0 || _ldsDwordMask + 1 < NativeTlasExchangeDwords
                    ? "no LDS for the exchange"
                    : string.Empty;
            return reason.Length == 0;
        }

        // A guest instruction for the lanes of `active` only (EXEC as the guest would have it);
        // scalar instructions run on every invocation, as on the wave.
        private void EmitNativeGuestMasked(SortedDictionary<uint, Gen5ShaderInstruction> instructions, uint pc, uint active)
        {
            var instruction = instructions[pc];
            Store(_exec, active);
            var previousPc = _emittingPc;
            var previousFull = _execKnownFull;
            var previousForbid = _module.ForbidWaveOperations;
            _emittingPc = pc;
            _execKnownFull = false;
            _nativeLaneEmit = true;
            _module.ForbidWaveOperations = true;
            try
            {
                if (!TryEmitInstruction(instruction, out var error))
                {
                    throw new InvalidOperationException($"native TLAS loop pc=0x{pc:X} {instruction.Opcode}: {error}");
                }
            }
            finally
            {
                _emittingPc = previousPc;
                _execKnownFull = previousFull;
                _nativeLaneEmit = false;
                _module.ForbidWaveOperations = previousForbid;
            }

            Store(_exec, IsNotZero(Load(_uintType, _tlasExec)));
        }

        private void DeclareNativeTlasState()
        {
            if (_tlasStack != 0)
            {
                return;
            }

            var arrayType = _module.TypeArray(_uintType, 64);
            _tlasStack = RtSharedStacks
                ? _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.Workgroup, _module.TypeArray(_uintType, 128)), SpirvStorageClass.Workgroup)
                : _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Private, arrayType),
                    SpirvStorageClass.Private,
                    _module.ConstantNull(arrayType));
            _module.AddName(_tlasStack, "nativeTlasStack");
            _interfaces.Add(_tlasStack);
            _tlasExec = NativeVariable("nativeTlasExec");
            _tlasInS0 = NativeVariable("nativeTlasInS0");
            _tlasAnyS0 = NativeVariable("nativeTlasAnyS0");
            _tlasAnyExec = NativeVariable("nativeTlasAnyExec");
            _tlasDone = NativeVariable("nativeTlasDone");
            _tlasPop = NativeVariable("nativeTlasPop");
            _tlasParity = NativeVariable("nativeTlasParity");
            _tlasCapped = NativeVariable("nativeTlasCapped");
        }

        // The stack is uniform over the wave. In workgroup memory each 32-lane half keeps its own
        // copy, so no half reads what the other half's subgroup wrote.
        private uint TlasStackSlot(uint index) =>
            RtSharedStacks
                ? _module.AddInstruction(SpirvOp.AccessChain, _module.TypePointer(SpirvStorageClass.Workgroup, _uintType), _tlasStack,
                    IAdd(BitwiseAnd(index, UInt(63)), ShiftLeftLogical(BitwiseAnd(GuestWaveLane(), UInt(32)), UInt(1))))
                : _module.AddInstruction(SpirvOp.AccessChain, _privateUintPointer, _tlasStack, BitwiseAnd(index, UInt(63)));

        // v_writelane v32, value, s96; s96 += 1.
        private void EmitTlasPush(uint value)
        {
            var depth = LoadS(96);
            Store(TlasStackSlot(depth), value);
            Store(ScalarPointer(96), IAdd(depth, UInt(1)));
        }

        private uint ExchangePointer(uint index) =>
            _module.AddInstruction(SpirvOp.AccessChain, _ldsElementPointer, _lds, index);

        private uint Ballot32(uint condition) =>
            _module.AddInstruction(SpirvOp.CompositeExtract, _uintType,
                _module.AddInstruction(SpirvOp.GroupNonUniformBallot, _uvec4Type, UInt(3), condition), 0);

        private uint ShuffleFrom(uint value, uint invocation) =>
            _module.AddInstruction(SpirvOp.GroupNonUniformShuffle, _uintType, UInt(3), value, invocation);

        private uint IsValidNode(uint value) => _module.AddInstruction(SpirvOp.INotEqual, _boolType, value, UInt(InvalidNode));

        private uint UEqual(uint left, uint right) => _module.AddInstruction(SpirvOp.IEqual, _boolType, left, right);

        // The first invocation of this subgroup where the condition holds: whether there is one and its id.
        private (uint Has, uint Id) FirstInSubgroup(uint condition)
        {
            var bits = Ballot32(condition);
            var has = IsNotZero(bits);
            return (has, SelectU(has, Ext(73, _uintType, bits), UInt(0)));
        }

        // One exchange: each half's leader stores `words` at its slot; afterwards every invocation
        // holds both halves' words (low half first).
        private (uint[] Low, uint[] High) ExchangeHalves(uint[] words)
        {
            var parity = Load(_uintType, _tlasParity);
            Store(_tlasParity, BitwiseXor(parity, UInt(1)));
            var area = _module.AddInstruction(SpirvOp.IMul, _uintType, parity, UInt(64));
            var half = ShiftRightLogical(GuestWaveLane(), UInt(5));
            var slot = IAdd(area, _module.AddInstruction(SpirvOp.IMul, _uintType, half, UInt(32)));
            EmitConditional(UEqual(Load(_uintType, _subgroupInvocationIdInput), UInt(0)), () =>
            {
                for (var word = 0; word < words.Length; word++)
                {
                    Store(ExchangePointer(IAdd(slot, UInt((uint)word))), words[word]);
                }
            });
            EmitWave64Barrier();
            var low = new uint[words.Length];
            var high = new uint[words.Length];
            for (var word = 0; word < words.Length; word++)
            {
                low[word] = Load(_uintType, ExchangePointer(IAdd(area, UInt((uint)word))));
                high[word] = Load(_uintType, ExchangePointer(IAdd(area, UInt(32 + (uint)word))));
            }

            return (low, high);
        }

        // The n-th entry (from 0) of a lane-ordered list after dropping invalid entries and the
        // excluded values; InvalidNode when there is none.
        private uint NthExcluding(IReadOnlyList<uint> list, IReadOnlyList<uint> excluded, uint n)
        {
            var result = UInt(InvalidNode);
            var count = UInt(0);
            foreach (var entry in list)
            {
                var kept = IsValidNode(entry);
                foreach (var value in excluded)
                {
                    kept = LogicalAnd(kept, _module.AddInstruction(SpirvOp.INotEqual, _boolType, entry, value));
                }

                result = SelectU(LogicalAnd(kept, UEqual(count, UInt(n))), entry, result);
                count = IAdd(count, SelectU(kept, UInt(1), UInt(0)));
            }

            return result;
        }

        // The distinct values of a slot over the wave in lane order: the low half's list, then the
        // high half's values the low half does not have (gaps are skipped by NthExcluding).
        private uint[] MergeLists(uint[] low, uint[] high)
        {
            var merged = new uint[8];
            for (var index = 0; index < 4; index++)
            {
                merged[index] = low[index];
                var seen = _module.ConstantBool(false);
                for (var other = 0; other < 4; other++)
                {
                    seen = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, seen, UEqual(high[index], low[other]));
                }

                merged[4 + index] = SelectU(seen, UInt(InvalidNode), high[index]);
            }

            return merged;
        }

        private void EmitNativeTlasRegion(NativeTlasPlan plan)
        {
            DeclareNativeBlasState();
            DeclareNativeTlasState();
            var verify = _nativeTlasMode == NativeTlasMode.Verify;
            if (verify && _request.TraceDeviceAddressFaults)
            {
                BvhTestCounter();
            }

            Console.Error.WriteLine($"[NATIVE_TLAS] hash=0x{_request.Hash:X16} mode={_nativeTlasMode} emitted");
            var enters = LogicalAnd(
                Load(_boolType, _programActive),
                UEqual(Load(_uintType, _programCounter), UInt((uint)plan.Header)));
            EmitConditional(enters, () =>
            {
                if (verify)
                {
                    SaveNativeTlasState(plan);
                    if (_request.TraceDeviceAddressFaults && _faultBuffer != 0)
                    {
                        // The halves' lane order is the subgroup's: a different mapping is counted (word 22).
                        var mapped = UEqual(Load(_uintType, _subgroupInvocationIdInput), BitwiseAnd(GuestWaveLane(), UInt(31)));
                        EmitConditional(LogicalNot(mapped), () =>
                            _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(22), UInt(1), UInt(0), UInt(1)));
                    }
                }

                var spilled = !verify && RtSpillEnabled ? RtSpillSet(plan.Instructions) : [];
                if (spilled.Length != 0)
                {
                    Console.Error.WriteLine($"[RT_SPILL] hash=0x{_request.Hash:X16} scalars={spilled.Length}: {string.Join(' ', spilled.Select(register => $"s{register}"))}");
                }

                EmitRtSpill(spilled);
                EmitNativeTlasLoop(plan);
                EmitRtRestore(spilled);
                if (verify)
                {
                    _tlasResults ??= NativeTlasLiveOuts.Select(register => NativeVariable($"nativeTlasResult_v{register}")).ToArray();
                    _tlasVerifyArmed = _tlasVerifyArmed != 0 ? _tlasVerifyArmed : NativeVariable("nativeTlasVerifyArmed");
                    for (var index = 0; index < NativeTlasLiveOuts.Length; index++)
                    {
                        Store(_tlasResults[index], LoadV(NativeTlasLiveOuts[index]));
                    }

                    Store(_tlasVerifyArmed, UInt(1));
                    RestoreNativeTlasState();
                }
                else
                {
                    if (_request.TraceDeviceAddressFaults && _faultBuffer != 0)
                    {
                        EmitConditional(IsNotZero(Load(_uintType, _tlasCapped)), () =>
                            _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(2), UInt(1), UInt(0), UInt(1)));
                    }

                    Store(_programCounter, UInt((uint)plan.Exit));
                }
            });
        }

        private void EmitNativeTlasLoop(NativeTlasPlan plan)
        {
            var instructions = plan.Instructions;
            // 0x0F8C on entry: s0 = EXEC, with s12 = 37 and s96 = 0 from before the loop.
            var exec = Load(_boolType, _exec);
            Store(_tlasExec, SelectU(exec, UInt(1), UInt(0)));
            Store(_tlasInS0, SelectU(exec, UInt(1), UInt(0)));
            Store(_tlasDone, UInt(0));
            Store(_tlasCapped, UInt(0));
            Store(_tlasParity, UInt(0));
            {
                var (has, _) = FirstInSubgroup(exec);
                var (low, high) = ExchangeHalves([SelectU(has, UInt(1), UInt(0))]);
                var any = SelectU(IsNotZero(BitwiseOr(low[0], high[0])), UInt(1), UInt(0));
                Store(_tlasAnyExec, any);
                Store(_tlasAnyS0, any);
            }

            var header = _module.AllocateId();
            var body = _module.AllocateId();
            var next = _module.AllocateId();
            var merge = _module.AllocateId();
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(header);
            _module.AddStatement(SpirvOp.LoopMerge, merge, next, 0);
            _module.AddStatement(SpirvOp.Branch, body);
            _module.AddLabel(body);

            Store(_tlasPop, UInt(0));
            var node = LoadS(12);
            // 0x0F90: s12 & 7 in 4..6 is a box node.
            var isBox = _module.AddInstruction(SpirvOp.ULessThanEqual, _boolType,
                _module.AddInstruction(SpirvOp.ISub, _uintType, BitwiseAnd(node, UInt(7)), UInt(4)), UInt(2));
            EmitConditional(isBox, () => EmitNativeTlasBox(instructions), () => EmitNativeTlasLeaf(instructions, node));

            // 0x19AC: pop, or leave the walk on an empty stack; a pop restarts at 0x0F8C (s0 = EXEC).
            EmitConditional(IsNotZero(Load(_uintType, _tlasPop)), () =>
            {
                var depth = LoadS(96);
                EmitConditional(UEqual(depth, UInt(0)), () => Store(_tlasDone, UInt(1)), () =>
                {
                    var top = _module.AddInstruction(SpirvOp.ISub, _uintType, depth, UInt(1));
                    Store(ScalarPointer(96), top);
                    Store(ScalarPointer(12), Load(_uintType, TlasStackSlot(top)));
                    Store(_tlasInS0, Load(_uintType, _tlasExec));
                    Store(_tlasAnyS0, Load(_uintType, _tlasAnyExec));
                });
            });

            _module.AddStatement(SpirvOp.Branch, next);
            _module.AddLabel(next);
            var again = LogicalNot(IsNotZero(Load(_uintType, _tlasDone)));
            if (_maxDispatcherSteps > 0)
            {
                // The guest counts each TLAS iteration on the same (wave-uniform) budget.
                var steps = IAdd(Load(_uintType, _iterationGuard), UInt(1));
                Store(_iterationGuard, steps);
                var within = _module.AddInstruction(SpirvOp.ULessThan, _boolType, steps, UInt((uint)_maxDispatcherSteps));
                Store(_tlasCapped, SelectU(LogicalAnd(again, LogicalNot(within)), UInt(1), UInt(0)));
                again = LogicalAnd(again, within);
            }

            _module.AddStatement(SpirvOp.BranchConditional, again, header, merge);
            _module.AddLabel(merge);
            Store(_exec, IsNotZero(Load(_uintType, _tlasExec)));
        }

        // 0x0FA0-0x10DC, the BLAS loop and 0x15E8: an instance leaf for the lanes in s0, then a pop.
        private void EmitNativeTlasLeaf(SortedDictionary<uint, Gen5ShaderInstruction> instructions, uint node)
        {
            Store(_tlasPop, UInt(1));
            // s_cbranch_execz 0x0FB4: an invalid node or no lane in s0 skips the instance.
            var entered = LogicalAnd(_module.AddInstruction(SpirvOp.INotEqual, _boolType, node, UInt(InvalidNode)),
                IsNotZero(Load(_uintType, _tlasAnyS0)));
            EmitConditional(entered, () =>
            {
                BeginRtProfileRegion(RtProfileInstance);
                var active = LogicalAnd(IsNotZero(Load(_uintType, _tlasInS0)), IsNotZero(Load(_uintType, _tlasExec)));
                CountRtProfile(3, SelectU(active, UInt(1), UInt(0)));
                foreach (var pc in instructions.Keys.Where(pc => pc is >= NativeTlasInstanceFirst and <= NativeTlasInstanceScalarLast))
                {
                    EmitNativeGuestMasked(instructions, pc, active);
                }

                // 0x0FDC/0x0FE4: an instance without a visible mask bit (s23 & 0x7F00) is skipped.
                EmitConditional(IsNotZero(LoadS(106)), () =>
                {
                    foreach (var pc in instructions.Keys.Where(pc => pc is >= NativeTlasTransformFirst and <= NativeTlasPrologueLast))
                    {
                        EmitNativeGuestMasked(instructions, pc, active);
                    }

                    EndRtProfileRegion(RtProfileInstance);
                    BeginRtProfileRegion(RtProfileBlas);
                    Store(_nativeSteps, UInt(0));
                    Store(_exec, active);
                    EmitRqMapProbe(); // [local] SHARPEMU_RQ_MAP
                    EmitConditional(active, () => EmitBlasWalk(_nativeBlas!)); // [local] SHARPEMU_RT_RAYQUERY
                    Store(_exec, IsNotZero(Load(_uintType, _tlasExec)));
                    EndRtProfileRegion(RtProfileBlas);
                    if (RtProfileActive)
                    {
                        CountRtProfile(1, Load(_uintType, _nativeSteps));
                        CountRtProfileWalk(active); // [local]
                    }

                    BeginRtProfileRegion(RtProfileInstance);
                });
                EndRtProfileRegion(RtProfileInstance);
            });
        }

        // 0x15F0-0x19A8: test the box node, then choose the next node and the children to push.
        private void EmitNativeTlasBox(SortedDictionary<uint, Gen5ShaderInstruction> instructions)
        {
            BeginRtProfileRegion(RtProfileTlasNode);
            var exec = IsNotZero(Load(_uintType, _tlasExec));
            var tested = LogicalAnd(exec, IsNotZero(Load(_uintType, _tlasInS0)));
            CountRtProfile(2, UInt(1));
            foreach (var pc in new uint[] { 0x15F0, 0x15F4, 0x15F8, 0x15FC })
            {
                EmitNativeGuestMasked(instructions, pc, exec);
            }

            foreach (var pc in new uint[] { 0x1608, 0x160C, 0x1610 })
            {
                EmitNativeGuestMasked(instructions, pc, tested);
            }

            // What each lane contributes to the wave's compares: its four results, or none outside EXEC.
            var a = new uint[4];
            for (var slot = 0u; slot < 4; slot++)
            {
                a[slot] = SelectU(exec, LoadV(slot), UInt(InvalidNode));
            }

            // 0x1628: s0 = the lanes of s0 whose nearest child is valid.
            Store(_tlasInS0, SelectU(IsValidNode(a[0]), UInt(1), UInt(0)));

            // Per half: the first lane holding child slot 3, 2 and 1 (its whole result), and the
            // distinct pointers of slots 0, 1 and 2 in lane order (four at most per half).
            var words = new List<uint>();
            foreach (var (slot, width) in new[] { (3, 4), (2, 3), (1, 2) })
            {
                var (has, id) = FirstInSubgroup(IsValidNode(a[slot]));
                for (var component = 0; component < width; component++)
                {
                    words.Add(SelectU(has, ShuffleFrom(a[component], id), UInt(InvalidNode)));
                }
            }

            for (var slot = 0; slot < 3; slot++)
            {
                var remaining = IsValidNode(a[slot]);
                for (var entry = 0; entry < 4; entry++)
                {
                    var (has, id) = FirstInSubgroup(remaining);
                    var value = SelectU(has, ShuffleFrom(a[slot], id), UInt(InvalidNode));
                    words.Add(value);
                    remaining = LogicalAnd(remaining, _module.AddInstruction(SpirvOp.INotEqual, _boolType, a[slot], value));
                }
            }

            var (low, high) = ExchangeHalves(words.ToArray());
            uint[] Leader(int offset, int width, int validComponent)
            {
                var lowValid = IsValidNode(low[offset + validComponent]);
                return Enumerable.Range(0, width).Select(component => SelectU(lowValid, low[offset + component], high[offset + component])).ToArray();
            }

            var lead3 = Leader(0, 4, 3);
            var lead2 = Leader(4, 3, 2);
            var lead1 = Leader(7, 2, 1);
            var list0 = MergeLists(low[9..13], high[9..13]);
            var list1 = MergeLists(low[13..17], high[13..17]);
            var list2 = MergeLists(low[17..21], high[17..21]);
            var has0 = IsValidNode(NthExcluding(list0, [], 0));
            var has1 = IsValidNode(lead1[1]);
            var has2 = IsValidNode(lead2[2]);
            var has3 = IsValidNode(lead3[3]);
            Store(_tlasAnyS0, SelectU(has0, UInt(1), UInt(0)));

            void PushIfValid(uint value) => EmitConditional(IsValidNode(value), () => EmitTlasPush(value));

            // 0x1638: no lane hit a child, pop.
            EmitConditional(LogicalNot(has0), () => Store(_tlasPop, UInt(1)), () =>
            {
                EmitConditional(LogicalNot(has1), () =>
                {
                    // 0x1910: only nearest children; the first lane's leads, the other distinct ones follow.
                    Store(ScalarPointer(12), NthExcluding(list0, [], 0));
                    PushIfValid(NthExcluding(list0, [], 1));
                    PushIfValid(NthExcluding(list0, [], 2));
                    PushIfValid(NthExcluding(list0, [], 3));
                }, () =>
                {
                    EmitConditional(LogicalNot(has2), () =>
                    {
                        // 0x17D8: two children at most per lane.
                        var c0 = lead1[0];
                        var c1 = lead1[1];
                        Store(ScalarPointer(12), c0);
                        EmitTlasPush(c1);
                        var x = NthExcluding(list1, [c0, c1], 0);
                        EmitConditional(IsValidNode(x), () =>
                        {
                            EmitTlasPush(x);
                            var y = NthExcluding(list1, [c0, c1], 1);
                            EmitConditional(IsValidNode(y), () => EmitTlasPush(y),
                                () => PushIfValid(NthExcluding(list0, [c0, c1, x], 0)));
                        }, () =>
                        {
                            var y0 = NthExcluding(list0, [c0, c1], 0);
                            EmitConditional(IsValidNode(y0), () =>
                            {
                                EmitTlasPush(y0);
                                PushIfValid(NthExcluding(list0, [c0, c1], 1));
                            });
                        });
                    }, () =>
                    {
                        EmitConditional(LogicalNot(has3), () =>
                        {
                            // 0x16B4: three children at most per lane.
                            var c0 = lead2[0];
                            var c1 = lead2[1];
                            var c2 = lead2[2];
                            Store(ScalarPointer(12), c0);
                            EmitTlasPush(c2);
                            var x = NthExcluding(list2, [c0, c1, c2], 0);
                            EmitConditional(IsValidNode(x), () =>
                            {
                                EmitTlasPush(x);
                                EmitTlasPush(c1);
                            }, () =>
                            {
                                EmitTlasPush(c1);
                                var y = NthExcluding(list1, [c0, c1, c2], 0);
                                EmitConditional(IsValidNode(y), () => EmitTlasPush(y),
                                    () => PushIfValid(NthExcluding(list0, [c0, c1, c2], 0)));
                            });
                        }, () =>
                        {
                            // 0x1660: a lane with four children leads with its whole order.
                            Store(ScalarPointer(12), lead3[0]);
                            EmitTlasPush(lead3[3]);
                            EmitTlasPush(lead3[2]);
                            EmitTlasPush(lead3[1]);
                        });
                    });
                });

                // s_min_u32 s96, 60 after every group of pushes.
                Store(ScalarPointer(96), Ext(38, _uintType, LoadS(96), UInt(NativeTlasStackClamp)));
            });
            EndRtProfileRegion(RtProfileTlasNode);
        }

        // Verify mode: the registers and flags the native walk may write, saved and restored.
        private void SaveNativeTlasState(NativeTlasPlan plan)
        {
            if (_tlasShadows.Count == 0)
            {
                var scalars = new SortedSet<uint> { 12, 96, 106, 107 };
                var vectors = new SortedSet<uint>();
                foreach (var instruction in plan.Instructions.Values)
                {
                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.ScalarRegister)
                        {
                            scalars.Add(destination.Value);
                            scalars.Add(destination.Value + 1);
                        }
                        else if (destination.Kind == Gen5OperandKind.VectorRegister)
                        {
                            vectors.Add(destination.Value);
                        }
                    }

                    var scalarDestination = instruction.Control switch
                    {
                        Gen5SdwaControl { ScalarDestination: { } sdwa } => sdwa,
                        Gen5Vop3Control { ScalarDestination: { } vop3 } => vop3,
                        _ => (uint?)null,
                    };
                    if (scalarDestination is { } pair)
                    {
                        scalars.Add(pair);
                        scalars.Add(pair + 1);
                    }
                }

                scalars.RemoveWhere(register => register is 126 or 127 || register >= ScalarRegisterCount);
                foreach (var register in scalars)
                {
                    _tlasShadows.Add((register, false, NativeVariable($"nativeTlasSaved_s{register}")));
                }

                foreach (var register in vectors)
                {
                    _tlasShadows.Add((register, true, NativeVariable($"nativeTlasSaved_v{register}")));
                }

                foreach (var variable in new[] { _scc, _vcc })
                {
                    _tlasVariableShadows.Add((variable, NativeVariable("nativeTlasSavedFlag")));
                }

                if (_maxDispatcherSteps > 0)
                {
                    _tlasVariableShadows.Add((_iterationGuard, NativeVariable("nativeTlasSavedSteps")));
                }

                if (_bvhTestCount != 0)
                {
                    _tlasVariableShadows.Add((_bvhTestCount, NativeVariable("nativeTlasSavedBvhTests")));
                }

                foreach (var key in _indirectKeyScratch.Values)
                {
                    _tlasVariableShadows.Add((key, NativeVariable("nativeTlasSavedKey")));
                }
            }

            foreach (var (register, vector, shadow) in _tlasShadows)
            {
                Store(shadow, vector ? LoadV(register) : LoadS(register));
            }

            foreach (var (variable, shadow) in _tlasVariableShadows)
            {
                Store(shadow, _flagVariables.Contains(variable)
                    ? SelectU(Load(_boolType, variable), UInt(1), UInt(0))
                    : Load(_uintType, variable));
            }
        }

        private void RestoreNativeTlasState()
        {
            foreach (var (register, vector, shadow) in _tlasShadows)
            {
                Store(vector ? VectorPointer(register) : ScalarPointer(register), Load(_uintType, shadow));
            }

            foreach (var (variable, shadow) in _tlasVariableShadows)
            {
                Store(variable, _flagVariables.Contains(variable)
                    ? IsNotZero(Load(_uintType, shadow))
                    : Load(_uintType, shadow));
            }
        }

        // The TLAS verify record, the trace tail's first 64 words (length-192): the BLAS record and the
        // region profile hold the next 64.
        //  0 compared exits  1 mismatches  2 capped walks  3 OR of mismatch masks  4 first-mismatch claim
        //  5 lane  6-12 guest v23 v29 v31 v21 v22 v6 v7  13-19 native  20 s97 21 s98 (instance table)
        //  22 invocations whose subgroup id is not their lane within the half
        private uint NativeTlasWord(uint slot)
        {
            var length = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _faultBuffer, 0);
            return BlockWordPointer(_faultBuffer, IAdd(_module.AddInstruction(SpirvOp.ISub, _uintType, length, UInt(192)), UInt(slot)));
        }

        private void EmitNativeTlasVerifyCompare(NativeTlasPlan plan)
        {
            if (_tlasVerifyArmed == 0 || _tlasResults is null)
            {
                return;
            }

            var reached = LogicalAnd(
                LogicalAnd(Load(_boolType, _programActive), UEqual(Load(_uintType, _programCounter), UInt((uint)plan.Exit))),
                IsNotZero(Load(_uintType, _tlasVerifyArmed)));
            EmitConditional(reached, () =>
            {
                Store(_tlasVerifyArmed, UInt(0));
                if (!_request.TraceDeviceAddressFaults || _faultBuffer == 0)
                {
                    return;
                }

                var mask = UInt(0);
                for (var index = 0; index < NativeTlasLiveOuts.Length; index++)
                {
                    var differs = _module.AddInstruction(SpirvOp.INotEqual, _boolType,
                        LoadV(NativeTlasLiveOuts[index]), Load(_uintType, _tlasResults[index]));
                    mask = BitwiseOr(mask, SelectU(differs, UInt(1u << index), UInt(0)));
                }

                var capped = IsNotZero(Load(_uintType, _tlasCapped));
                var mismatch = LogicalAnd(IsNotZero(mask), LogicalNot(capped));
                EmitConditional(IsNotZero(Load(_uintType, _tlasExec)), () =>
                {
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(0), UInt(1), UInt(0), UInt(1));
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(2), UInt(1), UInt(0), SelectU(capped, UInt(1), UInt(0)));
                });
                EmitConditional(mismatch, () =>
                {
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(1), UInt(1), UInt(0), UInt(1));
                    _module.AddInstruction(SpirvOp.AtomicOr, _uintType, NativeTlasWord(3), UInt(1), UInt(0), mask);
                    var claim = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType, NativeTlasWord(4), UInt(1), UInt(0), UInt(0), UInt(1), UInt(0));
                    EmitConditional(UEqual(claim, UInt(0)), () =>
                    {
                        Store(NativeTlasWord(5), GuestWaveLane());
                        for (var index = 0; index < NativeTlasLiveOuts.Length; index++)
                        {
                            Store(NativeTlasWord(6 + (uint)index), LoadV(NativeTlasLiveOuts[index]));
                            Store(NativeTlasWord(13 + (uint)index), Load(_uintType, _tlasResults[index]));
                        }

                        Store(NativeTlasWord(20), LoadS(97));
                        Store(NativeTlasWord(21), LoadS(98));
                    });
                });
            });
        }
    }
}
