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
    }
}
