// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_DEVICE_DATA_READS_VERIFY=1: a flattened data read of a SHARPEMU_DEVICE_DATA_READS shader is also
// performed through the page table, as the device-read mode performs it, and compared with the host's word. One lane per
// subgroup counts in the fault buffer's ray query area: word 110 the checks, 111 the mismatches, and the first mismatch
// claims 112 and leaves the host word, the device word, the address and pc << 8 | component in 113-117
// ([GPU][DEVICE_DATA_VERIFY]).
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        internal const uint DeviceDataVerifyFirstWord = 110;

        private void EmitDeviceDataVerify(uint address64, uint hostWord, uint pc, uint component)
        {
            var deviceWord = LoadDeviceDword(address64);
            var differs = _module.AddInstruction(SpirvOp.GroupNonUniformAny, _boolType, UInt(3),
                _module.AddInstruction(SpirvOp.INotEqual, _boolType, deviceWord, hostWord));
            EmitConditional(_module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3)), () =>
            {
                AtomicAdd(RqCounter(DeviceDataVerifyFirstWord), UInt(1));
                EmitConditional(differs, () =>
                {
                    AtomicAdd(RqCounter(DeviceDataVerifyFirstWord + 1), UInt(1));
                    var claim = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType, RqCounter(DeviceDataVerifyFirstWord + 2), UInt(1), UInt(0), UInt(0), UInt(1), UInt(0));
                    EmitConditional(UEqual(claim, UInt(0)), () =>
                    {
                        Store(RqCounter(DeviceDataVerifyFirstWord + 3), hostWord);
                        Store(RqCounter(DeviceDataVerifyFirstWord + 4), deviceWord);
                        Store(RqCounter(DeviceDataVerifyFirstWord + 5), _module.AddInstruction(SpirvOp.UConvert, _uintType, address64));
                        Store(RqCounter(DeviceDataVerifyFirstWord + 6), _module.AddInstruction(SpirvOp.UConvert, _uintType, _module.AddInstruction(SpirvOp.ShiftRightLogical, _ulongType, address64, ULong(32))));
                        Store(RqCounter(DeviceDataVerifyFirstWord + 7), UInt((pc << 8) | component));
                    });
                });
            });
        }
    }
}
