// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// [local] Read-ahead windows whose eager downloads keep going stale: after StrikeLimit wasted downloads in a row a
// window stops being read ahead for MuteBatches batch submissions. A download that is used clears the strikes.
internal sealed class EagerWindowMute(int strikeLimit, long muteBatches)
{
    private readonly Dictionary<ulong, (int Strikes, long MutedUntil)> _windows = new();

    public int StrikeLimit { get; } = strikeLimit;

    public long MuteBatches { get; } = muteBatches;

    public int Count => _windows.Count;

    public bool IsMuted(ulong key, long batch) => _windows.TryGetValue(key, out var window) && batch < window.MutedUntil;

    // True when this outcome mutes the window.
    public bool Note(ulong key, bool wasted, long batch)
    {
        if (!wasted)
        {
            _windows.Remove(key);
            return false;
        }

        _windows.TryGetValue(key, out var window);
        var strikes = window.Strikes + 1;
        if (strikes < StrikeLimit)
        {
            _windows[key] = (strikes, window.MutedUntil);
            return false;
        }

        _windows[key] = (0, batch + MuteBatches);
        return true;
    }

    // Forgets windows without strikes whose mute has run out.
    public void Prune(long batch)
    {
        List<ulong>? expired = null;
        foreach (var (key, window) in _windows)
        {
            if (window.Strikes == 0 && batch >= window.MutedUntil)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _windows.Remove(key);
        }
    }
}
