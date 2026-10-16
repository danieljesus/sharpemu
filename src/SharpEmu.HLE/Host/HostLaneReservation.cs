// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

public static unsafe class HostLaneReservation
{
    private const int RelationProcessorCore = 0;
    private const int ThreadPriorityHighest = 2;
    private const int ThreadPriorityAboveNormal = 1;

    public static readonly ulong ReservedMask;
    public static readonly ulong GuestMask;

    // [local] SHARPEMU_HOST_HELPER_LANES (on by default, =0 turns it off; needs the render core above and at least 6 physical
    // cores): the replay thread of the record ring and the GPU front thread each get a physical core of their own
    // (both SMT lanes, AboveNormal), the two highest-class cores after the render core, last in processor order
    // first. Guest threads lose those lanes too, and the other GPU helpers (submit, priority, deferred uploads) run
    // on the guest lanes, so no host thread is left free to land on the render core's sibling lane.
    public static readonly bool HelperLanesRequested = Environment.GetEnvironmentVariable("SHARPEMU_HOST_HELPER_LANES") != "0";
    public static readonly ulong RingMask;
    public static readonly ulong FrontMask;

    static HostLaneReservation()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SHARPEMU_RENDER_CORE") == "0")
        {
            return;
        }

        var lanes = Environment.ProcessorCount;
        if (lanes < 8 || lanes > 64)
        {
            return;
        }

        var cores = ReadCores();
        var core = SelectCore(cores);
        var all = lanes == 64 ? ulong.MaxValue : (1UL << lanes) - 1;
        if (core == 0 || (core & ~all) != 0 || (all & ~core) == 0)
        {
            return;
        }

        ReservedMask = core;
        GuestMask = all & ~core;
        if (HelperLanesRequested)
        {
            var (ring, front) = SelectHelperCores(cores, core);
            if (ring != 0 && front != 0 && ((ring | front) & ~all) == 0 && (GuestMask & ~(ring | front)) != 0)
            {
                RingMask = ring;
                FrontMask = front;
                GuestMask &= ~(ring | front);
            }
        }
    }

    public static bool Active => ReservedMask != 0;

    public static bool HelperLanesActive => RingMask != 0;

    public static void ApplyToRenderThread()
    {
        if (!Active)
        {
            return;
        }

        var thread = GetCurrentThread();
        _ = SetThreadAffinityMask(thread, (nuint)ReservedMask);
        _ = SetThreadPriority(thread, ThreadPriorityHighest);
        Console.Error.WriteLine($"[LOADER][INFO] Render thread reserved host lanes 0x{ReservedMask:X}; guest threads use 0x{GuestMask:X}.");
    }

    public static void ApplyToGuestThread()
    {
        if (Active)
        {
            _ = SetThreadAffinityMask(GetCurrentThread(), (nuint)GuestMask);
        }
    }

    // [local] SHARPEMU_HOST_HELPER_LANES=1: on the record ring's replay thread.
    public static void ApplyToRingThread() => ApplyToHelperCore(RingMask, "Record ring replay thread");

    // [local] SHARPEMU_HOST_HELPER_LANES=1: on the GPU front thread.
    public static void ApplyToFrontThread() => ApplyToHelperCore(FrontMask, "GPU front thread");

    // [local] SHARPEMU_HOST_HELPER_LANES=1: the GPU submit, priority and deferred upload threads share the guest lanes.
    public static void ApplyToHelperThread()
    {
        if (HelperLanesActive)
        {
            _ = SetThreadAffinityMask(GetCurrentThread(), (nuint)GuestMask);
        }
    }

    private static void ApplyToHelperCore(ulong mask, string what)
    {
        if (mask == 0)
        {
            return;
        }

        var thread = GetCurrentThread();
        _ = SetThreadAffinityMask(thread, (nuint)mask);
        _ = SetThreadPriority(thread, ThreadPriorityAboveNormal);
        Console.Error.WriteLine($"[LOADER][INFO] {what} reserved host lanes 0x{mask:X}; guest threads use 0x{GuestMask:X}.");
    }

    // The render core: the last core of the highest efficiency class.
    internal static ulong SelectCore(IReadOnlyList<(ulong Mask, int EfficiencyClass)> cores)
    {
        ulong selected = 0;
        var selectedClass = -1;
        foreach (var (mask, efficiencyClass) in cores)
        {
            if (mask != 0 && efficiencyClass >= selectedClass)
            {
                selected = mask;
                selectedClass = efficiencyClass;
            }
        }

        return selected;
    }

    // [local] The ring and front cores: the two highest-class cores other than the render core, the later one in
    // processor order first. None below 6 physical cores, which would leave the guest too few.
    internal static (ulong Ring, ulong Front) SelectHelperCores(IReadOnlyList<(ulong Mask, int EfficiencyClass)> cores, ulong render)
    {
        var candidates = new List<(ulong Mask, int EfficiencyClass, int Order)>();
        for (var index = 0; index < cores.Count; index++)
        {
            if (cores[index].Mask != 0 && cores[index].Mask != render)
            {
                candidates.Add((cores[index].Mask, cores[index].EfficiencyClass, index));
            }
        }

        if (candidates.Count + 1 < 6)
        {
            return (0, 0);
        }

        candidates.Sort((a, b) => a.EfficiencyClass != b.EfficiencyClass ? b.EfficiencyClass.CompareTo(a.EfficiencyClass) : b.Order.CompareTo(a.Order));
        return (candidates[0].Mask, candidates[1].Mask);
    }

    private static List<(ulong Mask, int EfficiencyClass)> ReadCores()
    {
        var cores = new List<(ulong Mask, int EfficiencyClass)>();
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, null, &length);
        if (length == 0)
        {
            return cores;
        }

        var buffer = new byte[length];
        fixed (byte* start = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, start, &length))
            {
                return cores;
            }

            for (uint offset = 0; offset + 48 <= length;)
            {
                var entry = start + offset;
                var size = *(uint*)(entry + 4);
                if (size == 0)
                {
                    break;
                }

                var efficiencyClass = entry[9];
                var groupCount = *(ushort*)(entry + 30);
                var mask = *(ulong*)(entry + 32);
                var group = *(ushort*)(entry + 40);
                if (*(int*)entry == RelationProcessorCore && groupCount == 1 && group == 0 && mask != 0)
                {
                    cores.Add((mask, efficiencyClass));
                }

                offset += size;
            }
        }

        return cores;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, uint* returnedLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
}
