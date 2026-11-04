// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.IO.Hashing;
using SharpEmu.HLE.Diagnostics;
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
    // The shader bounds a storage binding in whole dwords (OpArrayLength), so the view ends on a dword: a 16-bit element
    // in the binding's last partial dword stays in range. A view that would not fit keeps its exact size.
    internal static ulong StorageBufferViewRange(ulong bytes, ulong available, ulong maxRange)
    {
        var rounded = (bytes + 3) & ~3UL;
        return rounded <= available && rounded <= maxRange ? rounded : bytes;
    }

    private static long _stageMemoHits;
    private static long _stageMemoMisses;
    private static long _stageMemoCaptures;
    private static long _stageMemoStale;
    private static long _stageMemoInvalidations;
    private static long _stageMemoAcquireReuses;
    private static long _stageMemoContentHits;
    private static long _stageMemoEvictions;

    internal static string TakeStagePreparationReport() =>
        FormattableString.Invariant($"[PERF][STAGE_MEMO] hits={Interlocked.Exchange(ref _stageMemoHits, 0)} misses={Interlocked.Exchange(ref _stageMemoMisses, 0)} captures={Interlocked.Exchange(ref _stageMemoCaptures, 0)} ") +
        FormattableString.Invariant($"stale={Interlocked.Exchange(ref _stageMemoStale, 0)} invalidations={Interlocked.Exchange(ref _stageMemoInvalidations, 0)} acquire_reuses={Interlocked.Exchange(ref _stageMemoAcquireReuses, 0)} ") +
        FormattableString.Invariant($"content_hits={Interlocked.Exchange(ref _stageMemoContentHits, 0)} evictions={Interlocked.Exchange(ref _stageMemoEvictions, 0)}");

    private sealed class StagePreparationSet
    {
        public StagePreparationMemo? Last;
        public readonly Dictionary<ulong, StagePreparationMemo> ByContent = new();
    }

    private sealed class StagePreparationMemo
    {
        public uint[][] Images = [];
        public uint[][] Samplers = [];
        public uint[][] Buffers = [];
        public DeviceAddressRange[] Ranges = [];
        public ulong ShaderBase;
        public long RegistrationSerial;
        public long Spans;
        public TextureResource[] Textures = [];
        public GuestSpan[] TextureRanges = [];
        public ulong LookupGeneration;
        public Sampler[] SamplerHandles = [];
        public (BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer, ulong MappedSize)[] Sources = [];
        public bool Valid;

        public void Invalidate()
        {
            if (Valid)
            {
                Valid = false;
                Interlocked.Increment(ref _stageMemoInvalidations);
            }
        }
    }

    private sealed partial class Presenter
    {
        // [local] SHARPEMU_RENDER_POOLS=0 allocates every preparation, prepared stage and image binding again; on, a
        // closed preparation hands its objects to the next draw (each recording host keeps its own pools).
        internal static readonly bool RenderPools = Environment.GetEnvironmentVariable("SHARPEMU_RENDER_POOLS") != "0";

        // [local] SHARPEMU_IMAGE_BINDING_MEMO / SHARPEMU_IMAGE_BINDING_MEMO_WAYS and SHARPEMU_LEAN_TEXTURE_PATH were our
        // image-binding memo (adapted from #1077) and our copy-free texture path. Upstream's image-binding memo (#1082,
        // keyed by the descriptor words, always on) and its pooled bindings replace both; the switches are still read
        // and reported, and change nothing.
        internal static readonly bool ImageBindingMemoEnabled = Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_BINDING_MEMO") == "1";
        internal static readonly int ImageBindingMemoWays =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_BINDING_MEMO_WAYS"), out var ways) && ways is > 0 and <= 64 ? ways : 1;
        internal static readonly bool LeanTexturePath = Environment.GetEnvironmentVariable("SHARPEMU_LEAN_TEXTURE_PATH") != "0";
    }

    private sealed partial class RecordingHost
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
            public readonly RenderScratchPool<byte> SamplerClasses = new();
            public readonly RenderScratchPool<uint> ShaderData = new();
            public readonly RenderScratchPool<BufferView> Buffers = new();
            public readonly RenderScratchPool<(BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer, ulong MappedSize)> Sources = new();
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
            public BufferView CallRecords;
        }

        private sealed class PreparedStageBindings : IPreparedBindings
        {
            private ShaderStageResources _stage;
            private ShaderProgramInfo _program = null!;

            public ShaderStageResources Stage => _stage;

            public ShaderProgramInfo Program => _program;

            public void Reset(ShaderStageResources stage, ShaderProgramInfo program)
            {
                _stage = stage;
                _program = program;
                Memoized = false;
                TexturesKept = false;
                Memo = null;
                ImagesPrepared = false;
                Descriptors.GlobalDataShare = default;
                Descriptors.FlattenedTable = default;
                Descriptors.ShaderData = default;
                Descriptors.CallRecords = default;
            }

            public SpecializedResourceInfo Resources => _program.Resources!;

            public BindingLayout Layout => _program.Bindings!;

            public StageDescriptors Descriptors { get; } = new();

            public (BufferDescriptorWords Descriptor, ResourceSlotIdentifier Buffer, ulong MappedSize)[] BufferSources { get; set; } = [];

            public uint[] ShaderData { get; set; } = [];

            // The call records the shader's call entries search, and the memory their callees read through them
            // (PrepareCallRecords).
            public uint[] CallRecordWords { get; set; } = [];

            public DeviceAddressRange[] CallRecordRanges { get; set; } = [];

            public TextureResource[] Textures => Descriptors.Images;

            public bool Memoized { get; set; }

            public bool TexturesKept { get; set; }

            public StagePreparationMemo? Memo { get; set; }

            // [local] PrepareStageImages already recorded this stage's image work.
            public bool ImagesPrepared { get; set; }
        }

        private static ShaderStage StageOf(ShaderProgramInfo program) => program.Stage switch
        {
            ShaderStageKind.Vertex => ShaderStage.Vertex,
            ShaderStageKind.Pixel => ShaderStage.Pixel,
            ShaderStageKind.Compute => ShaderStage.Compute,
            ShaderStageKind.Mesh => ShaderStage.Mesh,
            ShaderStageKind.TessellationEvaluation => ShaderStage.TessellationEvaluation,
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

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _tracedTextureBindings = new();
        // The last lookup of one image of one program. A draw binds the same descriptor as the
        // previous draw of its program most of the time; while no image the lookup could find has
        // changed (the cache's lookup generation), the request and the image found are the same.
        private struct ImageBindingMemo
        {
            public ulong Generation;
            public ulong Begin;
            public ulong End;
            public TextureRequestResolution Resolution;
            public TextureRequestResolution OriginalResolution;
            public ResourceSlotIdentifier Found;
            public uint MetadataBaseLayer;
            public bool CompletesLookup;
        }

        private readonly record struct ImageBindingKey(ulong Words01, ulong Words23, ulong Words45, ulong Words67, int Length, ShaderImageShape Shape)
        {
            public static ImageBindingKey Of(uint[] words, ShaderImageShape shape)
            {
                Span<uint> padded = stackalloc uint[8];
                words.AsSpan(0, Math.Min(words.Length, 8)).CopyTo(padded);
                return new ImageBindingKey(
                    padded[0] | ((ulong)padded[1] << 32), padded[2] | ((ulong)padded[3] << 32),
                    padded[4] | ((ulong)padded[5] << 32), padded[6] | ((ulong)padded[7] << 32), words.Length, shape);
            }
        }

        private const int ImageBindingMemoCapacity = 1 << 16;
        private Dictionary<ImageBindingKey, ImageBindingMemo>? _imageBindingMemos;

        private readonly record struct BufferSourceMemo(ulong Address, ulong Requested, long RegionStamp, long Spans, long Mapping, ResourceSlotIdentifier Buffer, ulong Size);
        private Dictionary<ShaderProgramInfo, BufferSourceMemo[]>? _bufferSourceMemos;
        private ShaderProgramInfo? _lastBufferMemoProgram;
        private BufferSourceMemo[] _lastBufferMemos = [];

        private BufferSourceMemo[] BufferSourceMemosOf(ShaderProgramInfo program, int count)
        {
            if (!ReferenceEquals(program, _lastBufferMemoProgram) || _lastBufferMemos.Length < count)
            {
                var all = _bufferSourceMemos ??= new Dictionary<ShaderProgramInfo, BufferSourceMemo[]>(ReferenceEqualityComparer.Instance);
                if (!all.TryGetValue(program, out var memos) || memos.Length < count)
                {
                    memos = new BufferSourceMemo[count];
                    all[program] = memos;
                }

                _lastBufferMemoProgram = program;
                _lastBufferMemos = memos;
            }

            return _lastBufferMemos;
        }

        private readonly HashSet<string> _imageLookupTraceEntries = new(StringComparer.Ordinal);

        private void TraceImageLookup(ImageResource image, uint[] words, ShaderProgramInfo program, int index, in ImageRequest request)
        {
            if (!ImageClearTrace.Enabled ||
                !ImageTraceRange.Overlaps(request.Description.Data.Address, 1) ||
                _imageLookupTraceEntries.Count >= 256)
                return;

            var description = request.Description;
            var message = $"ImageLookup shader=0x{program.Hash:X16} stage={program.Stage} image={index} indirectRoot={image.IndirectRoot} " +
                $"address=0x{description.Data.Address:X16} size=0x{description.Data.Size:X} type={description.Type} tile={description.TileMode} " +
                $"extent={description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} levels={description.Resources.Levels} layers={description.Resources.Layers} format={description.PixelFormat} " +
                $"view={request.View} words={string.Join(',', words.Select(word => word.ToString("X8")))}";
            if (!_imageLookupTraceEntries.Add(message)) return;
            Console.Error.WriteLine($"[GPU][TRACE] {message}");
            if (_imageLookupTraceEntries.Count == 256)
                Console.Error.WriteLine("[GPU][TRACE] ImageLookup limit=256. Further entries are omitted.");
        }

        // Render-state discovery for one shader image; the view is acquired later with the draw.
        private const int StagePreparationCapacity = 1024;
        private Dictionary<ShaderProgramInfo, StagePreparationSet>? _stagePreparations;
        private XxHash3? _contentHasher;

        private StagePreparationSet StagePreparationsOf(ShaderProgramInfo program)
        {
            var all = _stagePreparations ??= new Dictionary<ShaderProgramInfo, StagePreparationSet>(ReferenceEqualityComparer.Instance);
            if (!all.TryGetValue(program, out var set))
            {
                set = new StagePreparationSet();
                all[program] = set;
            }

            return set;
        }

        private ulong ContentKeyOf(ResourceSnapshot snapshot, ulong shaderBase)
        {
            var hasher = _contentHasher ??= new XxHash3();
            hasher.Reset();
            Span<byte> head = stackalloc byte[sizeof(ulong)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(head, shaderBase);
            hasher.Append(head);
            foreach (var words in snapshot.Images)
            {
                hasher.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
            }

            foreach (var words in snapshot.Samplers)
            {
                hasher.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
            }

            return hasher.GetCurrentHashAsUInt64();
        }

        private static bool SameWords(uint[][] left, uint[][] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                if (!ReferenceEquals(left[index], right[index]) && !left[index].AsSpan().SequenceEqual(right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryFindStagePreparation(StagePreparationSet set, ResourceSnapshot snapshot, ulong shaderBase, out StagePreparationMemo memo)
        {
            if (set.Last is { } last && last.ShaderBase == shaderBase &&
                ReferenceEquals(last.Images, snapshot.Images) && ReferenceEquals(last.Samplers, snapshot.Samplers) && IsFreshStagePreparation(last))
            {
                memo = last;
                Interlocked.Increment(ref _stageMemoHits);
                return true;
            }

            var key = ContentKeyOf(snapshot, shaderBase);
            if (set.ByContent.TryGetValue(key, out var found) && found.ShaderBase == shaderBase &&
                SameWords(snapshot.Images, found.Images) && SameWords(snapshot.Samplers, found.Samplers) && IsFreshStagePreparation(found))
            {
                found.Images = snapshot.Images;
                found.Samplers = snapshot.Samplers;
                found.Ranges = snapshot.DeviceAddressRanges;
                set.Last = found;
                memo = found;
                Interlocked.Increment(ref _stageMemoContentHits);
                return true;
            }

            memo = null!;
            return false;
        }

        private bool IsFreshStagePreparation(StagePreparationMemo memo)
        {
            if (!memo.Valid)
            {
                return false;
            }

            var generation = memo.LookupGeneration;
            for (var index = 0; index < memo.Textures.Length; index++)
            {
                var texture = memo.Textures[index];
                var range = memo.TextureRanges[index];
                var checkedGeneration = memo.LookupGeneration;
                if (IsStaleImage(texture.ImageIdentifier, out _) ||
                    !ImageDescription.IsEmptyRange(texture.Request.Description.Data) && !(_worker
                        ? _imageCache.TryRememberedForSlice(texture.ImageIdentifier, ref checkedGeneration, range.Address, range.End)
                        : _imageCache.TryTouchRemembered(texture.ImageIdentifier, ref checkedGeneration, range.Address, range.End)))
                {
                    memo.Invalidate();
                    Interlocked.Increment(ref _stageMemoStale);
                    return false;
                }

                generation = checkedGeneration;
            }

            memo.LookupGeneration = generation;
            return true;
        }

        private uint[] AssembleShaderData(ShaderStageResources stage, ShaderProgramInfo program, BindingLayout layout)
        {
            var snapshot = stage.Resources;
            var shaderData = Scratch.ShaderData.Rent(checked((int)layout.ShaderDataDwordCount));
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
            stage.WriteMeshDrawParameters(shaderData);
            stage.WriteTessellationData(shaderData);
            stage.WriteCallData(shaderData);
            return shaderData;
        }

        private PreparedStageBindings ReuseStagePreparation(RenderPreparation preparation, ShaderStageResources stage, ShaderProgramInfo program,
            StagePreparationMemo memo)
        {
            var prepared = RentStage(stage, program);
            prepared.Memoized = true;
            prepared.Memo = memo;
            preparation.Stages.Add(prepared);
            var descriptors = prepared.Descriptors;
            descriptors.Images = Scratch.Images.Rent(memo.Textures.Length);
            memo.Textures.CopyTo(descriptors.Images, 0);
            descriptors.Samplers = Scratch.Samplers.Rent(memo.SamplerHandles.Length);
            memo.SamplerHandles.CopyTo(descriptors.Samplers, 0);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingMemoImages)) // [local]
            foreach (var texture in memo.Textures)
            {
                CompleteOverlappingPublications(texture.Request.Description.Data.Address,
                    texture.Request.Description.Data.Size, includeLabels: false);
                SynchronizeColorMetadata(texture.Request);
                EnsureBatchedGuestCommands();
                ApplyNativeColorClear(texture.ImageIdentifier, texture.Request);
                BindImage(texture.ImageIdentifier, texture.IsStorage);
            }

            var layout = program.Bindings!;
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingShaderData)) // [local]
            {
                prepared.ShaderData = AssembleShaderData(stage, program, layout);
                if (layout.Find(DescriptorBindingKind.GlobalDataShare) is not null)
                {
                    descriptors.GlobalDataShare = new BufferView(_bufferCache.GdsBuffer.Handle, 0, Vk.WholeSize);
                }
            }

            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingDeviceRanges)) // [local]
                FindDeviceAddressBuffers(prepared);
            using var buffersScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingMemoBuffers); // [local]
            var spans = GuestGpuMemoryHook.Current?.SpanVersion ?? 0;
            var sameBuffers = memo.Spans == spans && SameWords(memo.Buffers, stage.Resources.Buffers);
            if (sameBuffers)
            {
                var buffers = prepared.Resources.Info.Buffers;
                for (var index = 0; index < memo.Sources.Length; index++)
                {
                    if (memo.Sources[index].MappedSize != 0)
                    {
                        PublishImagesUnderWrite(buffers[index], memo.Sources[index].Descriptor.Address, memo.Sources[index].MappedSize);
                    }
                }
            }

            if (!sameBuffers || memo.RegistrationSerial != _bufferCache.RegistrationSerial)
            {
                FindBuffers(prepared);
                prepared.BufferSources.CopyTo(memo.Sources, 0);
                memo.Buffers = stage.Resources.Buffers;
                memo.RegistrationSerial = _bufferCache.RegistrationSerial;
                memo.Spans = spans;
            }
            else
            {
                prepared.BufferSources = Scratch.Sources.Rent(memo.Sources.Length);
                memo.Sources.CopyTo(prepared.BufferSources, 0);
            }

            return prepared;
        }

        private void NoteStagePreparation(StagePreparationSet set, PreparedStageBindings prepared, ResourceSnapshot snapshot, ulong shaderBase)
        {
            var key = ContentKeyOf(snapshot, shaderBase);
            if (!set.ByContent.TryGetValue(key, out var memo))
            {
                if (set.ByContent.Count >= StagePreparationCapacity)
                {
                    foreach (var entry in set.ByContent.Values)
                    {
                        entry.Invalidate();
                    }

                    set.ByContent.Clear();
                    set.Last = null;
                    Interlocked.Increment(ref _stageMemoEvictions);
                }

                memo = new StagePreparationMemo();
                set.ByContent[key] = memo;
            }

            var images = prepared.Descriptors.Images;
            if (memo.Textures.Length != images.Length)
            {
                foreach (var old in memo.Textures)
                {
                    old.Preparation = null;
                }

                memo.Textures = new TextureResource[images.Length];
                for (var index = 0; index < images.Length; index++)
                {
                    memo.Textures[index] = new TextureResource { Preparation = memo };
                }
            }

            if (memo.TextureRanges.Length != images.Length)
            {
                memo.TextureRanges = new GuestSpan[images.Length];
            }

            for (var index = 0; index < images.Length; index++)
            {
                memo.Textures[index].CopyBindingFrom(images[index]);
                memo.TextureRanges[index] = _imageCache.RememberedRange(images[index].ImageIdentifier, images[index].Request);
            }

            var samplers = prepared.Descriptors.Samplers;
            if (memo.SamplerHandles.Length != samplers.Length)
            {
                memo.SamplerHandles = new Sampler[samplers.Length];
            }

            memo.LookupGeneration = _imageCache.LookupGeneration;

            samplers.CopyTo(memo.SamplerHandles, 0);
            var sources = prepared.BufferSources;
            if (memo.Sources.Length != sources.Length)
            {
                memo.Sources = new (BufferDescriptorWords, ResourceSlotIdentifier, ulong)[sources.Length];
            }

            sources.CopyTo(memo.Sources, 0);
            memo.Images = snapshot.Images;
            memo.Samplers = snapshot.Samplers;
            memo.Buffers = snapshot.Buffers;
            memo.Ranges = snapshot.DeviceAddressRanges;
            memo.ShaderBase = shaderBase;
            memo.RegistrationSerial = _bufferCache.RegistrationSerial;
            memo.Spans = GuestGpuMemoryHook.Current?.SpanVersion ?? 0;
            memo.Valid = true;
            set.Last = memo;
            Interlocked.Increment(ref _stageMemoCaptures);
        }

        private TextureResource ResolveImageBinding(ImageResource image, uint[] words, ShaderProgramInfo program, int index)
        {
            if (words.Length < 4)
            {
                throw SubmissionScheduler.Fatal($"An image descriptor is too short: image={index} words={words.Length} hash=0x{program.Hash:X16}.");
            }

            var storage = image.ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage;
            EnsureBatchedGuestCommands();
            if (!_worker && program.Frontend?.PrepareTexture(words) == false)
                words = new uint[8];
            var shape = ImageRequestBuilders.ShapeOf(image);
            var key = ImageBindingKey.Of(words, shape);
            var memos = _imageBindingMemos ??= new Dictionary<ImageBindingKey, ImageBindingMemo>();
            ref var memo = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(memos, key);
            var known = !System.Runtime.CompilerServices.Unsafe.IsNullRef(ref memo);
            var originalResolution = known
                ? memo.OriginalResolution
                : program.Frontend is { } frontend
                    ? frontend.ResolveTexture(words, shape, program.Hash, index)
                    : ImageRequestBuilders.Texture(words, shape, program.Hash, index);
            TraceImageLookup(image, words, program, index, originalResolution.Request);
            CompleteOverlappingPublications(originalResolution.Request.Description.Data.Address,
                originalResolution.Request.Description.Data.Size, includeLabels: false);
            SynchronizeColorMetadata(originalResolution.Request);
            EnsureBatchedGuestCommands();
            TextureRequestResolution resolution;
            ResourceSlotIdentifier imageIdentifier;
            if (known &&
                (_worker
                    ? _imageCache.TryRememberedForSlice(memo.Found, ref memo.Generation, memo.Begin, memo.End)
                    : _imageCache.TryTouchRemembered(memo.Found, ref memo.Generation, memo.Begin, memo.End)))
            {
                GuestImageCache.CountBindingMemo(hit: true);
                resolution = memo.Resolution;
                imageIdentifier = memo.Found;
                if (memo.CompletesLookup)
                {
                    if (_worker)
                    {
                        if (!_imageCache.CompleteLookupIsNoopForSlice(imageIdentifier, resolution.Request))
                        {
                            throw Serial("dcc lookup");
                        }
                    }
                    else
                    {
                        _imageCache.CompleteLookup(imageIdentifier, resolution.Request, memo.MetadataBaseLayer);
                    }
                }
            }
            else
            {
                GuestImageCache.CountBindingMemo(hit: false);
                resolution = originalResolution;
                var request = resolution.Request;
                var metadataBaseLayer = request.View.BaseLayer;
                imageIdentifier = LookUpImage(ref request, resolution.ExactFormat);
                resolution = resolution with { Request = request };
                if (!ImageDescription.IsEmptyRange(request.Description.Data))
                {
                    if (memos.Count >= ImageBindingMemoCapacity)
                    {
                        memos.Clear();
                    }

                    var remembered = _imageCache.RememberedRange(imageIdentifier, request);
                    memos[key] = new ImageBindingMemo
                    {
                        Generation = _imageCache.LookupGeneration,
                        Begin = remembered.Address,
                        End = remembered.End,
                        Resolution = resolution,
                        OriginalResolution = originalResolution,
                        Found = imageIdentifier,
                        MetadataBaseLayer = metadataBaseLayer,
                        CompletesLookup = request.Description.Metadata.Kind == MetadataKind.Dcc ||
                            Image(imageIdentifier).Description.DccSliceSize != 0,
                    };
                }
            }

            _imageCache.TraceTextureMetadata(resolution.Request);
            if (Gpu.Images.ImageClearTrace.Enabled)
            {
                Gpu.Images.ImageTraceRange.NoteFollowedImage(program.Hash, index,
                    resolution.Request.Description.Data.Address, resolution.Request.Description.Data.Size);
                _imageCache.TraceTextureBinding(program.Hash, index, words, resolution.Request);
            }
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("image-binding", $"stage={program.Stage} hash=0x{program.Hash:X16} slot={index} storage={storage} address=0x{resolution.Request.Description.Data.Address:X16} size=0x{resolution.Request.Description.Data.Size:X} view={resolution.Request.View} words={string.Join(",", words.Select(value => value.ToString("X8")))}");
            imageIdentifier = ImageRequestBuilders.ValidateTextureOwner(_imageCache, imageIdentifier, resolution);
            ApplyNativeColorClear(imageIdentifier, resolution.Request);
            if (Gpu.Images.ImageClearTrace.Enabled)
                _imageCache.TraceVolumeClear("resolved-shader-image", resolution.Request, _imageCache.GetImage(imageIdentifier),
                    $"stage={program.Stage} hash=0x{program.Hash:X16} slot={index}");
            BindImage(imageIdentifier, storage);
            var descriptor = new TextureDescriptorWords(words);
            if (Presenter.ShouldTraceTextureBindings() &&
                (!Presenter._traceVolumeTextureBindingsOnly || image.Dimension == ImageDimension.Dim3D))
            {
                var cached = Image(imageIdentifier);
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
                if (!Presenter._traceVolumeTextureBindingsOnly || _tracedTextureBindings.TryAdd(line, 0))
                {
                    Console.Error.WriteLine(line);
                }
            }
            var texture = RentTextureResource();
            texture.Address = descriptor.BaseAddress;
            texture.ImageIdentifier = imageIdentifier;
            texture.Request = resolution.Request;
            texture.IsStorage = storage;
            texture.DestinationSelect = words[3] & 0xFFFu;
            texture.Width = descriptor.Width;
            texture.Height = descriptor.Height;
            return texture;
        }

        // Binding descriptions are large (an image request carries its whole mip layout) and one is made
        // per shader image per draw, so they are reused once the submission that read them has retired.
        // Only the render thread rents and returns them.
        // Sized for the bindings of every draw still in flight (a few frames of a thousand draws each);
        // a smaller cap drops returns during busy scenes and the pool allocates again.
        private const int MaxPooledTextureResources = 32768;
        private Stack<TextureResource>? _texturePool;

        private TextureResource RentTextureResource()
        {
            if (Presenter.RenderPools && _texturePool is not null && _texturePool.TryPop(out var texture))
            {
                texture.InPool = false;
                return texture;
            }

            return new TextureResource { Pooled = Presenter.RenderPools }; // [local] SHARPEMU_RENDER_POOLS=0: never pooled
        }

        internal void ReturnTextureResource(TextureResource texture)
        {
            if (!texture.Pooled)
            {
                if (texture.Preparation is not { Valid: true })
                {
                    texture.Reset();
                }

                return;
            }

            _texturePool ??= new Stack<TextureResource>();
            if (texture.InPool || _texturePool.Count >= MaxPooledTextureResources)
            {
                return;
            }

            texture.Reset();
            texture.InPool = true;
            _texturePool.Push(texture);
        }

        // A float view overrides integer views. Unpaired and movie-only samplers stay unclassified.
        private static void ClassifySamplerViews(ShaderResourceInfo info, TextureResource[] images, byte[] classes)
        {
            Array.Clear(classes);
            if (classes.Length == 0) return;
            foreach (var pair in info.SampledPairs)
            {
                if (pair.Sampler >= info.Samplers.Count || pair.Image >= images.Length || images[pair.Image].IsHostMovie)
                {
                    continue;
                }

                classes[pair.Sampler] |= ViewFormatRules.IsIntegerFormat(images[pair.Image].Request.View.Format)
                    ? (byte)1 : (byte)2;
            }
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

        public void UpdateTessellationData(IPreparedBindings prepared, uint[] data)
        {
            var stage = (PreparedStageBindings)prepared;
            (stage.Stage with { TessellationData = data }).WriteTessellationData(stage.ShaderData);
        }

        public IPreparedBindings PrepareBindings(ShaderStageResources stage)
        {
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("stage-binding", $"stage={stage.Program?.Stage} hash=0x{stage.Program?.Hash:X16} shaderBase=0x{stage.ShaderBase:X16} userData={string.Join(",", stage.Resources.UserData.Select(value => value.ToString("X8")))}");
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorPreparation);
            var program = RequireProgram(stage);
            if (_worker && program.Frontend is { RequiresOrderedImagePreparation: true } orderedFrontend)
            {
                foreach (var words in stage.Resources.Images)
                {
                    if (!orderedFrontend.IsTexturePrepared(words, ordered: false, out var source, out var sourceBytes))
                    {
                        throw Serial("frontend image preparation", resumable: true);
                    }

                    if (sourceBytes == 0)
                    {
                        continue;
                    }

                    if (Requests.WritesImageRange(new GuestSpan(source, sourceBytes)))
                    {
                        throw Serial("frontend image preparation", resumable: true);
                    }

                    Requests.FrontendTextures.Add((orderedFrontend, words));
                }
            }

            var preparation = RequirePreparation();
            var resources = program.Resources!;
            var layout = program.Bindings!;
            var snapshot = stage.Resources;
            var info = resources.Info;
            if (layout.UsesBindlessHeap)
            {
                if (_worker)
                {
                    throw Serial("bindless heap", resumable: true);
                }

                _presenter.PrepareBindlessHeap(preparation);
            }

            if (layout.Find(DescriptorBindingKind.CallMarks) is not null)
            {
                if (_worker)
                {
                    throw Serial("call marks", resumable: true);
                }

                _presenter.PollCallMarks();
            }

            using var stallProfile = DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.Prepare, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count);
            if (snapshot.Images.Length != info.Images.Count || snapshot.Samplers.Length != info.Samplers.Count || snapshot.Buffers.Length != info.Buffers.Count)
            {
                throw SubmissionScheduler.Fatal(
                    $"The resource snapshot does not match the program: hash=0x{program.Hash:X16} images={snapshot.Images.Length}/{info.Images.Count} " +
                    $"samplers={snapshot.Samplers.Length}/{info.Samplers.Count} buffers={snapshot.Buffers.Length}/{info.Buffers.Count}.");
            }

            var frontendReady = true;
            if (!_worker && program.Frontend is { RequiresOrderedImagePreparation: true } frontend)
            {
                foreach (var words in snapshot.Images)
                    frontendReady &= frontend.PrepareTexture(words);
            }

            if (BindingRepeatProbe.Enabled && !_worker) BindingRepeatProbe.Record(program.Hash, stage); // [local]
            if (ResolveProfile.Enabled && !_worker) CountResolveBindings(info, snapshot); // [local] SHARPEMU_PROFILE_RESOLVE
            var preparations = StagePreparationsOf(program);
            var movieActive = _presenter._hostMovieFramePixels is not null;
            if (movieActive && _worker)
            {
                throw Serial("host movie");
            }

            StagePreparationMemo? memo = null;
            var memoScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingMemo); // [local]
            var memoFound = frontendReady && !movieActive && !Gpu.Images.ImageClearTrace.Enabled && !MeshDrawTrace.Active &&
                TryFindStagePreparation(preparations, snapshot, stage.ShaderBase, out memo);
            memoScope.Dispose();
            if (memoFound)
            {
                return ReuseStagePreparation(preparation, stage, program, memo!);
            }

            Interlocked.Increment(ref _stageMemoMisses);
            using var setupScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingSetup); // [local]
            var prepared = RentStage(stage, program);
            // Register ownership before preparation can fail halfway through.
            preparation.Stages.Add(prepared);
            var descriptors = prepared.Descriptors;
            descriptors.Images = Scratch.Images.Rent(info.Images.Count);
            var movieCandidates = _presenter._hostMovieFramePixels is null ? null : MovieCandidates(resources, snapshot);
            var hostMovie = movieCandidates is null ? Presenter.HostMovieTextureBindings.None : _presenter.FindHostMovieTextureBindings(movieCandidates);
            using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.ResolveImages, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingTextureDecode)) // [local]
            for (var index = 0; index < info.Images.Count; index++)
            {
                descriptors.Images[index] = index == hostMovie.Luma
                    ? _presenter.CreateHostMovieTextureResource(movieCandidates![index], plane: 0)
                    : index == hostMovie.Chroma
                        ? _presenter.CreateHostMovieTextureResource(movieCandidates![index], plane: 1)
                        : ResolveImageBinding(info.Images[index], snapshot.Images[index], program, index);
            }

            descriptors.Samplers = Scratch.Samplers.Rent(info.Samplers.Count);
            var samplerClasses = Scratch.SamplerClasses.Rent(info.Samplers.Count);
            try
            {
                using var samplerProfile = DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.ResolveSamplers, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count);
                using var samplerScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingSamplers); // [local]
                ClassifySamplerViews(info, descriptors.Images, samplerClasses);
                for (var index = 0; index < info.Samplers.Count; index++)
                {
                    descriptors.Samplers[index] = ResolveSampler(info.Samplers[index], snapshot.Samplers[index], program, index, stage,
                        samplerClasses[index] == 1);
                }
            }
            finally
            {
                Scratch.SamplerClasses.Return(samplerClasses);
            }

            using var shaderDataScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingShaderData); // [local]
            var pureShaderData = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PartPure); // [local] SHARPEMU_PARREC_PROBE
            var shaderData = AssembleShaderData(stage, program, layout);
            pureShaderData.Dispose();
            prepared.ShaderData = shaderData;
            if (layout.Find(DescriptorBindingKind.GlobalDataShare) is not null)
            {
                descriptors.GlobalDataShare = new BufferView(_bufferCache.GdsBuffer.Handle, 0, Vk.WholeSize);
            }

            if (program.CallRecordCallees.Count != 0)
            {
                if (_worker)
                {
                    throw Serial("call records", resumable: true);
                }

                (prepared.CallRecordWords, prepared.CallRecordRanges) = _presenter.PrepareCallRecords(program, stage);
            }

            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingValidation)) // [local]
            {
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.FindAddressBuffers, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                    FindDeviceAddressBuffers(prepared);
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.FindBuffers, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                    FindBuffers(prepared);
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.ValidateImages, program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                    ValidateDrawImageTypes(prepared);
            }
            if (!movieActive && layout.CallTargetCount == 0 && layout.CallArgumentCount == 0 && !layout.UsesBindlessHeap)
            {
                if (frontendReady)
                    NoteStagePreparation(preparations, prepared, snapshot, stage.ShaderBase);
            }

            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"Bindings prepared stage={program.Stage} hash=0x{program.Hash:X16} buffers={info.Buffers.Count} images={info.Images.Count} " +
                    $"samplers={info.Samplers.Count} userData={snapshot.UserData.Length} flattened={snapshot.FlattenedResourceTable.Length} shaderData={shaderData.Length}");
            }

            return prepared;
        }

        // [local] SHARPEMU_PROFILE_RESOLVE: the bindings one PrepareBindings call resolves.
        private static void CountResolveBindings(ShaderResourceInfo info, ResourceSnapshot snapshot)
        {
            var storage = 0;
            foreach (var image in info.Images)
            {
                if (image.ResourceClass == ShaderCompiler.Resources.ImageResourceClass.Storage)
                    storage++;
            }

            ResolveProfile.CountBindings(info.Images.Count, storage, info.Samplers.Count, info.Buffers.Count, snapshot.DeviceAddressRanges.Length);
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

        private static IEnumerable<DeviceAddressRange> DeviceAddressRangesOf(PreparedStageBindings prepared) =>
            prepared.CallRecordRanges.Length == 0
                ? prepared.Stage.Resources.DeviceAddressRanges
                : prepared.Stage.Resources.DeviceAddressRanges.Concat(prepared.CallRecordRanges);

        private const int FoundRangeSetSlots = 16;
        private (DeviceAddressRange[]? Ranges, long RegionStamp, long Spans)[]? _foundRangeSets;
        private int _foundRangeSetNext;

        private void FindDeviceAddressBuffers(PreparedStageBindings prepared)
        {
            DeviceAddressRange[] ranges = prepared.CallRecordRanges.Length == 0
                ? prepared.Stage.Resources.DeviceAddressRanges
                : [.. prepared.Stage.Resources.DeviceAddressRanges, .. prepared.CallRecordRanges];
            if (ranges.Length == 0)
            {
                return;
            }

            var spans = GuestGpuMemoryHook.Current?.SpanVersion ?? 0;
            var foundSets = _worker ? null : _foundRangeSets ??= new (DeviceAddressRange[]?, long, long)[FoundRangeSetSlots];
            var found = false;
            if (foundSets is not null)
            {
                long? stamp = null;
                foreach (var set in foundSets)
                {
                    if (set.Ranges is { } known && set.Spans == spans &&
                        (ReferenceEquals(known, ranges) || known.AsSpan().SequenceEqual(ranges)) &&
                        set.RegionStamp == (stamp ??= RegionStampOf(ranges)))
                    {
                        _foundSetsHit.Increment();
                        found = true;
                        break;
                    }
                }
            }

            foreach (var range in ranges)
            {
                if ((found && !range.Written) || !range.Planned || range.Size == 0 ||
                    range.Base >= PageOwnerTable.AddressSpaceSize || range.Size > PageOwnerTable.AddressSpaceSize - range.Base ||
                    (!range.Written && !_guestMemory.CanRead(range.Base, 1)))
                {
                    continue;
                }

                var size = ClampMappedSize(range.Base, range.Size);
                if (range.Written)
                {
                    PublishImagesUnderWrite(formatted: false, range.Base, size);
                }

                if (!found)
                {
                    _ = FindBuffer(range.Base, size);
                }

                if (range.Written) Gpu.Buffers.RayQueryHle.Instance?.NoteWrite(prepared.Program.Hash, range.Base, range.Size); // [local] SHARPEMU_RQ_MAP
                if (BvhProbes && _bvhWriterCount < 400 && InBvhBlob(range.Base)) // [local]
                {
                    _bvhWriterCount++;
                    Console.Error.WriteLine($"[BVH_WRITER] hash=0x{prepared.Program.Hash:X16} stage={prepared.Program.Stage} range base=0x{range.Base:X} size=0x{range.Size:X} written={range.Written}");
                }
            }

            if (!found && foundSets is not null)
            {
                foundSets[_foundRangeSetNext] = (ranges, RegionStampOf(ranges), spans);
                _foundRangeSetNext = (_foundRangeSetNext + 1) % FoundRangeSetSlots;
            }
        }

        private static int _unresidentPrtBufferLines;

        private bool IsUnresidentPrtPage(ulong address)
        {
            if (_guestMemory.CanRead(address, 1) || !Kernel.KernelRuntimeCompatExports.ContainsPrtRange(address, 1))
            {
                return false;
            }

            if (Interlocked.Exchange(ref _unresidentPrtBufferLines, 1) == 0)
            {
                Console.Error.WriteLine($"[GPU][INFO] A buffer starts in an unresident PRT page and binds as a null buffer: address=0x{address:X16}.");
            }

            return true;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Hash, int Buffer), byte> _unmappedBufferStarts = new();

        private bool IsUnmappedBufferStart(PreparedStageBindings prepared, int index, ulong address)
        {
            if (_guestMemory.CanRead(address, 1))
            {
                return false;
            }

            if (_unmappedBufferStarts.TryAdd((prepared.Program.Hash, index), 0))
            {
                var resource = prepared.Resources.Info.Buffers[index];
                Console.Error.WriteLine($"[GPU][WARN] A buffer descriptor starts in unmapped memory and binds as a null buffer: " +
                    $"address=0x{address:X16} stage={prepared.Program.Stage} hash=0x{prepared.Program.Hash:X16} " +
                    $"shader=0x{prepared.Stage.ShaderBase:X16} buffer={index} pc=0x{resource.FirstUsePc:X} " +
                    $"written={resource.Written} descriptor=[{string.Join(",", prepared.Stage.Resources.Buffers[index].Select(word => $"{word:X8}"))}] " +
                    $"user_data=[{string.Join(",", prepared.Stage.Resources.UserData.Select(word => $"{word:X8}"))}]" +
                    $"{DescribeUserDataPointers(prepared.Stage.Resources.UserData)}.");
            }

            return true;
        }

        private string DescribeUserDataPointers(uint[] userData)
        {
            var text = new System.Text.StringBuilder();
            Span<uint> words = stackalloc uint[12];
            for (var index = 0; index + 1 < userData.Length; index += 2)
            {
                var pointer = userData[index] | ((ulong)(userData[index + 1] & 0xFFFF) << 32);
                if ((userData[index + 1] & 0xFFFF) == 0 || !_guestMemory.TryRead(pointer, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words)))
                {
                    continue;
                }

                text.Append($" pointer[{index}]=0x{pointer:X}:[{string.Join(",", words.ToArray().Select(word => $"{word:X8}"))}]");
            }

            return text.ToString();
        }

        // Publishing a partly written image can grow a buffer past this range:
        // every stage prepares here before any stage takes a buffer handle.
        private void PublishImagesUnderWrite(BufferResource resource, ulong address, ulong size)
        {
            if (resource.Written)
            {
                PublishImagesUnderWrite(resource.Formatted, address, size);
            }
        }

        private void PublishImagesUnderWrite(bool formatted, ulong address, ulong size)
        {
            if (_worker)
            {
                if (_imageCache.WouldPublishImagesUnderWrite(address, size, wholeImages: formatted))
                {
                    throw Serial("image publication", resumable: true);
                }
            }
            else if (formatted)
            {
                _imageCache.PublishImagesUnderFormattedWrite(address, size);
            }
            else
            {
                _imageCache.PublishImagesUnderRawWrite(address, size);
            }
        }

        private long RegionStampOf(DeviceAddressRange[] ranges)
        {
            var stamp = 0L;
            foreach (var range in ranges)
            {
                stamp += _bufferCache.RegionStamp(range.Base, range.Size);
            }

            return stamp;
        }

        // The cache buffer each descriptor's range lives in; a null descriptor has none.
        private void FindBuffers(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            var snapshot = prepared.Stage.Resources;
            var count = prepared.Resources.Info.Buffers.Count;
            var sources = prepared.BufferSources.Length == count && count != 0 ? prepared.BufferSources : Scratch.Sources.Rent(count);
            prepared.BufferSources = sources;
            var memos = BufferSourceMemosOf(program, sources.Length);
            var spans = GuestGpuMemoryHook.Current?.SpanVersion ?? 0;
            var mapping = _guestMemory.MappingGeneration; // [local]
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
                ref var memo = ref memos[index];
                // [local] A binding the memo remembers (same descriptor, footprint and guest spans, and no mapping change
                // since: readability only moves with the mapping generation) was readable when it was noted: the
                // readability checks are for new or changed bindings, and one page lookup answers both (each check
                // repeats it only for its warning).
                var known = mapping != long.MinValue && memo.Mapping == mapping &&
                    memo.Address == descriptor.Address && memo.Requested == requested && memo.Spans == spans;
                if (descriptor.Address == 0 || requested == 0 ||
                    (!known && !_guestMemory.CanRead(descriptor.Address, 1) &&
                     (IsUnresidentPrtPage(descriptor.Address) || IsUnmappedBufferStart(prepared, index, descriptor.Address))))
                {
                    if (ResolveProfile.Enabled && !_worker) ResolveProfile.CountNullBuffer(); // [local]
                    if (program.Hash == 0xFADBE3FB13E9DCFF && Environment.GetEnvironmentVariable("SHARPEMU_TRACK_BUILD") == "1") // [local] why a leaf-writer binding binds as a null buffer
                        Console.Error.WriteLine($"[FADB_NULL] b{index} address=0x{descriptor.Address:X} requested=0x{requested:X} stride={descriptor.Stride} records={descriptor.RecordCount} readable={_guestMemory.CanRead(descriptor.Address, 1)} prt={Kernel.KernelRuntimeCompatExports.ContainsPrtRange(descriptor.Address, 1)} tick={_scheduler.CurrentTick} words=[{string.Join(",", words.Select(w => $"{w:X8}"))}]");
                    sources[index] = (descriptor, default, 0);
                    continue;
                }

                if (Gpu.Buffers.ConservativeRecords.Mode == 2 && Gpu.Buffers.ConservativeRecords.IsVerifiedProgram(program.Hash)) // [local]
                    Gpu.Buffers.ConservativeRecords.VerifyBinding(index, descriptor.RecordCount, requested, _bufferCache.ContainingBufferExtent(descriptor.Address, ulong.MaxValue, 0));
                requested = ConservativeRequested(program.Hash, descriptor, requested); // [local] SHARPEMU_CONSERVATIVE_RECORDS
                var remembered = known || (memo.Address == descriptor.Address && memo.Requested == requested && memo.Spans == spans);
                var size = remembered ? memo.Size : ClampMappedSize(descriptor.Address, requested, prepared, index);
                PublishImagesUnderWrite(prepared.Resources.Info.Buffers[index], descriptor.Address, size);
                if (remembered && memo.RegionStamp == _bufferCache.RegionStamp(descriptor.Address, requested))
                {
                    sources[index] = (descriptor, memo.Buffer, memo.Size);
                }
                else
                {
                    var buffer = FindBuffer(descriptor.Address, size);
                    sources[index] = (descriptor, buffer, size);
                    memo = new BufferSourceMemo(descriptor.Address, requested, _bufferCache.RegionStamp(descriptor.Address, requested), spans, mapping, buffer, size);
                }

                if (Gpu.Buffers.RayQueryHle.Instance is { } rayQuery && prepared.Resources.Info.Buffers[index].Written) // [local] SHARPEMU_RQ_MAP
                    rayQuery.NoteWrite(program.Hash, descriptor.Address, size);
                ProbeBvhRefitBuffer(program.Hash, index, descriptor, requested, size, prepared); // [local]
            }

            // [local] the small blob's header before every dispatch, to catch the writer of +0x8C
            if (_bvhSmallBlob != 0 && _bvhAllDumps < 0 && prepared.Program.Stage == ShaderStageKind.Compute)
            {
                _bvhAllDumps++;
                DumpBvhBuffer($"all{_bvhAllDumps:D5}_{program.Hash:X16}_hdr", default(Gpu.Rendering.BufferDescriptorWords).WithAddress(_bvhSmallBlob + 0x80), 32);
            }

            prepared.BufferSources = sources;
        }

        // [local] SHARPEMU_CONSERVATIVE_RECORDS: a descriptor sized by the sentinel record count spans the rest of the cache
        // buffer holding its base.
        private ulong ConservativeRequested(ulong hash, in BufferDescriptorWords descriptor, ulong requested) =>
            descriptor.RecordCount == Gpu.Buffers.ConservativeRecords.SentinelRecords && Gpu.Buffers.ConservativeRecords.IsListedProgram(hash)
                ? Gpu.Buffers.ConservativeRecords.NoteExtent(_bufferCache.ContainingBufferExtent(descriptor.Address, requested))
                : requested;

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

        internal int _bvhSeq; // [local] also counted by Presenter.WriteCommandData
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
        private static readonly ulong BindWatch = Convert.ToUInt64(Environment.GetEnvironmentVariable("SHARPEMU_BIND_WATCH") ?? "0", 16); // [local]
        private int _bindWatchLogs;
        // [local] The BVH investigation probes (writer/bind logs, dumps, trace pages); off by default.
        internal static readonly bool BvhProbes = Environment.GetEnvironmentVariable("SHARPEMU_BVH_PROBES") == "1";
        private int _bvhRebuildSeenAt;
        private readonly List<(ulong Address, ulong Size)> _bvhScanTables = new();
        private ulong _bvhSmallBlob;
        private int _bvhBindLogs;
        private int _bvhHeaderLogs;
        private int _bvhBlockDumps;
        // [local] Slot dumps of one shader without the other BVH probes (SHARPEMU_SLOT_DUMP_AFTER_TICK delays them).
        private static readonly ulong SlotDumpAfterTick = ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SLOT_DUMP_AFTER_TICK"), out var after) ? after : 0;
        private void DumpSlotIfRequested(ulong hash, int index, BufferDescriptorWords descriptor, ulong requested)
        {
            if (SlotDumpHash == 0 || hash != SlotDumpHash || _slotDumps >= SlotDumpLimit || _scheduler.CurrentTick < SlotDumpAfterTick)
                return;
            _slotDumps++;
            var tick = _scheduler.CurrentTick; var slotAddress = descriptor.Address; var slotSize = Math.Min(requested, 0x400000UL);
            try { _bufferCache.DumpDeviceRange(slotAddress, slotSize, $"C:/Users/danyy/AppData/Local/Temp/gta/slot_{hash:X16}_s{index}_pre_{slotAddress:X}_{slotSize:X}_{tick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[SLOT_DUMP] {e.Message}"); }
        }

        // [local] More BVH build shaders whose written buffers are dumped in the rebuild window (SHARPEMU_BVH_WRITER_HASHES, ';'-separated).
        private static readonly HashSet<ulong> BvhWriterExtraHashes = (Environment.GetEnvironmentVariable("SHARPEMU_BVH_WRITER_HASHES") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => Convert.ToUInt64(h.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? h[2..] : h, 16)).ToHashSet();

        private void ProbeBvhWriter(PreparedStageBindings prepared, int index, BufferDescriptorWords descriptor, ulong requested)
        {
            if (!BvhProbes) return; // [local]
            var listProducer = prepared.Program.Hash is 0xF6F2D6298F2771AD or 0x35B85620669D35A4 or 0x4F1F0F92FE39EC8B or 0x4505F72A09CC1B12 or 0xF05BBF883D986FC9 or 0xCA99120EAF300BFD ||
                BvhWriterExtraHashes.Contains(prepared.Program.Hash); // [local] SHARPEMU_BVH_WRITER_HASHES=<hex;hex;...>
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
        internal void DumpBvhBuffer(string kind, BufferDescriptorWords descriptor, ulong size)
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

        private int _postDumps; // [local] SHARPEMU_TRACK_BUILD post-dispatch dumps
        private int _preDumps; // [local] SHARPEMU_TRACK_BUILD pre-dispatch dumps of 0xFADB
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
        // [local] The probe body is its own method: its lambdas capture hash, index and descriptor, and C# allocates that
        // closure on entry to the method that declares them, so with the probes off every bound buffer paid for it.
        private void ProbeBvhRefitBuffer(ulong hash, int index, BufferDescriptorWords descriptor, ulong requested, ulong size, PreparedStageBindings prepared)
        {
            if (!BvhProbes) { DumpSlotIfRequested(hash, index, descriptor, requested); return; } // [local]
            ProbeBvhRefitBufferCore(hash, index, descriptor, requested, size, prepared);
        }

        private void ProbeBvhRefitBufferCore(ulong hash, int index, BufferDescriptorWords descriptor, ulong requested, ulong size, PreparedStageBindings prepared)
        {
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
            if (Environment.GetEnvironmentVariable("SHARPEMU_TRACK_BUILD") == "1" && _postDumps < 400 &&
                hash is 0xB728F324C95A202C or 0xCA99120EAF300BFD or 0x4505F72A09CC1B12 or 0x4F1F0F92FE39EC8B or 0xF05BBF883D986FC9 or 0x35B85620669D35A4 or 0xFADBE3FB13E9DCFF or 0x260F2884A4A5E7C0 &&
                size <= 0x40000UL) // [local] every bound buffer of the build stages after the dispatch (post_<hash>_b<i>_...)
            {
                _postDumps++;
                var postHash = hash; var postIndex = index; var postAddress = descriptor.Address; var postSize = size; var postTick = _scheduler.CurrentTick;
                Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(postAddress, postSize, $"C:/Users/danyy/AppData/Local/Temp/gta/post_{postHash:X16}_b{postIndex}_{postAddress:X}_{postSize:X}_{postTick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] post dump failed: {e.Message}"); } });
            }
            if (Environment.GetEnvironmentVariable("SHARPEMU_TRACK_BUILD") == "1" && hash == 0xFADBE3FB13E9DCFF && _preDumps < 1500) // [local] every bound buffer of the leaf writer before its dispatch (no size cap)
            {
                _preDumps++;
                try { _bufferCache.DumpDeviceRange(descriptor.Address, size, $"C:/Users/danyy/AppData/Local/Temp/gta/pre_{hash:X16}_b{index}_{descriptor.Address:X}_{size:X}_{_scheduler.CurrentTick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] pre dump failed: {e.Message}"); }
                // [local] the CPU-side bytes of the inputs and the constant block (s[2:3] -> V#s + matrix; s4 = record base) at prepare time
                if (index is 0 or 1) try { _bufferCache.DumpGuestRange(descriptor.Address, size, $"C:/Users/danyy/AppData/Local/Temp/gta/guest_{hash:X16}_b{index}_{descriptor.Address:X}_{size:X}_{_scheduler.CurrentTick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] guest dump failed: {e.Message}"); }
                if (index == 0)
                {
                    var ud = prepared.Stage.Resources.UserData;
                    var blockAddress = ud.Length > 3 ? ud[2] | ((ulong)ud[3] << 32) : 0;
                    try { if (blockAddress != 0) _bufferCache.DumpGuestRange(blockAddress, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/guestblk_{hash:X16}_{descriptor.Address:X}_{string.Join("-", ud.Skip(4).Take(6).Select(w => w.ToString("X")))}_{_scheduler.CurrentTick}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] guest block dump failed: {e.Message}"); }
                }
            }
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
                lock (Presenter.WatchedBounds) if (Presenter.WatchedBounds.Count < 4096) Presenter.WatchedBounds.Add(descriptor.Address & ~0x1FUL);
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
            if (_worker)
            {
                Requests.NeedsBdaSweep = true;
                Requests.BdaSpans = spans;
                Requests.BdaMapping = _bdaSpanMapping;
                return;
            }

            foreach (var stage in preparation.Stages)
            {
                if (!stage.Program.UsesDeviceAddresses) continue;
                foreach (var range in stage.Stage.Resources.DeviceAddressRanges)
                {
                    _bufferCache.NoteDeviceAddressWindow(range.Base);
                }
            }

            // [local] One CPU-dirty sweep per guest submission (SHARPEMU_BDA_SWEEP_PER_SUBMIT=0 sweeps per dispatch).
            // The data a submission reads is what the CPU wrote before submitting it; later CPU
            // writes belong to the next submission, so a second sweep inside one only re-protects.
            var submitId = _currentSubmitId;
            if (BdaSweepPerSubmit && submitId != 0 && submitId == _bdaSweepSubmitId && _bdaSpanMapping == _bdaSweepSubmitMapping)
                return;
            _bdaSweepSubmitId = submitId;
            _bdaSweepSubmitMapping = _bdaSpanMapping;
            _bufferCache.PrepareBda(spans, _bdaSpanMapping);
        }

        private static readonly bool BdaSweepPerSubmit = Environment.GetEnvironmentVariable("SHARPEMU_BDA_SWEEP_PER_SUBMIT") != "0"; // [local] on by default: rt375 9,15 → 9,94 FPS
        private ulong _bdaSweepSubmitId, _bdaSweepSubmitMapping;

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
            var info = stage.Resources.Info;
            using var stallProfile = DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.Bind, stage.Program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count);
            if (Gpu.Buffers.CpWriteDeferProbe.Enabled && !_worker) // [local]
            {
                if (stage.Program.UsesDeviceAddresses) Gpu.Buffers.CpWriteDeferProbe.OnUnknownReads();
                foreach (var range in stage.Stage.Resources.DeviceAddressRanges)
                    if (range.Planned && range.Size != 0) Gpu.Buffers.CpWriteDeferProbe.OnRead(range.Base, range.Size);
            }
            if (Gpu.Vulkan.ScopeBarrierTracker.Enabled && !_worker && stage.Program.UsesDeviceAddresses) Gpu.Vulkan.ScopeBarrierTracker.NoteWrite(); // [local] counted as stores
            if (!_worker)
            {
                _bufferCache.BeginUploadBatch();
            }

            try
            {
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.BindBuffers, stage.Program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingBuffers)) // [local]
                    BindBuffers(stage);
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.ObtainAddressRanges, stage.Program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingDeviceRanges)) // [local]
                    ObtainDeviceAddressRanges(stage);
            }
            finally
            {
                if (!_worker)
                {
                    _bufferCache.EndUploadBatch();
                }
            }

            if (KeepsTextureSet(stage))
            {
                stage.TexturesKept = true;
                Interlocked.Increment(ref _texturesKept);
            }
            else
            {
                using (DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.BindImages, stage.Program.Hash, info.Buffers.Count, info.Images.Count, info.Samplers.Count))
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BindingImages)) // [local]
                    BindImages(stage);
            }

            _preparedTextures.Add(stage.Textures);
        }

        // Textures bound for the draw being prepared; cleared when its preparation closes.
        private readonly List<TextureResource[]> _preparedTextures = new();

        private BufferView NullStorageBuffer() => new(NullBufferHandle, 0, NullStorageBufferBytes);

        // A storage buffer view on the cache buffer, aligned down with the adjustment carried in the memory offsets.
        private BufferView BindStorageBuffer(
            in BufferDescriptorWords descriptor,
            BufferResource resource,
            ShaderProgramInfo program,
            int slot,
            ResourceSlotIdentifier bufferIdentifier,
            ulong size,
            out uint memoryOffset)
        {
            memoryOffset = 0;
            var address = descriptor.Address;
            if (address == 0 || size == 0 || IsUnresidentPrtPage(address) || !_guestMemory.CanRead(address, 1))
            {
                return NullStorageBuffer();
            }

            if (MeshDrawTrace.Active) MeshDrawTrace.Write("buffer-binding", $"stage={program.Stage} hash=0x{program.Hash:X16} slot={slot} address=0x{address:X16} requested=0x{descriptor.Footprint() ?? 0:X} size=0x{size:X} written={resource.Written} formatted={resource.Formatted}");
            var alignment = _presenter._minStorageBufferOffsetAlignment;
            var maxRange = _deviceInfo.MaxStorageBufferRange;
            if (alignment == 0 || size > maxRange)
            {
                throw SubmissionScheduler.Fatal($"A storage buffer range or the device alignment is unsupported: buffer={slot} size=0x{size:X} alignment={alignment} hash=0x{program.Hash:X16}.");
            }

            if (BindWatch != 0 && resource.Written && address <= BindWatch && BindWatch < address + size && _bindWatchLogs++ < 60) Console.Error.WriteLine($"[BIND_WATCH] hash=0x{program.Hash:X16} slot={slot} address=0x{address:X} size=0x{size:X} stride={descriptor.Stride} records={descriptor.RecordCount}"); // [local]
            if (SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TracesExtra(address, size)) SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Trace(address, size, $"bind hash=0x{program.Hash:X16} slot={slot} written={resource.Written} formatted={resource.Formatted} submission_tick={_scheduler.CurrentTick}"); // [local]
            if (program.Hash == 0x743E8AE5A36889D2 && !resource.Written && _hdrAges.Count != 0 && _refitInDumps < 600 && _scheduler.CurrentTick < 20000) // [local]
            {
                _refitInDumps++;
                try { _bufferCache.DumpDeviceRange(address, Math.Min(size, 0x4000UL), $"C:/Users/danyy/AppData/Local/Temp/gta/refitin_{_scheduler.CurrentTick}_{slot}_{address:X}_{size:X}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] refit input dump failed: {e.Message}"); }
            }
            if (resource.Written && _hdrAges.Count != 0 && program.Hash != 0x56D6851BA6028DF7 && _hdrAfterDumps < 3000) // [local]
            {
                foreach (var (hdrAddress, _, _) in _hdrAges)
                {
                    if (hdrAddress < address || hdrAddress >= address + size) continue;
                    _hdrAfterDumps++;
                    var hash = program.Hash; var tick = _scheduler.CurrentTick; var slotIndex = slot;
                    Gpu.Buffers.GuestBufferCache.PostDispatchHooks.Add(() => { try { _bufferCache.DumpDeviceRange(hdrAddress, 256, $"C:/Users/danyy/AppData/Local/Temp/gta/hdrafter_{hdrAddress:X}_{tick}_{hash:X16}_{slotIndex}.gpu.bin"); } catch (Exception e) { Console.Error.WriteLine($"[BVH_HDRWRITE] after dump failed: {e.Message}"); } });
                }
            }
            var (buffer, offset, available) = ObtainStorageBuffer(address, size, resource, bufferIdentifier, program.Hash);
            if (program.Hash == 0xF1D19431555095C0 && slot == 0 && _geomAfterLogs < 300 && Environment.GetEnvironmentVariable("SHARPEMU_GEOM_PROBE") == "1") // [local] after the upload
            {
                _geomAfterLogs++;
                Console.Error.WriteLine($"[GEOM_AFTER] tick={_scheduler.CurrentTick} table=0x{address:X} size=0x{size:X} cpuDirtyAfterObtain={_bufferCache.HasCpuDirtyPages(address, size)} buffer=0x{buffer.Handle.Handle:X} offset=0x{offset:X}");
            }
            var bufferHandle = buffer.Handle;
            _presenter.TraceBufferParameter(program.Hash, slot, address, size, bufferHandle.Handle, offset, resource.Written);
            var alignedOffset = offset - offset % alignment;
            // The shader adds the adjustment to its byte address, so a base that is only two byte aligned
            // keeps the byte phase the guest's 16 bit accesses expect.
            var adjustment = offset - alignedOffset;
            if (adjustment >= MaxMemoryOffsetAdjustment || size > maxRange - adjustment)
            {
                throw SubmissionScheduler.Fatal($"A storage buffer offset adjustment is unsupported: buffer={slot} adjustment={adjustment} hash=0x{program.Hash:X16}.");
            }

            memoryOffset = (uint)adjustment;
            return new BufferView(bufferHandle, alignedOffset,
                StorageBufferViewRange(size + adjustment, available + adjustment, maxRange));
        }

        private (GpuBuffer Buffer, ulong Offset, ulong Available) ObtainStorageBuffer(ulong address, ulong size, BufferResource resource,
            ResourceSlotIdentifier bufferIdentifier, ulong programHash)
        {
            var obtained = _worker
                ? ObtainBufferRangeForSlice(address, size, resource.Written, resource.Formatted, invalidateImages: resource.Formatted && resource.Written)
                : (_bufferCache.ObtainBuffer(address, size, resource.Written, isTexelBuffer: resource.Formatted, bufferIdentifier, programHash) is var found
                    ? (found.Buffer, found.Offset, found.Buffer.Size - found.Offset) : default);
            // The images a raw write covers in part, and all images under a formatted write, were published
            // when the stage was prepared (FindBuffers).
            if (!_worker && resource.Written && resource.Formatted)
            {
                _imageCache.InvalidateMemoryFromGpuAfterPublish(address, size);
            }
            else if (!_worker && resource.Written)
            {
                _imageCache.InvalidateMemoryCopiesFromGpu(address, size);
            }

            return obtained;
        }

        private uint[] _tableBlockWords = new uint[64];
        private static long _tableBlocks;
        private static long _descriptorsShadowed;
        private static readonly ThreadCounter _foundSetsHit = new();
        private static readonly ThreadCounter _sliceUseHits = new();
        private static readonly ThreadCounter _sliceUseMisses = new();
        private static readonly ThreadCounter _sliceUseStale = new();

        internal static string TakeTableReport() =>
            FormattableString.Invariant($"table_blocks={Interlocked.Exchange(ref _tableBlocks, 0)} descriptors_shadowed={Interlocked.Exchange(ref _descriptorsShadowed, 0)} ") +
            FormattableString.Invariant($"vsharp_cache=hit:{_sliceUseHits.Take()},miss:{_sliceUseMisses.Take()},stale:{_sliceUseStale.Take()} found_sets_hit={_foundSetsHit.Take()} ");

        private ulong NullBufferAddress => _nullBufferAddress != 0 ? _nullBufferAddress : _nullBufferAddress = _bufferCache.GetBuffer(GuestBufferCache.NullBufferId).DeviceAddress;

        private ulong _nullBufferAddress;

        private void WriteTableBlock(PreparedStageBindings prepared, uint[] shaderData)
        {
            var program = prepared.Program;
            var layout = prepared.Layout;
            var info = prepared.Resources.Info;
            var flattened = layout.UsesFlattenedTable ? prepared.Stage.Resources.FlattenedResourceTable : [];
            var length = (int)layout.TableFlattenedDword + flattened.Length;
            if (_tableBlockWords.Length < length)
            {
                _tableBlockWords = new uint[Math.Max(length, _tableBlockWords.Length * 2)];
            }

            var words = _tableBlockWords.AsSpan(0, length);
            words[0] = (uint)flattened.Length;
            for (var index = 0; index < info.Buffers.Count; index++)
            {
                var (descriptor, bufferIdentifier, size) = prepared.BufferSources[index];
                var record = (int)(BindingLayout.TableBlockHeaderDwords + (uint)index * BindingLayout.TableBufferDwords);
                var address = NullBufferAddress;
                var dwords = 0u;
                var bias = 0u;
                if (descriptor.Address != 0 && size != 0)
                {
                    var (buffer, offset, available) = ObtainStorageBuffer(descriptor.Address, size, info.Buffers[index], bufferIdentifier, program.Hash);
                    _presenter.TraceBufferParameter(program.Hash, index, descriptor.Address, size, buffer.Handle.Handle, offset, info.Buffers[index].Written);
                    var exact = buffer.DeviceAddress + offset;
                    bias = (uint)(exact % sizeof(uint));
                    address = exact - bias;
                    var bytes = StorageBufferViewRange(size + bias, available + bias, ulong.MaxValue);
                    if (bytes >> 2 > uint.MaxValue)
                    {
                        throw SubmissionScheduler.Fatal($"A table buffer size is unsupported: buffer={index} address=0x{exact:X} size=0x{size:X} hash=0x{program.Hash:X16}.");
                    }

                    dwords = (uint)(bytes >> 2);
                }

                words[record] = (uint)address;
                words[record + 1] = (uint)(address >> 32);
                words[record + 2] = dwords;
                words[record + 3] = bias;
            }

            flattened.CopyTo(words[(int)layout.TableFlattenedDword..]);
            Interlocked.Increment(ref _tableBlocks);
            var block = UploadTableBlock(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words), program);
            shaderData[layout.TableBlockDword] = (uint)block;
            shaderData[layout.TableBlockDword + 1] = (uint)(block >> 32);
        }

        private ulong UploadTableBlock(ReadOnlySpan<byte> bytes, ShaderProgramInfo program)
        {
            if (_worker)
            {
                if (!_arena!.TryReserve((ulong)bytes.Length, TableBlockAlignment, out var reservation))
                {
                    throw Serial("arena", resumable: true);
                }

                bytes.CopyTo(reservation.Destination);
                _arena.Commit(in reservation);
                return reservation.Chunk.Buffer.DeviceAddress + reservation.Offset;
            }

            if (_contentUploads is { } arena && arena.TryUploadAddress(bytes, TableBlockAlignment, out var address))
            {
                return address;
            }

            var stream = _bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream);
            if (stream.TryMap((ulong)bytes.Length, out var offset, TableBlockAlignment))
            {
                bytes.CopyTo(stream.Mapped[(int)offset..]);
                stream.Commit();
                return stream.DeviceAddress + offset;
            }

            throw SubmissionScheduler.Fatal($"No upload ring could take a table block: bytes={bytes.Length} hash=0x{program.Hash:X16}.");
        }

        private const uint TableBlockAlignment = 16;

        private BufferView UploadDwords(uint[] data, string label, ShaderProgramInfo program, bool byIdentity)
        {
            if (data.Length == 0)
            {
                throw SubmissionScheduler.Fatal($"The {label} upload is empty: hash=0x{program.Hash:X16}.");
            }

            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(data);
            var alignment = Math.Max(TransientDataAlignment, (uint)_presenter._minStorageBufferOffsetAlignment);
            if (!_worker && _contentUploads is { } arena &&
                (byIdentity ? arena.TryUpload(data, alignment, out var arenaBuffer, out var arenaOffset) : arena.TryUpload(bytes, alignment, out arenaBuffer, out arenaOffset)))
            {
                return new BufferView(arenaBuffer, arenaOffset, (ulong)bytes.Length);
            }

            var binding = UploadTransient(bytes, alignment);
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

            if (layout.UsesTableBlock)
            {
                WriteTableBlock(prepared, shaderData);
            }
            else
            {
                BindBufferViews(prepared, shaderData);
            }

            if (layout.MipStatisticsCount != 0)
            {
                if (_worker && _presenter._lodStatistics is null)
                {
                    throw Serial("lod statistics", resumable: true);
                }

                _presenter.LodStatistics().EnsureInitialized();
                _presenter.WriteMipStatisticsFields(shaderData, layout, info, snapshot.Images, snapshot.MipStatistics);
            }

            if (layout.Find(DescriptorBindingKind.FlattenedResourceTable) is not null)
            {
                prepared.Descriptors.FlattenedTable = UploadDwords(snapshot.FlattenedResourceTable, "flattened resource table", program, byIdentity: true);
                if (program.Hash == 0x4481C2A89BEA7646 && snapshot.FlattenedResourceTable.Length >= 13 && Environment.GetEnvironmentVariable("SHARPEMU_KEY_PROBE") == "1" && DegenerateBounds(snapshot.FlattenedResourceTable) && _keyTableLogs < 5000) // [local] the bounds words 0x4481 actually receives
                {
                    _keyTableLogs++;
                    Console.Error.WriteLine($"[KEYTABLE] tick={_scheduler.CurrentTick} words=[{string.Join(",", snapshot.FlattenedResourceTable.Select(w => w.ToString("X8")))}] floats=[{string.Join(",", snapshot.FlattenedResourceTable.Select(w => BitConverter.UInt32BitsToSingle(w).ToString("G4")))}]");
                }
            }

            if (layout.Find(DescriptorBindingKind.ShaderData) is not null)
            {
                prepared.Descriptors.ShaderData = UploadDwords(shaderData, "shader data", program, byIdentity: false);
            }

            if (layout.Find(DescriptorBindingKind.CallRecords) is not null)
            {
                prepared.Descriptors.CallRecords = UploadDwords(prepared.CallRecordWords, "call records", program, byIdentity: false);
            }
        }

        private void BindBufferViews(PreparedStageBindings prepared, uint[] shaderData)
        {
            var program = prepared.Program;
            var layout = prepared.Layout;
            var info = prepared.Resources.Info;
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
                var (descriptor, bufferIdentifier, mappedSize) = prepared.BufferSources[index];
                // [local] SHARPEMU_BUFFER_SIZE_REUSE: 1 (default) takes the size FindBuffers clamped, as upstream does; 0 clamps
                // again, 2 clamps again and counts the reused sizes that differ.
                if (BufferSizeReuseMode != 1 && mappedSize != 0)
                    mappedSize = ReuseClampedSize(descriptor.Address, ConservativeRequested(program.Hash, descriptor, descriptor.Footprint() ?? 0),
                        BufferSizeReuseMode == 0 ? 0 : mappedSize);
                views[index] = BindStorageBuffer(in descriptor, info.Buffers[index], program, index, bufferIdentifier, mappedSize, out var memoryOffset);
                var dword = layout.MemoryOffsetDword + (uint)index / 4;
                var shift = ((uint)index % 4) * 8;
                shaderData[dword] |= memoryOffset << (int)shift;
                if (layout.UsesBufferLengths)
                    shaderData[layout.BufferLengthsDword + (uint)index] = checked((uint)(views[index].Range / sizeof(uint)));
            }

            prepared.Descriptors.Buffers = views;
        }

        // Device-address reads need persistent page-table entries before the shader runs.
        private void ObtainDeviceAddressRanges(PreparedStageBindings prepared)
        {
            var program = prepared.Program;
            foreach (var range in DeviceAddressRangesOf(prepared))
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
                    if (Diagnostics.TableDirtyProbe.Overlaps(range.Base, size)) Diagnostics.TableDirtyProbe.Note($"bda hash=0x{program.Hash:X16} conservative={range.Conservative}", range.Base, size, _scheduler.CurrentTick); // [local]
                    if (_worker)
                    {
                        _ = ObtainBufferForSlice(range.Base, size, isWritten: true, isTexelBuffer: false);
                    }
                    else
                    {
                        _ = _bufferCache.ObtainBuffer(range.Base, size, isWritten: true, traceShaderHash: program.Hash, conservativeWrite: range.Conservative); // [local] conservativeWrite
                        _imageCache.InvalidateMemoryCopiesFromGpu(range.Base, size);
                    }
                }
                else
                {
                    // A held command write or a pending dump lands before the shader reads the range.
                    CompleteOverlappingPublications(range.Base, size, includeLabels: false);
                    // Stream buffers do not populate the device-address page table.
                    _ = FindBuffer(range.Base, size);
                    if (_worker)
                    {
                        Requests.SyncRanges.Add((range.Base, size));
                    }
                    else
                    {
                        _bufferCache.SynchronizeBuffersInRange(range.Base, size);
                    }
                }
            }
        }

        private const int MaxRebindPasses = 3;
        private static readonly uint[] NullImageWords = new uint[8];
        private static int _nullRebindReports;

        // Finding an image again can replace another image of the same dispatch: two bindings over
        // the same memory with layouts one cached image cannot hold at once, where the guest reads
        // two views of one memory. The stale bindings are found again until none is left; one still
        // losing its image after MaxRebindPasses reads as a null image. Returns how many did.
        // [local] The same loop with an explicit state, so the hot caller (BindImages, every stage of every draw) passes
        // static lambdas and allocates no closure: ~270 KB per frame in GTA V (gz08).
        internal static int RefindStaleBindingsOf<TState>(int count, TState state, Func<TState, int, bool> isStale, Action<TState, int, bool> findAgain)
        {
            var nulled = 0;
            for (var pass = 0; ; pass++)
            {
                var refound = false;
                for (var index = 0; index < count; index++)
                {
                    if (!isStale(state, index))
                    {
                        continue;
                    }

                    if (pass > MaxRebindPasses)
                    {
                        throw SubmissionScheduler.Fatal($"An image binding is stale after it was bound as a null image: image={index}.");
                    }

                    nulled += pass == MaxRebindPasses ? 1 : 0;
                    findAgain(state, index, pass == MaxRebindPasses);
                    refound = true;
                }

                if (!refound)
                {
                    return nulled;
                }
            }
        }

        internal static int RefindStaleBindings(int count, Func<int, bool> isStale, Action<int, bool> findAgain)
        {
            var nulled = 0;
            for (var pass = 0; ; pass++)
            {
                var refound = false;
                for (var index = 0; index < count; index++)
                {
                    if (!isStale(index))
                    {
                        continue;
                    }

                    if (pass > MaxRebindPasses)
                    {
                        throw SubmissionScheduler.Fatal($"An image binding is stale after it was bound as a null image: image={index}.");
                    }

                    nulled += pass == MaxRebindPasses ? 1 : 0;
                    findAgain(index, pass == MaxRebindPasses);
                    refound = true;
                }

                if (!refound)
                {
                    return nulled;
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
            var nulled = RefindStaleBindingsOf(
                images.Length,
                (Host: this, Prepared: prepared, Images: images),
                static (s, index) => !s.Images[index].IsHostMovie && s.Host.IsStaleImage(s.Images[index].ImageIdentifier, out _),
                static (s, index, asNull) =>
                {
                    if (s.Host._imageCache.TryGetImage(s.Images[index].ImageIdentifier, out var stale))
                    {
                        s.Host.BindingOf(stale) = default;
                    }

                    s.Images[index] = s.Host.ResolveImageBinding(s.Prepared.Resources.Info.Images[index],
                        asNull ? NullImageWords : s.Prepared.Stage.Resources.Images[index], s.Prepared.Program, index);
                    s.Prepared.Memo?.Invalidate();
                });
            if (nulled != 0 && Interlocked.Increment(ref _nullRebindReports) <= 32)
            {
                Console.Error.WriteLine($"[GPU][WARN] {nulled} image binding(s) of 0x{program.Hash:X16} kept losing their image to another binding over the same memory; they read as null images");
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
                if (prepared.Memoized && !binding.IsStorage && binding.View.Handle != 0 && binding.CachedImage is not null &&
                    (_worker ? _imageCache.CanReuseAcquiredForSlice(binding.ImageIdentifier, binding.Image.Handle) : _imageCache.TryReuseAcquired(binding.ImageIdentifier, binding.Image.Handle)))
                {
                    if (_worker)
                    {
                        Requests.AddImage(new ImageSliceUse(binding.ImageIdentifier, binding.CachedImage, binding.Request));
                    }

                    Interlocked.Increment(ref _stageMemoAcquireReuses);
                    continue;
                }

                var image = Image(binding.ImageIdentifier);
                if (binding.IsStorage && image.Description.HasStencil && ViewFormatRules.IsStencilViewFormat(view.Format))
                {
                    image = AcquireStencilStorage(binding, image);
                    binding.Preparation?.Invalidate();
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
                        mipViews[mip] = _worker ? AcquireTextureViewForSlice(binding.ImageIdentifier, in mipRequest) : _imageCache.AcquireTextureView(binding.ImageIdentifier, mipRequest);
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

                    binding.View = _worker ? AcquireTextureViewForSlice(binding.ImageIdentifier, in binding.Request) : _imageCache.AcquireTextureView(binding.ImageIdentifier, binding.Request);
                    binding.MipViews = [];
                }

                image.Uses.Storage |= binding.IsStorage;
                if (binding.IsStorage && Gpu.Vulkan.ScopeBarrierTracker.Enabled && !_worker) Gpu.Vulkan.ScopeBarrierTracker.NoteWrite(); // [local]
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
                    _presenter.RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
                }
            }
        }

        private readonly Stack<PreparedStageBindings> _spareStages = new();

        private PreparedStageBindings RentStage(ShaderStageResources stage, ShaderProgramInfo program)
        {
            var prepared = Presenter.RenderPools && _spareStages.Count != 0 ? _spareStages.Pop() : new PreparedStageBindings(); // [local] SHARPEMU_RENDER_POOLS
            prepared.Reset(stage, program);
            return prepared;
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
            stage.Reset(default, null!);
            if (Presenter.RenderPools && _spareStages.Count < 8) // [local] SHARPEMU_RENDER_POOLS
            {
                _spareStages.Push(stage);
            }
        }

        private static DescriptorImageInfo ImageInfo(TextureResource texture, uint element, ShaderProgramInfo program, int index)
        {
            var view = texture.MipViews.Length == 0
                ? (element == 0 ? texture.View : default)
                : (element < (uint)texture.MipViews.Length ? texture.MipViews[element] : default);
            if (view.Handle == 0)
            {
                throw SubmissionScheduler.Fatal($"An image binding has no view for its element: image={index} element={element} hash=0x{program.Hash:X16}.");
            }

            return new DescriptorImageInfo { ImageView = view, ImageLayout = ImageLayout.General };
        }

        // [local] P1 of transiciones-diferidas.md (SHARPEMU_PREPARE_STAGE_IMAGES, RenderExecutor): the image work of
        // CommitBindings, recorded right after the attachments are acquired. Between the two points the draw records only
        // pipeline creation and vertex buffer binds, which barriers do not order, so the GPU sees the same commands;
        // recording no longer ends a rendering scope.
        public void PrepareStageImages(PipelineBindPoint bindPoint, ReadOnlySpan<IPreparedBindings> stages)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.StageImagePreparation);
            var preparation = RequirePreparation();
            SubstituteStencilStorage(preparation, stages);
            var uploadStage = bindPoint == PipelineBindPoint.Compute ? PipelineStageFlags.ComputeShaderBit : PipelineStageFlags.FragmentShaderBit;
            foreach (var prepared in stages)
            {
                var stage = (PreparedStageBindings)prepared;
                _ = RecordStageImageWork(stage, uploadStage);
                stage.ImagesPrepared = true;
            }
        }

        // All stages that read the same stencil bytes share the shader's working image.
        private static void SubstituteStencilStorage(RenderPreparation preparation, ReadOnlySpan<IPreparedBindings> stages)
        {
            if (preparation.StencilStorageImages is not { } stencilImages)
            {
                return;
            }

            foreach (var prepared in stages)
            {
                foreach (var texture in ((PreparedStageBindings)prepared).Textures)
                {
                    if (texture.CachedImage is { } attachment && ViewFormatRules.IsStencilViewFormat(texture.Request.View.Format) &&
                        stencilImages.TryGetValue(attachment, out var storage))
                    {
                        texture.Preparation?.Invalidate();
                        texture.CachedImage = storage;
                        texture.Image = storage.Backing.Handle;
                        texture.View = storage.GetOrCreateView(texture.Request.View with { Aspect = ImageAspectFlags.ColorBit });
                        texture.MipViews = [];
                    }
                }
            }
        }

        // [local] The image work of one stage: the data-share barrier, the texture transitions and the host movie
        // uploads. True when it recorded the data-share barrier on the main command buffer.
        private bool RecordStageImageWork(PreparedStageBindings stage, PipelineStageFlags uploadStage)
        {
            var descriptors = stage.Descriptors;
            var dataShare = false;
            if (descriptors.GlobalDataShare.Buffer.Handle != 0)
            {
                // The host and every earlier queue write to the data share complete before the shader reads it.
                EndRendering(ScopeBreak.GlobalDataShare);
                var command = BeginBatchedGuestCommands();
                var barrier = GlobalDataShareBarrier.Make(descriptors.GlobalDataShare.Buffer);
                var stageFlag = DescriptorWriter.ShaderStageFlag(StageOf(stage.Program));
                VulkanSynchronization.PipelineBarrier(_vk,command, GlobalDataShareBarrier.SourceStages, DescriptorWriter.PipelineStageFlag(stageFlag), 0, 0, null, 1, &barrier, 0, null, BarrierSource.GlobalDataShare);
                dataShare = true;
            }

            if (!stage.TexturesKept || !KeepsTextureSet(stage))
            {
                RecordStageTextureTransitions(stage.Textures);
            }

            foreach (var texture in stage.Textures)
            {
                if (texture.IsHostMovie && texture.NeedsUpload)
                {
                    EndRendering();
                    break;
                }
            }

            if (!_worker)
            {
                _presenter.RecordHostMovieUploads(stage.Textures, uploadStage);
            }

            return dataShare;
        }

        // Joins every stage's writes, records the image work and binds the set by push or from the heap.
        public void CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DescriptorCommit);
            var preparation = RequirePreparation();
            var entry = RequirePipelineEntry(in pipeline);
            var command = BeginBatchedGuestStateCommands();
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
                if ((bindPoint == PipelineBindPoint.Graphics && (stageFlag & (ShaderStageFlags.VertexBit | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TessellationEvaluationBit)) == 0) ||
                    (bindPoint == PipelineBindPoint.Compute && stageFlag != ShaderStageFlags.ComputeBit))
                {
                    throw SubmissionScheduler.Fatal($"A stage does not belong to the bind point: stage={stage.Program.Stage} bindPoint={bindPoint}.");
                }

                // Indexed loops over the layout's lists: a foreach on IReadOnlyList allocates an enumerator per draw.
                var countedBindings = stage.Layout.Descriptors;
                for (var bindingIndex = 0; bindingIndex < countedBindings.Count; bindingIndex++)
                {
                    var binding = countedBindings[bindingIndex];
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

            // [local] Prepared stages substituted before their transitions (PrepareStageImages).
            if (stages.Length == 0 || !((PreparedStageBindings)stages[0]).ImagesPrepared)
            {
                SubstituteStencilStorage(preparation, stages);
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
            var textures = textureCount == 0 ? [] : System.Buffers.ArrayPool<TextureResource>.Shared.Rent(textureCount);
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
                    if (stage.ImagesPrepared) // [local] PrepareStageImages recorded it
                    {
                        if (descriptors.GlobalDataShare.Buffer.Handle != 0)
                        {
                            command = BeginBatchedGuestCommands();
                        }
                    }
                    else if (RecordStageImageWork(stage, uploadStage))
                    {
                        command = BeginBatchedGuestCommands();
                    }

                    if (TextureSetSlot(program) is var textureSlot and >= 0)
                    {
                        _committedTextureSets[textureSlot] = stage.Memoized && stage.Memo is { Valid: true } committedMemo ? committedMemo : null;
                    }

                    foreach (var texture in stage.Textures)
                    {
                        textures[textureIndex++] = texture;
                    }

                    var occurrences = occurrenceScratch[..descriptors.Images.Length];
                    occurrences.Clear();
                    var bindings = stage.Layout.Descriptors;
                    for (var bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                    {
                        var binding = bindings[bindingIndex];
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
                            for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
                            {
                                var resource = resources[resourceIndex];
                                imageInfos[imageIndex++] = ImageInfo(descriptors.Images[(int)resource], occurrences[(int)resource]++, program, (int)resource);
                            }
                        }
                        else
                        {
                            switch (binding.Kind)
                            {
                                case DescriptorBindingKind.Buffers:
                                    for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
                                    {
                                        var resource = resources[resourceIndex];
                                        var view = descriptors.Buffers[(int)resource];
                                        if (view.Buffer.Handle == 0)
                                        {
                                            throw SubmissionScheduler.Fatal($"A storage buffer binding has no buffer: buffer={resource} hash=0x{program.Hash:X16}.");
                                        }

                                        bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range };
                                    }

                                    break;
                                case DescriptorBindingKind.MipStatistics:
                                    bufferInfos[bufferIndex++] = _presenter.MipStatisticsDescriptor();
                                    break;
                                case DescriptorBindingKind.CallMarks:
                                    bufferInfos[bufferIndex++] = _worker ? throw Serial("call marks", resumable: true) : _presenter.CallMarksDescriptor();
                                    break;
                                case DescriptorBindingKind.DeviceAddressFallback:
                                {
                                    var fallback = _bufferCache.DeviceAddressFallbackBuffer;
                                    bufferInfos[bufferIndex++] = new DescriptorBufferInfo { Buffer = fallback.Handle, Offset = 0, Range = fallback.Size };
                                    break;
                                }
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
                                case DescriptorBindingKind.CallRecords:
                                {
                                    var view = binding.Kind switch
                                    {
                                        DescriptorBindingKind.FlattenedResourceTable => descriptors.FlattenedTable,
                                        DescriptorBindingKind.ShaderData => descriptors.ShaderData,
                                        DescriptorBindingKind.CallRecords => descriptors.CallRecords,
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
                                    for (var resourceIndex = 0; resourceIndex < resources.Count; resourceIndex++)
                                    {
                                        var resource = resources[resourceIndex];
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
                        var resources = stage.Resources.Info;
                        var canonical = resources.GetCanonicalImageBinding((uint)index);
                        if (canonical >= descriptors.Images.Length || resources.GetCanonicalImageBinding(canonical) != canonical)
                        {
                            throw SubmissionScheduler.Fatal($"An image alias has no canonical binding: image={index} canonical={canonical} hash=0x{program.Hash:X16}.");
                        }

                        var expected = canonical == index
                            ? Math.Max(1u, (uint)descriptors.Images[index].MipViews.Length)
                            : 0u;
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
                    var pushStages = bindPoint == PipelineBindPoint.Graphics
                        ? ((entry.Description?.VertexStage.Stage == ShaderStageKind.Mesh
                            ? ShaderStageFlags.MeshBitExt : ShaderStageFlags.VertexBit) |
                           ShaderStageFlags.FragmentBit |
                           (entry.Description?.Tessellation is null
                               ? 0 : ShaderStageFlags.TessellationControlBit | ShaderStageFlags.TessellationEvaluationBit))
                        : ShaderStageFlags.ComputeBit;
                    PushConstantsShadowed(command, entry.Layout, pushStages, pushData); // SHARPEMU_NATIVE_STATE_REUSE
                }

                if (writeIndex != 0 && DescriptorsAlreadyBound(command, bindPoint, entry.Layout, writes[..writeIndex])) // SHARPEMU_NATIVE_STATE_REUSE
                {
                    Interlocked.Increment(ref _descriptorsShadowed);
                }
                else if (writeIndex != 0)
                {
                    fixed (WriteDescriptorSet* writePointer = writes)
                    {
                        if (entry.UsesPushDescriptors)
                        {
                            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                            {
                                var vkProbe = SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Start(); // [local]
                                SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdPushDescriptorSet(_presenter._pushDescriptorApi, command, bindPoint, entry.Layout, 0, (uint)writeIndex, writePointer); // [local]
                                SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Stop(SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Kind.PushDescriptors, vkProbe); // [local]
                            }
                            AcceptOwnBind(); // [local] the VkRecord bind advanced the native binding epoch
                            ShaderCacheCounters.CountPushSet();
                        }
                        else
                        {
                            if (_worker)
                            {
                                throw Serial("descriptor heap", resumable: true);
                            }

                            var set = _descriptorHeap.Commit(entry.SetLayout, in entry.Demand);
                            for (var index = 0; index < writeIndex; index++)
                            {
                                writes[index].DstSet = set;
                            }

                            _vk.UpdateDescriptorSets(_device, (uint)writeIndex, writePointer, 0, null);
                            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                            {
                                var vkProbe = SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Start(); // [local]
                                SharpEmu.Libs.Gpu.Vulkan.VkRecord.CmdBindDescriptorSets(_vk, command, bindPoint, entry.Layout, 0, 1, &set, 0, null); // [local]
                                SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Stop(SharpEmu.Libs.Gpu.Vulkan.VkCallProbe.Kind.BindDescriptorSets, vkProbe); // [local]
                            }
                            AcceptOwnBind(); // [local]
                            ShaderCacheCounters.CountHeapSet();
                        }
                    }
                }

                if (entry.UsesBindlessHeap)
                {
                    if (_worker)
                    {
                        throw Serial("bindless heap", resumable: true);
                    }

                    _presenter.BindBindlessHeap(command, bindPoint, entry.Layout);
                }
            }

            var spare = _worker ? null : _presenter._spareSubmissionResources ??= new Stack<SubmissionUploadResources>();
            var uploads = spare is { Count: not 0 } ? spare.Pop() : new SubmissionUploadResources();
            uploads.DebugName = bindPoint == PipelineBindPoint.Compute ? Presenter.DispatchDebugName : Presenter.DrawDebugName;
            if (!_worker) _presenter._batchFirstDebugName ??= uploads.DebugName; // [local]
            uploads.Textures = textures;
            uploads.TextureCount = textureCount;
            uploads.FeedbackSnapshots = preparation.FeedbackSnapshots?.ToArray() ?? [];
            uploads.OverflowBuffers = preparation.OverflowBuffers.Count == 0 ? null : preparation.OverflowBuffers.ToArray();
            if (_worker)
            {
                Requests.Resources.Add(uploads);
            }
            else
            {
                _presenter._batchResources.Add(uploads);
            }

            preparation.OverflowBuffers.Clear();
            preparation.Committed = true;
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"Bindings committed bindPoint={bindPoint} pipeline={pipeline.Pipeline} writes={writeIndex} push={entry.UsesPushDescriptors} pushData={hasPushData}");
            }
        }
    }
}
