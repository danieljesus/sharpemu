// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] Ray queries for GTA V's RT reflections pass (0x8395E43F382309DF), at the seam of the native BLAS loop.
//
// SHARPEMU_RQ_MAP=1 (probe): at each entry to a BLAS, one lane per subgroup records the BLAS base (s8:s9, the key is
// base >> 8) in the map of the fault buffer's ray query area (DeviceAddressPaging.RqMapOffset) with the lanes that
// enter and the root node (v8), and counts subgroups, lanes, roots that are not box nodes and map overflows. The host
// reads the map after the dispatch ([GPU][RQ_MAP]).
//
// SHARPEMU_RT_RAYQUERY=1: the walk of a BLAS the host built an acceleration structure for (a TLAS of one identity
// instance over it, found by key in the area's table) is one rayQueryEXT with the ray in object space (v36..v41),
// tmin 0 and tmax v23. Every candidate comes back to the shader (no geometry is opaque): its primitive index plus
// the table's first pair index is the guest pair, loaded with the guest's own pair load (0x15D4, v8 = triangle node,
// v9 = flags); t goes to v47 and the barycentrics to v48/v3 (the host orders each triangle's vertices so that they
// are the guest's i and j after the triangle_id rotation). Then the guest's own code: the opacity test (0x1204), the
// alpha test (0x120C-0x150C), the accept test (0x1518) and the commit (0x1520-0x1538), which confirms the
// candidate. A candidate beyond the committed t is skipped (the guest tests !(t > tmax)). A BLAS without an entry,
// or whose root is not a box node, runs the native loop and goes to the map, for the host to build it.
// =2 (verify): the ray query runs first on a copy of the state and its result is kept aside; the native loop then runs
// and keeps its result; hit/miss, primitive, |dt|/t and |di|,|dj| are counted ([GPU][RQ_VERIFY]).
public static partial class Gen5SpirvTranslator
{
    // Set by the device when VK_KHR_ray_query and acceleration structures are enabled.
    public static bool RayQueryEnabled { get; set; }

    private sealed partial class CompilationContext
    {
        // Read on every compile, as the native loop's switch.
        private static bool ReadRqMapProbe() => Environment.GetEnvironmentVariable("SHARPEMU_RQ_MAP") == "1";

        private enum RqMode
        {
            Off,
            Native,
            Verify,
        }

        private static RqMode ReadRqMode() =>
            (Environment.GetEnvironmentVariable("SHARPEMU_RT_RAYQUERY") ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "1" or "on" => RqMode.Native,
                "2" or "verify" => RqMode.Verify,
                _ => RqMode.Off,
            };

        // Counter words of the area (after the map).
        private const uint RqCounterSubgroups = 0;
        private const uint RqCounterMapOverflows = 1;
        private const uint RqCounterLanes = 2;
        private const uint RqCounterNonBoxRoots = 3;
        internal const uint RqCounterQueryLanes = 8;
        internal const uint RqCounterFallbackLanes = 9;
        internal const uint RqCounterVerify = 10; // 10.. see EmitRqVerifyCompare
        internal const uint RqCounterSample = 64; // first differing lane: 64 claim, 65.. values

        private RqMode _rqMode;
        private bool _rqModeKnown;
        private uint _rqQuery;
        private uint _rqFound;
        private uint _rqAsLow;
        private uint _rqAsHigh;
        private uint _rqFirstPair;
        private uint _rqCommitted;
        private uint _rqDropped;
        private uint _rqSlot;
        private uint _rqSteps;
        private uint _rqDone;
        private uint[]? _rqResults;

        private uint RqWord(ulong areaOffset, uint dword) =>
            BlockWordPointer(_faultBuffer, UInt((uint)(areaOffset / 4) + dword));

        private uint RqWord(ulong areaOffset, uint slot, uint word) =>
            BlockWordPointer(_faultBuffer, IAdd(UInt((uint)(areaOffset / 4) + word), _module.AddInstruction(SpirvOp.IMul, _uintType, slot, UInt(DeviceAddressPaging.RqSlotWords))));

