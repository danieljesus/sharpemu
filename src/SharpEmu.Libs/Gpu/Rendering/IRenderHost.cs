// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// A host buffer and the byte offset a guest address maps to inside it.
public readonly record struct BufferBinding(ulong Handle, ulong Offset);

// [local] The image cache's lookup generation, the scheduler tick and the image cache's collection tick.
public readonly record struct TargetLookupStamp(ulong LookupGeneration, ulong Tick, ulong CollectionTick)
{
    public bool IsValid => LookupGeneration != 0;
}

// The packet arguments of a draw the host keeps until the next flip.
public readonly record struct TargetlessDrawArguments(ulong SubmitId, bool Indexed, GpuCommands.DrawIndexedArguments Indexed_, GpuCommands.DrawAutoArguments Auto);

public readonly record struct RenderHostLimits(uint MaxFramebufferWidth, uint MaxFramebufferHeight, uint MaxViewportWidth, uint MaxViewportHeight);

public readonly record struct ColorAttachmentAcquisition(
    ResourceSlotIdentifier Image,
    ImageView View,
    uint Samples,
    bool MetadataClear,
    ClearColorValue MetadataClearValue);

public readonly record struct DepthAttachmentAcquisition(ImageView View, uint Samples, bool MetadataClear);

public readonly record struct ScissorRectangle(int Left, int Top, int Right, int Bottom)
{
    public bool IsSet => Left != 0 || Top != 0 || Right != 0 || Bottom != 0;

    public bool IsValid => Right > Left && Bottom > Top;

    public ScissorRectangle Offset(int x, int y) => new(Left + x, Top + y, Right + x, Bottom + y);

    public ScissorRectangle Intersect(in ScissorRectangle other) =>
        new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}

// The state the host records before every draw.
public readonly record struct DynamicDrawState(
    float ViewportX,
    float ViewportY,
    float ViewportWidth,
    float ViewportHeight,
    float ViewportMinDepth,
    float ViewportMaxDepth,
    ScissorRectangle Scissor,
    float LineWidth,
    float BlendRed,
    float BlendGreen,
    float BlendBlue,
    float BlendAlpha,
    bool DepthTestEnabled,
    bool DepthWriteEnabled,
    CompareOp DepthCompare,
    bool DepthBiasEnabled,
    float DepthBiasConstantFactor,
    float DepthBiasClamp,
    float DepthBiasSlopeFactor,
    bool StencilTestEnabled,
    StencilMasks FrontStencil,
    StencilMasks BackStencil,
    uint ColorWriteCount,
    byte ColorWriteEnableMask,
    uint DepthCountControl = 0,
    bool DepthBoundsTestEnabled = false,
    float DepthMinBounds = 0f,
    float DepthMaxBounds = 1f);

// The host-side descriptors of one shader stage, prepared before the draw or dispatch records.
public interface IPreparedBindings
{
    ShaderStageResources Stage { get; }
}

// Keeps every prepared binding and the stream ring contents intact until the draw or dispatch is recorded.
public interface IResourcePreparation : IDisposable
{
}

public readonly record struct DrawScopeToken(long Scope, ulong Lookups, long Metadata)
{
    public bool IsActive => Scope != 0;
}

// Everything the executor needs from the renderer, the caches and the pipeline objects.
public interface IRenderHost
{
    bool TryCopyImage(in ImageRequest source, in ImageRequest destination) => false;
    int BeginImageSnapshot(IPreparedBindings? pixelBindings) => 0;

    int BeginComputeImageSnapshot(IPreparedBindings bindings) => 0;

    void EndComputeImageSnapshot(int sequence, IPreparedBindings bindings) { }

    void EndImageSnapshot(int sequence, ReadOnlySpan<ColorTargetState> targets) { }

    // Records a draw's color-target base for the image-clear trace; see docs/image-clear-tracing.md.
    void TraceDrawTarget(string disposition, ulong address, ulong pixelShaderAddress, ulong vertexShaderAddress) { }

    DrawScopeToken DrawScope => default;

    void KeepDepthAttachment(in DepthAttachmentState depth)
    {
    }

    RenderHostLimits Limits { get; }

    bool SupportsNativeRectangles => false;

    bool SupportsTessellation => false;

    IImageFormatSupport FormatSupport { get; }

    // True while a command buffer is recording.
    bool IsRecording { get; }

    void RunPendingOperations();

    void SetDebugInformation(RecordedOperation operation, ulong submitId, uint argument0, uint argument1, uint argument2, uint argument3, ulong argument4);

    bool TryReadGuest(ulong address, Span<byte> destination);

