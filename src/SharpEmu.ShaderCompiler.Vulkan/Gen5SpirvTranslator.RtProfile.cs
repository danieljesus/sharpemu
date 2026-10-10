// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_8395_PROFILE=1 (with SHARPEMU_TRACE_GPU_MEMORY_ADDRESS=auto): where GTA V's RT
// reflections pass (0x8395E43F382309DF) spends its time. Each region of the program reads the
// subgroup clock (VK_KHR_shader_clock, when the device has it) on entry and exit; the first
// invocation of every subgroup adds its region times to the fault buffer's trace tail, and every
// invocation adds its counters, printed as [GPU][RT_PROFILE].
public static partial class Gen5SpirvTranslator
{
    // Set by the presenter when it enabled VK_KHR_shader_clock (shaderSubgroupClock).
    public static bool ShaderClockEnabled { get; set; }

    private sealed partial class CompilationContext
    {
        private const ulong RtProfileHash = 0x8395E43F382309DF;

        // Regions by the pcs of their first and last blocks, in the structured program's nesting:
        // traversal holds the TLAS loops, which hold instance entry and the TLAS box-node work.
        // BLAS and alpha are timed inside the native BLAS loop.
        private static readonly (string Name, uint FirstPc, uint LastPc)[] RtProfileBlockRegions =
        [
            ("setup", 0x0000, 0x0190),
            ("raygen", 0x01A8, 0x0DB4),
            ("pretrav", 0x0E80, 0x0EC8),
            ("traversal", 0x0F8C, 0x19B4),
            ("instance", 0x0FE8, 0x0FE8),
            ("blas", 0, 0),
            ("alpha", 0, 0),
            ("tlasnode", 0x15F0, 0x19A8),
            ("hitshade", 0x19C4, 0x1DBC),
            ("hitlist", 0x1DC4, 0x1F8C),
            ("reduction", 0x1F98, 0x212C),
        ];

        public const int RtProfileBlas = 5;
        public const int RtProfileAlpha = 6;
        private const int RtProfileInstance = 4;
        private const int RtProfileTlasNode = 7;

        private bool? _rtProfileActive;
        private Dictionary<int, (int First, int Last)>? _rtProfileBlocks;
        private uint[]? _rtProfileTimes;
        private uint[]? _rtProfileStarts;
        private uint _rtProfileProgramStart;
        // Counters: alpha tests, native BLAS iterations, TLAS box-node visits, instance entries.
        private uint[]? _rtProfileCounters;

        private bool RtProfileActive =>
            _rtProfileActive ??= _request.Hash == RtProfileHash &&
                Environment.GetEnvironmentVariable("SHARPEMU_8395_PROFILE") == "1";

        private bool RtProfileClock => RtProfileActive && ShaderClockEnabled;

        private bool RtProfileRecords => RtProfileActive && _request.TraceDeviceAddressFaults && _faultBuffer != 0;

        private void DeclareRtProfileCapability()
        {
            if (RtProfileClock)
            {
                _module.AddExtension("SPV_KHR_shader_clock");
                _module.AddCapability(SpirvCapability.ShaderClockKhr);
            }
        }

        private void PrepareRtProfile(IReadOnlyList<ShaderBlock> blocks)
        {
            if (!RtProfileActive)
            {
                return;
            }

            _rtProfileBlocks = [];
            for (var region = 0; region < RtProfileBlockRegions.Length; region++)
            {
                var (name, firstPc, lastPc) = RtProfileBlockRegions[region];
                if (region is RtProfileBlas or RtProfileAlpha)
                {
                    continue;
                }

                if (!TryFindBlock(blocks, firstPc, out var first) || !TryFindBlock(blocks, lastPc, out var last) || last < first)
                {
                    Console.Error.WriteLine($"[RT_PROFILE] region {name} has no blocks at 0x{firstPc:X}..0x{lastPc:X}; skipped");
                    continue;
                }

                // A region must hold whole loops or sit inside one loop body.
                var crosses = _loopLatchByHeader.Any(loop =>
                    (first < loop.Key && last >= loop.Key && last < loop.Value) ||
                    (first > loop.Key && first <= loop.Value && last > loop.Value));
                if (crosses)
                {
                    Console.Error.WriteLine($"[RT_PROFILE] region {name} crosses a loop; skipped");
                    continue;
                }

                _rtProfileBlocks[region] = (first, last);
            }

            Console.Error.WriteLine($"[RT_PROFILE] hash=0x{_request.Hash:X16} clock={RtProfileClock} regions={string.Join(" ", _rtProfileBlocks.Select(pair => $"{RtProfileBlockRegions[pair.Key].Name}={pair.Value.First}..{pair.Value.Last}"))}");
        }

        private void DeclareRtProfileState()
        {
            if (_rtProfileTimes is not null)
            {
                return;
            }

            _rtProfileTimes = RtProfileBlockRegions.Select(region => NativeVariable($"rtProfileTime_{region.Name}")).ToArray();
            _rtProfileStarts = RtProfileBlockRegions.Select(region => NativeVariable($"rtProfileStart_{region.Name}")).ToArray();
            _rtProfileCounters = Enumerable.Range(0, 4).Select(index => NativeVariable($"rtProfileCounter{index}")).ToArray();
            _rtProfileProgramStart = NativeVariable("rtProfileProgramStart");
        }

