// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// [local] SHARPEMU_RECORD_RING: a vkCmd* that does not go through VkRecord would reach the driver out of order (and
// from a second thread) when its command buffer is the one the ring is recording. The runtime check cannot see such
// a call, so the source tree is scanned for one.
public sealed class CommandRingSourceTests
{
    // Files that may call the driver's vkCmd* entry points directly, and why.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VkRecord.cs"] = "the wrappers themselves",
        ["CommandRing.Replay.cs"] = "the replay thread issues the recorded commands",
        ["VulkanAsyncReadback.cs"] = "records its own command buffers from its own pool, never the ring's",
    };

    // Files that may load or declare command function pointers (not call them).
    private static readonly Dictionary<string, string> AllowedPointers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VulkanVideoPresenter.Device.Setup.cs"] = "loads the debug-utils label commands and hands them to VkRecord",
    };

    private static readonly Regex DirectCall = new(@"(?<!VkRecord)\.Cmd[A-Z]\w*\(", RegexOptions.Compiled);
    private static readonly Regex PointerCall = new(@"\b_cmd[A-Z]\w*\(", RegexOptions.Compiled);
    private static readonly Regex PointerSource = new(@"""vkCmd|delegate\*\s*unmanaged(\[\w+\])?<CommandBuffer", RegexOptions.Compiled);

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpEmu.slnx")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return Path.Combine(directory.FullName, "src");
            }
        }

        throw new DirectoryNotFoundException("The repository root (SharpEmu.slnx) was not found above the test binaries.");
    }

    private static IEnumerable<(string File, int Line, string Text)> SourceLines()
    {
        foreach (var path in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var separator = Path.DirectorySeparatorChar;
            if (path.Contains($"{separator}bin{separator}") || path.Contains($"{separator}obj{separator}"))
            {
                continue;
            }

            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var text = lines[index].TrimStart();
                if (!text.StartsWith("//", StringComparison.Ordinal))
                {
                    yield return (path, index + 1, text);
                }
            }
        }
    }

    [Fact]
    public void NoCommandIsRecordedOutsideVkRecord()
    {
        var offenders = SourceLines()
            .Where(line => !Allowed.ContainsKey(Path.GetFileName(line.File)) &&
                (DirectCall.IsMatch(line.Text) || PointerCall.IsMatch(line.Text) ||
                    PointerSource.IsMatch(line.Text) && !AllowedPointers.ContainsKey(Path.GetFileName(line.File))))
            .Select(line => $"{Path.GetFileName(line.File)}:{line.Line}: {line.Text}")
            .ToList();
        Assert.True(offenders.Count == 0, "vkCmd* outside VkRecord:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheScanFindsTheWrappers()
    {
        Assert.Contains(SourceLines(), line => Path.GetFileName(line.File) == "VkRecord.cs" && DirectCall.IsMatch(line.Text));
    }
}