        private uint RqCounter(uint index) => RqWord(DeviceAddressPaging.RqCounterOffset, index);

        private uint AtomicAdd(uint pointer, uint value) =>
            _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, pointer, UInt(1), UInt(0), value);

        // The key of the BLAS the wave enters: its 256-byte aligned base >> 8 (40-bit addresses).
        private uint RqBlasKey() => BitwiseOr(ShiftRightLogical(LoadS(8), UInt(8)), ShiftLeftLogical(LoadS(9), UInt(24)));

        private uint RqFirstSlot(uint key) =>
            BitwiseAnd(ShiftRightLogical(_module.AddInstruction(SpirvOp.IMul, _uintType, key, UInt(2654435761)), UInt(20)),
                UInt(DeviceAddressPaging.RqTableSlots - 1));

        private bool RqAreaUsable => _faultBuffer != 0 && DeviceAddressPaging.RqAreaEnabled;

        // Lanes of the subgroup (among the active invocations) for which `condition` holds.
        private uint SubgroupCount(uint condition)
        {
            _module.AddCapability(SpirvCapability.GroupNonUniformArithmetic);
            return _module.AddInstruction(SpirvOp.GroupNonUniformIAdd, _uintType, UInt(3), 0u, SelectU(condition, UInt(1), UInt(0)));
        }

        // In the BLAS region, for every invocation at the loop header (lanes outside EXEC count as not entering).
        private void EmitRqMapProbe()
        {
            if (!ReadRqMapProbe() || !RqAreaUsable)
            {
                return;
            }

            EmitRqMapInsert(Load(_boolType, _exec), countRoots: true);
        }

