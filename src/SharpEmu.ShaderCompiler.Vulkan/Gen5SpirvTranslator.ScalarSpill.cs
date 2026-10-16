// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_RT_SPILL_SCALARS=1 (off by default) for GTA V's RT reflections pass (0x8395E43F382309DF).
//
// The guest keeps about 34 scalar registers live across the whole BVH walk (pc 0x0F8C-0x19C0, the native TLAS
// loop with the BLAS loop inside) that the walk never reads or writes: descriptors and constants the shading
// after the walk needs. Each scalar register is a per-invocation variable here, so NVIDIA holds them in vector
// registers through the walk. Overwriting them after the walk (a lossy probe on the game's own request,
// LocalPrewarmModuleTests) takes the module from 168 registers / 7968 B of shared memory (12 warps per SM) to 128 /
// 6944 B (16 warps per SM). The spill below gives the same 128 / 6944 B (spill + SHARPEMU_BVH_UNIFIED_FETCH=1: 159).
//
// With the switch on they are stored before the walk and loaded back after it, in the subgroup's warp slot of the
// RT stack area (DeviceAddressPaging.RtSpillRows extra rows per slot). If every value is equal across the
// subgroup, as scalar registers are, each lane stores to the same dword of the uniform rows, so a subgroup writes
// one line per 32 values; otherwise every lane stores to its own column of a per-value row. Each lane reads back
// what it stored itself, so the result is bit-exact either way (=2 verifies it in the game: [GPU][RT_STACK]
// spill_restores / spill_mismatches / spill_nonuniform, with SHARPEMU_TRACE_GPU_MEMORY_ADDRESS=auto). A register is spilled only if no instruction of
// the walk writes it (a scalar destination counts as two registers, for 64-bit results named by their first
// register), so the restored value is the one the walk leaves. The candidates are the registers the offline
// liveness found live across the walk and not read in it; one the walk did read would only stay live as well.
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // Live across 0x0F8C-0x19C0 and untouched by it (experimentos/hito4/across.py 0F8C 19C0 19C4).
        private static readonly uint[] RtSpillCandidates =
        [
            4, 5, 6, 7, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 48, 49, 50, 51, 52, 53, 54, 55,
            68, 69, 70, 71, 72, 73, 92, 93, 94, 95,
        ];

        private const uint ScalarRegisterLimit = 106;
        private uint _rtSpillUniform;
        private uint[]? _rtSpillKept;

        private bool RtSpillEnabled => DeviceAddressPaging.RtSpillScalars && _rtStackSlot != 0 && _request.Hash == NativeBlasHash;

        private uint[] RtSpillSet(SortedDictionary<uint, Gen5ShaderInstruction> instructions)
        {
            var written = new HashSet<uint>();
            foreach (var instruction in instructions.Values)
            {
                foreach (var operand in instruction.Destinations)
                {
                    var register = operand.Kind switch
                    {
                        Gen5OperandKind.ScalarRegister => operand.Value,
                        Gen5OperandKind.EncodedConstant when operand.Value < ScalarRegisterLimit => operand.Value,
                        _ => uint.MaxValue,
                    };
                    if (register == uint.MaxValue)
                    {
                        continue;
                    }

                    written.Add(register);
                    written.Add(register + 1);
                }
            }

            return RtSpillCandidates.Where(register => !written.Contains(register)).Take((int)DeviceAddressPaging.RtSpillMaxScalars).ToArray();
        }

        // Row `row` of the spill area of this subgroup's slot, column `column`.
        private uint RtSpillPointer(uint row, uint column) =>
            RtStackDeepPointerAt(IAdd(UInt(DeviceAddressPaging.RtStackSlotEntries), row), column);

        private void EmitRtSpill(uint[] registers)
        {
            if (registers.Length == 0)
            {
                return;
            }

            _module.AddCapability(SpirvCapability.GroupNonUniformVote);
            _rtSpillUniform = _rtSpillUniform != 0 ? _rtSpillUniform : NativeVariable("rtSpillUniform");
            var values = registers.Select(LoadS).ToArray();
            if (DeviceAddressPaging.RtSpillVerify)
            {
                _rtSpillKept = registers.Select(register => NativeVariable($"rtSpillKept_s{register}")).ToArray();
                for (var index = 0; index < values.Length; index++)
                {
                    Store(_rtSpillKept[index], values[index]);
                }
            }

            var uniform = values
                .Select(value => _module.AddInstruction(SpirvOp.GroupNonUniformAllEqual, _boolType, UInt(3), value))
                .Aggregate(LogicalAnd);
            Store(_rtSpillUniform, SelectU(uniform, UInt(1), UInt(0)));
            var lane = Load(_uintType, _subgroupInvocationIdInput);
            EmitConditional(uniform, () =>
            {
                for (var index = 0; index < values.Length; index++)
                {
                    _module.AddStatement(SpirvOp.Store,
                        RtSpillPointer(UInt((uint)index / 32), UInt((uint)index % 32)), values[index], 1u);
                }
            }, () =>
            {
                for (var index = 0; index < values.Length; index++)
                {
                    _module.AddStatement(SpirvOp.Store,
                        RtSpillPointer(UInt(DeviceAddressPaging.RtSpillUniformRows + (uint)index), lane), values[index], 1u);
                }
            });
        }

        private void EmitRtRestore(uint[] registers)
        {
            if (registers.Length == 0)
            {
                return;
            }

            var lane = Load(_uintType, _subgroupInvocationIdInput);
            EmitConditional(IsNotZero(Load(_uintType, _rtSpillUniform)), () =>
            {
                for (var index = 0; index < registers.Length; index++)
                {
                    Store(ScalarPointer(registers[index]), _module.AddInstruction(SpirvOp.Load, _uintType,
                        RtSpillPointer(UInt((uint)index / 32), UInt((uint)index % 32)), 1u));
                }
            }, () =>
            {
                for (var index = 0; index < registers.Length; index++)
                {
                    Store(ScalarPointer(registers[index]), _module.AddInstruction(SpirvOp.Load, _uintType,
                        RtSpillPointer(UInt(DeviceAddressPaging.RtSpillUniformRows + (uint)index), lane), 1u));
                }

                if (_request.TraceDeviceAddressFaults)
                {
                    _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(36), UInt(1), UInt(0), UInt(1));
                }
            });

            // Verify: count restored values that differ from the kept ones (words 34 and 35), then use the kept ones.
            if (_rtSpillKept is { } kept)
            {
                for (var index = 0; index < registers.Length; index++)
                {
                    var restored = LoadS(registers[index]);
                    var expected = Load(_uintType, kept[index]);
                    if (_request.TraceDeviceAddressFaults)
                    {
                        _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(34), UInt(1), UInt(0), UInt(1));
                        EmitConditional(_module.AddInstruction(SpirvOp.INotEqual, _boolType, restored, expected), () =>
                            _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeTlasWord(35), UInt(1), UInt(0), UInt(1)));
                    }

                    Store(ScalarPointer(registers[index]), expected);
                }
            }
        }
    }
}
