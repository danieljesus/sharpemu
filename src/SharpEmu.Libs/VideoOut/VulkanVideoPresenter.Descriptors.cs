// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

// This partial binds the resources of one stage: storage buffers, images, samplers and push data.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private const ulong NullStorageBufferBytes = 16;
        private const uint MaxMemoryOffsetAdjustment = 256;
        private const uint TransientDataAlignment = 256;
        private const int MaxImageOccurrences = 64;

        private readonly record struct BufferView(VkBuffer Buffer, ulong Offset, ulong Range);

        private sealed class DescriptorScratch
        {
            public readonly RenderScratchPool<TextureResource> Images = new();
            public readonly RenderScratchPool<Sampler> Samplers = new();
            public readonly RenderScratchPool<uint> ShaderData = new();
            public readonly RenderScratchPool<BufferView> Buffers = new();
            public readonly RenderScratchPool<(BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer)> Sources = new();
        }

        private DescriptorScratch? _descriptorScratch;
        private DescriptorScratch Scratch => _descriptorScratch ??= new();

        // The host descriptors of one stage in the order its binding layout names them.
        private sealed class StageDescriptors
        {
            public BufferView[] Buffers = [];
            public TextureResource[] Images = [];
            public Sampler[] Samplers = [];
            public BufferView GlobalDataShare;
            public BufferView FlattenedTable;
            public BufferView ShaderData;
        }

        private sealed class PreparedStageBindings(ShaderStageResources stage, ShaderProgramInfo program) : IPreparedBindings
        {
            public ShaderStageResources Stage => stage;

            public ShaderProgramInfo Program => program;

            public SpecializedResourceInfo Resources => program.Resources!;

            public BindingLayout Layout => program.Bindings!;

            public StageDescriptors Descriptors { get; } = new();

            public (BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer)[] BufferSources { get; set; } = [];

            public uint[] ShaderData { get; set; } = [];

            public TextureResource[] Textures => Descriptors.Images;
        }

        private static ShaderStage StageOf(ShaderProgramInfo program) => program.Stage switch
        {
            ShaderStageKind.Vertex => ShaderStage.Vertex,
            ShaderStageKind.Pixel => ShaderStage.Pixel,
            ShaderStageKind.Compute => ShaderStage.Compute,
            _ => throw SubmissionScheduler.Fatal($"The stage kind is unknown: stage={program.Stage} hash=0x{program.Hash:X16}."),
        };

        private static ShaderProgramInfo RequireProgram(ShaderStageResources stage)
        {
            var program = stage.Program ?? throw SubmissionScheduler.Fatal("The stage has no program.");
            if (program.Resources is null || program.Bindings is null)
            {
                throw SubmissionScheduler.Fatal($"The stage program has no resource plan: stage={program.Stage} hash=0x{program.Hash:X16}.");
            }

            return program;
        }

        private static TextureNumericClass NumericClassOf(ImageResource image) => image.NumericClass switch
        {
            ImageNumericClass.Uint => TextureNumericClass.Uint,
            ImageNumericClass.Sint => TextureNumericClass.Sint,
            _ => TextureNumericClass.Float,
        };

        private static ShaderImageShape ShapeOf(ImageResource image) => new(
            Volume: image.Dimension == ImageDimension.Dim3D,
            Arrayed: image.Cube || image.Dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or ImageDimension.Dim2DMsaaArray,
            Cube: image.Cube,
            Storage: image.ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage,
            DynamicMip: image.MipMode == ImageMipMode.DynamicStorage,
            NumericClass: NumericClassOf(image),
            OneDimensional: image.Dimension is ImageDimension.Dim1D or ImageDimension.Dim1DArray,
            R128: image.R128,
            Multisampled: image.Dimension is ImageDimension.Dim2DMsaa or ImageDimension.Dim2DMsaaArray,
            DepthCompare: image.DepthCompare,
            Atomic: image.Atomic);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _tracedTextureBindings = new();

        // Render-state discovery for one shader image; the view is acquired later with the draw.
        private TextureResource ResolveImageBinding(ImageResource image, uint[] words, ShaderProgramInfo program, int index)
        {
            if (words.Length < 4)
            {
                throw SubmissionScheduler.Fatal($"An image descriptor is too short: image={index} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            var storage = image.ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage;
            var resolution = ImageRequestBuilders.Texture(words, ShapeOf(image));
            _ = BeginBatchedGuestCommands();
            var request = resolution.Request;
            var imageIdentifier = _imageCache.FindImage(ref request, resolution.ExactFormat);
            resolution = resolution with { Request = request };
            imageIdentifier = ImageRequestBuilders.ValidateTextureOwner(_imageCache, imageIdentifier, resolution);
            BindImage(imageIdentifier, storage);
            var descriptor = new TextureDescriptorWords(words);
            if (ShouldTraceTextureBindings() &&
                (!_traceVolumeTextureBindingsOnly || image.Dimension == ImageDimension.Dim3D))
            {
                var cached = _imageCache.GetImage(imageIdentifier);
                var description = cached.Description;
                var line =
                    $"TextureBinding stage={program.Stage} hash=0x{program.Hash:X16} index={index} " +
                    $"address=0x{new TextureDescriptorWords(words).BaseAddress:X16} " +
                    $"descriptor={descriptor.BaseAddress:X16} size={descriptor.Width + 1}x{descriptor.Height + 1} " +
                    $"format={(uint)descriptor.Format} tile={(uint)descriptor.TileMode} " +
                    $"image=0x{description.Data.Address:X16} size=0x{description.Data.Size:X} " +
                    $"extent={description.Extent.Width}x{description.Extent.Height} pitch={description.Pitch} " +
                    $"guestFormat={(uint)description.GuestFormat} imageTile={(uint)description.TileMode} " +
                    $"backing={cached.Backing.Extent.Width}x{cached.Backing.Extent.Height} format={cached.Backing.Format} " +
                    $"depth={descriptor.Depth + 1} type={descriptor.Type} dcc={descriptor.MetadataCompress} " +
                    $"metadata=0x{descriptor.MetadataAddress << 8:X} metaKind={description.Metadata.Kind} " +
                    $"words={string.Join(',', words.Select(static word => word.ToString("X8")))}";
                if (!_traceVolumeTextureBindingsOnly || _tracedTextureBindings.TryAdd(line, 0))
                {
                    Console.Error.WriteLine(line);
                }
            }
            return new TextureResource
            {
                Address = descriptor.BaseAddress,
                ImageIdentifier = imageIdentifier,
                Request = request,
                IsStorage = storage,
                DestinationSelect = words[3] & 0xFFFu,
                Width = descriptor.Width,
                Height = descriptor.Height,
            };
        }

        // Compare bits stay only on depth-compare samplers; a forced point sampler drops its filters.
        // A sampler takes the numeric class of the views it samples; integer only when every
        // paired view is integer, since a float view needs a float border and filtering.
        private static bool SamplesIntegerViews(ShaderResourceInfo info, TextureResource[] images, int sampler)
        {
            var paired = false;
            foreach (var pair in info.SampledPairs)
            {
                if (pair.Sampler != sampler || pair.Image >= images.Length || images[pair.Image].IsHostMovie)
                {
                    continue;
                }

                if (!ViewFormatRules.IsIntegerFormat(images[pair.Image].Request.View.Format))
                {
                    return false;
                }

                paired = true;
            }

            return paired;
        }

        private Sampler ResolveSampler(SamplerResource sampler, uint[] words, ShaderProgramInfo program, int index, ShaderStageResources stage,
            bool integerView)
        {
            if (words.Length < 4)
            {
                throw SubmissionScheduler.Fatal($"A sampler descriptor is too short: sampler={index} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            Span<uint> native = stackalloc uint[4] { words[0], words[1], words[2], words[3] };
            if (!sampler.DepthCompare)
            {
                native[0] &= ~(0x7u << 12);
            }

            if (sampler.ForcePointFiltering)
            {
                var mipmapped = ((native[2] >> 26) & 0x3u) != 0;
                native[2] &= ~(0xFFu << 20);
                native[2] |= 1u << 24;
                if (mipmapped)
                {
                    native[2] |= 1u << 26;
                }
            }

            var descriptor = new SamplerDescriptorWords(native);
            if (descriptor.MaxAnisotropyRatio > 4 &&
                (descriptor.MagnifyFilter >= (uint)SamplerFilter.AnisotropicPoint ||
                 descriptor.MinifyFilter >= (uint)SamplerFilter.AnisotropicPoint))
            {
                Console.Error.WriteLine(
                    $"[GPU][ERROR] Sampler source: stage={program.Stage} hash=0x{program.Hash:X16} shader=0x{stage.ShaderBase:X16} " +
                    $"sampler={index} source={sampler.Source} pc=0x{sampler.FirstUsePc:X} " +
                    $"descriptor=[{string.Join(",", words.Select(word => $"{word:X8}"))}] " +
                    $"user_data=[{string.Join(",", stage.Resources.UserData.Select(word => $"{word:X8}"))}]");
            }

            return _samplerStore.GetSampler(descriptor, integerView);
        }

        // The guest textures the movie path matches; built only while a decoded frame is active.
        private static List<GuestDrawTexture>? MovieCandidates(SpecializedResourceInfo resources, ResourceSnapshot snapshot)
        {
            var textures = new List<GuestDrawTexture>(resources.Info.Images.Count);
            for (var index = 0; index < resources.Info.Images.Count; index++)
            {
                var words = snapshot.Images[index];
                var storage = resources.Info.Images[index].ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage;
                textures.Add(AgcExports.TryDecodeTextureDescriptor(words, out var descriptor)
                    ? new GuestDrawTexture(descriptor.Address, descriptor.Width, descriptor.Height, descriptor.Format, descriptor.NumberType, [], false, storage, DstSelect: descriptor.DstSelect)
                    : new GuestDrawTexture(0, 1, 1, 0, 0, [], true, storage));
            }

            return textures;
        }

        public IPreparedBindings PrepareBindings(ShaderStageResources stage)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorPreparation);
            var preparation = RequirePreparation();
            var program = RequireProgram(stage);
            var resources = program.Resources!;
            var layout = program.Bindings!;
            var snapshot = stage.Resources;
            var info = resources.Info;
            if (snapshot.Images.Length != info.Images.Count || snapshot.Samplers.Length != info.Samplers.Count || snapshot.Buffers.Length != info.Buffers.Count)
            {
                throw SubmissionScheduler.Fatal(
                    $"The resource snapshot does not match the program: hash=0x{program.Hash:X16} images={snapshot.Images.Length}/{info.Images.Count} " +
                    $"samplers={snapshot.Samplers.Length}/{info.Samplers.Count} buffers={snapshot.Buffers.Length}/{info.Buffers.Count}.");
            }

            var prepared = new PreparedStageBindings(stage, program);
            // Register ownership before preparation can fail halfway through.
            preparation.Stages.Add(prepared);
            var descriptors = prepared.Descriptors;
            descriptors.Images = Scratch.Images.Rent(info.Images.Count);
            var movieCandidates = _hostMovieFramePixels is null ? null : MovieCandidates(resources, snapshot);
            var hostMovie = movieCandidates is null ? HostMovieTextureBindings.None : FindHostMovieTextureBindings(movieCandidates);
            for (var index = 0; index < info.Images.Count; index++)
            {
                descriptors.Images[index] = index == hostMovie.Luma
                    ? CreateHostMovieTextureResource(movieCandidates![index], plane: 0)
                    : index == hostMovie.Chroma
                        ? CreateHostMovieTextureResource(movieCandidates![index], plane: 1)
                        : ResolveImageBinding(info.Images[index], snapshot.Images[index], program, index);
            }

            descriptors.Samplers = Scratch.Samplers.Rent(info.Samplers.Count);
            for (var index = 0; index < info.Samplers.Count; index++)
            {
                descriptors.Samplers[index] = ResolveSampler(info.Samplers[index], snapshot.Samplers[index], program, index, stage,
                    SamplesIntegerViews(info, descriptors.Images, index));
            }

            var shaderData = Scratch.ShaderData.Rent(checked((int)layout.ShaderDataDwordCount));
            prepared.ShaderData = shaderData;
            for (var index = 0; index < layout.UserDataRegisters.Count; index++)
            {
                var register = layout.UserDataRegisters[index];
                var userIndex = (int)(register - program.UserDataBase);
                if (register < program.UserDataBase || userIndex >= snapshot.UserData.Length)
                {
                    throw SubmissionScheduler.Fatal($"A user register is outside the draw's user data: register={register} base={program.UserDataBase} count={snapshot.UserData.Length} hash=0x{program.Hash:X16}.");
                }

                shaderData[index] = snapshot.UserData[userIndex];
            }

            if (layout.UsesShaderBase)
            {
                shaderData[layout.ShaderBaseDword] = (uint)stage.ShaderBase;
                shaderData[layout.ShaderBaseDword + 1] = (uint)(stage.ShaderBase >> 32);
            }

            stage.WriteDispatchThreadLimits(shaderData);
            prepared.ShaderData = shaderData;
            if (layout.Find(DescriptorBindingKind.GlobalDataShare) is not null)
            {
                descriptors.GlobalDataShare = new BufferView(_bufferCache.GdsBuffer.Handle, 0, Vk.WholeSize);
            }

            FindDeviceAddressBuffers(prepared);
            FindBuffers(prepared);
            ValidateDrawImageTypes(prepared);
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"Bindings prepared stage={program.Stage} hash=0x{program.Hash:X16} buffers={info.Buffers.Count} images={info.Images.Count} " +
                    $"samplers={info.Samplers.Count} userData={snapshot.UserData.Length} flattened={snapshot.FlattenedResourceTable.Length} shaderData={shaderData.Length}");
            }

            return prepared;
        }

        // Reject incompatible draw views before buffer writes or image transitions are recorded.
        private void ValidateDrawImageTypes(PreparedStageBindings prepared)
        {
            if (prepared.Program.Stage == ShaderStageKind.Compute)
            {
                return;
            }

            foreach (var binding in prepared.Textures)
            {
                if (binding.IsHostMovie || IsStaleImage(binding.ImageIdentifier, out var image) || image is null)
                {
                    continue;
                }

                var view = binding.IsStorage ? binding.Request.View with { LevelCount = 1 } : binding.Request.View;
                if (!image.SupportsViewType(view))
                {
                    throw new DrawImageTypeMismatchException(
                        prepared.Program.Hash, binding.Address, image.Backing.ImageType, view.Type);
                }
            }
        }

        private void FindDeviceAddressBuffers(PreparedStageBindings prepared)
        {
            foreach (var range in prepared.Stage.Resources.DeviceAddressRanges)
            {
                if (!range.Planned || range.Size == 0 ||
                    range.Base >= PageOwnerTable.AddressSpaceSize || range.Size > PageOwnerTable.AddressSpaceSize - range.Base ||
                    (!range.Written && !_guestMemory.CanRead(range.Base, 1)))
                {
                    continue;
                }

                _ = _bufferCache.FindBuffer(range.Base, ClampMappedSize(range.Base, range.Size));
                if (BvhProbes && _bvhWriterCount < 400 && InBvhBlob(range.Base))
                {
                    _bvhWriterCount++;
                    Console.Error.WriteLine($"[BVH_WRITER] hash=0x{prepared.Program.Hash:X16} stage={prepared.Program.Stage} range base=0x{range.Base:X} size=0x{range.Size:X} written={range.Written}");
                }
            }
        }

        // The cache buffer each descriptor's range lives in; a null descriptor has none.
        private void FindBuffers(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var snapshot = prepared.Stage.Resources;
            var sources = Scratch.Sources.Rent(prepared.Resources.Info.Buffers.Count);
            prepared.BufferSources = sources;
            for (var index = 0; index < sources.Length; index++)
            {
                var words = snapshot.Buffers[index];
                if (words.Length < 4)
                {
                    throw SubmissionScheduler.Fatal($"A buffer descriptor is too short: buffer={index} words={words.Length} hash=0x{program.Hash:X16}.");
                }

                var descriptor = BufferDescriptorWords.From(words);
                var requested = descriptor.Footprint() ?? throw SubmissionScheduler.Fatal(
                    $"A storage buffer descriptor footprint overflows: buffer={index} stride={descriptor.Stride} records={descriptor.RecordCount} hash=0x{program.Hash:X16}.");
                if (descriptor.Address == 0 || requested == 0)
                {
                    sources[index] = (descriptor, default);
                    continue;
                }

                var size = ClampMappedSize(descriptor.Address, requested, prepared, index);
                sources[index] = (descriptor, _bufferCache.FindBuffer(descriptor.Address, size));
                ProbeBvhRefitBuffer(program.Hash, index, descriptor, requested, size, prepared);
            }

            // [local] the small blob's header before every dispatch, to catch the writer of +0x8C
            if (_bvhSmallBlob != 0 && _bvhAllDumps < 0 && prepared.Program.Stage == ShaderStageKind.Compute)
            {
                _bvhAllDumps++;
                DumpBvhBuffer($"all{_bvhAllDumps:D5}_{program.Hash:X16}_hdr", default(Gpu.Rendering.BufferDescriptorWords).WithAddress(_bvhSmallBlob + 0x80), 32);
            }

            prepared.BufferSources = sources;
        }

        // [local] Prints the resolved buffers of GTA V's BVH refit (0x5981...) for the first dispatches,
        // and scans its primitive list (stride 8) for the negative sentinel that ends its loop L2.
        private int _bvhProbeCount;
        private int _bvhWriterCount;
        private readonly List<(ulong Base, ulong Size)> _bvhBlobs = new();
        private static readonly uint[] BvhProbeNodes = [0x700, 0x2E4, 0x4A8, 0xD98, 0xE02, 0xF32, 0xF3C, 0xF6D, 0xF6E, 0x100B, 0x100C, 0x100D, 0x10BA, 0x1822];

        private bool InBvhBlob(ulong address)
        {
            foreach (var blob in _bvhBlobs)
                if (address >= blob.Base && address < blob.Base + blob.Size) return true;
            return false;
        }

        private int _bvhSeq;
        private int _bvhListSeq;
        private int _bvhHdrDumps;
        private int _bvhAllDumps;
        private int _keyDumps; // [local]
        private int _keyTableLogs; // [local]
        private int _geomLogs; // [local]
        private int _bigKeyLogs; // [local]
        private int _psbLogs; // [local]
        private int _tableDumps; // [local]
        private readonly List<(ulong Base, ulong Size, ulong Tick)> _vertexBases = new(); private int _laterDumps; private ulong _lastLaterDump; // [local]
        private int _geomAfterLogs; // [local]
        private int _slotDumps; // [local]
        private static readonly ulong SlotDumpHash = Convert.ToUInt64(Environment.GetEnvironmentVariable("SHARPEMU_SLOT_DUMP_HASH") ?? "0", 16); // [local]
        private static readonly int SlotDumpLimit = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SLOT_DUMP_LIMIT"), out var limit) ? limit : 200; // [local]
        private int _watchBinds; // [local]
        private readonly HashSet<ulong> _dirtyBounds = new(); private int _flipLogs; // [local]
        private static bool DegenerateBounds(uint[] t) // [local] words 7..12 = min xyz, max xyz
        {
            for (var i = 0; i < 3; i++)
            {
                float lo = BitConverter.UInt32BitsToSingle(t[7 + i]), hi = BitConverter.UInt32BitsToSingle(t[10 + i]);
                if (float.IsNaN(lo) || float.IsNaN(hi) || Math.Abs(lo) > 1e10f || Math.Abs(hi) > 1e10f || hi <= lo) return true;
            }
            return false;
        }
        private int _bvhListDumps;
        // [local] The BVH investigation probes (writer/bind logs, dumps, trace pages); off by default.
        internal static readonly bool BvhProbes = Environment.GetEnvironmentVariable("SHARPEMU_BVH_PROBES") == "1";
        private int _bvhRebuildSeenAt;
        private readonly List<(ulong Address, ulong Size)> _bvhScanTables = new();
        private ulong _bvhSmallBlob;
        private int _bvhBindLogs;
        private int _bvhHeaderLogs;
        private int _bvhBlockDumps;
        private void ProbeBvhWriter(PreparedStageBindings prepared, int index, BufferDescriptorWords descriptor, ulong requested)
        {
            if (!BvhProbes) return; // [local]
            var listProducer = prepared.Program.Hash is 0xF6F2D6298F2771AD or 0x35B85620669D35A4 or 0x4F1F0F92FE39EC8B or 0x4505F72A09CC1B12 or 0xF05BBF883D986FC9 or 0xCA99120EAF300BFD;
            if (prepared.Program.Hash is 0x5981037B07E391D5 or 0xAACC3636B355E928 or 0xF43B5FF6DD312D34 || _bvhWriterCount >= 40000 || (!listProducer && !InBvhBlob(descriptor.Address))) return;
            _bvhWriterCount++;
            var seq = _bvhSeq++;
            Console.Error.WriteLine($"[BVH_WRITER] seq={seq} hash=0x{prepared.Program.Hash:X16} stage={prepared.Program.Stage} buffer={index} words=[{descriptor.Word0:X8},{descriptor.Word1:X8},{descriptor.Word2:X8},{descriptor.Word3:X8}] address=0x{descriptor.Address:X} stride={descriptor.Stride} records={descriptor.RecordCount} requested=0x{requested:X} user_data=[{string.Join(",", prepared.Stage.Resources.UserData.Take(16).Select(w => w.ToString("X8")))}] info={prepared.Resources.Info.Buffers[index]}");
            foreach (var blob in _bvhBlobs)
                if (descriptor.Address >= blob.Base && descriptor.Address < blob.Base + blob.Size && blob.Base == _bvhSmallBlob && _bvhHdrDumps++ < 3000)
                { DumpBvhBuffer($"wr{seq:D4}_hdr", descriptor.WithAddress(blob.Base + 0x80), 32); break; }
            if (prepared.Program.Hash == 0x35B85620669D35A4 && (index == 1 || index == 2) && _bvhScanTables.Count < 64 && !_bvhScanTables.Contains((descriptor.Address, requested))) _bvhScanTables.Add((descriptor.Address, requested));
            if (prepared.Program.Hash == 0x35B85620669D35A4 && index == 1 && descriptor.RecordCount == 2064 && _bvhRebuildSeenAt == 0) _bvhRebuildSeenAt = _bvhWriterCount;
            var inWindow = _bvhRebuildSeenAt != 0 && _bvhWriterCount - _bvhRebuildSeenAt < 1500;
            if (((listProducer && inWindow) || _bvhWriterCount <= 300) && requested <= (256UL << 10) && _bvhListDumps++ < 6000) DumpBvhBuffer($"wr{seq:D4}_{prepared.Program.Hash:X16}_b{index}", descriptor, requested);
        }

        private readonly HashSet<ulong> _bvhDumped = new();
        private void DumpBvhBuffer(string kind, BufferDescriptorWords descriptor, ulong size)
        {
            var dir = Environment.GetEnvironmentVariable("LOCAL_DUMP_FAILED_SHADER");
            if (string.IsNullOrEmpty(dir) || descriptor.Address == 0 || size == 0) return;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"blob_{descriptor.Address:X}_{kind}");
            _bufferCache.DumpDeviceRange(descriptor.Address, size, path + ".gpu.bin");
            var guest = new byte[size];
            if (_guestMemory.TryRead(descriptor.Address, guest)) File.WriteAllBytes(path + ".guest.bin", guest);
            Console.Error.WriteLine($"[BVH_DUMP] {kind} address=0x{descriptor.Address:X} size=0x{size:X}");
        }

        private int _hdrPrePost; // [local]
        private int _hdrAfterDumps; // [local]
        private int _refitInDumps; // [local]
        private int _blobDumps; // [local]
        private bool _buildTracked; // [local]
        private bool _buildTraceCleared; // [local]
        private int _stageDumps; // [local]
        private int _sortDumps; // [local]
        private (ulong Address, ulong Size) _lastF6f2Nodes; // [local]
        private int _stageInDumps; // [local]
        private readonly HashSet<(ulong, bool)> _blobDumped = new(); // [local]
        private readonly List<(ulong Address, ulong Tick, int Next)> _hdrAges = new(); // [local]
        private static readonly ulong[] _hdrAgeSteps = { 1, 1024 };
        private void DumpHeaderAges()
        {
            for (var i = 0; i < _hdrAges.Count; i++)
            {
                var (address, tick, next) = _hdrAges[i];
                if (next >= _hdrAgeSteps.Length) continue;
                var age = _scheduler.CurrentTick - tick;
                if (age < _hdrAgeSteps[next]) continue;
                try { _bufferCache.DumpDeviceRange(address, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/hdrage_{address:X}_{_hdrAgeSteps[next]}_{_scheduler.CurrentTick}.gpu.bin"); _bufferCache.DumpGuestRange(address, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/hdrage_{address:X}_{_hdrAgeSteps[next]}_{_scheduler.CurrentTick}.guest.bin"); }
                catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] age dump failed: {e.Message}"); }
                _hdrAges[i] = (address, tick, next + 1);
            }
        }
        private void ProbeBvhRefitBuffer(ulong hash, int index, BufferDescriptorWords descriptor, ulong requested, ulong size, PreparedStageBindings prepared)
        {
            if (!BvhProbes) return; // [local]
            if (!_buildTracked && Environment.GetEnvironmentVariable("SHARPEMU_TRACK_BUILD") == "1") // [local] follow the roots of one small and one large dynamic BLAS from the start (addresses stable across runs)
            {
                _buildTracked = true;
                lock (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages)
                {
                    for (var page = 0x1AE300000UL >> 12; page < 0x1AED00000UL >> 12; page++) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages.Add(page); // [local] the BVH build scratch region
                    for (var page = 0x206A00000UL >> 12; page < 0x208400000UL >> 12; page++) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages.Add(page); // [local] the dynamic BLAS blobs
                }
                foreach (var b in new ulong[] { 0x207191A00, 0x2081BC000, 0x206C3BC00 })
                {
                    _hdrAges.Add((b + 0x100, 0, _hdrAgeSteps.Length));
                    _hdrAges.Add((b, 0, _hdrAgeSteps.Length));
                    lock (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages.Add(b >> 12);
                }
            }
            if (_buildTracked && !_buildTraceCleared && _scheduler.CurrentTick > 23000) // [local] stop the wide trace once the build is over
            {
                _buildTraceCleared = true;
                lock (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages.Clear();
            }
            if (_hdrAges.Count != 0 && index == 0) DumpHeaderAges(); // [local]
            if (Environment.GetEnvironmentVariable("SHARPEMU_TRACK_BUILD") == "1") // [local] build stages of three dynamic BLASes: full blob after the root init (260F, slot 2) and after the root writer (F6F2, slot 3); F6F2 inputs before its dispatch
            {
                var tick = _scheduler.CurrentTick;
                if (hash == 0x220AB61E9C35767C && index is 4 or 3 or 2 or 1 or 0 && _sortDumps < 160) // [local] the sort stage's output before and after each dispatch
                {
                    _sortDumps++;
                    var tag = hash == 0x220AB61E9C35767C ? "220A" : "D5D0"; var outAddress = descriptor.Address; var outSize = Math.Min(size, 0x100000UL);
                    try { _bufferCache.DumpDeviceRange(outAddress, outSize, $"C:/Users/danyy/AppData/Local/Temp/gta/sort_{tag}_s{index}_pre_{outAddress:X}_{outSize:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] sort pre dump failed: {e.Message}"); }
                    Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(outAddress, outSize, $"C:/Users/danyy/AppData/Local/Temp/gta/sort_{tag}_s{index}_post_{outAddress:X}_{outSize:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] sort post dump failed: {e.Message}"); } });
                }
                if (hash == 0x260F2884A4A5E7C0 && index == 2 && _stageDumps < 24)
                {
                    _stageDumps++;
                    var blobBase = descriptor.Address - 0x100; var blobSize = Math.Min(size + 0x100, 0x400000UL);
                    Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(blobBase, blobSize, $"C:/Users/danyy/AppData/Local/Temp/gta/blobstage_260F_{blobBase:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] stage dump failed: {e.Message}"); } });
                }
                if (hash == 0xF6F2D6298F2771AD && index == 3) _lastF6f2Nodes = (descriptor.Address, size);
                if (hash == 0xF6F2D6298F2771AD && index == 5 && _stageDumps < 24 && _lastF6f2Nodes.Address == descriptor.Address - 0x60 + 0x100)
                {
                    _stageDumps++;
                    var blobBase = descriptor.Address - 0x60; var blobSize = Math.Min(_lastF6f2Nodes.Size + 0x100, 0x400000UL);
                    Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(blobBase, blobSize, $"C:/Users/danyy/AppData/Local/Temp/gta/blobstage_F6F2_{blobBase:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] stage dump failed: {e.Message}"); } });
                }
                if ((hash == 0xF6F2D6298F2771AD && index != 3 || hash == 0x260F2884A4A5E7C0) && _stageInDumps < 120)
                {
                    _stageInDumps++;
                    try { _bufferCache.DumpDeviceRange(descriptor.Address, Math.Min(size, 0x400000UL), $"C:/Users/danyy/AppData/Local/Temp/gta/{(hash == 0x260F2884A4A5E7C0 ? "s260Fin" : "f6f2in")}_{index}_{descriptor.Address:X}_{size:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] input dump failed: {e.Message}"); }
                }
            }
            if (hash == 0x5981037B07E391D5 && index == 1 && (_scheduler.CurrentTick is > 19300 and < 19600 || _scheduler.CurrentTick is > 25000 and < 25300) && _blobDumps < 24 && !_blobDumped.Contains((descriptor.Address, _scheduler.CurrentTick > 25000)))
            {
                _blobDumps++; _blobDumped.Add((descriptor.Address, _scheduler.CurrentTick > 25000));
                var hdrAddress = descriptor.Address; var tick = _scheduler.CurrentTick; var blobSize = Math.Min(size, 0x400000UL);
                Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(hdrAddress, blobSize, $"C:/Users/danyy/AppData/Local/Temp/gta/blob5981_{hdrAddress:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] blob dump failed: {e.Message}"); } });
            }
            if (hash == 0x5981037B07E391D5 && index == 1 && _scheduler.CurrentTick > 18000 && _hdrAges.Count < 200 && !_hdrAges.Any(h => h.Address == descriptor.Address))
            {
                _hdrAges.Add((descriptor.Address, _scheduler.CurrentTick, _hdrAgeSteps.Length)); // [local] track the per-frame refit's node buffers too (no age dumps, only after-dispatch dumps)
                lock (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages.Add(descriptor.Address >> 12);
                foreach (var node in new ulong[] { 4, 0xE88, 0x6C4 })
                {
                    var nodeAddress = descriptor.Address + node * 64;
                    _hdrAges.Add((nodeAddress, _scheduler.CurrentTick, _hdrAgeSteps.Length));
                    lock (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages.Add(nodeAddress >> 12);
                }
            }
            ProbeBvhWriter(prepared, index, descriptor, requested);
            if (hash == 0x6A53456E7EF5D1B0 && index == 2 && _tableDumps < 3 && Environment.GetEnvironmentVariable("SHARPEMU_LOG_GLOBAL_STORES") == "1") // [local] the store V# table
            {
                _tableDumps++;
                try { _bufferCache.DumpGuestRange(descriptor.Address, Math.Min(requested, 0x200000UL), $"C:/Users/danyy/AppData/Local/Temp/gta/storetable_{descriptor.Address:X}_{_scheduler.CurrentTick}.guest.bin"); } catch (Exception e) { Console.Error.WriteLine($"[STORETABLE] {e.Message}"); }
            }
            if ((hash == 0x10CD966BBCFB2BAA || hash == 0x6A53456E7EF5D1B0) && _psbLogs < 400 && Environment.GetEnvironmentVariable("SHARPEMU_LOG_GLOBAL_STORES") == "1") { _psbLogs++; Console.Error.WriteLine($"[PSB_DISPATCH] tick={_scheduler.CurrentTick} hash=0x{hash:X16} slot={index} addr=0x{descriptor.Address:X} size=0x{requested:X}"); } // [local]
            if (hash == 0x5981037B07E391D5 && _vertexBases.Count != 0 && _laterDumps < 3) // [local] the dynamic vertex buffers long after the build
            {
                var now = _scheduler.CurrentTick;
                if (_vertexBases.All(v => now > v.Tick + 6000) && (_laterDumps == 0 || now > _lastLaterDump + 8000))
                {
                    _laterDumps++; _lastLaterDump = now;
                    foreach (var (vb, vsize, _) in _vertexBases.Take(8))
                    {
                        var path = $"C:/Users/danyy/AppData/Local/Temp/gta/vtxlater_{vb:X}_{now}";
                        try { _bufferCache.DumpDeviceRange(vb, Math.Min(vsize, 0x1000UL), path + ".gpu.bin"); _bufferCache.DumpGuestRange(vb, Math.Min(vsize, 0x1000UL), path + ".guest.bin"); } catch (Exception e) { Console.Error.WriteLine($"[VTXLATER] {e.Message}"); }
                    }
                }
            }
            if (hash == 0xF1D19431555095C0 && index == 0 && _geomLogs < 300 && Environment.GetEnvironmentVariable("SHARPEMU_GEOM_PROBE") == "1") // [local] geometry table at bind: guest bytes and CPU-dirty state
            {
                _geomLogs++;
                Span<byte> geomHead = stackalloc byte[16];
                var guestOk = _guestMemory.TryRead(descriptor.Address, geomHead);
                Console.Error.WriteLine($"[GEOM] tick={_scheduler.CurrentTick} table=0x{descriptor.Address:X} size=0x{requested:X} cpuDirty={_bufferCache.HasCpuDirtyPages(descriptor.Address, requested)} guestHead={(guestOk ? Convert.ToHexString(geomHead) : "unreadable")}");
                if (requested >= 0x500)
                {
                    Span<byte> entry = stackalloc byte[0x78];
                    for (var g = 0; g < (int)(requested / 0x78) && g < 3; g++)
                    {
                        if (!_guestMemory.TryRead(descriptor.Address + (ulong)g * 0x78, entry)) break;
                        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(entry);
                        var vbase = words[2] | ((ulong)(words[3] & 0xFFFF) << 32); var vstride = (words[3] >> 16) & 0x3FFF; var vcount = words[4];
                        Console.Error.WriteLine($"[GEOM_ENTRY] tick={_scheduler.CurrentTick} table=0x{descriptor.Address:X} g={g} words=[{string.Join(",", words.ToArray().Take(12).Select(w => w.ToString("X8")))}] vbase=0x{vbase:X} stride={vstride} records={vcount}");
                        if (vbase >= 0x206000000 && vbase < 0x209000000 && vstride != 0) lock (_vertexBases) { if (_vertexBases.Count < 64) _vertexBases.Add((vbase, (ulong)vstride * vcount, _scheduler.CurrentTick)); }
                        var dumpBase = vbase; var dumpTick = _scheduler.CurrentTick; var gi = g;
                        if (vbase != 0 && vstride != 0)
                        {
                            try { _bufferCache.DumpGuestRange(dumpBase, 0x100, $"C:/Users/danyy/AppData/Local/Temp/gta/vtx_{dumpBase:X}_{dumpTick}_{gi}.guest.bin"); } catch { }
                            try { _bufferCache.DumpDeviceRange(dumpBase, 0x100, $"C:/Users/danyy/AppData/Local/Temp/gta/vtx_{dumpBase:X}_{dumpTick}_{gi}.gpupre.bin"); } catch { }
                            Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(dumpBase, 0x100, $"C:/Users/danyy/AppData/Local/Temp/gta/vtx_{dumpBase:X}_{dumpTick}_{gi}.gpu.bin"); } catch { } });
                        }
                    }
                }
            }
            if (SlotDumpHash != 0 && hash == SlotDumpHash && _slotDumps < SlotDumpLimit) // [local] every slot of one shader before and after its dispatch
            {
                _slotDumps++;
                var tick = _scheduler.CurrentTick; var slotAddress = descriptor.Address; var slotSize = Math.Min(requested, 0x400000UL); var slot = index;
                try { _bufferCache.DumpDeviceRange(slotAddress, slotSize, $"C:/Users/danyy/AppData/Local/Temp/gta/slot_{hash:X16}_s{slot}_pre_{slotAddress:X}_{slotSize:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[SLOT_DUMP] {e.Message}"); }
                Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(slotAddress, slotSize, $"C:/Users/danyy/AppData/Local/Temp/gta/slot_{hash:X16}_s{slot}_post_{slotAddress:X}_{slotSize:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[SLOT_DUMP] {e.Message}"); } });
            }
            if (_dirtyBounds.Count != 0 && _flipLogs < 3000) // [local] which binding is current when a written bounds box stops being GPU-dirty
            {
                List<ulong>? cleaned = null;
                foreach (var watched in _dirtyBounds)
                    if (!_bufferCache.HasGpuDirtyBytes(watched, 24)) (cleaned ??= new()).Add(watched);
                if (cleaned is not null)
                    foreach (var watched in cleaned)
                    {
                        _dirtyBounds.Remove(watched); _flipLogs++;
                        Console.Error.WriteLine($"[BOUNDS_FLIP] tick={_scheduler.CurrentTick} bounds=0x{watched:X} clean at hash=0x{hash:X16} slot={index} addr=0x{descriptor.Address:X} size=0x{requested:X}");
                    }
            }
            if (hash == 0xF1D19431555095C0 && index == 1 && requested <= 0x40 && Environment.GetEnvironmentVariable("SHARPEMU_KEY_PROBE") == "1") // [local] remember the bounds boxes the reduction writes
            {
                lock (WatchedBounds) if (WatchedBounds.Count < 4096) WatchedBounds.Add(descriptor.Address & ~0x1FUL);
                lock (Gpu.Buffers.GuestBufferCache.WatchedModified) if (Gpu.Buffers.GuestBufferCache.WatchedModified.Count < 4096) Gpu.Buffers.GuestBufferCache.WatchedModified.Add(descriptor.Address);
                if (_bufferCache.HasGpuDirtyBytes(descriptor.Address, 24)) _dirtyBounds.Add(descriptor.Address);
                if (_watchBinds++ < 400) Console.Error.WriteLine($"[BOUNDS_WRITE] tick={_scheduler.CurrentTick} addr=0x{descriptor.Address:X} size=0x{requested:X} dirtyBytes={_bufferCache.HasGpuDirtyBytes(descriptor.Address, 24)}");
            }
            if (hash == 0x4481C2A89BEA7646 && index == 2 && requested >= 0x70000 && _bigKeyLogs++ < 3000 && Environment.GetEnvironmentVariable("SHARPEMU_KEY_PROBE") == "1") Console.Error.WriteLine($"[BIGKEY] tick={_scheduler.CurrentTick} out=0x{descriptor.Address:X} size=0x{requested:X}"); // [local]
            if (hash == 0x4481C2A89BEA7646 && index == 2 && requested >= 0x70000 && _keyDumps < 24 && Environment.GetEnvironmentVariable("SHARPEMU_KEY_PROBE") == "1") // [local] Morton key writer: user data, its bounds pointer (s4:s5) and both copies of the bounds
            {
                _keyDumps++;
                var regs = prepared.Layout.UserDataRegisters; var data = prepared.ShaderData;
                uint Reg(uint r) { for (var i = 0; i < regs.Count && i < data.Length; i++) if (regs[i] == r) return data[i]; return 0xDEADBEEF; }
                var bounds = ((ulong)Reg(5) << 32) | Reg(4);
                Console.Error.WriteLine($"[KEYS] dirtyBounds={_bufferCache.HasGpuDirtyBytes(bounds, 24)} tick={_scheduler.CurrentTick} out=0x{descriptor.Address:X} size=0x{requested:X} s4:s5=0x{bounds:X} s6=0x{Reg(6):X} s7=0x{Reg(7):X} s8=0x{Reg(8):X} shaderData=[{string.Join(",", data.Take(24).Select(w => w.ToString("X")))}]");
                var tick = _scheduler.CurrentTick;
                try { _bufferCache.DumpGuestRange(bounds, 0x100, $"C:/Users/danyy/AppData/Local/Temp/gta/keybounds_{bounds:X}_{tick}.guest.bin"); } catch (Exception e) { Console.Error.WriteLine($"[KEYS] guest dump failed: {e.Message}"); }
                try { _bufferCache.DumpDeviceRange(bounds, 0x100, $"C:/Users/danyy/AppData/Local/Temp/gta/keybounds_{bounds:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[KEYS] gpu dump failed: {e.Message}"); }
                var postBounds = bounds; var postTick = tick;
                Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(postBounds, 0x100, $"C:/Users/danyy/AppData/Local/Temp/gta/keybounds_{postBounds:X}_{postTick}.post.bin"); } catch (Exception e) { Console.Error.WriteLine($"[KEYS] post dump failed: {e.Message}"); } });
            }
            if (hash == 0x5981037B07E391D5 && index == 0 && !InBvhBlob(descriptor.Address) && _bvhBlobs.Count < 32) _bvhBlobs.Add((descriptor.Address, Math.Max(requested, 4096)));
            if (hash == 0x5981037B07E391D5 && index <= 2 && _bvhDumped.Count < 40)
            {
                var key = descriptor.Address ^ ((ulong)index << 56) ^ ((ulong)descriptor.RecordCount << 40);
                if (_bvhDumped.Add(key)) DumpBvhBuffer(index switch { 0 => "list", 1 => "nodes", _ => "pairs" } + $"_{descriptor.RecordCount}", descriptor, Math.Min(size, 16UL << 20));
            }
            if (hash == 0x56D6851BA6028DF7 && index == 1 && _bvhHeaderLogs++ < 6000)
            {
                var ud = prepared.Stage.Resources.UserData;
                var block = ud.Length > 5 ? ((ulong)ud[5] << 32 | ud[4]) : 0;
                var guestWords = new byte[128]; var g96 = 0u; var g104 = 0u;
                if (block != 0 && _guestMemory.TryRead(block, guestWords)) { g96 = BitConverter.ToUInt32(guestWords, 96); g104 = BitConverter.ToUInt32(guestWords, 104); }
                if (_scheduler.CurrentTick > 18000 && Gpu.Buffers.GuestBufferCache.BvhHeaderWrites.Count < 80) { Gpu.Buffers.GuestBufferCache.BvhHeaderWrites.Enqueue((descriptor.Address, _scheduler.CurrentTick)); lock (Gpu.Buffers.GuestBufferCache.BvhWatchPages) Gpu.Buffers.GuestBufferCache.BvhWatchPages.Add(descriptor.Address >> 12); }
                if (_scheduler.CurrentTick > 18000 && _hdrPrePost < 120)
                {
                    _hdrPrePost++;
                    var hdrAddress = descriptor.Address; var hdrTick = _scheduler.CurrentTick; var n0 = g96; var n1 = g104;
                    lock (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.ExtraPages.Add(hdrAddress >> 12);
                    SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Trace(hdrAddress, 256, $"bvh-header-write submission_tick={hdrTick} nodes=0x{n0:X} pairs=0x{n1:X}");
                    if (_hdrAges.Count < 96) _hdrAges.Add((hdrAddress, hdrTick, 0));
                    try { _bufferCache.DumpDeviceRange(hdrAddress, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/hdrpre_{hdrAddress:X}_{hdrTick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] pre dump failed: {e.Message}"); }
                    Gpu.Buffers.GuestBufferCache.PostDispatchHook = () =>
                    {
                        try
                        {
                            _bufferCache.DumpDeviceRange(hdrAddress, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/hdrpost_{hdrAddress:X}_{hdrTick}.gpu.bin");
                            if (Gpu.Buffers.GuestBufferCache.ForceBvhCounts && n0 != 0) { _bufferCache.PatchDeviceDwords(hdrAddress + 0x50, n0, hdrAddress + 0x58, n1); Console.Error.WriteLine($"[BVH_HDRWRITE] forced counts at 0x{hdrAddress:X}: nodes=0x{n0:X} pairs=0x{n1:X}"); }
                        }
                        catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] post hook failed: {e.Message}"); }
                    };
                }
                var table = prepared.Stage.Resources.FlattenedResourceTable;
                Console.Error.WriteLine($"[BVH_HDRWRITE] address=0x{descriptor.Address:X} tick={_scheduler.CurrentTick} block=0x{block:X} guest+96=0x{g96:X} guest+104=0x{g104:X} table_words={table.Length} table=[{string.Join(",", table.Take(160).Select(w => w.ToString("X")))}]");
                if (block != 0 && _bvhBlockDumps < 8)
                {
                    _bvhBlockDumps++;
                    try { _bufferCache.DumpDeviceRange(block, 128, $"C:/Users/danyy/AppData/Local/Temp/gta/hdrblock_{_scheduler.CurrentTick}_{descriptor.Address:X}.gpu.bin"); _bufferCache.DumpGuestRange(block, 128, $"C:/Users/danyy/AppData/Local/Temp/gta/hdrblock_{_scheduler.CurrentTick}_{descriptor.Address:X}.guest.bin"); }
                    catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] dump failed: {e.Message}"); }
                }
            }
            if (Gpu.Buffers.GuestBufferCache.BvhWatchBase != 0 && descriptor.Address + (ulong)descriptor.RecordCount * Math.Max(descriptor.Stride, 1u) > Gpu.Buffers.GuestBufferCache.BvhWatchBase && descriptor.Address < Gpu.Buffers.GuestBufferCache.BvhWatchBase + 0x10000 && _bvhBindLogs++ < 2000)
                Console.Error.WriteLine($"[BVH_BIND] hash=0x{hash:X16} buffer={index} address=0x{descriptor.Address:X} stride={descriptor.Stride} records={descriptor.RecordCount} tick={_scheduler.CurrentTick}" + (hash == 0x56D6851BA6028DF7 ? $" user_data=[{string.Join(",", prepared.Stage.Resources.UserData.Select(word => $"{word:X8}"))}]" : ""));
            if (hash == 0x5981037B07E391D5 && index == 1 && descriptor.RecordCount < 5000) { _bvhSmallBlob = descriptor.Address; if (Gpu.Buffers.GuestBufferCache.BvhWatchBase == 0) Gpu.Buffers.GuestBufferCache.BvhWatchBase = descriptor.Address; }
            if (hash == 0x5981037B07E391D5 && index == 0 && descriptor.RecordCount < 1000 && _bvhListSeq < 30)
            {
                var seq = _bvhSeq++;
                _bvhListSeq++;
                Console.Error.WriteLine($"[BVH_WRITER] seq={seq} hash=0x{hash:X16} REFIT list address=0x{descriptor.Address:X} records={descriptor.RecordCount}");
                DumpBvhBuffer($"wr{seq:D4}_refit_list", descriptor, requested);
                if (_bvhSmallBlob != 0)
                {
                    foreach (var table in _bvhScanTables) DumpBvhBuffer($"wr{seq:D4}_scan_{table.Address:X}", descriptor.WithAddress(table.Address), table.Size);
                    DumpBvhBuffer($"wr{seq:D4}_refit_hdr", descriptor.WithAddress(_bvhSmallBlob + 0x80), 32);
                    DumpBvhBuffer($"wr{seq:D4}_refit_nodes", descriptor.WithAddress(_bvhSmallBlob), 4132 * 64);
                }
            }
            if (hash != 0x5981037B07E391D5 || _bvhProbeCount >= 60) return;
            _bvhProbeCount++;
            if (descriptor.Stride == 8 && !InBvhBlob(descriptor.Address) && _bvhBlobs.Count < 32) _bvhBlobs.Add((descriptor.Address, requested));
            if (descriptor.Stride == 64 && (descriptor.Word3 & 0x10000000) != 0)
            {
                var node = new uint[16];
                var nodeBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(node.AsSpan());
                var nodes = new System.Text.StringBuilder();
                foreach (var nodeIndex in BvhProbeNodes)
                {
                    if (nodeIndex >= descriptor.RecordCount) continue;
                    var ok = _guestMemory.TryRead(descriptor.Address + (ulong)nodeIndex * 64, nodeBytes);
                    nodes.Append($" node[0x{nodeIndex:X}]=[{(ok ? string.Join(",", node.Select(w => w.ToString("X8"))) : "unreadable")}]");
                }
                Console.Error.WriteLine($"[BVH_NODES] address=0x{descriptor.Address:X} records={descriptor.RecordCount}{nodes}");
            }
            var head = new uint[8];
            var headBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(head.AsSpan());
            var headOk = descriptor.Address != 0 && _guestMemory.TryRead(descriptor.Address, headBytes);
            var headerDirty = descriptor.Address != 0 && (_bufferCache.HasGpuDirtyBytes(descriptor.Address, 128) || _bufferCache.HasGpuDirtyPages(descriptor.Address, 128));
            var cpuDirty = descriptor.Address != 0 && _bufferCache.HasCpuDirtyPages(descriptor.Address, 256);
            var text = $"[BVH_PROBE] header_dirty={headerDirty} cpu_dirty={cpuDirty} buffer={index} words=[{descriptor.Word0:X8},{descriptor.Word1:X8},{descriptor.Word2:X8},{descriptor.Word3:X8}] address=0x{descriptor.Address:X} stride={descriptor.Stride} records={descriptor.RecordCount} requested=0x{requested:X} bound=0x{size:X} head=[{(headOk ? string.Join(",", head.Select(w => w.ToString("X8"))) : "unreadable")}] user_data=[{string.Join(",", prepared.Stage.Resources.UserData.Take(8).Select(w => w.ToString("X8")))}]";
            if (descriptor.Stride == 8 && size >= 8)
            {
                var count = (int)Math.Min(size / 8, 1 << 20);
                var pairs = new uint[count * 2];
                if (_guestMemory.TryRead(descriptor.Address, System.Runtime.InteropServices.MemoryMarshal.AsBytes(pairs.AsSpan())))
                {
                    int negatives = 0, firstNegative = -1, zeros = 0;
                    for (var pair = 0; pair < count; pair++)
                    {
                        var second = (int)pairs[pair * 2 + 1];
                        if (second < 0) { negatives++; if (firstNegative < 0) firstNegative = pair; }
                        if (pairs[pair * 2] == 0 && pairs[pair * 2 + 1] == 0) zeros++;
                    }
                    text += $" pairs={count} negatives={negatives} first_negative={firstNegative} zero_pairs={zeros}";
                }
            }
            Console.Error.WriteLine(text);
        }

        // Uploads every mapped range into the cache before a device-address draw; the fault pass follows.
        public void PrepareDeviceAddresses()
        {
            var preparation = RequirePreparation();
            ulong vertexProgramHash = 0, pixelProgramHash = 0, computeProgramHash = 0;
            if (BufferUploadProfile.Enabled)
            {
                foreach (var stage in preparation.Stages)
                {
                    if (!stage.Program.UsesDeviceAddresses) continue;
                    switch (stage.Program.Stage)
                    {
                        case ShaderStageKind.Vertex: vertexProgramHash = stage.Program.Hash; break;
                        case ShaderStageKind.Pixel: pixelProgramHash = stage.Program.Hash; break;
                        case ShaderStageKind.Compute: computeProgramHash = stage.Program.Hash; break;
                    }
                }
            }
            using var profileScope = BufferUploadProfile.BeginSweep(vertexProgramHash, pixelProgramHash, computeProgramHash);
            var memory = GuestGpuMemoryHook.Current ?? throw SubmissionScheduler.Fatal("A device-address program needs the guest GPU memory registry.");
            var spans = DeviceAddressSpans(memory);
            var traceAddress = GuestGpuMemoryHook.TraceAddress;
            if (traceAddress != 0)
            {
                foreach (var stage in preparation.Stages)
                {
                    if (!stage.Program.UsesDeviceAddresses) continue;
                    GuestGpuMemoryHook.Trace(traceAddress, 1,
                        $"device-address-program submission_tick={_scheduler.CurrentTick} stage={stage.Program.Stage} hash=0x{stage.Program.Hash:X16} shader=0x{stage.Stage.ShaderBase:X16} ranges={stage.Stage.Resources.DeviceAddressRanges.Length}");
                    foreach (var range in stage.Stage.Resources.DeviceAddressRanges)
                        GuestGpuMemoryHook.Trace(traceAddress, 1,
                            $"device-address-range submission_tick={_scheduler.CurrentTick} hash=0x{stage.Program.Hash:X16} handle={range.Handle} base=0x{range.Base:X16} size=0x{range.Size:X} planned={range.Planned} written={range.Written}");
                }
            }
            if (traceAddress != 0 && !memory.Covers(traceAddress, 1))
                GuestGpuMemoryHook.Trace(traceAddress, 1,
                    $"device-address-mapping-check readable={_guestMemory.CanRead(traceAddress, 1)} backed={_guestBacking.IsBackedView(traceAddress)}");
            _bufferCache.PrepareBda(spans, _bdaSpanMapping);
        }

        private List<GuestSpan>? _bdaSpans;
        private GuestGpuMemory? _bdaSpanMemory;
        private long _bdaSpanVersion = -1;
        private ulong _bdaSpanMapping;

        private List<GuestSpan> DeviceAddressSpans(GuestGpuMemory memory)
        {
            var version = memory.SpanVersion;
            if (_bdaSpans is { } cached && ReferenceEquals(_bdaSpanMemory, memory) && _bdaSpanVersion == version)
            {
                return cached;
            }

            var spans = new List<GuestSpan>();
            memory.ForEachSpan((address, size) => spans.Add(new GuestSpan(address, size)));
            _bdaSpans = spans;
            _bdaSpanMemory = memory;
            _bdaSpanVersion = version;
            _bdaSpanMapping = GuestBufferCache.MappingKey(spans);
            return spans;
        }

        public void BindResources(IPreparedBindings prepared)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorPreparation);
            var stage = (PreparedStageBindings)prepared;
            BindBuffers(stage);
            ObtainDeviceAddressRanges(stage);
            BindImages(stage);
            _preparedTextures.Add(stage.Textures);
        }

        // Textures bound for the draw being prepared; cleared when its preparation closes.
        private readonly List<TextureResource[]> _preparedTextures = new();

        bool IRenderHost.SamplesDepthAttachment(in DepthAttachmentState depth)
        {
            var image = _imageCache.GetImage(depth.Image);
            var attachmentView = depth.Target.Target.Request.View;
            foreach (var textures in _preparedTextures)
            {
                foreach (var texture in textures)
                {
                    if (!texture.IsHostMovie && ReferenceEquals(texture.CachedImage, image) && ViewsOverlap(texture.Request.View, attachmentView))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private BufferView NullStorageBuffer() => new(_bufferCache.GetBuffer(GuestBufferCache.NullBufferId).Handle, 0, NullStorageBufferBytes);

        // A storage buffer view on the cache buffer, aligned down with the adjustment carried in the memory offsets.
        private BufferView BindStorageBuffer(
            in BufferDescriptorWords descriptor,
            BufferResource resource,
            ShaderProgramInfo program,
            int slot,
            ResourceSlotIdentifier bufferIdentifier,
            out uint memoryOffset)
        {
            memoryOffset = 0;
            var address = descriptor.Address;
            var requested = descriptor.Footprint() ?? throw SubmissionScheduler.Fatal($"A storage buffer descriptor footprint overflows: buffer={slot} hash=0x{program.Hash:X16}.");
            if (address == 0 || requested == 0)
            {
                return NullStorageBuffer();
            }

            var size = ClampMappedSize(address, requested);
            var alignment = _minStorageBufferOffsetAlignment;
            var maxRange = _deviceInfo.MaxStorageBufferRange;
            if (alignment == 0 || size > maxRange)
            {
                throw SubmissionScheduler.Fatal($"A storage buffer range or the device alignment is unsupported: buffer={slot} size=0x{size:X} alignment={alignment} hash=0x{program.Hash:X16}.");
            }

            if (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TracesExtra(address, size)) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Trace(address, size, $"bind hash=0x{program.Hash:X16} slot={slot} written={resource.Written} formatted={resource.Formatted} submission_tick={_scheduler.CurrentTick}"); // [local]
            if (program.Hash == 0x743E8AE5A36889D2 && !resource.Written && _hdrAges.Count != 0 && _refitInDumps < 600 && _scheduler.CurrentTick < 20000)
            {
                _refitInDumps++;
                try { _bufferCache.DumpDeviceRange(address, Math.Min(size, 0x4000UL), $"C:/Users/danyy/AppData/Local/Temp/gta/refitin_{_scheduler.CurrentTick}_{slot}_{address:X}_{size:X}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] refit input dump failed: {e.Message}"); }
            }
            if (resource.Written && _hdrAges.Count != 0 && program.Hash != 0x56D6851BA6028DF7 && _hdrAfterDumps < 3000)
            {
                foreach (var (hdrAddress, _, _) in _hdrAges)
                {
                    if (hdrAddress < address || hdrAddress >= address + size) continue;
                    _hdrAfterDumps++;
                    var hash = program.Hash; var tick = _scheduler.CurrentTick; var slotIndex = slot;
                    Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(hdrAddress, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/hdrafter_{hdrAddress:X}_{tick}_{hash:X16}_{slotIndex}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] after dump failed: {e.Message}"); } });
                }
            }
            var (buffer, offset) = _bufferCache.ObtainBuffer(address, size, resource.Written, isTexelBuffer: resource.Formatted, bufferIdentifier);
            if (program.Hash == 0xF1D19431555095C0 && slot == 0 && _geomAfterLogs < 300 && Environment.GetEnvironmentVariable("SHARPEMU_GEOM_PROBE") == "1") // [local] after the upload
            {
                _geomAfterLogs++;
                Console.Error.WriteLine($"[GEOM_AFTER] tick={_scheduler.CurrentTick} table=0x{address:X} size=0x{size:X} cpuDirtyAfterObtain={_bufferCache.HasCpuDirtyPages(address, size)} buffer=0x{buffer.Handle.Handle:X} offset=0x{offset:X}");
            }
            var alignedOffset = offset - offset % alignment;
            var adjustment = offset - alignedOffset;
            if (adjustment % sizeof(uint) != 0 || adjustment >= MaxMemoryOffsetAdjustment || size > maxRange - adjustment)
            {
                throw SubmissionScheduler.Fatal($"A storage buffer offset adjustment is unsupported: buffer={slot} adjustment={adjustment} hash=0x{program.Hash:X16}.");
            }

            memoryOffset = (uint)adjustment;
            if (resource.Formatted && resource.Written)
            {
                _imageCache.InvalidateMemoryFromGpu(address, size);
            }

            return new BufferView(buffer.Handle, alignedOffset, size + adjustment);
        }

        private BufferView UploadDwords(uint[] data, string label, ShaderProgramInfo program)
        {
            if (data.Length == 0)
            {
                throw SubmissionScheduler.Fatal($"The {label} upload is empty: hash=0x{program.Hash:X16}.");
            }

            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(data);
            var binding = UploadTransient(bytes, Math.Max(TransientDataAlignment, (uint)_minStorageBufferOffsetAlignment));
            return new BufferView(new VkBuffer(binding.Handle), binding.Offset, (ulong)bytes.Length);
        }

        private void BindBuffers(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var layout = prepared.Layout;
            var info = prepared.Resources.Info;
            var snapshot = prepared.Stage.Resources;
            var shaderData = prepared.ShaderData;
            if (prepared.BufferSources.Length != info.Buffers.Count || shaderData.Length != layout.ShaderDataDwordCount)
            {
                throw SubmissionScheduler.Fatal($"The prepared bindings are stale: hash=0x{program.Hash:X16}.");
            }

            // Reset only packed buffer offsets; dispatch limits follow them in shader data.
            Array.Clear(shaderData, (int)layout.MemoryOffsetDword, (int)((layout.MemoryOffsetCount + 3) / 4));
            var views = prepared.Descriptors.Buffers;
            if (views.Length != info.Buffers.Count)
            {
                Scratch.Buffers.Return(views);
                views = Scratch.Buffers.Rent(info.Buffers.Count);
                prepared.Descriptors.Buffers = views;
            }
            for (var index = 0; index < views.Length; index++)
            {
                var (descriptor, bufferIdentifier) = prepared.BufferSources[index];
                views[index] = BindStorageBuffer(in descriptor, info.Buffers[index], program, index, bufferIdentifier, out var memoryOffset);
                var dword = layout.MemoryOffsetDword + (uint)index / 4;
                var shift = ((uint)index % 4) * 8;
                shaderData[dword] |= memoryOffset << (int)shift;
            }

            prepared.Descriptors.Buffers = views;
            if (layout.Find(DescriptorBindingKind.FlattenedResourceTable) is not null)
            {
                prepared.Descriptors.FlattenedTable = UploadDwords(snapshot.FlattenedResourceTable, "flattened resource table", program);
                if (program.Hash == 0x4481C2A89BEA7646 && snapshot.FlattenedResourceTable.Length >= 13 && Environment.GetEnvironmentVariable("SHARPEMU_KEY_PROBE") == "1" && DegenerateBounds(snapshot.FlattenedResourceTable) && _keyTableLogs < 5000) // [local] the bounds words 0x4481 actually receives
                {
                    _keyTableLogs++;
                    Console.Error.WriteLine($"[KEYTABLE] tick={_scheduler.CurrentTick} words=[{string.Join(",", snapshot.FlattenedResourceTable.Select(w => w.ToString("X8")))}] floats=[{string.Join(",", snapshot.FlattenedResourceTable.Select(w => BitConverter.UInt32BitsToSingle(w).ToString("G4")))}]");
                }
            }

            if (layout.Find(DescriptorBindingKind.ShaderData) is not null)
            {
                prepared.Descriptors.ShaderData = UploadDwords(shaderData, "shader data", program);
            }
        }

        // Device-address reads need persistent page-table entries before the shader runs.
        private void ObtainDeviceAddressRanges(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            foreach (var range in prepared.Stage.Resources.DeviceAddressRanges)
            {
                if (!range.Planned)
                {
                    if (!range.Written) continue;
                    throw SubmissionScheduler.Fatal($"A written device-address range cannot be planned: handle={range.Handle} hash=0x{program.Hash:X16}.");
                }

                if (range.Size == 0)
                {
                    continue;
                }

                if (range.Base >= PageOwnerTable.AddressSpaceSize || range.Size > PageOwnerTable.AddressSpaceSize - range.Base)
                {
                    throw SubmissionScheduler.Fatal($"A device-address range is outside the cache: handle={range.Handle} base=0x{range.Base:X16} size=0x{range.Size:X} hash=0x{program.Hash:X16}.");
                }

                // Unmapped read-only pointers resolve to zero through the page table.
                if (!range.Written && !_guestMemory.CanRead(range.Base, 1))
                {
                    continue;
                }

                var size = ClampMappedSize(range.Base, range.Size);
                if (range.Written)
                {
                    if (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TracesExtra(range.Base, size)) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Trace(range.Base, size, $"bda-written-range hash=0x{program.Hash:X16} submission_tick={_scheduler.CurrentTick}"); // [local]
                    _ = _bufferCache.ObtainBuffer(range.Base, size, isWritten: true);
                }
                else
                {
                    // Stream buffers do not populate the device-address page table.
                    _ = _bufferCache.FindBuffer(range.Base, size);
                    _bufferCache.SynchronizeBuffersInRange(range.Base, size);
                }
            }
        }

        // Views for every image after the targets are bound; a stale image is found again first.
        private void BindImages(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var info = prepared.Resources.Info;
            var snapshot = prepared.Stage.Resources;
            var images = prepared.Descriptors.Images;
            for (var index = 0; index < images.Length; index++)
            {
                var binding = images[index];
                if (binding.IsHostMovie)
                {
                    continue;
                }

                if (IsStaleImage(binding.ImageIdentifier, out var stale))
                {
                    if (stale is not null)
                    {
                        stale.Binding = default;
                    }

                    images[index] = binding = ResolveImageBinding(info.Images[index], snapshot.Images[index], program, index);
                }
            }

            for (var index = 0; index < images.Length; index++)
            {
                var binding = images[index];
                if (binding.IsHostMovie)
                {
                    continue;
                }

                var resource = info.Images[index];
                var view = binding.Request.View;
                var image = _imageCache.GetImage(binding.ImageIdentifier);
                if (binding.IsStorage && image.Description.HasStencil && ViewFormatRules.IsStencilViewFormat(view.Format))
                {
                    image = AcquireStencilStorage(binding, image);
                    binding.Request = binding.Request with { View = view with { LevelCount = 1 } };
                    binding.View = image.GetOrCreateView(binding.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                    binding.MipViews = [];
                }
                else if (resource.MipMode == ImageMipMode.DynamicStorage)
                {
                    if (resource.MipCount == 0 || resource.MipCount != view.LevelCount)
                    {
                        throw SubmissionScheduler.Fatal(
                            $"A storage image's mip count does not match its view: image={index} mips={resource.MipCount} levels={view.LevelCount} hash=0x{program.Hash:X16}.");
                    }

                    var mipViews = new ImageView[resource.MipCount];
                    for (var mip = 0u; mip < resource.MipCount; mip++)
                    {
                        var mipRequest = binding.Request with { View = view with { BaseLevel = view.BaseLevel + mip, LevelCount = 1 } };
                        mipViews[mip] = _imageCache.AcquireTextureView(binding.ImageIdentifier, mipRequest);
                    }

                    binding.MipViews = mipViews;
                    binding.View = mipViews[0];
                }
                else
                {
                    if (binding.IsStorage)
                    {
                        binding.Request = binding.Request with { View = view with { LevelCount = 1 } };
                    }

                    binding.View = _imageCache.AcquireTextureView(binding.ImageIdentifier, binding.Request);
                    binding.MipViews = [];
                }

                image.Uses.Storage |= binding.IsStorage;
                image.Uses.Texture |= !binding.IsStorage;
                binding.CachedImage = image;
                binding.Image = image.Backing.Handle;
            }
        }

        private void DestroyStageBindings(PreparedStageBindings stage)
        {
            foreach (var texture in stage.Textures)
            {
                if (texture is { StagingBuffer.Handle: not 0 })
                {
                    RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
                }
            }
        }

        private void ReturnStageScratch(PreparedStageBindings stage)
        {
            // CommitBindings copies texture references into submission-owned
            // storage. These CPU descriptions are no longer referenced by GPU work.
            Scratch.Images.Return(stage.Descriptors.Images);
            Scratch.Samplers.Return(stage.Descriptors.Samplers);
            Scratch.Buffers.Return(stage.Descriptors.Buffers);
            Scratch.Sources.Return(stage.BufferSources);
            Scratch.ShaderData.Return(stage.ShaderData);
            stage.Descriptors.Images = [];
            stage.Descriptors.Samplers = [];
            stage.Descriptors.Buffers = [];
            stage.BufferSources = [];
            stage.ShaderData = [];
        }

        private static DescriptorImageInfo ImageInfo(TextureResource texture, uint element, ShaderProgramInfo program, int index)
        {
            var view = texture.MipViews.Length == 0
                ? (element == 0 ? texture.View : default)
                : (element < (uint)texture.MipViews.Length ? texture.MipViews[element] : default);
            if (view.Handle == 0 || texture.Layout == ImageLayout.Undefined)
            {
                throw SubmissionScheduler.Fatal($"An image binding has no view for its element: image={index} element={element} hash=0x{program.Hash:X16}.");
            }

            return new DescriptorImageInfo { ImageView = view, ImageLayout = texture.Layout };
        }

        // Joins every stage's writes, records the image work and binds the set by push or from the heap.
        public void CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorCommit);
            var preparation = RequirePreparation();
            var entry = RequirePipelineEntry(in pipeline);
            var command = BeginBatchedGuestCommands();
            _commandBuffer = command;
            var writeCount = 0;
            var bufferCount = 0;
            var imageCount = 0;
            var textureCount = 0;
            var maxStageImages = 0;
            foreach (var prepared in stages)
            {
                var stage = (PreparedStageBindings)prepared;
                var stageFlag = DescriptorWriter.ShaderStageFlag(StageOf(stage.Program));
                if ((bindPoint == PipelineBindPoint.Graphics && (stageFlag & (ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit)) == 0) ||
                    (bindPoint == PipelineBindPoint.Compute && stageFlag != ShaderStageFlags.ComputeBit))
                {
                    throw SubmissionScheduler.Fatal($"A stage does not belong to the bind point: stage={stage.Program.Stage} bindPoint={bindPoint}.");
                }

                var layoutDescriptors = stage.Layout.Descriptors;
                for (var descriptorIndex = 0; descriptorIndex < layoutDescriptors.Count; descriptorIndex++)
                {
                    var binding = layoutDescriptors[descriptorIndex];
                    writeCount++;
                    var count = (int)DescriptorWriter.DescriptorCount(binding);
                    if (ImageDescriptorBinding.ResourceClass(binding.Kind) != ShaderCompiler.Resources.ImageResourceClass.None || binding.Kind == DescriptorBindingKind.Samplers)
                    {
                        imageCount += count;
                    }
                    else
                    {
                        bufferCount += count;
                    }
                }

                textureCount += stage.Textures.Length;
                maxStageImages = Math.Max(maxStageImages, stage.Textures.Length);
            }

            // All stages that read the same stencil bytes share the shader's working image.
            if (preparation.StencilStorageImages is { } stencilImages)
            {
                foreach (var prepared in stages)
                {
                    foreach (var texture in ((PreparedStageBindings)prepared).Textures)
                    {
                        if (texture.CachedImage is { } attachment && ViewFormatRules.IsStencilViewFormat(texture.Request.View.Format) &&
                            stencilImages.TryGetValue(attachment, out var storage))
                        {
                            texture.CachedImage = storage;
                            texture.Image = storage.Backing.Handle;
                            texture.View = storage.GetOrCreateView(texture.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                            texture.MipViews = [];
                        }
                    }
                }
            }

            // Vulkan consumes these host-side descriptions during the call.
            // Only the resources themselves remain alive until GPU completion.
            // Bound stack use for unusually large descriptor layouts.
            const int StackDescriptorLimit = 128;
            Span<DescriptorBufferInfo> bufferInfos = bufferCount <= StackDescriptorLimit
                ? stackalloc DescriptorBufferInfo[bufferCount] : new DescriptorBufferInfo[bufferCount];
            Span<DescriptorImageInfo> imageInfos = imageCount <= StackDescriptorLimit
                ? stackalloc DescriptorImageInfo[imageCount] : new DescriptorImageInfo[imageCount];
            Span<WriteDescriptorSet> writes = writeCount <= StackDescriptorLimit
                ? stackalloc WriteDescriptorSet[writeCount] : new WriteDescriptorSet[writeCount];
            Span<uint> occurrenceScratch = maxStageImages <= StackDescriptorLimit
                ? stackalloc uint[maxStageImages] : new uint[maxStageImages];
            var pushData = stackalloc uint[(int)PushData.DwordCount];
            var hasPushData = false;
            var bufferIndex = 0;
            var imageIndex = 0;
            var writeIndex = 0;
            var uploadStage = bindPoint == PipelineBindPoint.Compute ? PipelineStageFlags.ComputeShaderBit : PipelineStageFlags.FragmentShaderBit;
            TextureResource[] textures = textureCount == 0 ? [] : new TextureResource[textureCount];
            var textureIndex = 0;
            fixed (DescriptorBufferInfo* bufferInfoPointer = bufferInfos)
            fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
            {
                foreach (var prepared in stages)
                {
                    var stage = (PreparedStageBindings)prepared;
                    var program = stage.Program;
                    var descriptors = stage.Descriptors;
                    var shaderStage = StageOf(program);
                    var stageFlag = DescriptorWriter.ShaderStageFlag(shaderStage);
                    if (descriptors.GlobalDataShare.Buffer.Handle != 0)
                    {
                        // The host and every earlier queue write to the data share complete before the shader reads it.
                        EndRendering();
                        command = BeginBatchedGuestCommands();
                        var barrier = GlobalDataShareBarrier.Make(descriptors.GlobalDataShare.Buffer);
                        VulkanSynchronization.PipelineBarrier(_vk,command, GlobalDataShareBarrier.SourceStages, DescriptorWriter.PipelineStageFlag(stageFlag), 0, 0, null, 1, &barrier, 0, null);
                    }

                    RecordStageTextureTransitions(stage.Textures);
                    foreach (var texture in stage.Textures)
                    {
                        if (texture.IsHostMovie && texture.NeedsUpload)
                        {
                            EndRendering();
                            break;
                        }
                    }

                    RecordHostMovieUploads(stage.Textures, uploadStage);
                    foreach (var texture in stage.Textures)
                    {
                        textures[textureIndex++] = texture;
                    }

                    var occurrences = occurrenceScratch[..descriptors.Images.Length];
                    occurrences.Clear();
                    var layoutDescriptors = stage.Layout.Descriptors;
                    for (var descriptorIndex = 0; descriptorIndex < layoutDescriptors.Count; descriptorIndex++)
                    {
                        var binding = layoutDescriptors[descriptorIndex];
                        var resources = binding.Resources;
                        var write = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet,
                            DstBinding = BindingLayout.NativeBindingIndex(shaderStage, binding.Kind),
                            DescriptorType = DescriptorWriter.DescriptorType(binding.Kind),
                            DescriptorCount = DescriptorWriter.DescriptorCount(binding),
                        };
                        var bufferStart = bufferIndex;
                        var imageStart = imageIndex;
                        if (ImageDescriptorBinding.ResourceClass(binding.Kind) != ShaderCompiler.Resources.ImageResourceClass.None)
                        {
                            for (var slot = 0; slot < resources.Count; slot++)
                            {
                                var resource = resources[slot];
                                imageInfos[imageIndex++] = ImageInfo(descriptors.Images[(int)resource], occurrences[(int)resource]++, program, (int)resource);
                            }
                        }
                        else
                        {
                            switch (binding.Kind)
                            {
                                case DescriptorBindingKind.Buffers:
                                    for (var slot = 0; slot < resources.Count; slot++)
                                    {
                                        var resource = resources[slot];
                                        var view = descriptors.Buffers[(int)resource];
                                        if (view.Buffer.Handle == 0)
                                        {
                                            throw SubmissionScheduler.Fatal($"A storage buffer binding has no buffer: buffer={resource} hash=0x{program.Hash:X16}.");
                                        }

                                        bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range };
                                    }

                                    break;
                                case DescriptorBindingKind.DeviceAddressPageTable:
                                case DescriptorBindingKind.FaultBuffer:
                                {
                                    var shared = binding.Kind == DescriptorBindingKind.DeviceAddressPageTable ? _bufferCache.BdaPageTableBuffer : _bufferCache.FaultBuffer;
                                    bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = shared.Handle, Offset = 0, Range = shared.Size };
                                    break;
                                }

                                case DescriptorBindingKind.FlattenedResourceTable:
                                case DescriptorBindingKind.ShaderData:
                                case DescriptorBindingKind.GlobalDataShare:
                                {
                                    var view = binding.Kind switch
                                    {
                                        DescriptorBindingKind.FlattenedResourceTable => descriptors.FlattenedTable,
                                        DescriptorBindingKind.ShaderData => descriptors.ShaderData,
                                        _ => descriptors.GlobalDataShare,
                                    };
                                    if (view.Buffer.Handle == 0)
                                    {
                                        throw SubmissionScheduler.Fatal($"A shared buffer binding has no buffer: kind={binding.Kind} hash=0x{program.Hash:X16}.");
                                    }

                                    bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range };
                                    break;
                                }

                                case DescriptorBindingKind.Samplers:
                                    for (var slot = 0; slot < resources.Count; slot++)
                                    {
                                        var resource = resources[slot];
                                        var sampler = descriptors.Samplers[(int)resource];
                                        if (sampler.Handle == 0)
                                        {
                                            throw SubmissionScheduler.Fatal($"A sampler binding has no sampler: sampler={resource} hash=0x{program.Hash:X16}.");
                                        }

                                        imageInfos[imageIndex++] = new DescriptorImageInfo { Sampler = sampler, ImageLayout = ImageLayout.Undefined };
                                    }

                                    break;
                                default:
                                    throw SubmissionScheduler.Fatal($"The descriptor binding kind is invalid: kind={binding.Kind}.");
                            }
                        }

                        if (bufferIndex != bufferStart)
                        {
                            write.PBufferInfo = bufferInfoPointer + bufferStart;
                        }

                        if (imageIndex != imageStart)
                        {
                            write.PImageInfo = imageInfoPointer + imageStart;
                        }

                        writes[writeIndex++] = write;
                    }

                    for (var index = 0; index < descriptors.Images.Length; index++)
                    {
                        var expected = descriptors.Images[index].MipViews.Length == 0 ? 1u : (uint)descriptors.Images[index].MipViews.Length;
                        if (occurrences[index] != expected)
                        {
                            throw SubmissionScheduler.Fatal($"An image is bound a different number of times than its views: image={index} occurrences={occurrences[index]} views={expected} hash=0x{program.Hash:X16}.");
                        }
                    }

                    if (stage.ShaderData.Length != stage.Layout.ShaderDataDwordCount)
                    {
                        throw SubmissionScheduler.Fatal($"The shader data does not match the layout: dwords={stage.ShaderData.Length} layout={stage.Layout.ShaderDataDwordCount} hash=0x{program.Hash:X16}.");
                    }

                    if (stage.Layout.UsesPushData)
                    {
                        for (var index = 0; index < stage.ShaderData.Length; index++)
                        {
                            pushData[stage.Layout.PushDataStartDword + index] = stage.ShaderData[index];
                        }

                        hasPushData = true;
                    }
                }

                if (hasPushData)
                {
                    var pushStages = bindPoint == PipelineBindPoint.Graphics ? ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit : ShaderStageFlags.ComputeBit;
                    _vk.CmdPushConstants(command, entry.Layout, pushStages, 0, PushData.ByteSize, pushData);
                }

                if (writeIndex != 0)
                {
                    fixed (WriteDescriptorSet* writePointer = writes)
                    {
                        if (entry.UsesPushDescriptors)
                        {
                            _pushDescriptorApi.CmdPushDescriptorSet(command, bindPoint, entry.Layout, 0, (uint)writeIndex, writePointer);
                            ShaderCacheCounters.CountPushSet();
                        }
                        else
                        {
                            var set = _descriptorHeap.Commit(entry.SetLayout, in entry.Demand);
                            for (var index = 0; index < writeIndex; index++)
                            {
                                writes[index].DstSet = set;
                            }

                            _vk.UpdateDescriptorSets(_device, (uint)writeIndex, writePointer, 0, null);
                            _vk.CmdBindDescriptorSets(command, bindPoint, entry.Layout, 0, 1, &set, 0, null);
                            ShaderCacheCounters.CountHeapSet();
                        }
                    }
                }
            }

            _batchResources.Add(new SubmissionUploadResources
            {
                DebugName = bindPoint == PipelineBindPoint.Compute ? "SharpEmu dispatch" : "SharpEmu draw",
                Textures = textures,
                FeedbackSnapshots = preparation.FeedbackSnapshots?.ToArray() ?? [],
                OverflowBuffers = preparation.OverflowBuffers.Count == 0 ? null : preparation.OverflowBuffers.ToArray(),
            });
            preparation.OverflowBuffers.Clear();
            preparation.Committed = true;
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"Bindings committed bindPoint={bindPoint} pipeline={pipeline.Pipeline} writes={writeIndex} push={entry.UsesPushDescriptors} pushData={hasPushData}");
            }
        }
    }
}