    // [local] SHARPEMU_INDIRECT_ARGS_PROBE: the CPU's view of what a known indirect-argument writer reads (no sync).
    void NoteIndirectArgsWriter(ulong shaderHash, ReadOnlySpan<uint> userScalars, ReadOnlySpan<uint> flattenedTable, uint threads)
    {
    }

    // [local] SHARPEMU_ARGS_WRITER_INJECT: called right before a dispatch is recorded.
    void BeforeArgsWriterDispatch(ulong shaderHash, ReadOnlySpan<uint> userScalars)
    {
    }

    // [local] SHARPEMU_ARGS_WRITER_PROBE: a dispatch was recorded with these bindings (still bound).
    void NoteArgsWriterRecorded(ulong shaderHash, IPreparedBindings bindings, ReadOnlySpan<uint> userScalars, ulong submitId, uint threads)
    {
    }

    // The part of the range that is mapped from its start; zero when the start is unmapped.
    ulong ClampMappedSize(ulong address, ulong size);

    ResourceSlotIdentifier FindImage(ref ImageRequest request, bool exactFormat);

    // [local] SHARPEMU_TARGET_MEMO_DCC: a remembered target lookup whose image is known does everything FindImage does
    // around the lookup itself (metadata synchronization, publications, the DCC clear), without the lookup. The
    // default looks the image up again, which is exact for any host.
    ResourceSlotIdentifier FindImageRemembered(ResourceSlotIdentifier remembered, ref ImageRequest request) => FindImage(ref request, exactFormat: false);

    // [local] SHARPEMU_TARGET_MEMO: what a target lookup's result depends on besides its registers; the default never
    // lets a remembered lookup be reused.
    TargetLookupStamp TargetLookupStamp => default;

    void BindRenderTarget(ResourceSlotIdentifier image);

    void ResetBindings();

    // Finds the image again when it changed, makes the view and resolves a metadata clear.
    ColorAttachmentAcquisition AcquireColorAttachment(in ColorTargetState target);

    // Makes the view and consumes the metadata clear state; the transition follows in a second call.
    DepthAttachmentAcquisition AcquireDepthAttachment(in DepthAttachmentState depth);

    void TransitionDepthAttachment(in DepthAttachmentState depth, ImageAspectFlags writeAspects);

    BufferBinding NullBuffer { get; }

    BufferBinding ObtainBuffer(ulong address, ulong size, bool isWritten);

    void PrepareBufferAllocations(ReadOnlySpan<SharpEmu.HLE.GpuMemory.GuestSpan> ranges) =>
        throw new NotSupportedException("The render host does not support tessellation buffer preparation.");

    // The device address of a guest range's cached buffer, for shaders that read it through a physical pointer.
    ulong ObtainBufferDeviceAddress(ulong address, ulong size, bool isWritten) =>
        throw new NotSupportedException("The render host does not expose buffer addresses for tessellation.");

    void TraceVertexBuffer(ulong shaderHash, VertexAttributeResource attribute, VertexInputBuffer input,
        BufferBinding binding, ulong requestedBytes, ulong acquiredBytes) { }

    // Copies host bytes into the stream ring for the current recording.
    BufferBinding UploadTransient(ReadOnlySpan<byte> data, uint alignment);

    ulong UploadMeshVertexIndices(ReadOnlySpan<uint> indices) =>
        throw new NotSupportedException("The renderer does not support indexed mesh inputs.");

    bool SupportsIndirectMeshDraw => false;

    IIndirectMeshDraw PrepareIndirectMeshDraw(in IndirectMeshDrawRequest request) =>
        throw new NotSupportedException("The renderer does not support indirect mesh inputs.");

