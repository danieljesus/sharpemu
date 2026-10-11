// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_NATIVE_BLAS_LOOP for GTA V's RT reflections pass (0x8395E43F382309DF).
//
// The pass walks each instance's BLAS in a per-lane loop (pc 0x10E0-0x15E4) with a short stack:
// 14 entries in LDS, the rest in scratch. Translated as a wave, every iteration pays the EXEC
// bookkeeping of the whole wave, and under wave64 emulation each EXEC branch is an exchange with a
// workgroup barrier, until the slowest lane finishes. The loop has one entry and one exit, writes
// nothing outside its own stack, and only v23, v29, v31, v21, v22, v6 and v7 (the closest hit) are
// read after it; v9 and v43 also carry into the next BLAS when a root is not a box node.
//
// With the switch on, each lane runs the loop alone as a structured loop. Every value still comes
// from the guest instruction through its usual emitter (node test, triangle test, alpha test with
// its index, UV and texture reads); only the wave machinery is replaced: VCMPX/EXEC moves and the
// branches become lane-local ifs, the waterfalls one pass with the lane's own key, and the
// interleaved LDS/scratch stack a private array with the guest's capacity rule (the room check
// n < 67 once per box node, so up to 69 entries, children dropped when full). The order of pushes,
// pops and pair lists is the guest's, so the closest hit (t <= tmax, last one on ties) is the same.
//
// 1 (or on): the native loop replaces the guest loop.
// 2 (or verify): the native loop runs first on a copy of the state, the guest loop then runs as
// before and keeps its result; at the exit the live-outs are compared and differences counted in the
// fault buffer's trace tail ([GPU][NATIVE_BLAS], needs SHARPEMU_TRACE_GPU_MEMORY_ADDRESS=auto).
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private const ulong NativeBlasHash = 0x8395E43F382309DF;
        private const uint NativeBlasHeaderPc = 0x10E0;
        private const uint NativeBlasLatchPc = 0x15E4;
        private const uint NativeBlasExitPc = 0x15E8;
        private const uint NativeBlasStackCapacity = 72;
        private const uint NativeBlasPushRoom = 67;
        // FNV-1a over "pc:opcode;" of the loop's instructions, so another program never takes this path.
        private const ulong NativeBlasSignature = 0xE9476074B92723FDUL;

        private enum NativeBlasMode
        {
            Off,
            Native,
            Verify,
        }

        private sealed record NativeBlasPlan(int Header, int Latch, int Exit, SortedDictionary<uint, Gen5ShaderInstruction> Instructions);

        // Read on every compile: the translator tests switch it within one process.
        private static NativeBlasMode ReadNativeBlasMode() =>
            (Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_BLAS_LOOP") ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "" or "1" or "on" or "native" => NativeBlasMode.Native, // on by default (rt425, rt426: bit-exact)
                "2" or "verify" => NativeBlasMode.Verify,
                _ => NativeBlasMode.Off,
            };

        // The registers the code after the loop reads, in the order of the verify record.
        private static readonly uint[] NativeBlasLiveOuts = [23, 29, 31, 21, 22, 6, 7, 9, 43];

        // Guest instructions the native loop emits as they are, outside the alpha test.
        private static readonly uint[] NativeBlasEmitted =
        [
            0x10E4, 0x10F8, 0x110C, 0x11CC, 0x11D0, 0x11D4, 0x11DC, 0x11E0, 0x11E4, 0x11F0, 0x11F4, 0x11F8, 0x11FC,
            0x1520, 0x1524, 0x1528, 0x152C, 0x1530, 0x1534, 0x1538, 0x1540, 0x1548, 0x15B8, 0x15D0, 0x15D4, 0x15DC,
        ];

        // Compares whose lane result becomes a branch.
        private static readonly uint[] NativeBlasCompares = [0x1100, 0x1114, 0x1128, 0x1134, 0x1204, 0x1518, 0x1544, 0x1550, 0x15BC];

        // The stack: room check, address arithmetic, the three pushes and the pop (LDS or scratch).
        private static readonly uint[] NativeBlasStack =
        [
            0x1118, 0x113C, 0x1144, 0x1150, 0x1158, 0x1168, 0x1170, 0x117C, 0x1184, 0x1194, 0x119C, 0x11A8, 0x11B0,
            0x1558, 0x1560, 0x1564, 0x156C, 0x1578, 0x157C, 0x1584, 0x158C, 0x1594, 0x159C, 0x15A4,
        ];

        // EXEC saves, restores and branches, and waits between them.
        private static readonly uint[] NativeBlasControl =
        [
            0x10E0, 0x10FC, 0x1104, 0x1108, 0x1110, 0x1120, 0x1124, 0x112C, 0x1130, 0x1138, 0x1164, 0x1190, 0x11BC,
            0x11C0, 0x11C4, 0x11C8, 0x11E8, 0x11EC, 0x1200, 0x1208, 0x1514, 0x151C, 0x153C, 0x154C, 0x1554, 0x1568,
            0x15A0, 0x15AC, 0x15B0, 0x15B4, 0x15C4, 0x15C8, 0x15CC, 0x15E0, 0x15E4,
        ];

        // The alpha test runs 0x120C-0x150C in order without these: EXEC saves and restores and the
        // waterfall loop tests (each waterfall runs once, keyed by the lane's own value).
        private const uint NativeBlasAlphaFirst = 0x120C;
        private const uint NativeBlasAlphaLast = 0x150C;
        private static readonly HashSet<uint> NativeBlasAlphaSkipped =
        [
            0x1224, 0x1288, 0x128C, 0x12AC, 0x12B0, 0x12B4, 0x12B8, 0x12C8, 0x12CC, 0x12EC, 0x12F0, 0x12F4, 0x12F8,
            0x1308, 0x130C, 0x132C, 0x1330, 0x1334, 0x1338, 0x14D0, 0x14D4, 0x14F4, 0x14F8, 0x14FC, 0x1500,
        ];

        private NativeBlasMode _nativeBlasMode;
        private NativeBlasPlan? _nativeBlas;
        private bool _nativeLaneEmit;
        private uint _nativeCompareResult;
        private uint _nativeStack;
        private uint _nativeStackPointer;
        private uint _nativeAlive;
        private uint _nativeSteps;
        private uint _nativeCapped;
        private uint _nativeVerifyArmed;
        private uint[]? _nativeResults;
        private readonly List<(uint Register, bool Vector, uint Shadow)> _nativeShadows = [];
        private readonly List<(uint Flag, uint Shadow)> _nativeFlagShadows = [];

        private NativeBlasPlan? PrepareNativeBlasLoop(IReadOnlyList<ShaderBlock> blocks)
        {
            if (_request.Hash != NativeBlasHash)
            {
                return null;
            }

            _nativeBlasMode = ReadNativeBlasMode();
            if (_nativeBlasMode == NativeBlasMode.Off)
            {
                return null;
            }

            var plan = TryBuildNativeBlasPlan(blocks, out var reason);
            Console.Error.WriteLine(plan is null
                ? $"[NATIVE_BLAS] hash=0x{_request.Hash:X16} not applied: {reason}"
                : $"[NATIVE_BLAS] hash=0x{_request.Hash:X16} mode={_nativeBlasMode} prepared: blocks={plan.Header}..{plan.Latch} exit={plan.Exit} instructions={plan.Instructions.Count}");
            if (plan is null)
            {
                _nativeBlasMode = NativeBlasMode.Off;
            }

            return plan;
        }

        private NativeBlasPlan? TryBuildNativeBlasPlan(IReadOnlyList<ShaderBlock> blocks, out string reason)
        {
            reason = string.Empty;
            if (!TryFindBlock(blocks, NativeBlasHeaderPc, out var header) || !_loopLatchByHeader.TryGetValue(header, out var latch))
            {
                reason = "no loop at 0x10E0";
                return null;
            }

            if (_program.Instructions[blocks[latch].EndIndex - 1].Pc != NativeBlasLatchPc ||
                latch + 1 >= blocks.Count || blocks[latch + 1].StartPc != NativeBlasExitPc)
            {
                reason = "the loop does not end at 0x15E4 with its exit at 0x15E8";
                return null;
            }

            var instructions = new SortedDictionary<uint, Gen5ShaderInstruction>();
            var signature = 0xCBF29CE484222325UL;
            for (var index = blocks[header].StartIndex; index < blocks[latch].EndIndex; index++)
            {
                var instruction = _program.Instructions[index];
                instructions[instruction.Pc] = instruction;
                foreach (var character in $"{instruction.Pc:X4}:{instruction.Opcode};")
                {
                    signature = (signature ^ character) * 0x100000001B3UL;
                }
            }

            if (signature != NativeBlasSignature)
            {
                reason = $"loop signature 0x{signature:X16} is not 0x{NativeBlasSignature:X16}";
                return null;
            }

            var classified = new HashSet<uint>();
            foreach (var pc in NativeBlasEmitted.Concat(NativeBlasCompares).Concat(NativeBlasStack).Concat(NativeBlasControl))
            {
                if (!instructions.ContainsKey(pc) || !classified.Add(pc))
                {
                    reason = $"pc 0x{pc:X} is missing or classified twice";
                    return null;
                }
            }

            foreach (var pc in instructions.Keys.Where(pc => pc is >= NativeBlasAlphaFirst and <= NativeBlasAlphaLast))
            {
                if (!classified.Add(pc))
                {
                    reason = $"alpha test pc 0x{pc:X} is also classified elsewhere";
                    return null;
                }
            }

            var unclassified = instructions.Keys.Where(pc => !classified.Contains(pc)).ToArray();
            if (unclassified.Length != 0)
            {
                reason = $"unclassified instructions {string.Join(",", unclassified.Select(pc => $"0x{pc:X}"))}";
                return null;
            }

            return new NativeBlasPlan(header, latch, latch + 1, instructions);
        }

        // One guest instruction through its emitter, for this lane alone.
        private void EmitNativeGuest(NativeBlasPlan plan, uint pc)
        {
            var instruction = plan.Instructions[pc];
            var previousPc = _emittingPc;
            var previousFull = _execKnownFull;
            var previousForbid = _module.ForbidWaveOperations;
            _emittingPc = pc;
            _execKnownFull = true;
            _nativeLaneEmit = true;
            _module.ForbidWaveOperations = true;
            try
            {
                if (!TryEmitInstruction(instruction, out var error))
                {
                    throw new InvalidOperationException($"native BLAS loop pc=0x{pc:X} {instruction.Opcode}: {error}");
                }
            }
            finally
            {
                _emittingPc = previousPc;
                _execKnownFull = previousFull;
                _nativeLaneEmit = false;
                _module.ForbidWaveOperations = previousForbid;
            }
        }

        // A guest compare for its lane result; VCMPX leaves EXEC alone.
        private uint EmitNativeCompare(NativeBlasPlan plan, uint pc)
        {
            _nativeCompareResult = 0;
            EmitNativeGuest(plan, pc);
            if (_nativeCompareResult == 0)
            {
                throw new InvalidOperationException($"native BLAS loop pc=0x{pc:X} is not a vector compare");
            }

            return _nativeCompareResult;
        }

        // A lane mask with only this lane's bit: what a compare leaves for the lane's own later reads.
        private void StoreNativeLaneMask(uint register, uint condition)
        {
            if (register is 126 or 127)
            {
                throw new InvalidOperationException("EXEC write inside a lane-local region");
            }

            var mask = BooleanToLaneMask(condition);
            if (_waveLaneCount == 32)
            {
                StoreS(register, Narrow(mask));
                return;
            }

            StoreS64(register, mask);
        }

        private uint NativeVariable(string name, uint initial = 0)
        {
            var variable = _module.AddGlobalVariable(_privateUintPointer, SpirvStorageClass.Private, UInt(initial));
            _module.AddName(variable, name);
            _interfaces.Add(variable);
            return variable;
        }

        private void DeclareNativeBlasState()
        {
            if (_nativeStack != 0)
            {
                return;
            }

            if (RtSharedStacks)
            {
                // Slot-major, entry n of lane i at n * 64 + i, so a push or pop of a warp hits 32 banks.
                var rows = RtStackSplit ? RtStackSharedRows : NativeBlasStackCapacity;
                var sharedType = _module.TypeArray(_uintType, rows * 64);
                _nativeStack = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Workgroup, sharedType),
                    SpirvStorageClass.Workgroup);
                _workgroupUintPointer = _module.TypePointer(SpirvStorageClass.Workgroup, _uintType);
                if (RtStackSplit)
                {
                    _nativePopped = NativeVariable("nativeBlasPopped");
                }
            }
            else
            {
                var arrayType = _module.TypeArray(_uintType, NativeBlasStackCapacity);
                _nativeStack = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Private, arrayType),
                    SpirvStorageClass.Private,
                    _module.ConstantNull(arrayType));
            }

            _module.AddName(_nativeStack, "nativeBlasStack");
            _interfaces.Add(_nativeStack);
            _nativeStackPointer = NativeVariable("nativeBlasStackPointer");
            _nativeAlive = NativeVariable("nativeBlasAlive");
            _nativeSteps = NativeVariable("nativeBlasSteps");
            _nativeCapped = NativeVariable("nativeBlasCapped");
        }

        private uint NativeStackSlot(uint index)
        {
            var clamped = Ext(38, _uintType, index, UInt(NativeBlasStackCapacity - 1));
            if (RtSharedStacks)
            {
                return _module.AddInstruction(SpirvOp.AccessChain, _workgroupUintPointer, _nativeStack,
                    IAdd(_module.AddInstruction(SpirvOp.IMul, _uintType, clamped, UInt(64)), RtStackLane()));
            }

            return _module.AddInstruction(SpirvOp.AccessChain, _privateUintPointer, _nativeStack, clamped);
        }

        // [local] SHARPEMU_RT_SHARED_STACKS (on by default, 0 = off): the BLAS and TLAS stacks live in
        // workgroup memory instead of per-invocation private arrays. NVIDIA keeps a dynamically
        // indexed private array in registers, which pinned the pass at 255 registers (8 warps per SM);
        // in workgroup memory it compiles to 180 registers and no local memory (hito4, rg002stk).
        // Needs one guest wave per workgroup: the slots are indexed by the lane within the wave.
        private static readonly bool RtSharedStacksSwitch = Environment.GetEnvironmentVariable("SHARPEMU_RT_SHARED_STACKS") != "0";
        private bool RtSharedStacks => RtSharedStacksSwitch && _waveLaneCount == 64 && Wave64WavesPerWorkgroup == 1;
        private uint _workgroupUintPointer;

        private uint RtStackLane() => BitwiseAnd(GuestWaveLane(), UInt(63));

        // [local] SHARPEMU_RT_STACK_SPLIT=<rows>: the BLAS stack in three tiers. Entries 0-13 use rows
        // 2-15 of the guest LDS (64 dwords a row, the lane's own column), which the pass only uses for
        // the guest stack (the same columns, rows 1-14, run after the native loop of the same lane in
        // verify) and the TLAS exchange (rows 0-1). The next <rows> entries use a smaller workgroup
        // array, and the rest the RT stack area of the fault buffer, in the warp slot the subgroup
        // claimed at the start of the program. NVIDIA sizes the register budget by the occupancy the
        // workgroup memory allows: with less of it per group, more groups are resident. A private
        // array for the deep entries is not an option: NVIDIA keeps it in registers (hito 5).
        private const uint RtStackLdsRows = 14;
        private const uint RtStackLdsFirstRow = 2;
        private static uint RtStackSplitRows => DeviceAddressPaging.RtStackSplitRows;
        private bool RtStackSplit => RtSharedStacks && RtStackSplitRows != 0 && DeviceAddressPaging.RtStackAreaEnabled &&
            _lds != 0 && _ldsDwordMask + 1 >= 1024 && _faultBuffer != 0 && _subgroupInvocationIdInput != 0 && _request.Hash == NativeBlasHash;
        private static uint RtStackSharedRows => RtStackSplitRows;
        private uint _nativePopped;
        private uint _rtStackSlot;

        private const uint RtStackSlotBitmapWords = DeviceAddressPaging.RtStackWarpSlots / 32;
        private const uint RtStackBitmapDword = (uint)(DeviceAddressPaging.FaultBitmapBytes / 4);
        private const uint RtStackEntriesDword = RtStackBitmapDword + RtStackSlotBitmapWords;
        private const uint RtStackFallbackBit = 0x8000_0000;

        // At the start of the program the first lane of each 32-lane subgroup claims a free warp slot
        // (a set bit is taken) and broadcasts it. One full pass over the bitmap is enough while fewer
        // warps are resident than there are slots; if none is free the hashed slot is shared and the
        // claim is counted, so the result may differ instead of the dispatch hanging.
        private void EmitRtStackClaim()
        {
            if (!RtStackSplit && !ScratchInSlots)
            {
                return;
            }

            _rtStackSlot = NativeVariable("rtStackSlot");
            var cursor = NativeVariable("rtStackCursor");
            var steps = NativeVariable("rtStackSteps");
            var group = Load(_uvec3Type, _workGroupIdInput);
            var key = IAdd(_module.AddInstruction(SpirvOp.IMul, _uintType, IAdd(
                    _module.AddInstruction(SpirvOp.IMul, _uintType, _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, group, 1), UInt(4099)),
                    _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, group, 0)), UInt(2)),
                ShiftRightLogical(BitwiseAnd(GuestWaveLane(), UInt(63)), UInt(5)));
            var start = BitwiseAnd(ShiftRightLogical(_module.AddInstruction(SpirvOp.IMul, _uintType, key, UInt(2654435761)), UInt(20)), UInt(RtStackSlotBitmapWords - 1));
            var elected = _module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3));
            EmitConditional(elected, () =>
            {
                Store(_rtStackSlot, BitwiseOr(ShiftLeftLogical(start, UInt(5)), UInt(RtStackFallbackBit)));
                Store(cursor, start);
                Store(steps, UInt(0));
                var header = _module.AllocateId();
                var body = _module.AllocateId();
                var next = _module.AllocateId();
                var merge = _module.AllocateId();
                _module.AddStatement(SpirvOp.Branch, header);
                _module.AddLabel(header);
                _module.AddStatement(SpirvOp.LoopMerge, merge, next, 0);
                _module.AddStatement(SpirvOp.Branch, body);
                _module.AddLabel(body);
                var word = Load(_uintType, cursor);
                var pointer = BlockWordPointer(_faultBuffer, IAdd(UInt(RtStackBitmapDword), word));
                var free = _module.AddInstruction(SpirvOp.Not, _uintType,
                    _module.AddInstruction(SpirvOp.AtomicLoad, _uintType, pointer, UInt(1), UInt(0)));
                EmitConditional(IsNotZero(free), () =>
                {
                    var bit = ShiftLeftLogical(UInt(1), Ext(73, _uintType, free));
                    var before = _module.AddInstruction(SpirvOp.AtomicOr, _uintType, pointer, UInt(1), UInt(0), bit);
                    EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(before, bit), UInt(0)),
                        () => Store(_rtStackSlot, IAdd(ShiftLeftLogical(word, UInt(5)), Ext(73, _uintType, free))));
                }, () => Store(cursor, BitwiseAnd(IAdd(word, UInt(1)), UInt(RtStackSlotBitmapWords - 1))));
                _module.AddStatement(SpirvOp.Branch, next);
                _module.AddLabel(next);
                var count = IAdd(Load(_uintType, steps), UInt(1));
                Store(steps, count);
                var again = LogicalAnd(IsNotZero(BitwiseAnd(Load(_uintType, _rtStackSlot), UInt(RtStackFallbackBit))),
                    ULess(count, UInt(RtStackSlotBitmapWords * 4)));
                _module.AddStatement(SpirvOp.BranchConditional, again, header, merge);
                _module.AddLabel(merge);
                if (_request.TraceDeviceAddressFaults)
                {
                    EmitConditional(IsNotZero(BitwiseAnd(Load(_uintType, _rtStackSlot), UInt(RtStackFallbackBit))), () =>
                        _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(31), UInt(1), UInt(0), UInt(1)));
                }
            });
            Store(_rtStackSlot, _module.AddInstruction(SpirvOp.GroupNonUniformBroadcastFirst, _uintType, UInt(3), Load(_uintType, _rtStackSlot)));
        }

        // [local] SHARPEMU_SCRATCH_SLOTS (on by default, 0 = off, 2 = verify): a compute program's
        // scratch (at most RtStackSlotEntries dwords per lane) lives in the warp slot of the RT stack
        // area instead of a private array, which NVIDIA keeps in registers when the index is dynamic. Not for 0x8395 with the split
        // stack, whose deep tier owns the slot.
        private static readonly bool ScratchSlotsSwitch = Environment.GetEnvironmentVariable("SHARPEMU_SCRATCH_SLOTS") != "0";

        private bool ScratchInSlots => ScratchSlotsSwitch && DeviceAddressPaging.RtStackAreaEnabled && !RtStackSplit &&
            _stage == Gen5SpirvStage.Compute && _scratch != 0 && _scratchDwordCount <= DeviceAddressPaging.RtStackSlotEntries &&
            _faultBuffer != 0 && _subgroupInvocationIdInput != 0 && _workGroupIdInput != 0;

        private uint ScratchSlotPointer(uint dwordIndex) => RtStackDeepPointer(dwordIndex);

        // SHARPEMU_SCRATCH_SLOTS=2 (verify): the private array is kept as well; every store goes to
        // both and every load compares them. Differences (a read before any write sees zero in the
        // private array and stale data in the slot) are counted in [GPU][RT_STACK]; the private
        // value is used, so the image does not change.
        private static readonly bool ScratchSlotsVerify = Environment.GetEnvironmentVariable("SHARPEMU_SCRATCH_SLOTS") == "2";
        private readonly Dictionary<uint, uint> _scratchShadows = [];

        private void ScratchStore(uint pointer, uint value)
        {
            Store(pointer, value);
            if (_scratchShadows.TryGetValue(pointer, out var shadow))
            {
                Store(shadow, value);
            }
        }

        private uint ScratchLoad(uint pointer)
        {
            var value = Load(_uintType, pointer);
            if (!_scratchShadows.TryGetValue(pointer, out var shadow))
            {
                return value;
            }

            var expected = Load(_uintType, shadow);
            if (_request.TraceDeviceAddressFaults)
            {
                _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(32), UInt(1), UInt(0), UInt(1));
                EmitConditional(_module.AddInstruction(SpirvOp.INotEqual, _boolType, value, expected), () =>
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(33), UInt(1), UInt(0), UInt(1)));
            }

            return expected;
        }

        // At the end of the program the subgroup frees its slot.
        private void EmitRtStackRelease()
        {
            if (_rtStackSlot == 0)
            {
                return;
            }

            var slot = Load(_uintType, _rtStackSlot);
            var owned = LogicalAnd(_module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3)),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(slot, UInt(RtStackFallbackBit)), UInt(0)));
            EmitConditional(owned, () =>
                _module.AddInstruction(SpirvOp.AtomicAnd, _uintType,
                    BlockWordPointer(_faultBuffer, IAdd(UInt(RtStackBitmapDword), ShiftRightLogical(slot, UInt(5)))), UInt(1), UInt(0),
                    _module.AddInstruction(SpirvOp.Not, _uintType, ShiftLeftLogical(UInt(1), BitwiseAnd(slot, UInt(31))))));
        }

        // Entry e of the deep tier for this lane: entry-major within the slot, so a warp's push is one line.
        private uint RtStackDeepPointer(uint entry)
        {
            var slot = BitwiseAnd(Load(_uintType, _rtStackSlot), UInt(DeviceAddressPaging.RtStackWarpSlots - 1));
            var row = IAdd(_module.AddInstruction(SpirvOp.IMul, _uintType, slot, UInt(DeviceAddressPaging.RtStackSlotEntries)), entry);
            return BlockWordPointer(_faultBuffer, IAdd(UInt(RtStackEntriesDword),
                IAdd(ShiftLeftLogical(row, UInt(5)), Load(_uintType, _subgroupInvocationIdInput))));
        }

        private uint ULess(uint left, uint right) => _module.AddInstruction(SpirvOp.ULessThan, _boolType, left, right);

        private uint USub(uint left, uint right) => _module.AddInstruction(SpirvOp.ISub, _uintType, left, right);

        // The three tiers for entry `index` (already below the capacity): LDS row, workgroup row, private.
        private void EmitSplitStackAccess(uint index, Action<uint> access)
        {
            EmitConditional(ULess(index, UInt(RtStackLdsRows)), () =>
                access(_module.AddInstruction(SpirvOp.AccessChain, _ldsElementPointer, _lds,
                    IAdd(_module.AddInstruction(SpirvOp.IMul, _uintType, IAdd(index, UInt(RtStackLdsFirstRow)), UInt(64)), RtStackLane()))),
                () => EmitConditional(ULess(index, UInt(RtStackLdsRows + RtStackSharedRows)), () =>
                    access(_module.AddInstruction(SpirvOp.AccessChain, _workgroupUintPointer, _nativeStack,
                        IAdd(_module.AddInstruction(SpirvOp.IMul, _uintType, USub(index, UInt(RtStackLdsRows)), UInt(64)), RtStackLane()))),
                    () => access(RtStackDeepPointer(USub(index, UInt(RtStackLdsRows + RtStackSharedRows))))));
        }

        private void EmitNativeStackStore(uint index, uint value)
        {
            if (!RtStackSplit)
            {
                Store(NativeStackSlot(index), value);
                return;
            }

            EmitSplitStackAccess(Ext(38, _uintType, index, UInt(NativeBlasStackCapacity - 1)), pointer => Store(pointer, value));
            if (_request.TraceDeviceAddressFaults)
            {
                EmitConditional(_module.AddInstruction(SpirvOp.UGreaterThanEqual, _boolType, index, UInt(RtStackLdsRows + RtStackSharedRows)), () =>
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(30), UInt(1), UInt(0), UInt(1)));
            }
        }

        private uint EmitNativeStackLoad(uint index)
        {
            if (!RtStackSplit)
            {
                return Load(_uintType, NativeStackSlot(index));
            }

            EmitSplitStackAccess(Ext(38, _uintType, index, UInt(NativeBlasStackCapacity - 1)), pointer => Store(_nativePopped, Load(_uintType, pointer)));
            return Load(_uintType, _nativePopped);
        }

        private void EmitNativePush(uint value)
        {
            var pointer = Load(_uintType, _nativeStackPointer);
            EmitNativeStackStore(pointer, value);
            Store(_nativeStackPointer, IAdd(pointer, UInt(1)));
        }

        private void EmitNativeBlasRegion(NativeBlasPlan plan)
        {
            if (HalfMaskPlan() is { } halfMasks)
            {
                bool InLoop(uint pc) => pc is >= NativeBlasHeaderPc and <= NativeBlasLatchPc;
                Console.Error.WriteLine($"[NATIVE_BLAS] hash=0x{_request.Hash:X16} mode={_nativeBlasMode} emitted; half-mask plan inside the loop:" +
                    $" exact=[{string.Join(" ", halfMasks.ExactPairsBefore.Where(pair => InLoop(pair.Key)).Select(pair => $"0x{pair.Key:X}:s{string.Join("/s", pair.Value)}"))}]" +
                    $" half=[{string.Join(" ", halfMasks.HalfMaskWrites.Where(pair => InLoop(pair.Key)).Select(pair => $"0x{pair.Key:X}:s{pair.Value}"))}]" +
                    $" barriers=[{string.Join(" ", halfMasks.SharedMemoryBarriersBefore.Where(InLoop).Select(pc => $"0x{pc:X}"))}]");
            }

            DeclareNativeBlasState();
            var verify = _nativeBlasMode == NativeBlasMode.Verify;
            if (verify && _request.TraceDeviceAddressFaults)
            {
                BvhTestCounter();
            }

            var enters = LogicalAnd(
                Load(_boolType, _programActive),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, _programCounter), UInt((uint)plan.Header)));
            EmitConditional(enters, () =>
            {
                if (verify)
                {
                    SaveNativeBlasState(plan);
                }

                Store(_nativeCapped, UInt(0));
                Store(_nativeSteps, UInt(0));
                BeginRtProfileRegion(RtProfileBlas);
                EmitConditional(Load(_boolType, _exec), () => EmitNativeBlasLoop(plan));
                EndRtProfileRegion(RtProfileBlas);
                if (RtProfileActive)
                {
                    CountRtProfile(1, Load(_uintType, _nativeSteps));
                }
                if (verify)
                {
                    _nativeResults ??= NativeBlasLiveOuts.Select(register => NativeVariable($"nativeBlasResult_v{register}")).ToArray();
                    _nativeVerifyArmed = _nativeVerifyArmed != 0 ? _nativeVerifyArmed : NativeVariable("nativeBlasVerifyArmed");
                    for (var index = 0; index < NativeBlasLiveOuts.Length; index++)
                    {
                        Store(_nativeResults[index], LoadV(NativeBlasLiveOuts[index]));
                    }

                    Store(_nativeVerifyArmed, UInt(1));
                    RestoreNativeBlasState();
                }
                else
                {
                    if (_request.TraceDeviceAddressFaults && _faultBuffer != 0)
                    {
                        EmitConditional(IsNotZero(Load(_uintType, _nativeCapped)), () =>
                            _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(2), UInt(1), UInt(0), UInt(1)));
                    }

                    Store(_programCounter, UInt((uint)plan.Exit));
                }
            });
        }

        // do { one guest iteration for this lane } while (alive), bounded by the dispatcher's step
        // budget per lane; a capped lane leaves with the hit it has, as the wave's other lanes go on.
        private void EmitNativeBlasLoop(NativeBlasPlan plan)
        {
            Store(_nativeAlive, UInt(1));
            Store(_nativeStackPointer, UInt(0));
            var header = _module.AllocateId();
            var body = _module.AllocateId();
            var next = _module.AllocateId();
            var merge = _module.AllocateId();
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(header);
            _module.AddStatement(SpirvOp.LoopMerge, merge, next, 0);
            _module.AddStatement(SpirvOp.Branch, body);
            _module.AddLabel(body);
            EmitNativeBlasIteration(plan);
            _module.AddStatement(SpirvOp.Branch, next);
            _module.AddLabel(next);
            var steps = IAdd(Load(_uintType, _nativeSteps), UInt(1));
            Store(_nativeSteps, steps);
            var again = IsNotZero(Load(_uintType, _nativeAlive));
            if (_maxDispatcherSteps > 0)
            {
                again = LogicalAnd(again, _module.AddInstruction(SpirvOp.ULessThan, _boolType, steps, UInt((uint)_maxDispatcherSteps)));
            }

            _module.AddStatement(SpirvOp.BranchConditional, again, header, merge);
            _module.AddLabel(merge);
            Store(_nativeCapped, Load(_uintType, _nativeAlive));
        }

        private void EmitNativeBlasIteration(NativeBlasPlan plan)
        {
            // Node test and type (0x10E4-0x10F8).
            EmitNativeGuest(plan, 0x10E4);
            EmitNativeGuest(plan, 0x10F8);
            var box = EmitNativeCompare(plan, 0x1100);
            EmitConditional(box, () =>
            {
                // Box: continue with the nearest child, push the others farthest first while
                // there is room (one check per node, so the stack holds up to 69 entries).
                EmitNativeGuest(plan, 0x110C);
                var second = EmitNativeCompare(plan, 0x1114);
                var room = _module.AddInstruction(SpirvOp.ULessThan, _boolType, Load(_uintType, _nativeStackPointer), UInt(NativeBlasPushRoom));
                EmitConditional(LogicalAnd(second, room), () =>
                {
                    var third = EmitNativeCompare(plan, 0x1128);
                    EmitConditional(third, () =>
                    {
                        var fourth = EmitNativeCompare(plan, 0x1134);
                        EmitConditional(fourth, () => EmitNativePush(LoadV(3)));
                        EmitNativePush(LoadV(2));
                    });
                    EmitNativePush(LoadV(1));
                });
            }, () =>
            {
                // Triangle (0x11CC-0x1548): t = t_num / t_den, a candidate unless t_num is +inf or
                // t > tmax; a non-opaque pair runs the alpha test; a commit shrinks tmax. Then the
                // next pair of the list, or a pop after its last one.
                foreach (var pc in new uint[] { 0x11CC, 0x11D0, 0x11D4, 0x11DC, 0x11E0, 0x11E4 })
                {
                    EmitNativeGuest(plan, pc);
                }

                var candidate = IsCurrentLaneSet(LoadS64(106));
                EmitConditional(candidate, () =>
                {
                    foreach (var pc in new uint[] { 0x11F0, 0x11F4, 0x11F8, 0x11FC })
                    {
                        EmitNativeGuest(plan, pc);
                    }

                    var alphaTested = EmitNativeCompare(plan, 0x1204);
                    EmitConditional(alphaTested, () =>
                    {
                        BeginRtProfileRegion(RtProfileAlpha);
                        CountRtProfile(0, UInt(1));
                        foreach (var pc in plan.Instructions.Keys)
                        {
                            if (pc is >= NativeBlasAlphaFirst and <= NativeBlasAlphaLast && !NativeBlasAlphaSkipped.Contains(pc))
                            {
                                EmitNativeGuest(plan, pc);
                            }
                        }

                        EndRtProfileRegion(RtProfileAlpha);
                    });
                    var accepted = EmitNativeCompare(plan, 0x1518);
                    EmitConditional(accepted, () =>
                    {
                        foreach (var pc in new uint[] { 0x1520, 0x1524, 0x1528, 0x152C, 0x1530, 0x1534, 0x1538 })
                        {
                            EmitNativeGuest(plan, pc);
                        }
                    });
                });
                EmitNativeGuest(plan, 0x1540);
                var lastPair = EmitNativeCompare(plan, 0x1544);
                EmitConditional(lastPair, () => EmitNativeGuest(plan, 0x1548));
            });

            // No node left: pop, or end the lane on an empty stack (0x1550-0x15A4).
            var pop = EmitNativeCompare(plan, 0x1550);
            EmitConditional(pop, () =>
            {
                var pointer = Load(_uintType, _nativeStackPointer);
                EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, pointer, UInt(0)),
                    () => Store(_nativeAlive, UInt(0)),
                    () =>
                    {
                        var top = _module.AddInstruction(SpirvOp.ISub, _uintType, pointer, UInt(1));
                        Store(_nativeStackPointer, top);
                        StoreV(8, EmitNativeStackLoad(top), guardWithExec: false);
                    });
            });

            // A pair-list entry (type <= 3) loads its triangle node and flags (0x15B8-0x15DC).
            EmitNativeGuest(plan, 0x15B8);
            var notPair = EmitNativeCompare(plan, 0x15BC);
            EmitConditional(LogicalAnd(IsNotZero(Load(_uintType, _nativeAlive)), LogicalNot(notPair)), () =>
            {
                EmitNativeGuest(plan, 0x15D0);
                EmitNativeGuest(plan, 0x15D4);
                EmitNativeGuest(plan, 0x15DC);
            });
        }

        // Verify mode: every register and flag the native loop may write, saved before it and
        // restored after, so the guest loop starts from the same state.
        private void SaveNativeBlasState(NativeBlasPlan plan)
        {
            if (_nativeShadows.Count == 0)
            {
                var scalars = new SortedSet<uint> { 106, 107 };
                var vectors = new SortedSet<uint> { 8 };
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
                    _nativeShadows.Add((register, false, NativeVariable($"nativeBlasSaved_s{register}")));
                }

                foreach (var register in vectors)
                {
                    _nativeShadows.Add((register, true, NativeVariable($"nativeBlasSaved_v{register}")));
                }

                foreach (var flag in new[] { _scc, _vcc })
                {
                    _nativeFlagShadows.Add((flag, NativeVariable("nativeBlasSavedFlag")));
                }

                if (_bvhTestCount != 0)
                {
                    _nativeFlagShadows.Add((_bvhTestCount, NativeVariable("nativeBlasSavedBvhTests")));
                }

                foreach (var key in _indirectKeyScratch.Values)
                {
                    _nativeFlagShadows.Add((key, NativeVariable("nativeBlasSavedKey")));
                }
            }

            foreach (var (register, vector, shadow) in _nativeShadows)
            {
                Store(shadow, vector ? LoadV(register) : LoadS(register));
            }

            foreach (var (variable, shadow) in _nativeFlagShadows)
            {
                Store(shadow, _flagVariables.Contains(variable)
                    ? SelectU(Load(_boolType, variable), UInt(1), UInt(0))
                    : Load(_uintType, variable));
            }
        }

        private void RestoreNativeBlasState()
        {
            foreach (var (register, vector, shadow) in _nativeShadows)
            {
                Store(vector ? VectorPointer(register) : ScalarPointer(register), Load(_uintType, shadow));
            }

            foreach (var (variable, shadow) in _nativeFlagShadows)
            {
                Store(variable, _flagVariables.Contains(variable)
                    ? IsNotZero(Load(_uintType, shadow))
                    : Load(_uintType, shadow));
            }
        }

        private uint NativeSavedValue(bool vector, uint register)
        {
            foreach (var (saved, isVector, shadow) in _nativeShadows)
            {
                if (saved == register && isVector == vector)
                {
                    return Load(_uintType, shadow);
                }
            }

            return vector ? LoadV(register) : LoadS(register);
        }

        // The verify record: 64 words before the [GPU][STEPS] area of the fault buffer's trace tail.
        //  0 compared exits  1 mismatches  2 native lanes capped by the step budget  3 OR of mismatch masks
        //  4 first-mismatch claim  5 native iterations (sum)  6 native iterations (max)  7 lane of the first mismatch
        //  8-16 guest live-outs  17-25 native live-outs (v23 v29 v31 v21 v22 v6 v7 v9 v43)
        //  26 root (v8)  27 tmax (v23) and 28 v29 at entry  29-32 s8 s9 s78 s79  33 native iterations of that lane
        private uint NativeVerifyWord(uint slot)
        {
            var length = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _faultBuffer, 0);
            return BlockWordPointer(_faultBuffer, IAdd(_module.AddInstruction(SpirvOp.ISub, _uintType, length, UInt(128)), UInt(slot)));
        }

        private void EmitNativeBlasVerifyCompare(NativeBlasPlan plan)
        {
            if (_nativeVerifyArmed == 0 || _nativeResults is null)
            {
                return;
            }

            var reached = LogicalAnd(
                LogicalAnd(
                    Load(_boolType, _programActive),
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, _programCounter), UInt((uint)plan.Exit))),
                IsNotZero(Load(_uintType, _nativeVerifyArmed)));
            EmitConditional(reached, () =>
            {
                Store(_nativeVerifyArmed, UInt(0));
                if (!_request.TraceDeviceAddressFaults || _faultBuffer == 0)
                {
                    return;
                }

                var mask = UInt(0);
                for (var index = 0; index < NativeBlasLiveOuts.Length; index++)
                {
                    var differs = _module.AddInstruction(SpirvOp.INotEqual, _boolType,
                        LoadV(NativeBlasLiveOuts[index]), Load(_uintType, _nativeResults[index]));
                    mask = BitwiseOr(mask, SelectU(differs, UInt(1u << index), UInt(0)));
                }

                var capped = IsNotZero(Load(_uintType, _nativeCapped));
                var steps = Load(_uintType, _nativeSteps);
                var mismatch = LogicalAnd(IsNotZero(mask), LogicalNot(capped));
                // Lanes outside EXEC at the loop run no iteration and are not counted as compared.
                EmitConditional(IsNotZero(steps), () =>
                {
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(0), UInt(1), UInt(0), UInt(1));
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(2), UInt(1), UInt(0), SelectU(capped, UInt(1), UInt(0)));
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(5), UInt(1), UInt(0), steps);
                    _module.AddInstruction(SpirvOp.AtomicUMax, _uintType, NativeVerifyWord(6), UInt(1), UInt(0), steps);
                });
                EmitConditional(mismatch, () =>
                {
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(1), UInt(1), UInt(0), UInt(1));
                    _module.AddInstruction(SpirvOp.AtomicOr, _uintType, NativeVerifyWord(3), UInt(1), UInt(0), mask);
                    var claim = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType, NativeVerifyWord(4), UInt(1), UInt(0), UInt(0), UInt(1), UInt(0));
                    EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, claim, UInt(0)), () =>
                    {
                        Store(NativeVerifyWord(7), GuestWaveLane());
                        for (var index = 0; index < NativeBlasLiveOuts.Length; index++)
                        {
                            Store(NativeVerifyWord(8 + (uint)index), LoadV(NativeBlasLiveOuts[index]));
                            Store(NativeVerifyWord(17 + (uint)index), Load(_uintType, _nativeResults[index]));
                        }

                        Store(NativeVerifyWord(26), NativeSavedValue(true, 8));
                        Store(NativeVerifyWord(27), NativeSavedValue(true, 23));
                        Store(NativeVerifyWord(28), NativeSavedValue(true, 29));
                        Store(NativeVerifyWord(29), LoadS(8));
                        Store(NativeVerifyWord(30), LoadS(9));
                        Store(NativeVerifyWord(31), LoadS(78));
                        Store(NativeVerifyWord(32), LoadS(79));
                        Store(NativeVerifyWord(33), steps);
                    });
                });
            });
        }
    }
}
