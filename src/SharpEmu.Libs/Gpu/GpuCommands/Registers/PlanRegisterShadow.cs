// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ContextRegisterOffset;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// [local] SHARPEMU_PLAN_PROBE: a value hash per context stamp group, the key a draw plan would compare instead of the
// stamps (which move on every write, repeated value or not). Each group's hash is the XOR of Mix(register, value) over
// the registers of that group written since the last reset of the shadow, so it depends only on the set of
// (register, value) pairs: a value that goes A, B, A comes back to the same hash, and the order of writes does not
// matter. TargetFormats is the color and depth target groups without their address and clear-value registers (the
// part a pipeline can depend on). A register never written contributes nothing, so a written zero differs from the
// typed default of an unwritten register.
public struct PlanGroupHashes : IEquatable<PlanGroupHashes>
{
    public ulong ColorTargets;
    public ulong DepthTargets;
    public ulong ShaderInterface;
    public ulong PipelineState;
    public ulong ContextDynamic;
    public ulong TargetFormats;

    public readonly bool Equals(PlanGroupHashes other) =>
        ColorTargets == other.ColorTargets && DepthTargets == other.DepthTargets && ShaderInterface == other.ShaderInterface &&
        PipelineState == other.PipelineState && ContextDynamic == other.ContextDynamic && TargetFormats == other.TargetFormats;

    public override readonly bool Equals(object? obj) => obj is PlanGroupHashes other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(ColorTargets, DepthTargets, ShaderInterface, PipelineState, ContextDynamic, TargetFormats);
}