    void BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings, VertexInputInfo input);

    void BindIndexBuffer(BufferBinding binding, IndexType type);

    // Opens the preparation scope; a ring wrap inside it is refused, and disposal releases it on every path.
    IResourcePreparation BeginPreparation();

    IPreparedBindings PrepareBindings(ShaderStageResources stage);

    void UpdateTessellationData(IPreparedBindings prepared, uint[] data) =>
        throw new NotSupportedException("The render host does not support tessellation runtime data.");

    void PrepareDeviceAddresses();

    void BindResources(IPreparedBindings prepared);

    void CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages);

    // [local] Records the stages' image work (data share barrier, texture transitions, movie uploads) ahead of the
    // pipeline and descriptor writes; CommitBindings then skips it for these stages.
    void PrepareStageImages(PipelineBindPoint bindPoint, ReadOnlySpan<IPreparedBindings> stages)
    {
    }

    void SetDynamicState(in DynamicDrawState state);

    void BeginRendering(in RenderingState state);

    void EndRendering();

    void EndRendering(Vulkan.ScopeBreak reason) => EndRendering();

    // [local] SHARPEMU_ATTACH_REPEAT: the recording tick and the BeginRendering calls so far, captured after an
    // acquisition; then whether the next draw may take that acquisition again (see the Vulkan host), and with
    // apply its bookkeeping replayed. Hosts without it never repeat.
    (ulong Tick, long BeginCalls) AttachmentRepeatToken => (0, 0);

    bool TryRepeatAttachments(ReadOnlySpan<ColorTargetState> colors, ReadOnlySpan<ImageLayout> colorLayouts, in DepthAttachmentState depth,
        ImageLayout depthLayout, in RenderingState rendering, (ulong Tick, long BeginCalls) token, ReadOnlySpan<(object? Backing, long Version)> versions,
        bool apply) => false;

    // The backing of a target image and its access-state version (TryRepeatAttachments compares them).
    (object? Backing, long Version) AttachmentStateVersion(ResourceSlotIdentifier image) => (null, -1);

    // The next draw stores to buffers or storage images; called before its BeginRendering.
    void PrepareMemoryWritingDraw() { }

    void BindPipeline(PipelineBindPoint bindPoint, in PipelineHandle pipeline);

    void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);

    void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance);

    // One indexed draw whose counts the GPU reads from the buffer (VkDrawIndexedIndirectCommand layout).
    void DrawIndexedIndirect(BufferBinding arguments) =>
        throw new NotSupportedException("The render host does not draw from indirect arguments.");

    void DrawIndirect(BufferBinding arguments) =>
        throw new NotSupportedException("The render host does not draw from indirect arguments.");

    void InsertDrawTraceMarker(string label) { }

    void DrawMeshTasks(uint groupCountX, uint groupCountY, uint groupCountZ) =>
        throw new NotSupportedException("The render host does not support mesh shaders.");

    void Dispatch(uint groupsX, uint groupsY, uint groupsZ);

    // Executes a dispatch from three uint32 group counts in guest memory when the backend can
    // consume the guest buffer directly. Backends without native indirect dispatch return false.
    bool TryDispatchIndirect(ulong argumentsAddress, ReadOnlySpan<int> physicalAxisOfLogical);

    // [local] A thread-unit indirect dispatch: copies the three thread counts into the stage's
    // dispatch thread limits and dispatches them as group counts. False when the stage keeps
    // no thread limits in an uploaded shader-data buffer.
    bool TryDispatchIndirectThreads(ulong argumentsAddress, IPreparedBindings bindings) => false;

    bool SupportsIndirectThreadDispatch => false;

    // Orders the buffer writes of the given shader stages before every later access.
    void ShaderWriteBarrier(PipelineStageFlags sourceStages);

    void ShaderWriteHazardBarrier();

    void ShaderAccessBarrier();

    // False when the host recorded the barrier at once; true when it waits for the next native
    // command outside a checked dispatch or the next barrier request.
    bool TryDeferShaderAccessBarrier()
    {
        ShaderAccessBarrier();
        return false;
    }

    void BeginCheckedDispatch()
    {
    }

    void EndCheckedDispatch()
    {
    }

    long DeferredBarrierPromotions => 0;

    void NoteKnownGpuContents(ulong address, ReadOnlySpan<uint> words)
    {
    }

    // Clears the bound color targets to one colour in place of the draw.
    void ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear);

    // Keeps a draw without targets for the next flip; false leaves the draw to run as it is.
    bool TryRetainTargetlessDraw(GpuCommands.Registers.RegisterBanks banks, GraphicsPrograms programs, in TargetlessDrawArguments arguments);

    void MarkGpuWritten(ResourceSlotIdentifier image);

    void ResolveImage(ResourceSlotIdentifier source, uint sourceMip, uint sourceLayer, ResourceSlotIdentifier destination, uint destinationMip, uint destinationLayer);

    void CopyDepthStencilImage(ResourceSlotIdentifier source, ResourceSlotIdentifier destination, in SubresourceRange range, in Extent3D extent, ImageAspectFlags aspects);

    bool IsMetadata(ulong address);

    bool ClearMetadata(ulong address);

    bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear);

    bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue);

    bool TryFillDccMetadata(ulong address, ulong size, uint fillValue);

    // [local] A fill applied in stream order like a command-processor fill: guest memory and
    // the device copies hold the value without a shader writing it.
    bool TryFillGuestBuffer(ulong address, ulong size, uint fillValue) => false;

    bool TryCopyWordsOnHost(ulong destination, ulong source, ulong sourceWords, ulong words);

    Exception Fatal(string message);
}