        // One lane per subgroup adds the lanes for which `entering` holds to the map entry of the BLAS.
        // maxValue (SHARPEMU_BLAS_CYCLE): word 2 keeps the largest value among the lanes instead of counting subgroups.
        private void EmitRqMapInsert(uint entering, bool countRoots, uint maxValue = 0)
        {
            var lanes = SubgroupCount(entering);
            var largest = maxValue == 0 ? 0u : _module.AddInstruction(SpirvOp.GroupNonUniformUMax, _uintType, UInt(3), 0u, SelectU(entering, maxValue, UInt(0)));
            var root = LoadV(8);
            var elected = _module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3));
            EmitConditional(LogicalAnd(elected, IsNotZero(lanes)), () =>
            {
                var key = RqBlasKey();
                AtomicAdd(RqCounter(RqCounterSubgroups), UInt(1));
                AtomicAdd(RqCounter(RqCounterLanes), lanes);
                if (countRoots)
                {
                    EmitConditional(_module.AddInstruction(SpirvOp.ULessThanEqual, _boolType, BitwiseAnd(root, UInt(7)), UInt(3)),
                        () => AtomicAdd(RqCounter(RqCounterNonBoxRoots), UInt(1)));
                }

                var slot = NativeVariable("rqMapSlot");
                var steps = NativeVariable("rqMapSteps");
                var placed = NativeVariable("rqMapPlaced");
                Store(slot, RqFirstSlot(key));
                Store(steps, UInt(0));
                Store(placed, UInt(0));
                EmitRqBoundedLoop(steps, () => _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, placed), UInt(0)), () =>
                {
                    var current = Load(_uintType, slot);
                    var previous = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType,
                        RqWord(DeviceAddressPaging.RqMapOffset, current, 0), UInt(1), UInt(0), UInt(0), key, UInt(0));
                    var mine = _module.AddInstruction(SpirvOp.LogicalOr, _boolType,
                        _module.AddInstruction(SpirvOp.IEqual, _boolType, previous, UInt(0)),
                        _module.AddInstruction(SpirvOp.IEqual, _boolType, previous, key));
                    EmitConditional(mine, () =>
                    {
                        AtomicAdd(RqWord(DeviceAddressPaging.RqMapOffset, current, 1), lanes);
                        if (largest == 0)
                            AtomicAdd(RqWord(DeviceAddressPaging.RqMapOffset, current, 2), UInt(1));
                        else
                            _module.AddInstruction(SpirvOp.AtomicUMax, _uintType, RqWord(DeviceAddressPaging.RqMapOffset, current, 2), UInt(1), UInt(0), largest);
                        _module.AddInstruction(SpirvOp.AtomicExchange, _uintType, RqWord(DeviceAddressPaging.RqMapOffset, current, 3), UInt(1), UInt(0), root);
                        Store(placed, UInt(1));
                    }, () => Store(slot, BitwiseAnd(IAdd(current, UInt(1)), UInt(DeviceAddressPaging.RqMapSlots - 1))));
                });
                EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, placed), UInt(0)),
                    () => AtomicAdd(RqCounter(RqCounterMapOverflows), UInt(1)));
            });
        }

        // do { body } while (again() && ++steps < RqProbeLimit)
        private void EmitRqBoundedLoop(uint steps, Func<uint> again, Action body)
        {
            var header = _module.AllocateId();
            var bodyLabel = _module.AllocateId();
            var next = _module.AllocateId();
            var merge = _module.AllocateId();
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(header);
            _module.AddStatement(SpirvOp.LoopMerge, merge, next, 0);
            _module.AddStatement(SpirvOp.Branch, bodyLabel);
            _module.AddLabel(bodyLabel);
            body();
            _module.AddStatement(SpirvOp.Branch, next);
            _module.AddLabel(next);
            var count = IAdd(Load(_uintType, steps), UInt(1));
            Store(steps, count);
            _module.AddStatement(SpirvOp.BranchConditional, LogicalAnd(again(), ULess(count, UInt(DeviceAddressPaging.RqProbeLimit))), header, merge);
            _module.AddLabel(merge);
        }

        // The walk of one BLAS for the lanes in EXEC: the ray query when the host built the BLAS, else the native loop.
        private void EmitBlasWalk(NativeBlasPlan plan)
        {
            if (!_rqModeKnown)
            {
                _rqModeKnown = true;
                _rqMode = RayQueryEnabled && RqAreaUsable && _nativeBlasMode == NativeBlasMode.Native ? ReadRqMode() : RqMode.Off;
                if (_rqMode != RqMode.Off)
                {
                    Console.Error.WriteLine($"[NATIVE_BLAS] hash=0x{_request.Hash:X16} ray query mode={_rqMode}");
                }
            }

            if (_rqMode == RqMode.Off)
            {
                EmitNativeBlasLoop(plan);
                return;
            }

            DeclareRqState();
            EmitRqLookup();
            var query = LogicalAnd(LogicalAnd(IsNotZero(Load(_uintType, _rqFound)),
                _module.AddInstruction(SpirvOp.UGreaterThan, _boolType, BitwiseAnd(LoadV(8), UInt(7)), UInt(3))), RqRayValid());
            var queryLanes = SubgroupCount(query);
            var fallbackLanes = SubgroupCount(LogicalNot(query));
            EmitConditional(_module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3)), () =>
            {
                AtomicAdd(RqCounter(RqCounterQueryLanes), queryLanes);
                AtomicAdd(RqCounter(RqCounterFallbackLanes), fallbackLanes);
            });
            EmitRqMapInsert(LogicalNot(query), countRoots: false);
            if (_rqMode == RqMode.Native)
            {
                EmitConditional(query, () => EmitRayQueryWalk(plan), () => EmitNativeBlasLoop(plan));
                return;
            }

            EmitConditional(query, () =>
            {
                SaveNativeBlasState(plan);
                Store(_rqCommitted, UInt(0));
                EmitRayQueryWalk(plan);
                _rqResults ??= RqLiveOuts.Select(register => NativeVariable($"rqResult_v{register}")).ToArray();
                for (var index = 0; index < RqLiveOuts.Length; index++)
                {
                    Store(_rqResults[index], LoadV(RqLiveOuts[index]));
                }

                var queryHit = Load(_uintType, _rqCommitted);
                RestoreNativeBlasState();
                Store(_rqCommitted, UInt(0));
                Store(_rqDropped, UInt(0));
                ResetTriProbeCommit();
                EmitNativeBlasLoop(plan);
                EmitRqVerifyCompare(queryHit);
            }, () => EmitNativeBlasLoop(plan));
        }

        // The live-outs compared in verify: t, pair flags, triangle node, j, i, instance record.
        private static readonly uint[] RqLiveOuts = [23, 29, 31, 21, 22, 6, 7];

        private void DeclareRqState()
        {
            if (_rqQuery != 0)
            {
                return;
            }

            _module.AddCapability(SpirvCapability.RayQueryKhr);
            _module.AddExtension("SPV_KHR_ray_query");
            _rqQuery = _module.AddGlobalVariable(_module.TypePointer(SpirvStorageClass.Private, _module.TypeRayQuery()), SpirvStorageClass.Private);
            _module.AddName(_rqQuery, "rqQuery");
            _interfaces.Add(_rqQuery);
            _rqFound = NativeVariable("rqFound");
            _rqAsLow = NativeVariable("rqAsLow");
            _rqAsHigh = NativeVariable("rqAsHigh");
            _rqFirstPair = NativeVariable("rqFirstPair");
            _rqCommitted = NativeVariable("rqCommitted");
            _rqDropped = NativeVariable("rqDropped");
            _rqSlot = NativeVariable("rqSlot");
            _rqSteps = NativeVariable("rqSteps");
            _rqDone = NativeVariable("rqDone");
        }

        // The table entry of the BLAS key: open addressing from its hash slot, an empty key ends the search.
        private void EmitRqLookup()
        {
            var key = RqBlasKey();
            Store(_rqFound, UInt(0));
            Store(_rqSlot, RqFirstSlot(key));
            Store(_rqSteps, UInt(0));
            Store(_rqDone, UInt(0));
            EmitRqBoundedLoop(_rqSteps, () => _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, _rqDone), UInt(0)), () =>
            {
                var slot = Load(_uintType, _rqSlot);
                var stored = Load(_uintType, RqWord(DeviceAddressPaging.RqAreaOffset, slot, 0));
                EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, stored, key), () =>
                {
                    var low = Load(_uintType, RqWord(DeviceAddressPaging.RqAreaOffset, slot, 1));
                    var high = Load(_uintType, RqWord(DeviceAddressPaging.RqAreaOffset, slot, 2));
                    Store(_rqAsLow, low);
                    Store(_rqAsHigh, high);
                    Store(_rqFirstPair, Load(_uintType, RqWord(DeviceAddressPaging.RqAreaOffset, slot, 3)));
                    Store(_rqFound, SelectU(IsNotZero(BitwiseOr(low, high)), UInt(1), UInt(0)));
                    Store(_rqDone, UInt(1));
                }, () => EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, stored, UInt(0)),
                    () => Store(_rqDone, UInt(1)),
                    () => Store(_rqSlot, BitwiseAnd(IAdd(slot, UInt(1)), UInt(DeviceAddressPaging.RqTableSlots - 1)))));
            });
        }

        private uint RqFloat(uint register) => Bitcast(_floatType, LoadV(register));

        // rayQueryInitializeEXT needs a finite origin, direction and tmax, tmax >= tmin (0) and a direction that is not
        // zero (otherwise undefined behaviour on the traversal hardware); such lanes take the native loop.
        private uint RqRayValid()
        {
            uint Finite(uint register)
            {
                var value = RqFloat(register);
                return LogicalNot(LogicalOr(_module.AddInstruction(SpirvOp.IsNan, _boolType, value), _module.AddInstruction(SpirvOp.IsInf, _boolType, value)));
            }

            var valid = Finite(23);
            for (var register = 36u; register <= 41; register++)
            {
                valid = LogicalAnd(valid, Finite(register));
            }

            valid = LogicalAnd(valid, FCompare(SpirvOp.FOrdGreaterThanEqual, RqFloat(23), Float(0f)));
            var direction = LogicalOr(LogicalOr(
                FCompare(SpirvOp.FOrdNotEqual, RqFloat(39), Float(0f)),
                FCompare(SpirvOp.FOrdNotEqual, RqFloat(40), Float(0f))),
                FCompare(SpirvOp.FOrdNotEqual, RqFloat(41), Float(0f)));
            return LogicalAnd(valid, direction);
        }

        private uint RqVec3(uint first) =>
            _module.AddInstruction(SpirvOp.CompositeConstruct, _vec3Type, RqFloat(first), RqFloat(first + 1), RqFloat(first + 2));

        // while (rayQueryProceed) for this lane, every candidate through the guest's own pair load, alpha test and commit.
        private void EmitRayQueryWalk(NativeBlasPlan plan)
        {
            var address = _module.AddInstruction(SpirvOp.CompositeConstruct, _uvec2Type, Load(_uintType, _rqAsLow), Load(_uintType, _rqAsHigh));
            var structure = _module.AddInstruction(SpirvOp.ConvertUToAccelerationStructureKhr, _module.TypeAccelerationStructure(), address);
            _module.AddStatement(SpirvOp.RayQueryInitializeKhr, _rqQuery, structure, UInt(0), UInt(0xFF),
                RqVec3(36), Float(0f), RqVec3(39), RqFloat(23));
            Store(_rqDone, UInt(0));
            var header = _module.AllocateId();
            var body = _module.AllocateId();
            var next = _module.AllocateId();
            var merge = _module.AllocateId();
            _module.AddStatement(SpirvOp.Branch, header);
            _module.AddLabel(header);
            _module.AddStatement(SpirvOp.LoopMerge, merge, next, 0);
            _module.AddStatement(SpirvOp.Branch, body);
            _module.AddLabel(body);
            var proceed = _module.AddInstruction(SpirvOp.RayQueryProceedKhr, _boolType, _rqQuery);
            EmitConditional(proceed, () =>
            {
                var t = _module.AddInstruction(SpirvOp.RayQueryGetIntersectionTKhr, _floatType, _rqQuery, UInt(0));
                var inRange = LogicalNot(FCompare(SpirvOp.FOrdGreaterThan, t, RqFloat(23)));
                EmitConditional(inRange, () =>
                {
                    var primitive = _module.AddInstruction(SpirvOp.RayQueryGetIntersectionPrimitiveIndexKhr, _uintType, _rqQuery, UInt(0));
                    var barycentrics = _module.AddInstruction(SpirvOp.RayQueryGetIntersectionBarycentricsKhr, _vec2Type, _rqQuery, UInt(0));
                    StoreV(0, IAdd(primitive, Load(_uintType, _rqFirstPair)), guardWithExec: false);
                    EmitNativeGuest(plan, 0x15D4);
                    StoreV(47, Bitcast(_uintType, t), guardWithExec: false);
                    StoreV(48, Bitcast(_uintType, _module.AddInstruction(SpirvOp.CompositeExtract, _floatType, barycentrics, 0)), guardWithExec: false);
                    StoreV(3, Bitcast(_uintType, _module.AddInstruction(SpirvOp.CompositeExtract, _floatType, barycentrics, 1)), guardWithExec: false);
                    EmitNativeGuest(plan, 0x11F0);
                    EmitNativeGuest(plan, 0x11FC);
                    var alphaTested = EmitNativeCompare(plan, 0x1204);
                    EmitConditional(alphaTested, () =>
                    {
                        foreach (var pc in plan.Instructions.Keys)
                        {
                            if (pc is >= NativeBlasAlphaFirst and <= NativeBlasAlphaLast && !NativeBlasAlphaSkipped.Contains(pc))
                            {
                                EmitNativeGuest(plan, pc);
                            }
                        }
                    });
                    var accepted = EmitNativeCompare(plan, 0x1518);
                    EmitConditional(accepted, () =>
                    {
                        foreach (var pc in new uint[] { 0x1520, 0x1524, 0x1528, 0x152C, 0x1530, 0x1534, 0x1538 })
                        {
                            EmitNativeGuest(plan, pc);
                        }

                        Store(_rqCommitted, UInt(1));
                        _module.AddStatement(SpirvOp.RayQueryConfirmIntersectionKhr, _rqQuery);
                    });
                });
            }, () => Store(_rqDone, UInt(1)));
            _module.AddStatement(SpirvOp.Branch, next);
            _module.AddLabel(next);
            _module.AddStatement(SpirvOp.BranchConditional, _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, _rqDone), UInt(0)), header, merge);
            _module.AddLabel(merge);
        }

        // Native loop hooks for verify: a commit, and a box node whose children are dropped with the stack full.
        private void NoteRqNativeCommit()
        {
            NoteTriProbeCommit(); // [local] SHARPEMU_BVH_TRI_PROBE
            if (_rqMode == RqMode.Verify && _rqCommitted != 0)
            {
                Store(_rqCommitted, UInt(1));
            }
        }

        private void NoteRqNativeDrop(uint dropped)
        {
            if (_rqMode == RqMode.Verify && _rqDropped != 0)
            {
                EmitConditional(dropped, () => Store(_rqDropped, UInt(1)));
            }
        }

        // Verify counters (from RqCounterVerify): 0 compared, 1 both miss, 2 both hit, 3 query-only hit, 4 native-only hit,
        // 5 same primitive, 6 other primitive, 7 other primitive at equal t, 8 differing lanes whose native walk dropped
        // children, 9 native lanes capped, 10-15 |dt|/t of the same primitive (0, <1e-7, <1e-6, <1e-5, <1e-4, more),
        // 16-21 max |di|,|dj| of the same primitive (0, <1e-6, <1e-5, <1e-4, <1e-3, more), 22-24 |dt|/t of another
        // primitive (<1e-6, <1e-4, more), 25 query-only hits on a non-opaque pair, 26 native-only on a non-opaque pair,
        // 27 other primitive with either non-opaque, 28 lanes with a dropped child (all), 29/30 native/query t is NaN. Sample from RqCounterSample.
        private void EmitRqVerifyCompare(uint queryHit)
        {
            var nativeHit = Load(_uintType, _rqCommitted);
            var query = IsNotZero(queryHit);
            var native = IsNotZero(nativeHit);
            var dropped = IsNotZero(Load(_uintType, _rqDropped));
            uint Count(uint word) => AtomicAdd(RqCounter(RqCounterVerify + word), UInt(1));
            uint Result(int index) => Load(_uintType, _rqResults![index]);
            Count(0);
            EmitConditional(dropped, () => Count(28));
            EmitConditional(IsNotZero(Load(_uintType, _nativeCapped)), () => Count(9));
            var bothMiss = LogicalAnd(LogicalNot(query), LogicalNot(native));
            var bothHit = LogicalAnd(query, native);
            EmitConditional(bothMiss, () => Count(1));
            EmitConditional(LogicalAnd(query, LogicalNot(native)), () =>
            {
                Count(3);
                EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(Result(1), UInt(0x4000_0000)), UInt(0)), () => Count(25));
            });
            EmitConditional(LogicalAnd(native, LogicalNot(query)), () =>
            {
                Count(4);
                EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(LoadV(29), UInt(0x4000_0000)), UInt(0)), () => Count(26));
            });
            var tQuery = Bitcast(_floatType, Result(0));
            var tNative = RqFloat(23);
            EmitConditional(_module.AddInstruction(SpirvOp.IsNan, _boolType, tNative), () => Count(29));
            CountTriProbeVerify(LogicalAnd(native, _module.AddInstruction(SpirvOp.IsNan, _boolType, tNative)), LogicalAnd(native, LogicalNot(query)));
            EmitConditional(_module.AddInstruction(SpirvOp.IsNan, _boolType, tQuery), () => Count(30));
            var relative = FDiv(Ext(4, _floatType, FSub(tQuery, tNative)), Ext(40, _floatType, Ext(4, _floatType, tNative), Float(1e-30f)));
            var samePrimitive = LogicalAnd(
                _module.AddInstruction(SpirvOp.IEqual, _boolType, Result(1), LoadV(29)),
                _module.AddInstruction(SpirvOp.IEqual, _boolType, Result(2), LoadV(31)));
            uint Below(uint value, float limit) => FCompare(SpirvOp.FOrdLessThan, value, Float(limit));
            EmitConditional(bothHit, () => Count(2));
            EmitConditional(bothHit, () => EmitConditional(samePrimitive, () =>
            {
                Count(5);
                EmitConditional(FCompare(SpirvOp.FOrdEqual, relative, Float(0f)), () => Count(10),
                    () => EmitConditional(Below(relative, 1e-7f), () => Count(11),
                    () => EmitConditional(Below(relative, 1e-6f), () => Count(12),
                    () => EmitConditional(Below(relative, 1e-5f), () => Count(13),
                    () => EmitConditional(Below(relative, 1e-4f), () => Count(14), () => Count(15))))));
                var di = Ext(4, _floatType, FSub(Bitcast(_floatType, Result(4)), RqFloat(22)));
                var dj = Ext(4, _floatType, FSub(Bitcast(_floatType, Result(3)), RqFloat(21)));
                var delta = Ext(40, _floatType, di, dj);
                EmitConditional(FCompare(SpirvOp.FOrdEqual, delta, Float(0f)), () => Count(16),
                    () => EmitConditional(Below(delta, 1e-6f), () => Count(17),
                    () => EmitConditional(Below(delta, 1e-5f), () => Count(18),
                    () => EmitConditional(Below(delta, 1e-4f), () => Count(19),
                    () => EmitConditional(Below(delta, 1e-3f), () => Count(20), () => Count(21))))));
            }, () =>
            {
                Count(6);
                EmitConditional(FCompare(SpirvOp.FOrdEqual, relative, Float(0f)), () => Count(7));
                EmitConditional(Below(relative, 1e-6f), () => Count(22),
                    () => EmitConditional(Below(relative, 1e-4f), () => Count(23), () => Count(24)));
                EmitConditional(_module.AddInstruction(SpirvOp.LogicalOr, _boolType,
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(Result(1), UInt(0x4000_0000)), UInt(0)),
                    _module.AddInstruction(SpirvOp.IEqual, _boolType, BitwiseAnd(LoadV(29), UInt(0x4000_0000)), UInt(0))), () => Count(27));
            }));
            var differs = LogicalOr(LogicalNot(LogicalOr(bothMiss, bothHit)), LogicalAnd(bothHit, LogicalNot(samePrimitive)));
            EmitConditional(LogicalAnd(differs, dropped), () => Count(8));
            EmitConditional(differs, () =>
            {
                var claim = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType, RqCounter(RqCounterSample), UInt(1), UInt(0), UInt(0), UInt(1), UInt(0));
                EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, claim, UInt(0)), () =>
                {
                    uint[] values =
                    [
                        RqBlasKey(), queryHit, nativeHit, Result(0), Result(1), Result(2), LoadV(23), LoadV(29), LoadV(31),
                        NativeSavedValue(true, 23), LoadV(36), LoadV(37), LoadV(38), LoadV(39), LoadV(40), LoadV(41),
                    ];
                    for (var index = 0; index < values.Length; index++)
                    {
                        Store(RqCounter(RqCounterSample + 1 + (uint)index), values[index]);
                    }
                });
            });
        }
    }
}