        private uint ReadSubgroupClock() =>
            _module.AddInstruction(SpirvOp.CompositeExtract, _uintType,
                _module.AddInstruction(SpirvOp.ReadClockKhr, _uvec2Type, UInt(3)), 0);

        private void BeginRtProfileRegion(int region)
        {
            if (!RtProfileClock)
            {
                return;
            }

            DeclareRtProfileState();
            Store(_rtProfileStarts![region], ReadSubgroupClock());
        }

        private void EndRtProfileRegion(int region)
        {
            if (!RtProfileClock)
            {
                return;
            }

            DeclareRtProfileState();
            var elapsed = _module.AddInstruction(SpirvOp.ISub, _uintType, ReadSubgroupClock(), Load(_uintType, _rtProfileStarts![region]));
            Store(_rtProfileTimes![region], IAdd(Load(_uintType, _rtProfileTimes[region]), elapsed));
        }

        private void CountRtProfile(int counter, uint amount)
        {
            if (!RtProfileActive)
            {
                return;
            }

            DeclareRtProfileState();
            Store(_rtProfileCounters![counter], IAdd(Load(_uintType, _rtProfileCounters[counter]), amount));
        }

        private void BeginRtProfileProgram()
        {
            if (!RtProfileActive)
            {
                return;
            }

            DeclareRtProfileState();
            if (RtProfileClock)
            {
                Store(_rtProfileProgramStart, ReadSubgroupClock());
            }
        }

        // Opens the regions that start at a block of this structured range (not at its own header).
        private void OpenRtProfileRegions(int index, int enclosingHeader, List<int> open)
        {
            if (_rtProfileBlocks is null || index == enclosingHeader)
            {
                return;
            }

            foreach (var (region, (first, _)) in _rtProfileBlocks)
            {
                if (first == index)
                {
                    BeginRtProfileRegion(region);
                    open.Add(region);
                    if (region is RtProfileInstance or RtProfileTlasNode)
                    {
                        // Visits: the invocations whose next block is the region's first.
                        var taken = LogicalAnd(Load(_boolType, _programActive),
                            _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, _programCounter), UInt((uint)index)));
                        CountRtProfile(region == RtProfileInstance ? 3 : 2, SelectU(taken, UInt(1), UInt(0)));
                    }
                }
            }
        }

        // Closes the open regions whose last block lies before the range's next block.
        private void CloseRtProfileRegions(int next, List<int> open)
        {
            if (_rtProfileBlocks is null)
            {
                return;
            }

            for (var position = open.Count - 1; position >= 0; position--)
            {
                var region = open[position];
                if (_rtProfileBlocks[region].Last < next)
                {
                    EndRtProfileRegion(region);
                    open.RemoveAt(position);
                }
            }
        }

        // Fault buffer trace tail, words 34..63 of the 64 before the probe area:
        //  34 subgroups  35/36 whole program  37+2r/38+2r region r (low/high, 11 regions)
        //  59 alpha tests  60 native BLAS iterations  61 TLAS box-node visits  62 instance entries
        //  63 bit 0: clocks present
        private void FlushRtProfile()
        {
            if (!RtProfileRecords)
            {
                return;
            }

            DeclareRtProfileState();
            for (var counter = 0; counter < 4; counter++)
            {
                _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(59 + (uint)counter), UInt(1), UInt(0),
                    Load(_uintType, _rtProfileCounters![counter]));
            }

            if (!RtProfileClock || _subgroupInvocationIdInput == 0)
            {
                return;
            }

            var elapsed = _module.AddInstruction(SpirvOp.ISub, _uintType, ReadSubgroupClock(), Load(_uintType, _rtProfileProgramStart));
            var first = _module.AddInstruction(SpirvOp.IEqual, _boolType, Load(_uintType, _subgroupInvocationIdInput), UInt(0));
            EmitConditional(first, () =>
            {
                _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(34), UInt(1), UInt(0), UInt(1));
                _module.AddInstruction(SpirvOp.AtomicOr, _uintType, NativeVerifyWord(63), UInt(1), UInt(0), UInt(1));
                AddRtProfile64(35, elapsed);
                for (var region = 0; region < RtProfileBlockRegions.Length; region++)
                {
                    AddRtProfile64(37 + 2 * (uint)region, Load(_uintType, _rtProfileTimes![region]));
                }
            });
        }

        // A 64-bit sum from 32-bit atomics: the add that wraps the low word carries into the high one.
        private void AddRtProfile64(uint lowWord, uint value)
        {
            var old = _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(lowWord), UInt(1), UInt(0), value);
            var wrapped = _module.AddInstruction(SpirvOp.ULessThan, _boolType, IAdd(old, value), old);
            EmitConditional(wrapped, () =>
                _module.AddInstruction(SpirvOp.AtomicIAdd, _uintType, NativeVerifyWord(lowWord + 1), UInt(1), UInt(0), UInt(1)));
        }
    }
}
