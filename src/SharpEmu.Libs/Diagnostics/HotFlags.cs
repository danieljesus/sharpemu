// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Diagnostics;

// [local] SHARPEMU_HOTFLAGS_FILE=<path>: a running emulator takes new values of a few SHARPEMU_* switches from that file
// (lines NAME=value, with or without the SHARPEMU_ prefix, # comments), so an A/B alternates in one scene without a
// restart. The render thread looks at the file at most every 250 ms after a present and re-reads it only when its
// write time or size changed; each applied change prints [HOTFLAG] NAME=old->new frame=<presented frames>. Unset, the
// file is never looked at. Each switch keeps its startup value from its own variable; the switches that cache state
// under their mode drop it on the thread that owns it (a generation the owner compares). docs/hotflags.md.
internal static class HotFlags
{
    internal static readonly string? FilePath =
        Environment.GetEnvironmentVariable("SHARPEMU_HOTFLAGS_FILE") is { Length: > 0 } path ? path : null;

    internal static readonly bool Enabled = FilePath is not null;

    // Presents since start, counted with or without the file.
    internal static long PresentedFrames;

    private static long _nextPoll;
    private static HotFlagSet? _set;

    public static void FramePresented()
    {
        PresentedFrames++;
        if (!Enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now < _nextPoll)
        {
            return;
        }

        _nextPoll = now + Stopwatch.Frequency / 4;
        if (_set is null)
        {
            _set = new HotFlagSet(Registry());
            Console.Error.WriteLine($"[HOTFLAG] watching {FilePath} frame={PresentedFrames} {_set.Describe()}");
        }

        _set.PollFile(FilePath!, PresentedFrames, Console.Error.WriteLine);
    }

    // The switches a running emulator may change. Min..max is the accepted range; a clamped one takes any integer.
    internal static HotFlag[] Registry() =>
    [
        new("STAMP_ON_CHANGE", 0, 1, false, () => RegisterBanks.StampOnChange ? 1 : 0, value => RegisterBanks.SetStampOnChange(value != 0), resets: true),
        new("BUFFER_SOURCE_MEMO", 0, 2, false, () => VulkanVideoPresenter.BufferSourceMemoMode, value => VulkanVideoPresenter.BufferSourceMemoMode = value),
        new("NATIVE_STATE_REUSE", 0, 2, false, () => VulkanVideoPresenter.NativeStateReuseMode, VulkanVideoPresenter.SetNativeStateReuse, resets: true),
        new("FRONT_SPECULATE", 0, 2, false, () => FrontSpeculation.DefaultMode, value => FrontSpeculation.DefaultMode = value),
        new("SUBMIT_BATCH", 0, 32, true, () => SubmissionScheduler.SubmitBatchDefault, value => SubmissionScheduler.SubmitBatchDefault = value),
        new("TEXEL_CLEAN_SKIP", 0, 2, false, () => GuestBufferCache.TexelCleanSkipMode, value => GuestBufferCache.TexelCleanSkipMode = value),
        new("IDLE_FLUSH", 0, int.MaxValue, true, () => VulkanVideoPresenter.IdleFlushDraws, value => VulkanVideoPresenter.IdleFlushDraws = value),
        new("STAGE_MEMO", 0, 1, false, () => VulkanVideoPresenter.StageMemoEnabled ? 1 : 0, value => VulkanVideoPresenter.SetStageMemo(value != 0), resets: true),
        new("TARGET_MEMO_DCC", 0, 1, false, () => RenderExecutor.TargetMemoDcc ? 1 : 0, value => RenderExecutor.TargetMemoDcc = value != 0),
        new("ASYNC_GRAPHICS_PIPELINES", 0, 2, false, () => RenderRecorder.AsyncGraphicsPipelinesMode, value => RenderRecorder.AsyncGraphicsPipelinesMode = value),
    ];
}

internal sealed class HotFlag(string name, int min, int max, bool clamp, Func<int> get, Action<int> set, bool resets = false)
{
    public string Name { get; } = name;
    public bool Resets { get; } = resets;
    public int Get() => get();
    public void Set(int value) => set(value);

    public bool TryParse(string text, out int value)
    {
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (clamp)
        {
            value = Math.Clamp(value, min, max);
            return true;
        }

        return value >= min && value <= max;
    }

    public string Range => FormattableString.Invariant($"{min}..{max}");
}

// The file's state and the switches it may change; HotFlags keeps one, tests make their own.
internal sealed class HotFlagSet
{
    private const string Prefix = "SHARPEMU_";
    private readonly Dictionary<string, HotFlag> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private FileInfo? _file;
    private DateTime _lastWrite;
    private long _lastLength = -1;
    private bool _reportedFailure;

    public HotFlagSet(IEnumerable<HotFlag> flags)
    {
        foreach (var flag in flags)
        {
            _flags[flag.Name] = flag;
        }
    }

    public string Describe() => string.Join(' ', _flags.Values.Select(flag => FormattableString.Invariant($"{flag.Name}={flag.Get()}")));

    // NAME=value lines in order, the last of a repeated name kept; names lose the SHARPEMU_ prefix.
    public static List<KeyValuePair<string, string>> Parse(string text)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment];
            }

            line = line.Trim();
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var name = line[..equals].Trim();
            if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[Prefix.Length..];
            }

            if (name.Length == 0)
            {
                continue;
            }

            name = name.ToUpperInvariant();
            var value = line[(equals + 1)..].Trim();
            var existing = entries.FindIndex(entry => entry.Key == name);
            if (existing >= 0)
            {
                entries.RemoveAt(existing);
            }

            entries.Add(new(name, value));
        }

        return entries;
    }

    // Applies the lines whose value differs from the one this set last read for that name; returns the switches changed.
    // A name the file no longer lists keeps its value.
    public int Apply(string text, long frame, Action<string> log)
    {
        var changed = 0;
        foreach (var (name, value) in Parse(text))
        {
            if (_seen.TryGetValue(name, out var last) && last == value)
            {
                continue;
            }

            _seen[name] = value;
            if (!_flags.TryGetValue(name, out var flag))
            {
                log($"[HOTFLAG] rejected {name} (not hot-safe)");
                continue;
            }

            if (!flag.TryParse(value, out var parsed))
            {
                log($"[HOTFLAG] rejected {name}={value} (invalid, expected {flag.Range})");
                continue;
            }

            var old = flag.Get();
            if (old == parsed)
            {
                continue;
            }

            flag.Set(parsed);
            changed++;
            log(FormattableString.Invariant($"[HOTFLAG] {name}={old}->{parsed} frame={frame}"));
        }

        return changed;
    }

    // Re-reads the file when its write time or size changed; false when it did not (or could not be read).
    public bool PollFile(string path, long frame, Action<string> log)
    {
        try
        {
            var file = _file ??= new FileInfo(path);
            file.Refresh();
            if (!file.Exists)
            {
                return false;
            }

            var write = file.LastWriteTimeUtc;
            var length = file.Length;
            if (write == _lastWrite && length == _lastLength)
            {
                return false;
            }

            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
            }

            _lastWrite = write;
            _lastLength = length;
            Apply(text, frame, log);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!_reportedFailure)
            {
                _reportedFailure = true;
                log($"[HOTFLAG] cannot read {path}: {exception.Message}");
            }

            return false;
        }
    }
}
