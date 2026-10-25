// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Iced.Intel;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.Core.Cpu.Native;

// [local] SHARPEMU_FAULT_READ_EMULATE: a guest load that faults on a watched page whose asked bytes are not
// GPU-modified is emulated from the backing instead of downloading the page's GPU-written ranges and unprotecting it.
// A download only writes GPU-modified ranges into the backing, so the asked bytes already hold the value the load
// would see if the page were unprotected now; the ordinary path shows the value of a later instant (after the
// download), and any instant within the fault is one the guest could have read. Only plain MOV / MOVZX / MOVSX /
// MOVSXD into a general register (not AH-DH) from a DS/SS operand of at most 8 bytes whose effective address is the
// fault address and stays in one page; anything else takes the usual path.
//
// A poll (GTA V's RenderThread spinning on a CP WRITE_DATA label that shares its page with GPU-written ranges) would
// then fault in a tight loop: the download it no longer waits for was the loop's sleep (~2,950 emulated faults/s,
// FPS 28 -> 15-26 on 2026-10-22). So a repeated read, the same thread reading the same bytes and value again within
// SHARPEMU_FAULT_READ_REPEAT_US (1000) of its previous emulated read, parks: it waits until a write touches the
// bytes or SHARPEMU_FAULT_READ_PARK_US (2000; 0 never parks) passes, then reads them again. A delayed read is still
// a read at the instant it returns.
//
// 0 (default) off; 1 emulates (and parks); 2 decodes and asks the store but still takes the ordinary path, then compares
// the bytes the load reads after it with the emulated ones: equal is verified, different with a write noted on the
// bytes in between is changed (the CP wrote the label meanwhile), different with none is a mismatch, and a page the
// download left protected is pending (the load faults again). [FAULT_READ_EMU] every 5 s, per second.
public sealed partial class DirectExecutionBackend
{
	private static readonly int FaultReadEmulateMode =
		int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FAULT_READ_EMULATE"), out var faultReadMode) ? faultReadMode : 0;

	private static readonly long FaultReadParkTicks = MicrosecondTicks("SHARPEMU_FAULT_READ_PARK_US", 2000);
	private static readonly long FaultReadRepeatTicks = MicrosecondTicks("SHARPEMU_FAULT_READ_REPEAT_US", 1000);

	private static long MicrosecondTicks(string name, long fallback) =>
		(long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback) * Stopwatch.Frequency / 1_000_000;

	private enum FaultReadOutcome { Emulated, Parked, Verified, Changed, Mismatch, Pending, Unwatched, NoCode, Form, Segment, Address, CrossPage, Store, Count }

	// =2: the emulated value, its size and the write watch over the bytes, checked after the ordinary path.
	private readonly record struct FaultReadVerify(bool Pending, ulong Value, int Size, int Watch);

	private static readonly long[] _faultReadOutcomes = new long[(int)FaultReadOutcome.Count];
	private static readonly object _faultReadMsGate = new();
	private static readonly List<double> _faultReadMs = new();
	private static long _faultReadReportAt;
	private static int _faultReadMismatchLogs;

	// The thread's previous emulated read: a repeat of it is a poll.
	[ThreadStatic] private static ulong t_faultReadAddress;
	[ThreadStatic] private static ulong t_faultReadValue;
	[ThreadStatic] private static int t_faultReadSize;
	[ThreadStatic] private static long t_faultReadAt;

	private const int CtxRax = 120;
	private const int CtxRip = 248;

	private static int GprOffset(Register register)
	{
		var full = register.GetFullRegister();
		return full >= Register.RAX && full <= Register.R15 ? CtxRax + 8 * (full - Register.RAX) : -1;
	}

	private static ulong FaultReadValue(ReadOnlySpan<byte> bytes) => bytes.Length switch
	{
		1 => bytes[0],
		2 => BitConverter.ToUInt16(bytes),
		4 => BitConverter.ToUInt32(bytes),
		_ => BitConverter.ToUInt64(bytes),
	};

