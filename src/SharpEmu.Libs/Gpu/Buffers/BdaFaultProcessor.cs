// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// Turns the GPU fault bitmap into buffers: a compute pass lists faulted pages per area.
public sealed unsafe class BdaFaultProcessor : IDisposable
{
    private const int MaxPendingFaults = 8;
    private const uint MaxPageFaults = 1024;
    private const ulong PageFaultAreaSize = MaxPageFaults * sizeof(ulong);

    private static long _reportedFaults;
    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly GuestBufferCache _cache;
    private int _inst0Dumps;
    private readonly HashSet<ulong> _hdrSnap1 = new(); private readonly HashSet<ulong> _hdrSnap2 = new();
    private ulong _traceArmed;
    private int _armedDumps;
    private int _repairs;
    private static readonly bool RepairHeaders = Environment.GetEnvironmentVariable("SHARPEMU_REPAIR_BVH_HEADERS") == "1";
    private readonly HashSet<uint> _tlasDumped = new();
    private readonly SpanSet _faultRanges = new();
    private readonly ulong _pageSize;
    private readonly ulong _pageCount;
    private readonly ulong _faultBufferSize;
    private readonly GpuBuffer _faultBuffer;
    private readonly GpuBuffer _downloadBuffer;
    private readonly GpuBuffer? _traceDownloadBuffer;
    private bool _traceInitialized;
    private readonly ulong[] _faultAreas = new ulong[MaxPendingFaults];
    private readonly DescriptorSet[] _sets = new DescriptorSet[MaxPendingFaults];
    private readonly DescriptorSetLayout _layout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;
    private readonly DescriptorPool _pool;
    private uint _currentArea;

