// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.LocalScratch;

// [local] Replays one compute dispatch of the game offline: the guest binary (LOCAL_DUMP_SHADER_HASHES dump), the bound
// buffers as SHARPEMU_TRACK_BUILD dumped them before the dispatch (<prefix>_<buffer>_<address>_<size>_<tick>.gpu.bin) and
// the user data of the dispatch ([BVH_WRITER] user_data). Writes every buffer after the dispatch to LOCAL_REPLAY_OUT as
// out_<buffer>.bin, so the translation can be checked against the in-game result and across branches.
// LOCAL_REPLAY_BIN=<shader_<hash>.bin> LOCAL_REPLAY_DIR=<dumps> LOCAL_REPLAY_PREFIX=s260Fin LOCAL_REPLAY_TICK=22145
// LOCAL_REPLAY_HASH=<hex> LOCAL_REPLAY_GROUPS=33 LOCAL_REPLAY_USERDATA=<hex,hex,...> LOCAL_REPLAY_OUT=<dir>
// LOCAL_REPLAY_LOCAL=64 (threads per group) LOCAL_REPLAY_SLACK=65536 (bytes past each dump).
public sealed class LocalReplayDispatchTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void ReplaysTheDispatch()
    {
        var binary = Environment.GetEnvironmentVariable("LOCAL_REPLAY_BIN");
        var directory = Environment.GetEnvironmentVariable("LOCAL_REPLAY_DIR");
        var outDirectory = Environment.GetEnvironmentVariable("LOCAL_REPLAY_OUT");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(outDirectory) || !File.Exists(binary))
        {
            return;
        }

        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var prefix = Environment.GetEnvironmentVariable("LOCAL_REPLAY_PREFIX") ?? "s260Fin";
        var tick = Environment.GetEnvironmentVariable("LOCAL_REPLAY_TICK") ?? "0";
        var hash = ulong.Parse((Environment.GetEnvironmentVariable("LOCAL_REPLAY_HASH") ?? "260F2884A4A5E7C0").Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var groups = uint.Parse(Environment.GetEnvironmentVariable("LOCAL_REPLAY_GROUPS") ?? "1", CultureInfo.InvariantCulture);
        var local = uint.Parse(Environment.GetEnvironmentVariable("LOCAL_REPLAY_LOCAL") ?? "64", CultureInfo.InvariantCulture);
        var slack = ulong.Parse(Environment.GetEnvironmentVariable("LOCAL_REPLAY_SLACK") ?? "65536", CultureInfo.InvariantCulture);
        var userData = (Environment.GetEnvironmentVariable("LOCAL_REPLAY_USERDATA") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => uint.Parse(word, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();

        var code = File.ReadAllBytes(binary);
        var memory = new FakeCpuMemory(ShaderAddress, ((code.Length + 0xFFF) & ~0xFFF) + 0x1000);
        Assert.True(memory.TryWrite(ShaderAddress, code));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error), error);
        output.WriteLine($"decoded {program.Instructions.Count} instructions");
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, hash, 0, (uint)Math.Max(userData.Length, 16), waveSize: 64);
        // LOCAL_REPLAY_STRIDES=16,4,... : the packed strides of the game's specialization (the variant dump).
        var specialization = ResourceSpecialization.Default(plan.Info);
        var strides = (Environment.GetEnvironmentVariable("LOCAL_REPLAY_STRIDES") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        for (var index = 0; index < Math.Min(strides.Length, specialization.Buffers.Count); index++)
        {
            // stride[/format/swizzle]: the descriptor format and swizzle of a formatted load (74/940 = 32_32_32_FLOAT xyz)
            var parts = strides[index].Split('/');
            var entry = specialization.Buffers[index] with { PackedStride = uint.Parse(parts[0], CultureInfo.InvariantCulture) };
            if (parts.Length > 1) entry = entry with { DescriptorFormat = uint.Parse(parts[1], CultureInfo.InvariantCulture) };
            if (parts.Length > 2) entry = entry with { DescriptorSwizzle = uint.Parse(parts[2], CultureInfo.InvariantCulture) };
            specialization.Buffers[index] = entry;
        }

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, (uint)Math.Max(userData.Length, 16)),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            0,
            usesDispatchThreadLimits: false,
            limitsInShaderData: false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            EnableExecGuardElision = true,
            SupportsSharedInt64Atomics = true,
            SupportsBufferInt64Atomics = true,
            SupportsFloat16Conversions = true,
            ShaderSignedZeroInfNanPreserveFloat32Supported = true,
            HostComputeSubgroup32 = true,
            LocalSizeX = local,
            // LOCAL_REPLAY_GROUP_REGISTER: the SGPR that receives the workgroup X id (the user data count in the game).
            ComputeSystemRegisters = new Gen5ComputeSystemRegisters(
                uint.Parse(Environment.GetEnvironmentVariable("LOCAL_REPLAY_GROUP_REGISTER") ?? userData.Length.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                null, null, null),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        output.WriteLine($"buffers={resources.Info.Buffers.Count} images={resources.Info.Images.Count} spirv={shader.Spirv.Length} push={layout.PushDataStartDword}");
        foreach (var (buffer, index) in resources.Info.Buffers.Select((buffer, index) => (buffer, index)))
        {
            string text;
            try { text = System.Text.Json.JsonSerializer.Serialize(buffer, buffer.GetType()); } catch (Exception e) { text = e.Message; }
            output.WriteLine($"  buffer {index}: {text}");
        }

        foreach (var descriptor in layout.Descriptors)
        {
            output.WriteLine($"  descriptor {descriptor.Kind} x{descriptor.Resources.Count}");
        }

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var bound = new GpuBuffer[resources.Info.Buffers.Count];
        var sizes = new ulong[bound.Length];
        var pages = new List<(ulong GuestAddress, GpuBuffer Buffer, ulong BufferOffset)>();
        var maxPage = 0ul;
        for (var index = 0; index < bound.Length; index++)
        {
            var file = Directory.GetFiles(directory, $"{prefix}_{index}_*_{tick}.gpu.bin").OrderBy(name => name).FirstOrDefault();
            var data = file is null ? [] : File.ReadAllBytes(file);
            output.WriteLine($"  input {index}: {(file is null ? "(none)" : Path.GetFileName(file))} bytes={data.Length}");
            sizes[index] = (ulong)data.Length + slack;
            if (file is not null)
            {
                // The dump starts at the buffer's guest address; the buffer gets the page offset of that address so
                // page-granular device addresses land on the right bytes.
                var address = ulong.Parse(Path.GetFileName(file).Split('_')[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var pageOffset = address & (Gen5SpirvTranslator.DeviceAddressPageSize - 1);
                bound[index] = runner.CreateBuffer(data, sizes[index]);
                output.WriteLine($"    guest address 0x{address:X} page offset 0x{pageOffset:X} (bound at offset 0)");
                continue;
            }

            bound[index] = runner.CreateBuffer(data, sizes[index]);
        }

        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = bound };
        if (layout.Descriptors.Any(descriptor => descriptor.Kind == DescriptorBindingKind.DeviceAddressPageTable))
        {
            var pageCount = maxPage + 1;
            bindings[DescriptorBindingKind.DeviceAddressPageTable] = [runner.CreatePageTable(pageCount, pages)];
            bindings[DescriptorBindingKind.FaultBuffer] = [runner.CreateBuffer(((pageCount + 31) / 32) * sizeof(uint) + 4096)];
            output.WriteLine($"  page table: {pageCount} entries, {pages.Count} pages mapped");
        }

        // LOCAL_REPLAY_BLOCK=<offset>:<hex>,... : the dwords of the constant block the planning-only scalar loads read
        // (user data s[0:1] in the game); they fill the flattened slots in plan order.
        var block = new Dictionary<uint, uint>();
        foreach (var item in (Environment.GetEnvironmentVariable("LOCAL_REPLAY_BLOCK") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':');
            block[uint.Parse(parts[0].Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = uint.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        var slots = new uint[request.FlattenedSlotByMemoryIndex.Count == 0 ? 1 : request.FlattenedSlotByMemoryIndex.Values.Max() + 1];
        foreach (var (memoryIndex, slot) in request.FlattenedSlotByMemoryIndex)
        {
            var entry = request.Memory[memoryIndex];
            block.TryGetValue(entry.Offset, out var value);
            slots[slot] = value;
            output.WriteLine($"  slot {slot}: memory {memoryIndex} pc=0x{entry.Pc:X} offset={entry.Offset} value=0x{value:X8}");
        }

        var registers = new uint[256];
        userData.CopyTo(registers, 0);
        harness.Run(() => runner.Dispatch(registers, bindings, groups, flattenedTable: slots));
        Directory.CreateDirectory(outDirectory);
        for (var index = 0; index < bound.Length; index++)
        {
            var bytes = runner.ReadBack(bound[index], 0, sizes[index]);
            File.WriteAllBytes(Path.Combine(outDirectory, $"out_{index}.bin"), bytes);
        }

        harness.AssertNoValidationMessages();
    }

    // [local] LOCAL_REPLAY_BIN_DIR=<dir with shader_<hash>.bin> LOCAL_REPLAY_OUT=<dir>: translates every binary with the
    // default specialization and writes <hash>.spv, for opcode histograms across branches.
    [Fact]
    public void CompilesEveryBinary()
    {
        var directory = Environment.GetEnvironmentVariable("LOCAL_REPLAY_BIN_DIR");
        var outDirectory = Environment.GetEnvironmentVariable("LOCAL_REPLAY_OUT");
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(outDirectory) || !Directory.Exists(directory))
        {
            return;
        }

        Directory.CreateDirectory(outDirectory);
        foreach (var binary in Directory.GetFiles(directory, "shader_*.bin"))
        {
            var hash = ulong.Parse(Path.GetFileNameWithoutExtension(binary)["shader_".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var code = File.ReadAllBytes(binary);
            var memory = new FakeCpuMemory(ShaderAddress, ((code.Length + 0xFFF) & ~0xFFF) + 0x1000);
            Assert.True(memory.TryWrite(ShaderAddress, code));
            var ctx = new CpuContext(memory, Generation.Gen5);
            if (!Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error))
            {
                output.WriteLine($"{hash:X16}: decode failed: {error}");
                continue;
            }

            ShaderResourcePlan plan;
            SpecializedResourceInfo resources;
            BindingLayout layout;
            // LOCAL_REPLAY_JSON_DIR: the game's variant dump (SHARPEMU_DUMP_SPIRV) of the hash gives the specialization,
            // the user data count, the group size, the wave size and the thread-limit use.
            uint userDataCount = 16, localX = 64, localY = 1, localZ = 1, waveSize = 64, scratch = 0;
            var usesLimits = false;
            System.Text.Json.JsonElement? variant = null;
            var jsonDirectory = Environment.GetEnvironmentVariable("LOCAL_REPLAY_JSON_DIR");
            if (!string.IsNullOrEmpty(jsonDirectory))
            {
                var json = Directory.GetFiles(jsonDirectory, $"*-{hash:X16}.compute.*variant*.json").OrderBy(name => name).FirstOrDefault();
                if (json is not null)
                {
                    variant = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json)).RootElement;
                    var v = variant.Value;
                    userDataCount = v.GetProperty("UserDataCount").GetUInt32();
                    localX = v.GetProperty("LocalSizeX").GetUInt32(); localY = v.GetProperty("LocalSizeY").GetUInt32(); localZ = v.GetProperty("LocalSizeZ").GetUInt32();
                    waveSize = v.GetProperty("WaveSize").GetUInt32(); scratch = v.GetProperty("ScratchDwords").GetUInt32();
                    usesLimits = v.GetProperty("UsesDispatchThreadLimits").GetBoolean();
                }
            }

            try
            {
                plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, hash, 0, userDataCount, waveSize: waveSize);
                var specialization = ResourceSpecialization.Default(plan.Info);
                if (variant is { } element)
                {
                    var buffers = element.GetProperty("Specialization").GetProperty("Buffers");
                    var index = 0;
                    foreach (var buffer in buffers.EnumerateArray())
                    {
                        if (index >= specialization.Buffers.Count) break;
                        specialization.Buffers[index] = new BufferSpecialization(buffer.GetProperty("PackedStride").GetUInt32(),
                            buffer.GetProperty("DescriptorFormat").GetUInt32(), buffer.GetProperty("DescriptorSwizzle").GetUInt32());
                        index++;
                    }
                }

                resources = ResourceMaterializer.ApplyTo(plan, specialization);
                layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, userDataCount),
                    BindingLayout.UsesGlobalDataShare(program), ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
                    BindingLayout.ReadsShaderBase(program), 0, usesDispatchThreadLimits: usesLimits, limitsInShaderData: false);
            }
            catch (Exception e)
            {
                output.WriteLine($"{hash:X16}: plan failed: {e.Message}");
                continue;
            }
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                WaveSize = waveSize, EnableExecGuardElision = true, SupportsSharedInt64Atomics = true, SupportsBufferInt64Atomics = true,
                SupportsFloat16Conversions = true, ShaderSignedZeroInfNanPreserveFloat32Supported = true,
                LocalSizeX = localX, LocalSizeY = localY, LocalSizeZ = localZ, ScratchDwords = scratch,
                LocalDataShareDwords = 8192,
                ComputeSystemRegisters = new Gen5ComputeSystemRegisters(userDataCount, null, null, null),
                HostComputeSubgroup32 = true,
            };
            if (!Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error))
            {
                output.WriteLine($"{hash:X16}: compile failed: {error}");
                continue;
            }

            File.WriteAllBytes(Path.Combine(outDirectory, $"{hash:X16}.spv"), shader.Spirv);
            output.WriteLine($"{hash:X16}: {program.Instructions.Count} instructions, {shader.Spirv.Length} bytes wave={waveSize} local={localX}x{localY}x{localZ} user_data={userDataCount} limits={usesLimits} " +
                $"layout: shader_data={layout.ShaderDataDwordCount} push={layout.PushDataStartDword} memory_offset={layout.MemoryOffsetDword} buffers={resources.Info.Buffers.Count} descriptors={string.Join(",", layout.Descriptors.Select(d => d.Kind + ":" + d.Resources.Count))} flattened={plan.FlattenedSlotByMemoryIndex.Count}");
        }
    }
}