	private unsafe static bool TryEmulateFaultingRead(void* contextRecord, ulong rip, ulong faultAddress, out FaultReadVerify verify)
	{
		verify = default;
		var started = Stopwatch.GetTimestamp();
		var code = new byte[15];
		if (!TryReadExecutableBytes(rip, code))
		{
			return NoteFaultRead(FaultReadOutcome.NoCode);
		}

		var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(code));
		decoder.IP = rip;
		decoder.Decode(out var instruction);
		if (instruction.Code == Code.INVALID || instruction.OpCount != 2 ||
			instruction.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Movsxd) ||
			instruction.GetOpKind(0) != OpKind.Register || instruction.GetOpKind(1) != OpKind.Memory ||
			!instruction.Op0Register.IsGPR() || GprOffset(instruction.Op0Register) < 0 ||
			instruction.Op0Register is Register.AH or Register.BH or Register.CH or Register.DH)
		{
			return NoteFaultRead(FaultReadOutcome.Form);
		}

		if (instruction.SegmentPrefix is Register.FS or Register.GS || instruction.MemorySegment is Register.FS or Register.GS)
		{
			return NoteFaultRead(FaultReadOutcome.Segment);
		}

		var size = instruction.MemorySize.GetSize();
		if (size is not (1 or 2 or 4 or 8))
		{
			return NoteFaultRead(FaultReadOutcome.Form);
		}

		var context = (nint)contextRecord;
		if (!instruction.TryGetVirtualAddress(1, 0, out var effective, (Register register, int _, int _, out ulong value) =>
			{
				value = 0;
				if (register is Register.ES or Register.CS or Register.SS or Register.DS)
				{
					return true;
				}

				var offset = GprOffset(register);
				if (offset < 0)
				{
					return false;
				}

				var full = ReadCtxU64((void*)context, offset);
				value = register.GetSize() switch
				{
					8 => full,
					4 => full & 0xFFFF_FFFFUL,
					2 => full & 0xFFFFUL,
					_ => full & 0xFFUL,
				};
				return true;
			}) || effective != faultAddress)
		{
			return NoteFaultRead(FaultReadOutcome.Address);
		}

		if ((faultAddress & 0xFFFUL) + (ulong)size > 0x1000UL)
		{
			return NoteFaultRead(FaultReadOutcome.CrossPage);
		}

		Span<byte> bytes = stackalloc byte[8];
		var asked = bytes[..size];
		// =2 watches the bytes from before the emulated read until the ordinary path's result is read.
		var watch = FaultReadEmulateMode == 2 ? GuestGpuMemoryHook.BeginWriteWatch(faultAddress, (ulong)size) : -1;
		if (!GuestGpuMemoryHook.TryReadWithoutDownload(faultAddress, asked))
		{
			if (watch >= 0) _ = GuestGpuMemoryHook.EndWriteWatch(watch);
			t_faultReadAt = 0;
			return NoteFaultRead(FaultReadOutcome.Store);
		}

		if (FaultReadEmulateMode == 2)
		{
			verify = new FaultReadVerify(true, FaultReadValue(asked), size, watch);
			return false;
		}

		var raw = FaultReadValue(asked);
		var outcome = FaultReadOutcome.Emulated;
		if (FaultReadParkTicks > 0 && t_faultReadAt != 0 && t_faultReadAddress == faultAddress && t_faultReadSize == size &&
			t_faultReadValue == raw && started - t_faultReadAt <= FaultReadRepeatTicks)
		{
			if (!GuestGpuMemoryHook.TryReadWithoutDownloadParked(faultAddress, asked, started + FaultReadParkTicks))
			{
				t_faultReadAt = 0;
				return NoteFaultRead(FaultReadOutcome.Store);
			}

			raw = FaultReadValue(asked);
			outcome = FaultReadOutcome.Parked;
		}

		t_faultReadAddress = faultAddress;
		t_faultReadSize = size;
		t_faultReadValue = raw;
		if (instruction.Mnemonic is Mnemonic.Movsx or Mnemonic.Movsxd)
		{
			raw = size switch
			{
				1 => (ulong)(long)(sbyte)raw,
				2 => (ulong)(long)(short)raw,
				4 => (ulong)(long)(int)raw,
				_ => raw,
			};
		}

		var destination = instruction.Op0Register;
		var destinationOffset = GprOffset(destination);
		var current = ReadCtxU64(contextRecord, destinationOffset);
		var result = destination.GetSize() switch
		{
			8 => raw,
			4 => raw & 0xFFFF_FFFFUL,
			2 => (current & ~0xFFFFUL) | (raw & 0xFFFFUL),
			_ => (current & ~0xFFUL) | (raw & 0xFFUL),
		};
		WriteCtxU64(contextRecord, destinationOffset, result);
		WriteCtxU64(contextRecord, CtxRip, instruction.NextIP);
		var finished = Stopwatch.GetTimestamp();
		t_faultReadAt = finished;
		lock (_faultReadMsGate)
		{
			_faultReadMs.Add((finished - started) * 1000.0 / Stopwatch.Frequency);
		}

		NoteFaultRead(outcome);
		return true;
	}

	// =2: after the ordinary path, the bytes the load now reads against the emulated ones. The result is read before
	// the watch ends, so every write between the emulated read and it is counted.
	private unsafe static void VerifyFaultingRead(ulong faultAddress, FaultReadVerify verify, bool resolved)
	{
		var actual = new byte[verify.Size];
		var readable = resolved && TryReadHostBytes(faultAddress, actual);
		var hits = GuestGpuMemoryHook.EndWriteWatch(verify.Watch);
		if (!readable)
		{
			NoteFaultRead(FaultReadOutcome.Pending);
			return;
		}

		var value = FaultReadValue(actual);
		if (value == verify.Value)
		{
			NoteFaultRead(FaultReadOutcome.Verified);
		}
		else if (hits > 0)
		{
			NoteFaultRead(FaultReadOutcome.Changed);
		}
		else if (hits < 0)
		{
			NoteFaultRead(FaultReadOutcome.Unwatched);
		}
		else
		{
			if (Interlocked.Increment(ref _faultReadMismatchLogs) <= 20)
				Console.Error.WriteLine($"[FAULT_READ_EMU] mismatch address=0x{faultAddress:X} size={verify.Size} emulated=0x{verify.Value:X} actual=0x{value:X}");
			NoteFaultRead(FaultReadOutcome.Mismatch);
		}
	}

	private static bool NoteFaultRead(FaultReadOutcome outcome)
	{
		Interlocked.Increment(ref _faultReadOutcomes[(int)outcome]);
		var now = Stopwatch.GetTimestamp();
		var due = Volatile.Read(ref _faultReadReportAt);
		if (due == 0)
		{
			Interlocked.CompareExchange(ref _faultReadReportAt, now + Stopwatch.Frequency * 5, 0);
			return false;
		}

		if (now >= due && Interlocked.CompareExchange(ref _faultReadReportAt, now + Stopwatch.Frequency * 5, due) == due)
		{
			var seconds = (now - due) / (double)Stopwatch.Frequency + 5.0;
			var text = new System.Text.StringBuilder("[FAULT_READ_EMU] mode=").Append(FaultReadEmulateMode).Append(" window_s=").Append(seconds.ToString("F1"));
			for (var index = 0; index < (int)FaultReadOutcome.Count; index++)
			{
				text.Append(' ').Append(((FaultReadOutcome)index).ToString().ToLowerInvariant()).Append("_per_s=")
					.Append((Interlocked.Exchange(ref _faultReadOutcomes[index], 0) / seconds).ToString("F0"));
			}

			lock (_faultReadMsGate)
			{
				if (_faultReadMs.Count != 0)
				{
					_faultReadMs.Sort();
					text.Append(System.FormattableString.Invariant(
						$" read_ms_mean/p50/p90={_faultReadMs.Average():F3}/{_faultReadMs[_faultReadMs.Count / 2]:F3}/{_faultReadMs[(int)(_faultReadMs.Count * 0.9)]:F3} read_ms_per_s={_faultReadMs.Sum() / seconds:F0}"));
					_faultReadMs.Clear();
				}
			}

			Console.Error.WriteLine(text.ToString());
		}

		return false;
	}
}
