// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Tests;

// [local] SHARPEMU_DEVICE_DATA_READS is =auto by default; these tests were written for flattened scalar data loads,
// so they run with it off unless the environment sets it. The auto rule has its own tests, which set it per test.
internal static class DeviceDataReadsDefault
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_DATA_READS") is null)
        {
            Environment.SetEnvironmentVariable("SHARPEMU_DEVICE_DATA_READS", "0");
        }

        // [local] The pipeline cache tests read the files a checkpoint writes as soon as it returns.
        if (Environment.GetEnvironmentVariable("SHARPEMU_PIPELINE_CACHE_WRITE_ASYNC") is null)
        {
            Environment.SetEnvironmentVariable("SHARPEMU_PIPELINE_CACHE_WRITE_ASYNC", "0");
        }

        // [local] SHARPEMU_DRAW_PLAN: the context group hashes follow every register write the tests make, so the plan
        // tests can turn the plan on per recorder (DrawPlan.ModeOverride). The plan itself stays off.
        if (Environment.GetEnvironmentVariable("SHARPEMU_DRAW_PLAN") is null)
        {
            Environment.SetEnvironmentVariable("SHARPEMU_DRAW_PLAN", "0");
        }

        // [local] SHARPEMU_BDA_WRITE_MEMO: on by default in the emulator; the tests that use it set the mode per cache.
        if (Environment.GetEnvironmentVariable("SHARPEMU_BDA_WRITE_MEMO") is null)
        {
            Environment.SetEnvironmentVariable("SHARPEMU_BDA_WRITE_MEMO", "0");
        }

        // [local] SHARPEMU_BDA_DIRTY_LOG: on by default in the emulator; the tests that use it set the mode per tracker.
        if (Environment.GetEnvironmentVariable("SHARPEMU_BDA_DIRTY_LOG") is null)
        {
            Environment.SetEnvironmentVariable("SHARPEMU_BDA_DIRTY_LOG", "0");
        }

        // [local] SHARPEMU_TABLE_WORD_MEMO_WRITERS: the emulator also trusts 0x5F74; the memo tests use it as an untrusted writer.
        if (Environment.GetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_WRITERS") is null)
        {
            Environment.SetEnvironmentVariable("SHARPEMU_TABLE_WORD_MEMO_WRITERS", "5981037B07E391D5");
        }

        if (Environment.GetEnvironmentVariable("SHARPEMU_PLAN_SHADOW") is null)
        {
            Environment.SetEnvironmentVariable("SHARPEMU_PLAN_SHADOW", "1");
        }
    }
}
