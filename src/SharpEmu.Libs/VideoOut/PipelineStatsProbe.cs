// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SharpEmu.Libs.VideoOut;

// [local] SHARPEMU_PIPELINE_STATS=1: prints the driver's statistics of each compute pipeline
// (on NVIDIA: registers, local and shared memory), which decide how many warps fit on an SM.
internal static unsafe class PipelineStatsProbe
{
    private const PipelineCreateFlags CaptureStatistics = (PipelineCreateFlags)0x40;

    public static bool Enabled { get; set; }

    public static PipelineCreateFlags CreateFlags => Enabled ? CaptureStatistics : 0;

    private static KhrPipelineExecutableProperties? _extension;

    public static void Initialize(Vk vk, Instance instance, Device device)
    {
        Enabled = Enabled && vk.TryGetDeviceExtension(instance, device, out _extension);
    }

    public static void Report(Vk vk, Device device, Pipeline pipeline, string label)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (_extension is null)
            {
                return;
            }

            var info = new PipelineExecutableInfoKHR { SType = StructureType.PipelineExecutableInfoKhr, Pipeline = pipeline, ExecutableIndex = 0 };
            uint count = 0;
            _extension!.GetPipelineExecutableStatistic(device, &info, &count, null);
            var statistics = stackalloc PipelineExecutableStatisticKHR[(int)Math.Min(count, 16u)];
            count = Math.Min(count, 16u);
            for (var index = 0; index < count; index++)
            {
                statistics[index] = new PipelineExecutableStatisticKHR { SType = StructureType.PipelineExecutableStatisticKhr };
            }

            _extension.GetPipelineExecutableStatistic(device, &info, &count, statistics);
            var parts = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var statistic = statistics[index];
                var value = statistic.Format switch
                {
                    PipelineExecutableStatisticFormatKHR.PipelineExecutableStatisticFormatBool32Khr => statistic.Value.B32.ToString(),
                    PipelineExecutableStatisticFormatKHR.PipelineExecutableStatisticFormatInt64Khr => statistic.Value.I64.ToString(),
                    // NVIDIA fills only the low 32 bits of some values.
                    PipelineExecutableStatisticFormatKHR.PipelineExecutableStatisticFormatUint64Khr => ((uint)statistic.Value.U64).ToString(),
                    _ => statistic.Value.F64.ToString("F1"),
                };
                parts.Add($"{SilkMarshal.PtrToString((nint)statistic.Name)?.Replace(' ', '_')}={value}");
            }

            Console.Error.WriteLine($"[PIPELINE_STATS] {label} {string.Join(' ', parts)}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PIPELINE_STATS] {label} failed: {exception.Message}");
        }
    }
}
