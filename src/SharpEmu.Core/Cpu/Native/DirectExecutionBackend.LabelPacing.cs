// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.Libs.Diagnostics;

namespace SharpEmu.Core.Cpu.Native;

// [local] SHARPEMU_GUEST_CALL_TIMELINE=1 (off by default; needs SHARPEMU_LABEL_TIMELINE and SHARPEMU_LABEL_PROBE):
// what the guest threads do around the polls of the probed label, in the label timeline's file and clock.
//   T  a guest read fault on the label: rip, thread id, address, then the 16 general registers at the fault
//      (rax rcx rdx rbx rsp rbp rsi rdi r8..r15). The first fault of each rip (up to 16 sites) also prints the code
//      around it and the guest return addresses found on its stack to stderr as [LABEL_READER_CODE] / _STACK.
//   H  an HLE import that returned: arg0, (thread id << 20) | import index, return address, ms in the call; the line's
//      time is the return. Calls of a thread that has faulted on the label that last
//      SHARPEMU_GUEST_CALL_TIMELINE_POLLER_MIN_US (default 20) or more, and calls of the other guest threads that last
//      SHARPEMU_GUEST_CALL_TIMELINE_MIN_US (default 500) or more.
// Names go to stderr once: [GUEST_CALL_NAME] index name, [GUEST_CALL_THREAD] id handle name.
public sealed partial class DirectExecutionBackend
{
    private static readonly bool _guestCallTimeline =
        LabelTimeline.Enabled && LabelLatencyProbe.Enabled &&
        Environment.GetEnvironmentVariable("SHARPEMU_GUEST_CALL_TIMELINE") == "1";

    private static readonly long _guestCallOtherMinTicks =
        (long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_GUEST_CALL_TIMELINE_MIN_US"), out var minUs) ? minUs : 500) *
        Stopwatch.Frequency / 1_000_000;

    private static readonly long _guestCallPollerMinTicks =
        (long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_GUEST_CALL_TIMELINE_POLLER_MIN_US"), out var pollerUs) ? pollerUs : 20) *
        Stopwatch.Frequency / 1_000_000;

    [ThreadStatic] private static bool t_labelPoller;
    [ThreadStatic] private static int t_guestCallThreadId;

    private static int _guestCallThreadCount;
    private static readonly ConcurrentDictionary<int, byte> _guestCallNamed = new();
    private static readonly ConcurrentDictionary<ulong, byte> _labelReaderSites = new();

    private static int GuestCallThreadId()
    {
        if (t_guestCallThreadId != 0) return t_guestCallThreadId;
        var id = Interlocked.Increment(ref _guestCallThreadCount);
        t_guestCallThreadId = id;
        Console.Error.WriteLine(
            $"[GUEST_CALL_THREAD] {id} 0x{GuestThreadExecution.CurrentGuestThreadHandle:X} " +
            $"{_activeGuestThreadState?.Name ?? Thread.CurrentThread.Name ?? "<unnamed>"}");
        return id;
    }

    private unsafe ulong DispatchImportTimed(int importIndex, nint argPackPtr)
    {
        var arg0 = *(ulong*)argPackPtr;
        var returnAddress = *(ulong*)(argPackPtr + 96);
        var started = Stopwatch.GetTimestamp();
        var result = DispatchImport(importIndex, argPackPtr);
        var elapsed = Stopwatch.GetTimestamp() - started;
        if (elapsed >= (t_labelPoller ? _guestCallPollerMinTicks : _guestCallOtherMinTicks) && GuestThreadExecution.IsGuestThread)
        {
            if ((uint)importIndex < (uint)_importEntries.Length && _guestCallNamed.TryAdd(importIndex, 0))
            {
                var entry = _importEntries[importIndex];
                Console.Error.WriteLine($"[GUEST_CALL_NAME] {importIndex} {entry.Export?.Name ?? entry.Nid}");
            }

            LabelTimeline.Log('H', arg0, ((ulong)GuestCallThreadId() << 20) | (uint)importIndex, returnAddress,
                elapsed * 1000.0 / Stopwatch.Frequency);
        }

        return result;
    }

    private unsafe static void NoteLabelReadFault(void* contextRecord, ulong rip, ulong address)
    {
        if (!LabelLatencyProbe.Overlaps(address, 8)) return;
        t_labelPoller = true;
        var id = GuestCallThreadId();
        var line = new StringBuilder(256);
        line.Append(CultureInfo.InvariantCulture, $"{rip:X} {id:X} {address:X}");
        for (var offset = CTX_RAX; offset <= CTX_R15; offset += 8)
            line.Append(CultureInfo.InvariantCulture, $" {ReadCtxU64(contextRecord, offset):X}");
        LabelTimeline.LogText('T', line.ToString());

        if (_labelReaderSites.Count >= 16 || !_labelReaderSites.TryAdd(rip, 0)) return;
        DumpLabelReaderCode("site", rip, rip, 0x200, 0x200);
        var rsp = ReadCtxU64(contextRecord, CTX_RSP);
        var stack = new byte[0x800];
        if (!TryReadHostBytes(rsp, stack)) return;
        var found = new StringBuilder();
        var dumped = 0;
        for (var offset = 0; offset < stack.Length; offset += 8)
        {
            var value = BitConverter.ToUInt64(stack, offset);
            if (value < GuestImageBase || value >= GuestImageLimit) continue;
            found.Append(CultureInfo.InvariantCulture, $" +{offset:X}={value:X}");
            if (dumped++ < 8) DumpLabelReaderCode($"ret+{offset:X}", rip, value, 0x180, 0x80);
        }

        Console.Error.WriteLine($"[LABEL_READER_STACK] rip={rip:X} rsp={rsp:X}{found}");
    }

    private static void DumpLabelReaderCode(string what, ulong site, ulong at, int before, int after)
    {
        var bytes = new byte[before + after];
        var start = at - (ulong)before;
        if (!TryReadExecutableBytes(start, bytes))
        {
            Console.Error.WriteLine($"[LABEL_READER_CODE] site={site:X} {what} at={at:X} unreadable");
            return;
        }

        Console.Error.WriteLine($"[LABEL_READER_CODE] site={site:X} {what} at={at:X} start={start:X} {Convert.ToHexString(bytes)}");
    }
}
