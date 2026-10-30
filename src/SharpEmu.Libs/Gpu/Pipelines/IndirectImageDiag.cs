// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// [local] SHARPEMU_INDIRECT_IMAGE_DIAG=<hash>: for that program, the indirect image tables' candidate classes. A failed
// materialization walks again (uncached, rate-limited) with a capture and prints every candidate that differs from the
// exemplar with its decoded T#; a success prints the class histogram now and then. Off: one static read per prepare.
internal static class IndirectImageDiag
{
    internal static readonly ulong Hash = ParseHash(Environment.GetEnvironmentVariable("SHARPEMU_INDIRECT_IMAGE_DIAG"));
    private static long _failures, _successes;
    private static string? _lastSignature;
    private static int _signatureChanges;
    private static readonly long Started = Environment.TickCount64;

    private static ulong ParseHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
        return ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out var hash) ? hash : 0;
    }

    private static double Uptime => (Environment.TickCount64 - Started) / 1000.0;

    internal static bool ShouldCapture()
    {
        var count = Interlocked.Increment(ref _failures);
        return count <= 4 || count % 2048 == 0;
    }

    internal static void NoteSuccess(ResourceSpecialization specialization, int baseImageCount)
    {
        var count = Interlocked.Increment(ref _successes);
        var signature = string.Join(",", specialization.Images.Where(image => image.IndirectRoot != DescriptorConstants.NoIndex)
            .Select(Class).Distinct().OrderBy(text => text, StringComparer.Ordinal));
        var changed = signature != _lastSignature && _signatureChanges < 64;
        if (changed)
        {
            _lastSignature = signature;
            _signatureChanges++;
        }
        else if (count > 2 && count % 2048 != 0) return;
        var builder = new StringBuilder();
        builder.Append(FormattableString.Invariant($"[GPU][INDIRECT_DIAG] ok{(changed ? "*" : "")} t={Uptime:F1}s n={count} images={specialization.Images.Count} base={baseImageCount}"));
        foreach (var group in specialization.Images.Where(image => image.IndirectRoot != DescriptorConstants.NoIndex)
                     .GroupBy(image => image.IndirectRoot))
        {
            builder.Append($" root{group.Key}:");
            foreach (var cls in group.GroupBy(Class).OrderByDescending(cls => cls.Count()))
                builder.Append($" [{cls.Key}]x{cls.Count()}");
        }

        Console.Error.WriteLine(builder.ToString());
    }

    private static string Class(ImageSpecialization image) =>
        $"{image.NumericClass}/{image.Dimension}{(image.Cube ? "/cube" : "")}/mips{image.MipCount}/conv{image.ConversionFormat:x}/sw{image.ShaderSwizzle:x}";

    internal static void Write(IndirectImageFailure failure)
    {
        var images = failure.ImageSpecializations;
        var root = failure.RootResource;
        var exemplar = images[(int)failure.ExemplarResource];
        var builder = new StringBuilder();
        builder.Append(FormattableString.Invariant(
            $"[GPU][INDIRECT_DIAG] fail t={Uptime:F1}s n={Interlocked.Read(ref _failures)} pc=0x{failure.InstructionAddress:x} root={root} exemplar={failure.ExemplarResource} first={failure.IncompatibleResource} keys={failure.Keys.Length} descriptors={failure.TableDescriptors.Length} selector={failure.SelectorDiagnostic?.SelectionMode}"));
        var members = Enumerable.Range(0, images.Length).Where(index => images[index].IndirectRoot == root).ToArray();
        builder.Append($" members={members.Length} classes:");
        foreach (var cls in members.GroupBy(index => Class(images[index])).OrderByDescending(cls => cls.Count()))
            builder.Append($" [{cls.Key}]x{cls.Count()}");
        Console.Error.WriteLine(builder.ToString());
        Console.Error.WriteLine($"[GPU][INDIRECT_DIAG]   exemplar {Describe(failure, (int)failure.ExemplarResource)}");
        var printed = 0;
        foreach (var index in members)
        {
            var image = images[index];
            if (Class(image) == Class(exemplar) || IsNull(failure.ImageDescriptors[index])) continue;
            if (printed++ >= 12) break;
            Console.Error.WriteLine($"[GPU][INDIRECT_DIAG]   differs {Describe(failure, index)}");
        }
    }

    private static bool IsNull(uint[] words) => words.All(word => word == 0);

    private static string Describe(IndirectImageFailure failure, int index)
    {
        var words = failure.ImageDescriptors[index];
        var image = failure.ImageSpecializations[index];
        var table = Array.FindIndex(failure.TableDescriptors, candidate => candidate.AsSpan().SequenceEqual(words));
        var keys = new List<uint>();
        for (var key = 0; key < failure.Keys.Length && table >= 0; key++)
            if (failure.CandidateIndices[key] == table) keys.Add(failure.Keys[key]);
        var text = $"image={index} [{Class(image)}] table={table} keys={string.Join(",", keys.Take(6).Select(key => $"0x{key:x}"))}{(keys.Count > 6 ? $"..({keys.Count})" : "")} words={string.Join(",", words.Select(word => $"{word:x8}"))}";
        if (words.Length >= 4 && Agc.AgcExports.TryDecodeTextureDescriptor(words, out var descriptor))
            text += $" addr=0x{descriptor.Address:x} {descriptor.Width}x{descriptor.Height}x{descriptor.Depth} fmt={descriptor.Format} num={descriptor.NumberType} type={descriptor.Type} mips={descriptor.BaseLevel}..{descriptor.LastLevel} tile={descriptor.TileMode}";
        return text;
    }
}
