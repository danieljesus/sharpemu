// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// [local] Upstream's cache tests count hits, misses and refreshes of upstream's cache alone. Two of our default-on
// mechanisms legitimately turn some of those misses or refreshes into hits with the same snapshot: the content key
// (SHARPEMU_MATERIALIZATION_CONTENT_KEY: pointer and buffer-dword registers leave the key, a hit moves the ranges and
// evaluates the dwords again) and word patches (a recorded word copied verbatim into table slots or buffer base dwords
// is taken by a hit). These scopes set the upstream-equivalent value for the current async flow only (AsyncLocal), so
// tests running in parallel keep the defaults; the previous value comes back on Dispose.
internal static class LocalCacheSwitches
{
    // SHARPEMU_MATERIALIZATION_CONTENT_KEY=0: upstream's learned key and match alone.
    public static IDisposable WithoutContentKey() => new Scope<bool?>(ResourceMaterializationCache.ContentKeyedOverride, false);

    // No word patches: a changed word is a miss or a table refresh, as upstream has it.
    public static IDisposable WithoutWordPatches() => new Scope<bool?>(ResourceMaterializationCache.WordPatchesOverride, false);

    private sealed class Scope<T> : IDisposable
    {
        private readonly AsyncLocal<T> _local;
        private readonly T _previous;

        public Scope(AsyncLocal<T> local, T value)
        {
            _local = local;
            _previous = local.Value;
            local.Value = value;
        }

        public void Dispose() => _local.Value = _previous;
    }
}