// [local] SHARPEMU_PLAN_PROBE: the raw context dwords a bank was written with (only while the probe is on), to update
// the group hashes by the change of each written register.
// [local] Also on with SHARPEMU_DRAW_PLAN (on by default; off only with =0), whose plans compare these hashes, and with SHARPEMU_PLAN_SHADOW=1
// (the shadow alone: the tests turn it on so the hashes follow every register write they make). The hashes cost the
// thread that writes the registers (the front) a mix and two XORs per written register whose value changes.
// [local] A write reads its register's groups from one table (GroupOf, built from RegisterStamps.ContextStampOf and the
// target addresses), and a repeated value returns before any mix; the hashes are the same as with the stamp lookup.
public sealed class PlanRegisterShadow
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PLAN_PROBE") == "1" ||
        Environment.GetEnvironmentVariable("SHARPEMU_DRAW_PLAN") != "0" || Environment.GetEnvironmentVariable("SHARPEMU_PLAN_SHADOW") == "1";

    private static readonly bool[] TargetAddress = BuildTargetAddresses();

    // Per context register: the group its value hashes into (the RegisterStamp of ColorTargets, DepthTargets,
    // ShaderInterface or ContextDynamic; PipelineState for every other register), plus FormatsBit when TargetFormats
    // takes it too. After TargetAddress, which it reads.
    private const byte FormatsBit = 0x80;
    private static readonly byte[] GroupOf = BuildGroups();

    private readonly uint[] _values = new uint[RegisterBankLayout.ContextRegisterCount];
    private readonly ulong[] _known = new ulong[(RegisterBankLayout.ContextRegisterCount + 63) / 64];
    public PlanGroupHashes Hashes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(uint register, uint value)
    {
        var mixed = (((ulong)register << 32) | value) + 0x9E3779B97F4A7C15UL;
        mixed ^= mixed >> 30;
        mixed *= 0xBF58476D1CE4E5B9UL;
        mixed ^= mixed >> 27;
        mixed *= 0x94D049BB133111EBUL;
        return mixed ^ (mixed >> 31);
    }

    public void Write(uint offset, ReadOnlySpan<uint> values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            Write(offset + (uint)index, values[index]);
        }
    }

    public void Write(uint register, uint value)
    {
        var values = _values;
        if (register >= (uint)values.Length)
        {
            return;
        }

        var word = (int)(register >> 6);
        var bit = 1UL << (int)(register & 63);
        ulong delta;
        if ((_known[word] & bit) != 0)
        {
            var old = values[register];
            if (old == value)
            {
                return;
            }

            delta = Mix(register, value) ^ Mix(register, old);
        }
        else
        {
            delta = Mix(register, value);
            _known[word] |= bit;
        }

        values[register] = value;
        var group = GroupOf[register];
        switch ((RegisterStamp)(group & ~FormatsBit))
        {
            case RegisterStamp.ColorTargets:
                Hashes.ColorTargets ^= delta;
                break;
            case RegisterStamp.DepthTargets:
                Hashes.DepthTargets ^= delta;
                break;
            case RegisterStamp.ShaderInterface:
                Hashes.ShaderInterface ^= delta;
                break;
            case RegisterStamp.ContextDynamic:
                Hashes.ContextDynamic ^= delta;
                break;
            default:
                Hashes.PipelineState ^= delta;
                break;
        }

        if ((group & FormatsBit) != 0)
        {
            Hashes.TargetFormats ^= delta;
        }
    }

    // Hashes that no written state has: for a bank whose dwords are not known.
    public static PlanGroupHashes UniqueHashes(long serial)
    {
        var seed = Mix(0xFFFF_FFFFu, (uint)serial) ^ ((ulong)serial << 40);
        return new PlanGroupHashes
        {
            ColorTargets = seed ^ 1, DepthTargets = seed ^ 2, ShaderInterface = seed ^ 3, PipelineState = seed ^ 4, ContextDynamic = seed ^ 5,
            TargetFormats = seed ^ 6,
        };
    }

    // A context replaced by its defaults (reset, clear): nothing written since.
    public void Clear()
    {
        Array.Clear(_known);
        Hashes = default;
    }

    public PlanRegisterShadow Copy()
    {
        var copy = new PlanRegisterShadow { Hashes = Hashes };
        _values.CopyTo(copy._values, 0);
        _known.CopyTo(copy._known, 0);
        return copy;
    }

    public void CopyFrom(PlanRegisterShadow? source)
    {
        if (source is null)
        {
            Clear();
            return;
        }

        source._values.CopyTo(_values, 0);
        source._known.CopyTo(_known, 0);
        Hashes = source.Hashes;
    }

    // The target registers a pipeline cannot depend on: base addresses (with their high and extension words) and
    // clear values.
    internal static bool IsTargetAddress(uint register) => register < TargetAddress.Length && TargetAddress[register];

    private static byte[] BuildGroups()
    {
        var groups = new byte[RegisterBankLayout.ContextRegisterCount];
        for (var register = 0u; register < groups.Length; register++)
        {
            var stamp = RegisterStamps.ContextStampOf(register);
            groups[register] = stamp switch
            {
                RegisterStamp.ColorTargets or RegisterStamp.DepthTargets => (byte)((byte)stamp | (TargetAddress[register] ? 0 : FormatsBit)),
                RegisterStamp.ShaderInterface or RegisterStamp.ContextDynamic => (byte)stamp,
                _ => (byte)RegisterStamp.PipelineState,
            };
        }

        return groups;
    }

    private static bool[] BuildTargetAddresses()
    {
        var addresses = new bool[RegisterBankLayout.ContextRegisterCount];
        const uint colorStride = 0xF; // CB_COLOR0_BASE .. CB_COLOR0_DCC_BASE, then the next target
        for (var target = 0u; target < ContextRegisters.ColorTargetCount; target++)
        {
            var first = target * colorStride;
            foreach (var register in (ReadOnlySpan<uint>)[CbColor0Base, CbColor0Cmask, CbColor0Fmask, CbColor0ClearWord0, CbColor0ClearWord1, CbColor0DccBase])
            {
                addresses[register + first] = true;
            }

            foreach (var register in (ReadOnlySpan<uint>)[CbColor0BaseExt, CbColor0CmaskBaseExt, CbColor0FmaskBaseExt, CbColor0DccBaseExt])
            {
                addresses[register + target] = true;
            }
        }

        foreach (var register in (ReadOnlySpan<uint>)[DbHtileDataBase, DbZReadBase, DbStencilReadBase, DbZWriteBase, DbStencilWriteBase,
                     DbZReadBaseHi, DbStencilReadBaseHi, DbZWriteBaseHi, DbStencilWriteBaseHi, DbHtileDataBaseHi])
        {
            addresses[register] = true;
        }

        return addresses;
    }
}
