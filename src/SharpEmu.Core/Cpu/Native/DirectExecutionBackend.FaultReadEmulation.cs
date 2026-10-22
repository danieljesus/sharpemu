// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Diagnostics;
using System.Threading;
using Iced.Intel;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.Core.Cpu.Native;

// [local] SHARPEMU_FAULT_READ_EMULATE: a guest load that faults on a watched page whose asked bytes are not
// GPU-modified is emulated from the backing instead of downloading the page's GPU-written ranges and unprotecting it:
// the download would leave those bytes as the backing holds them, so the load sees the same value without waiting for
// the GPU. Only plain MOV / MOVZX / MOVSX / MOVSXD into a general register (not AH-DH) from a DS/SS operand of at most
// 8 bytes whose effective address is the fault address and stays in one page; anything else takes the usual path.
// 0 (default) off; 1 emulates; 2 decodes and asks the store but still downloads, then counts loads whose emulated
// bytes differ from the downloaded ones (a CP write to the backing during the wait also counts). [FAULT_READ_EMU] every
// 5 s, per second.
public sealed partial class DirectExecutionBackend
{
	private static readonly int FaultReadEmulateMode =
		int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FAULT_READ_EMULATE"), out var faultReadMode) ? faultReadMode : 0;

	private enum FaultReadOutcome { Emulated, Verified, Mismatch, NoCode, Form, Segment, Address, CrossPage, Store, Count }

	private static readonly long[] _faultReadOutcomes = new long[(int)FaultReadOutcome.Count];
	private static long _faultReadReportAt;

	private const int CtxRax = 120;
	private const int CtxRip = 248;

	private static int GprOffset(Register register)
	{
		var full = register.GetFullRegister();
		return full >= Register.RAX && full <= Register.R15 ? CtxRax + 8 * (full - Register.RAX) : -1;
	}

	private unsafe static bool TryEmulateFaultingRead(void* contextRecord, ulong rip, ulong faultAddress, out bool verifyPending,
		out byte[] emulated, out int emulatedSize)
	{
		verifyPending = false;
		emulated = Array.Empty<byte>();
		emulatedSize = 0;
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
		if (!GuestGpuMemoryHook.TryReadWithoutDownload(faultAddress, bytes[..size]))
		{
			return NoteFaultRead(FaultReadOutcome.Store);
		}

		if (FaultReadEmulateMode == 2)
		{
			verifyPending = true;
			emulated = bytes[..size].ToArray();
			emulatedSize = size;
			return false;
		}

		var raw = size switch
		{
			1 => bytes[0],
			2 => BitConverter.ToUInt16(bytes),
			4 => BitConverter.ToUInt32(bytes),
			_ => BitConverter.ToUInt64(bytes),
		};
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
		NoteFaultRead(FaultReadOutcome.Emulated);
		return true;
	}

	// =2: after the usual download, the bytes the load now reads against the emulated ones.
	private unsafe static void VerifyFaultingRead(ulong faultAddress, byte[] emulated, int size)
	{
		var actual = new byte[size];
		var same = TryReadHostBytes(faultAddress, actual) && actual.AsSpan().SequenceEqual(emulated.AsSpan(0, size));
		NoteFaultRead(same ? FaultReadOutcome.Verified : FaultReadOutcome.Mismatch);
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

			Console.Error.WriteLine(text.ToString());
		}

		return false;
	}
}