    public BdaFaultProcessor(GpuDeviceInfo device, SubmissionScheduler scheduler, GuestBufferCache cache, int pageBits, ulong pageCount)
    {
        _device = device;
        _scheduler = scheduler;
        _cache = cache;
        _pageSize = 1UL << pageBits;
        _pageCount = pageCount;
        _faultBufferSize = pageCount / 8;
        _faultBuffer = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags,
            _faultBufferSize + (GuestGpuMemoryHook.TraceEnabled ? 256UL : 0UL));
        if (GuestGpuMemoryHook.TraceEnabled)
            _traceDownloadBuffer = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, MaxPendingFaults * 256);
        _downloadBuffer = new GpuBuffer(device, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, MaxPendingFaults * PageFaultAreaSize);

        var vk = device.Vk;
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        for (uint index = 0; index < 2; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = index,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        RequireSuccess(vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out _layout), "Fault-buffer descriptor layout creation");

        var spirv = SpirvFixedShaders.CreateFaultBufferProcess();
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            RequireSuccess(vk.CreateShaderModule(device.Device, &moduleInfo, null, out module), "Fault-buffer shader module creation");
        }

        var setLayout = _layout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
        };
        RequireSuccess(vk.CreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out _pipelineLayout), "Fault-buffer pipeline layout creation");

        ReadOnlySpan<byte> entryPoint = "main\0"u8;
        Result created;
        fixed (byte* entry = entryPoint)
        {
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Layout = _pipelineLayout,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entry,
                },
            };
            created = vk.CreateComputePipelines(device.Device, default, 1, &pipelineInfo, null, out _pipeline);
        }

        vk.DestroyShaderModule(device.Device, module, null);
        RequireSuccess(created, "Fault-buffer pipeline creation");

        // One pre-written set per area replaces push descriptors; the bindings never change.
        var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 2 * MaxPendingFaults };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = MaxPendingFaults,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        RequireSuccess(vk.CreateDescriptorPool(device.Device, &poolInfo, null, out _pool), "Fault-buffer descriptor pool creation");
        var layouts = stackalloc DescriptorSetLayout[MaxPendingFaults];
        for (var index = 0; index < MaxPendingFaults; index++)
        {
            layouts[index] = _layout;
        }

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = MaxPendingFaults,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* sets = _sets)
        {
            RequireSuccess(vk.AllocateDescriptorSets(device.Device, &allocateInfo, sets), "Fault-buffer descriptor set allocation");
        }

        var infos = stackalloc DescriptorBufferInfo[2 * MaxPendingFaults];
        var writes = stackalloc WriteDescriptorSet[2 * MaxPendingFaults];
        for (var area = 0; area < MaxPendingFaults; area++)
        {
            infos[2 * area] = new DescriptorBufferInfo(_faultBuffer.Handle, 0, _faultBufferSize);
            infos[2 * area + 1] = new DescriptorBufferInfo(_downloadBuffer.Handle, (ulong)area * PageFaultAreaSize, PageFaultAreaSize);
            for (uint binding = 0; binding < 2; binding++)
            {
                writes[2 * area + binding] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[area],
                    DstBinding = binding,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = infos + 2 * area + binding,
                };
            }
        }

        vk.UpdateDescriptorSets(device.Device, 2 * MaxPendingFaults, writes, 0, null);
    }

    public GpuBuffer FaultBuffer
    {
        get
        {
            if (_traceDownloadBuffer is not null && !_traceInitialized)
            {
                _faultBuffer.Fill(_faultBufferSize, 256, 0);
                _traceInitialized = true;
            }
            return _faultBuffer;
        }
    }

    public void ProcessFaultBuffer()
    {
        var waitTick = _faultAreas[_currentArea];
        if (waitTick != 0)
        {
            _scheduler.Wait(waitTick);
            _scheduler.RunCompletedOperations();
        }

        var offset = _currentArea * PageFaultAreaSize;
        _downloadBuffer.Mapped.Slice((int)offset, (int)PageFaultAreaSize).Clear();
        _downloadBuffer.Flush(offset, PageFaultAreaSize);

        var preBarrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcAccessMask = AccessFlags2.ShaderWriteBit,
            DstAccessMask = AccessFlags2.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _faultBuffer.Handle,
            Offset = 0,
            Size = _faultBufferSize,
        };
        var postBarrier = preBarrier;
        postBarrier.DstAccessMask = AccessFlags2.ShaderWriteBit;

        _scheduler.EndRendering();
        var vk = _device.Vk;
        var command = new CommandBuffer(_scheduler.Current.Handle);
        VulkanSynchronization.PipelineBarrier(vk,
            command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.ComputeShaderBit, DependencyFlags.ByRegionBit,
            0, null, 1, &preBarrier, 0, null);
        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
        var set = _sets[_currentArea];
        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        var threads = _pageCount / 32;
        vk.CmdDispatch(command, (uint)((threads + 63) / 64), 1, 1);
        VulkanSynchronization.PipelineBarrier(vk,
            command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit,
            0, null, 1, &postBarrier, 0, null);

        var area = _currentArea;
        var scanTick = _scheduler.CurrentTick;
        foreach (var (hdrAddress, hdrTick) in GuestBufferCache.BvhHeaderWrites) // [local] header snapshots at +2 and +40 ticks
        {
            var age = scanTick - hdrTick;
            if (age >= 2 && _hdrSnap1.Add(hdrAddress) || age >= 300 && _hdrSnap2.Add(hdrAddress))
            {
                var stage = _hdrSnap2.Contains(hdrAddress) ? "b" : "a";
                try { _cache.DumpDeviceRange(hdrAddress, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/hdr_{hdrAddress:X}_{stage}_{scanTick}.gpu.bin"); _cache.DumpGuestRange(hdrAddress, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/hdr_{hdrAddress:X}_{stage}_{scanTick}.guest.bin"); }
                catch (Exception e) { Console.Error.WriteLine($"[GPU][HDRSNAP] dump failed: {e.Message}"); }
            }
        }
        if (_traceDownloadBuffer is not null)
        {
            _traceDownloadBuffer.CopyFrom(_scheduler.Current, _faultBuffer, _faultBufferSize, area * 256UL, 256,
                destinationAfter: AccessFlags.HostReadBit);
            _faultBuffer.Fill(_faultBufferSize, 256, 0);
        }
        _scheduler.QueueCompletionAction(() =>
        {
            if (_traceDownloadBuffer is not null)
            {
                _traceDownloadBuffer.Invalidate(area * 256UL, 256);
                var stack = MemoryMarshal.Cast<byte, uint>(_traceDownloadBuffer.Mapped.Slice((int)area * 256, 128));
                var stats = MemoryMarshal.Cast<byte, uint>(_traceDownloadBuffer.Mapped.Slice((int)area * 256 + 160, 32));
                var oob = MemoryMarshal.Cast<byte, uint>(_traceDownloadBuffer.Mapped.Slice((int)area * 256 + 224, 32));
                var record = MemoryMarshal.Cast<byte, uint>(_traceDownloadBuffer.Mapped.Slice((int)area * 256 + 192, 32));
                if (stack[20] != 0)
                {
                    var table = (ulong)stack[30] << 32 | stack[29];
                    Console.Error.WriteLine($"[GPU][SPIN_INST] scan_tick={scanTick} active_spinning={stack[21]} s12=0x{stack[22]:X} blas_base=0x{(ulong)stack[20] << 8:X} last_node=0x{stack[23]:X} s8=0x{stack[24]:X} s9=0x{stack[25]:X} s23=0x{stack[26]:X} table=0x{table:X}");
                    var blas = (ulong)stack[20] << 8;
                    Console.Error.WriteLine($"[GPU][BLAS_HDR] scan_tick={scanTick} base=0x{blas:X} s0..s3=0x{stack[13]:X},0x{stack[14]:X},0x{stack[15]:X},0x{stack[16]:X} page: {_cache.DescribePage(blas, 256)}");
                    if (table != 0 && _inst0Dumps < 2)
                    {
                        _inst0Dumps++;
                        try
                        {
                            _cache.DumpDeviceRange(table, 160 * 128, $"C:/Users/danyy/AppData/Local/Temp/gta/insttab_{scanTick}_{table:X}.gpu.bin");
                            _cache.DumpGuestRange(table, 160 * 128, $"C:/Users/danyy/AppData/Local/Temp/gta/insttab_{scanTick}_{table:X}.guest.bin");
                        }
                        catch (Exception e) { Console.Error.WriteLine($"[GPU][INSTTAB] dump failed: {e.Message}"); }
                    }
                    if (stack[23] == 0xFFFFFFFF && RepairHeaders)
                    {
                        _repairs++;
                        if (_repairs <= 6)
                        {
                            try
                            {
                                _cache.DumpDeviceRange(blas, 1024, $"C:/Users/danyy/AppData/Local/Temp/gta/repair_{_repairs:D2}_{blas:X}.gpu.bin");
                                _cache.DumpGuestRange(blas, 1024, $"C:/Users/danyy/AppData/Local/Temp/gta/repair_{_repairs:D2}_{blas:X}.guest.bin");
                            }
                            catch (Exception e) { Console.Error.WriteLine($"[GPU][REPAIR] dump failed: {e.Message}"); }
                        }
                        var ok = _cache.RepairFromGuest(blas, 4096);
                        if (_repairs <= 20 || (_repairs & (_repairs - 1)) == 0) Console.Error.WriteLine($"[GPU][REPAIR] scan_tick={scanTick} base=0x{blas:X} ok={ok} count={_repairs}");
                    }
                    if (_traceArmed != 0 && _armedDumps < 16)
                    {
                        _armedDumps++;
                        try
                        {
                            _cache.DumpDeviceRange(_traceArmed, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/armed_{_armedDumps:D2}_{scanTick}.gpu.bin");
                            _cache.DumpGuestRange(_traceArmed, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/armed_{_armedDumps:D2}_{scanTick}.guest.bin");
                        }
                        catch (Exception e) { Console.Error.WriteLine($"[GPU][ARMED] dump failed: {e.Message}"); }
                    }
                    if (stack[23] == 0xFFFFFFFF && _traceArmed == 0)
                    {
                        _traceArmed = blas;
                        SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.SelectDeviceFaultTracePage(blas);
                        GuestBufferCache.BvhWatchBase = blas;
                        Console.Error.WriteLine($"[GPU][TRACE_ARMED] scan_tick={scanTick} base=0x{blas:X}");
                    }
                    if (_tlasDumped.Count < 4 && _tlasDumped.Add(stack[20]))
                    {
                        try
                        {
                            _cache.DumpDeviceRange(blas, 512, $"C:/Users/danyy/AppData/Local/Temp/gta/blashdr_{scanTick}_{blas:X}.gpu.bin");
                            _cache.DumpGuestRange(blas, 512, $"C:/Users/danyy/AppData/Local/Temp/gta/blashdr_{scanTick}_{blas:X}.guest.bin");
                        }
                        catch (Exception e) { Console.Error.WriteLine($"[GPU][SPIN_INST] dump failed: {e.Message}"); }
                    }
                }
                if (false && stack[20] != 0 && _tlasDumped.Count < 3 && _tlasDumped.Add(stack[20]))
                {
                    var tlasBase = (ulong)stack[20] << 8; var tlasBytes = Math.Min(((ulong)stack[23] + 1) * 64, 8UL << 20);
                    Console.Error.WriteLine($"[GPU][BLAS0] scan_tick={scanTick} base=0x{tlasBase:X} last_node=0x{stack[23]:X} bytes={tlasBytes}");
                    try { _cache.DumpDeviceRange(tlasBase, tlasBytes, $"C:/Users/danyy/AppData/Local/Temp/gta/blas0_{scanTick}_{tlasBase:X}.gpu.bin"); }
                    catch (Exception e) { Console.Error.WriteLine($"[GPU][TLAS] dump failed: {e.Message}"); }
                }
                if (stack[12] != 0)
                    Console.Error.WriteLine($"[GPU][TLASHIST] scan_tick={scanTick} active_spinning={stack[21]} iters={stack[30]} ring={stack[30] & 3} e0: s12=0x{stack[13]:X} s96={stack[14]} v0=0x{stack[15]:X} | e1: s12=0x{stack[16]:X} s96={stack[17]} v0=0x{stack[18]:X} | e2: s12=0x{stack[19]:X} s96={stack[24]} v0=0x{stack[25]:X} | e3: s12=0x{stack[26]:X} s96={stack[27]} v0=0x{stack[29]:X}");
                if (stack[28] != 0)
                    Console.Error.WriteLine($"[GPU][SPIN_BVH] scan_tick={scanTick} spinning={stack[28]} v8_samples=0x{stack[24]:X},0x{stack[25]:X},0x{stack[26]:X},0x{stack[27]:X} v0=0x{stack[29]:X} v1=0x{stack[30]:X} v42=0x{stack[31]:X} ");
                var hw = MemoryMarshal.Cast<byte, uint>(_traceDownloadBuffer.Mapped.Slice((int)area * 256 + 128, 32)); // [local]
                var wf = MemoryMarshal.Cast<byte, uint>(_traceDownloadBuffer.Mapped.Slice((int)area * 256 + 176, 16)); // [local] words 44..47
                if (wf[2] != 0)
                    Console.Error.WriteLine($"[GPU][GUARD5981] scan_tick={scanTick} max_steps={wf[0]} lanes_past_90pct={wf[1]} lanes={wf[2]}");
                if (hw[0] != 0)
                    Console.Error.WriteLine($"[GPU][APPEND] scan_tick={scanTick} adds={hw[0]} sum_operand={hw[1]} sum_saved_popcount={hw[2]} sum_inbounds={hw[3]} adds_over={hw[4]} max_excess={hw[5]} sum_excess={hw[6]} max_limit={hw[7]}");
                if (stack[0] != 0 || stack[2] != 0 || stack[3] != 0)
                    Console.Error.WriteLine($"[GPU][STACK] scan_tick={scanTick} push_lds={stack[0]} push_scr={stack[1]} pop_lds={stack[2]} pop_scr={stack[3]} lds_zero={stack[4]} scr_zero={stack[5]} lds_m1={stack[6]} scr_m1={stack[7]} max_push_off=0x{stack[8]:X} lds_dwords={stack[9]} scratch_dwords={stack[10]} max_pop_off=0x{stack[11]:X}");
                if (stats[2] != 0)
                    Console.Error.WriteLine($"[GPU][STEPS] scan_tick={scanTick} max={stats[0]} over1k={stats[3]} over10k={stats[1]} over30k={stats[4]} over90k={stats[5]} total={stats[2]}");
                if (oob[0] != 0)
                    Console.Error.WriteLine($"[GPU][OOB_STORE] scan_tick={scanTick} hash=0x{((ulong)oob[2] << 32 | oob[1]):X16} pc=0x{oob[3]:X} binding={oob[4]} dword=0x{oob[5]:X} bound_dwords=0x{oob[6]:X} site={oob[7]}");
                if (record[0] != 0)
                    Console.Error.WriteLine($"[GPU][DEVICE_ADDRESS_FAULT] scan_tick={scanTick} hash=0x{((ulong)record[2] << 32 | record[1]):X16} pc=0x{record[3]:X} address=0x{((ulong)record[5] << 32 | record[4]):X16} stage={record[6]} extra=0x{record[7]:X}");
            }
            _downloadBuffer.Invalidate(offset, PageFaultAreaSize);
            _faultRanges.Clear();
            var faults = MemoryMarshal.Cast<byte, ulong>(_downloadBuffer.Mapped.Slice((int)offset, (int)PageFaultAreaSize));
            var count = Math.Min((uint)faults[0], MaxPageFaults - 1);
            for (var index = 1; index <= count; index++)
            {
                _faultRanges.Add(faults[index], _pageSize);
                GuestGpuMemoryHook.SelectDeviceFaultTracePage(faults[index]);
                if (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Traces(faults[index], _pageSize))
                    SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Trace(faults[index], _pageSize,
                        $"device-address-fault scan_tick={scanTick} callback_tick={_scheduler.CurrentTick} registered={_cache.IsRegionRegistered(faults[index], _pageSize)} reported_count={(uint)faults[0]} retained_count={count}");
                var reported = Interlocked.Increment(ref _reportedFaults);
                if (reported <= 16 || (reported & (reported - 1)) == 0)
                {
                    Console.Error.WriteLine($"[GPU][INFO] Accessed non-GPU cached memory at 0x{faults[index]:X16} count={reported}");
                }
            }

            _faultRanges.ForEach((start, size) =>
            {
                if (size > uint.MaxValue)
                {
                    throw SubmissionScheduler.Fatal("The fault range exceeds the buffer.");
                }

                _ = _cache.FindBuffer(start, size);
                if (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Traces(start, size))
                    SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Trace(start, size,
                        $"device-address-fault-prepared scan_tick={scanTick} submission_tick={_scheduler.CurrentTick} registered={_cache.IsRegionRegistered(start, size)}");
            });
            _faultAreas[area] = 0;
        });

        _faultAreas[_currentArea++] = _scheduler.CurrentTick;
        _currentArea %= MaxPendingFaults;
    }

    public void Dispose()
    {
        var vk = _device.Vk;
        vk.DestroyPipeline(_device.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_device.Device, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_device.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_device.Device, _layout, null);
        _downloadBuffer.Dispose();
        _traceDownloadBuffer?.Dispose();
        _faultBuffer.Dispose();
    }

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed with {result}");
        }
    }
}
