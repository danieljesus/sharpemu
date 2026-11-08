// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// The three typed register banks of one queue, with the state the register writers carry between packets.
public sealed class RegisterBanks
{
    private ContextRegisters? _savedContext;
    private uint? _savedCompositeDepthSizeXy;
    private static long _writeSerial;
    private RegisterStampSet _stamps = InitialStamps();

    public RegisterBanks(Func<string, Exception> fatal)
        : this(fatal, new ContextRegisters(), new ShaderProgramRegisters(), new UserConfigRegisters())
    {
    }

    // Banks over given parts, so a copy or a snapshot does not first build empty ones.
    private RegisterBanks(
        Func<string, Exception> fatal,
        ContextRegisters context,
        ShaderProgramRegisters shader,
        UserConfigRegisters userConfig)
    {
        Fatal = fatal;
        Context = context;
        Shader = shader;
        UserConfig = userConfig;
    }

    public Func<string, Exception> Fatal { get; private set; }

    public ContextRegisters Context { get; private set; }

    public ShaderProgramRegisters Shader { get; private set; }

    public UserConfigRegisters UserConfig { get; private set; }

    public bool ContextPushed => _savedContext is not null;

    public ulong Stamp(RegisterStamp stamp) => _stamps[(int)stamp];

    public ulong Stamp(RegisterStamp first, RegisterStamp second) => Math.Max(_stamps[(int)first], _stamps[(int)second]);

    public ulong Stamp(RegisterStamp first, RegisterStamp second, RegisterStamp third) =>
        Math.Max(Math.Max(_stamps[(int)first], _stamps[(int)second]), _stamps[(int)third]);

    // [local] SHARPEMU_PLAN_PROBE: the context group value hashes (PlanRegisterShadow). A bank keeps a shadow once it
    // is written; a snapshot carries the hashes of its source. A bank whose typed context came from somewhere the
    // shadow does not know (CopyFrom a snapshot) starts its shadow from unique hashes, so it equals no other state
    // until the context is next reset or cleared.
    private PlanRegisterShadow? _planShadow;
    private PlanRegisterShadow? _savedPlanShadow;
    private PlanGroupHashes _planHashes;
    private PlanGroupHashes _savedPlanHashes;
    private bool _planBaseUnknown;
    private static long _planUnknownBases;

    public PlanGroupHashes PlanHashes => _planShadow?.Hashes ?? _planHashes;

    internal void NotePlanContextWrite(uint offset, ReadOnlySpan<uint> values)
    {
        if (_planShadow is null)
        {
            _planShadow = new PlanRegisterShadow();
            // A bank without a shadow holds either its base hashes (zero: nothing written since a reset) or the
            // hashes of a state it copied without its dwords.
            _planShadow.Hashes = _planBaseUnknown || !_planHashes.Equals(default)
                ? PlanRegisterShadow.UniqueHashes(Interlocked.Increment(ref _planUnknownBases))
                : default;
            _planBaseUnknown = false;
        }

        _planShadow.Write(offset, values);
    }

    // [local] SHARPEMU_DRAW_PLAN: the typed context was written without the register writers (a replayed draw's
    // retargeting, GnmMode's color target translation), so the hashes no longer follow it: from here on they equal no
    // other state until the context is next reset or cleared, as for a bank whose dwords are not known.
    internal void ForgetPlanHashes()
    {
        if (!PlanRegisterShadow.Enabled)
        {
            return;
        }

        _planShadow = null;
        _planHashes = PlanRegisterShadow.UniqueHashes(Interlocked.Increment(ref _planUnknownBases));
        _planBaseUnknown = true;
    }

    private void ClearPlanShadow()
    {
        _planShadow?.Clear();
        _planHashes = default;
        _planBaseUnknown = false;
    }

    public ulong ContextStamp =>
        Math.Max(
            Math.Max(_stamps[(int)RegisterStamp.ColorTargets], _stamps[(int)RegisterStamp.DepthTargets]),
            Math.Max(_stamps[(int)RegisterStamp.ShaderInterface], _stamps[(int)RegisterStamp.PipelineState]));

    // [local] SHARPEMU_STAMP_PROBE=1: how many context and shader register writes repeat the register's current raw
    // value, per stamp group, to size a stamp that would only move when a value changes. Counting only: the stamps
    // move on every write as before. Reported as [PERF][STAMP_PROBE] with the render profile.
    internal static readonly bool StampProbe = Environment.GetEnvironmentVariable("SHARPEMU_STAMP_PROBE") == "1";
    private uint[]? _probeContext, _probeShader;
    private bool[]? _probeContextKnown, _probeShaderKnown;
    private static readonly long[] _probeWrites = new long[RegisterStamps.Count], _probeUnchanged = new long[RegisterStamps.Count];

    internal void ProbeContextWrite(uint offset, ReadOnlySpan<uint> values)
    {
        _probeContext ??= new uint[RegisterBankLayout.ContextRegisterCount];
        _probeContextKnown ??= new bool[RegisterBankLayout.ContextRegisterCount];
        for (var index = 0u; index < values.Length && offset + index < _probeContext.Length; index++)
        {
            var register = offset + index;
            var stamp = (int)RegisterStamps.ContextStampOf(register);
            Interlocked.Increment(ref _probeWrites[stamp]);
            if (_probeContextKnown[register] && _probeContext[register] == values[(int)index])
            {
                Interlocked.Increment(ref _probeUnchanged[stamp]);
            }

            _probeContext[register] = values[(int)index];
            _probeContextKnown[register] = true;
        }
    }

    internal void ProbeShaderWrite(uint offset, ReadOnlySpan<uint> values)
    {
        _probeShader ??= new uint[RegisterBankLayout.ShaderRegisterCount];
        _probeShaderKnown ??= new bool[RegisterBankLayout.ShaderRegisterCount];
        for (var index = 0u; index < values.Length && offset + index < _probeShader.Length; index++)
        {
            var register = offset + index;
            var stamp = (int)RegisterStamps.ShaderStampOf(register);
            Interlocked.Increment(ref _probeWrites[stamp]);
            if (_probeShaderKnown[register] && _probeShader[register] == values[(int)index])
            {
                Interlocked.Increment(ref _probeUnchanged[stamp]);
            }

            _probeShader[register] = values[(int)index];
            _probeShaderKnown[register] = true;
        }
    }

    private void ProbeForget()
    {
        if (_probeContextKnown is { } context) Array.Clear(context);
        if (_probeShaderKnown is { } shader) Array.Clear(shader);
    }

    internal static string? TakeStampProbeReport()
    {
        if (!StampProbe)
        {
            return null;
        }

        var parts = new string[RegisterStamps.Count];
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = FormattableString.Invariant($"{(RegisterStamp)index}={Interlocked.Exchange(ref _probeWrites[index], 0)}/{Interlocked.Exchange(ref _probeUnchanged[index], 0)}");
        }

        return "[PERF][STAMP_PROBE] writes/unchanged " + string.Join(" ", parts);
    }

    // [local] SHARPEMU_STAMP_ON_CHANGE (0 by default, =1 on): a context or shader register write whose raw dwords all
    // repeat the bank's current values does not move the stamps (GTA V repeats 72-95 % of its context writes and 30 %
    // of its program writes, [PERF][STAMP_PROBE] dc07), so the memos keyed by stamps (target layout, pipeline key,
    // dynamic state, in-place draws) keep their answers across draws that only rewrite the same registers. The typed
    // state after such a write is what it was before it: the writers decode the dwords they are given into their own
    // fields, except for the registers listed in HasWriteSideEffects (the depth writes that clear the composite extent
    // and the user data, whose marker the write consumes), which always move their stamp. The raw shadows are forgotten
    // whenever the typed state changes by another route (Reset, the context state operations, CopyFrom), after which
    // every register's next write moves its stamp. A clone or a snapshot starts without shadows. Off by default: in
    // GTA V 39 % of the stamped writes stop moving their stamp (dc17), but the program registers change in 70 % of
    // their writes, so the layout memo resolves 5 % less (55.7k vs 58.9k per window) and no phase moves >= 0.1 ms.
    // Hot (SHARPEMU_HOTFLAGS_FILE): a change bumps the shadow generation first, so each bank forgets its shadows at its
    // next compared write (the writes made while it was off did not update them).
    internal static volatile bool StampOnChange = Environment.GetEnvironmentVariable("SHARPEMU_STAMP_ON_CHANGE") == "1"; // [local]
    private static int _rawGeneration; // [local]
    private int _rawGenerationSeen; // [local]
    private uint[]? _rawContext, _rawShader;
    private bool[]? _rawContextKnown, _rawShaderKnown;

    internal static void SetStampOnChange(bool on) // [local] SHARPEMU_HOTFLAGS_FILE
    {
        ForgetAllRawShadows();
        StampOnChange = on;
    }

    // [local] Every bank forgets its raw shadows at its next compared write, on the thread that writes it.
    internal static void ForgetAllRawShadows() => Interlocked.Increment(ref _rawGeneration);

    private void SyncRawGeneration() // [local]
    {
        var generation = Volatile.Read(ref _rawGeneration);
        if (generation != _rawGenerationSeen)
        {
            ForgetRaw();
            _rawGenerationSeen = generation;
        }
    }
    private static long _stampWrites, _stampSkipped;

    private static bool HasContextWriteSideEffects(uint register) => register is ContextRegisterOffset.DbZInfo or ContextRegisterOffset.DbDepthSizeXy;

    private static bool HasShaderWriteSideEffects(uint register) =>
        RegisterStamps.ShaderStampOf(register) is RegisterStamp.VertexUserData or RegisterStamp.PixelUserData or RegisterStamp.ComputeUserData;

    // The stamp bits a context write of these dwords at offset moves: those of the registers whose value changed, or
    // that carry side effects, or that have no shadow yet.
    internal uint ContextBitsChanged(uint offset, ReadOnlySpan<uint> values)
    {
        SyncRawGeneration(); // [local]
        var raw = _rawContext ??= new uint[RegisterBankLayout.ContextRegisterCount];
        var known = _rawContextKnown ??= new bool[RegisterBankLayout.ContextRegisterCount];
        var bits = 0u;
        for (var index = 0u; index < values.Length; index++)
        {
            var register = offset + index;
            var value = values[(int)index];
            _stampWrites++;
            if (register >= raw.Length || HasContextWriteSideEffects(register) || !known[register] || raw[register] != value)
            {
                bits |= 1u << (int)RegisterStamps.ContextStampOf(register);
            }
            else
            {
                _stampSkipped++;
            }

            if (register < raw.Length)
            {
                raw[register] = value;
                known[register] = true;
            }
        }

        return bits;
    }

    internal uint ShaderBitsChanged(uint offset, ReadOnlySpan<uint> values)
    {
        SyncRawGeneration(); // [local]
        var raw = _rawShader ??= new uint[RegisterBankLayout.ShaderRegisterCount];
        var known = _rawShaderKnown ??= new bool[RegisterBankLayout.ShaderRegisterCount];
        var bits = 0u;
        for (var index = 0u; index < values.Length; index++)
        {
            var register = offset + index;
            var value = values[(int)index];
            _stampWrites++;
            if (register >= raw.Length || HasShaderWriteSideEffects(register) || !known[register] || raw[register] != value)
            {
                bits |= 1u << (int)RegisterStamps.ShaderStampOf(register);
            }
            else
            {
                _stampSkipped++;
            }

            if (register < raw.Length)
            {
                raw[register] = value;
                known[register] = true;
            }
        }

        return bits;
    }

    private void ForgetRaw()
    {
        if (_rawContextKnown is { } context) Array.Clear(context);
        if (_rawShaderKnown is { } shader) Array.Clear(shader);
    }

    internal static string? TakeStampOnChangeReport() =>
        StampOnChange ? FormattableString.Invariant($"[PERF][STAMP_ON_CHANGE] writes={Interlocked.Exchange(ref _stampWrites, 0)} unmoved={Interlocked.Exchange(ref _stampSkipped, 0)}") : null;

    public void MarkWritten(uint stampBits)
    {
        while (stampBits != 0)
        {
            _stamps[System.Numerics.BitOperations.TrailingZeroCount(stampBits)] = NextSerial();
            stampBits &= stampBits - 1;
        }
    }

    private static ulong NextSerial() => (ulong)Interlocked.Increment(ref _writeSerial);

    private static RegisterStampSet InitialStamps()
    {
        var stamps = new RegisterStampSet();
        for (var index = 0; index < RegisterStamps.Count; index++)
        {
            stamps[index] = NextSerial();
        }

        return stamps;
    }

    // A depth extent carried by a composite binding packet; a direct write clears it.
    // [local] SHARPEMU_STAMP_ON_CHANGE: the extent is draw state the depth registers do not carry, so a change of it
    // moves the DepthTargets stamp itself (the register write that used to carry the bump may now repeat its value).
    private uint? _compositeDepthSizeXy;

    public uint? CompositeDepthSizeXy
    {
        get => _compositeDepthSizeXy;
        set
        {
            if (_compositeDepthSizeXy != value)
            {
                _compositeDepthSizeXy = value;
                MarkWritten(1u << (int)RegisterStamp.DepthTargets);
            }
        }
    }

    public UserScalarKind UserDataMarker { get; set; }

    public uint IndexTypeAndSize { get; set; }

    // A copy of the live banks; the saved context and the draw-time markers are not part of it.
    public RegisterBanks Clone()
    {
        var clone = new RegisterBanks(Fatal, Context.Copy(), Shader.Copy(), UserConfig.Copy())
        {
            CompositeDepthSizeXy = CompositeDepthSizeXy,
            UserDataMarker = UserDataMarker,
            IndexTypeAndSize = IndexTypeAndSize,
            _stamps = _stamps,
            _planShadow = _planShadow?.Copy(), // [local] SHARPEMU_PLAN_PROBE
            _planHashes = _planHashes,
            _planBaseUnknown = _planBaseUnknown,
        };
        return clone;
    }

    // Copies the live banks into this instance without allocating; like Clone, the saved
    // context is not part of it.
    public void CopyFrom(RegisterBanks source)
    {
        RegisterBankCopier.Copy(source.Context, Context);
        RegisterBankCopier.Copy(source.Shader, Shader);
        RegisterBankCopier.Copy(source.UserConfig, UserConfig);
        CompositeDepthSizeXy = source.CompositeDepthSizeXy;
        UserDataMarker = source.UserDataMarker;
        IndexTypeAndSize = source.IndexTypeAndSize;
        _stamps = source._stamps;
        ForgetRaw(); // [local] SHARPEMU_STAMP_ON_CHANGE: the typed state came from elsewhere
        if (PlanRegisterShadow.Enabled) // [local] SHARPEMU_PLAN_PROBE: the source's dwords when it has them
        {
            if (source._planShadow is { } shadow)
            {
                (_planShadow ??= new PlanRegisterShadow()).CopyFrom(shadow);
                _planBaseUnknown = false;
            }
            else
            {
                _planShadow = null;
                _planHashes = source._planHashes;
                _planBaseUnknown = source._planBaseUnknown || !source._planHashes.Equals(default);
            }
        }
    }

    // The parts a snapshot shares with these banks; each is copied before its next write here.
    private bool _contextShared;
    private bool _vertexShared;
    private bool _pixelShared;
    private bool _computeShared;
    private bool _userConfigShared;

    // A read-only copy that shares every part with the live banks until they next write it: a
    // deferred draw takes one per draw, and between draws only a few parts change.
    public RegisterBanks Snapshot() => Snapshot(null);

    public RegisterBanks Snapshot(RegisterBanks? shell)
    {
        RegisterBanks snapshot;
        if (shell is null)
        {
            snapshot = new RegisterBanks(Fatal, Context, Shader.ShareStages(), UserConfig);
        }
        else
        {
            snapshot = shell;
            snapshot.Fatal = Fatal;
            snapshot.Shader.ShareStagesOf(Shader);
            snapshot.UserConfig = UserConfig;
            snapshot._savedContext = null;
            snapshot._savedCompositeDepthSizeXy = null;
            snapshot._userConfigShared = snapshot._vertexShared = snapshot._pixelShared = snapshot._computeShared = false;
            snapshot._pixelScalarsShared = snapshot._hullScalarsShared = snapshot._geometryScalarsShared = false;
            snapshot._legacyVertexScalarsShared = snapshot._exportScalarsShared = snapshot._computeScalarsShared = false;
            snapshot.OwnContext(Context);
        }

        snapshot.CompositeDepthSizeXy = CompositeDepthSizeXy;
        snapshot.UserDataMarker = UserDataMarker;
        snapshot.IndexTypeAndSize = IndexTypeAndSize;
        snapshot._stamps = _stamps;
        if (PlanRegisterShadow.Enabled) // [local] SHARPEMU_PLAN_PROBE: the hashes only; a snapshot is not written
        {
            snapshot._planShadow = null;
            snapshot._planHashes = PlanHashes;
        }

        SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteSnapshot();
        _contextShared = _vertexShared = _pixelShared = _computeShared = _userConfigShared = true;
        _pixelScalarsShared = _hullScalarsShared = _geometryScalarsShared = _legacyVertexScalarsShared = _exportScalarsShared = _computeScalarsShared = true;
        return snapshot;
    }

    public ContextRegisters ShareContext()
    {
        _contextShared = true;
        return Context;
    }

    // Called by the register writers before they store into a part.
    public void PrepareContextWrite()
    {
        if (_contextShared)
        {
            Context = Context.ShallowCopy();
            SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContext();
            _contextShared = false;
            SetContextPartsShared(true);
        }
    }

    // The context is copied without its nested parts, and a part only when a writer asks for it
    // through these accessors: a draw usually rewrites a few plain fields.
    private bool _screenViewportShared;
    private bool _secondaryViewportsShared;
    private bool _sampleLocationsShared;
    private bool _shaderInterfaceShared;
    private bool _blendControlsShared;
    private bool _colorTargetsShared;
    private bool _colorClearWord1Shared;
    private bool _unmodeledShared;

    private void SetContextPartsShared(bool shared) =>
        _screenViewportShared = _secondaryViewportsShared = _sampleLocationsShared = _shaderInterfaceShared =
            _blendControlsShared = _colorTargetsShared = _colorClearWord1Shared = _unmodeledShared = shared;

    private void OwnContext(ContextRegisters context)
    {
        Context = context;
        _contextShared = false;
        SetContextPartsShared(false);
    }

    public ScreenViewportRegisters ScreenViewportForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_screenViewportShared)
            {
                Context.ScreenViewport = Context.ScreenViewport.CopySharingSecondary();
                _screenViewportShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.ScreenViewport;
        }
    }

    public ref ViewportRegisters ViewportForWrite(int slot)
    {
        var screenViewport = ScreenViewportForWrite;
        if (slot == 0)
        {
            return ref screenViewport.Primary;
        }

        if (_secondaryViewportsShared)
        {
            screenViewport.Secondary = (ViewportRegisters[])screenViewport.Secondary.Clone();
            _secondaryViewportsShared = false;
            SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
        }

        return ref screenViewport.Secondary[slot - 1];
    }

    public SampleLocationRegisters SampleLocationsForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_sampleLocationsShared)
            {
                Context.SampleLocations = Context.SampleLocations.Copy();
                _sampleLocationsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.SampleLocations;
        }
    }

    public ShaderInterfaceRegisters ShaderInterfaceForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_shaderInterfaceShared)
            {
                Context.ShaderInterface = Context.ShaderInterface.Copy();
                _shaderInterfaceShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.ShaderInterface;
        }
    }

    public BlendRegisters[] BlendControlsForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_blendControlsShared)
            {
                Context.BlendControls = (BlendRegisters[])Context.BlendControls.Clone();
                _blendControlsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.BlendControls;
        }
    }

    public ColorTargetWords[] ColorTargetsForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_colorTargetsShared)
            {
                Context.ColorTargets = (ColorTargetWords[])Context.ColorTargets.Clone();
                _colorTargetsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.ColorTargets;
        }
    }

    public uint[] ColorClearWord1ForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_colorClearWord1Shared)
            {
                Context.ColorClearWord1 = (uint[])Context.ColorClearWord1.Clone();
                _colorClearWord1Shared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteContextPart();
            }

            return Context.ColorClearWord1;
        }
    }

    public Dictionary<uint, uint> UnmodeledTableRegistersForWrite
    {
        get
        {
            PrepareContextWrite();
            if (_unmodeledShared)
            {
                Context.UnmodeledTableRegisters = new Dictionary<uint, uint>(Context.UnmodeledTableRegisters);
                _unmodeledShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteUnmodeledTable();
            }

            return Context.UnmodeledTableRegisters;
        }
    }

    public void PrepareUserConfigWrite()
    {
        if (_userConfigShared)
        {
            UserConfig = UserConfig.Copy();
            _userConfigShared = false;
            SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteUserConfig();
        }
    }

    // COMPUTE_DISPATCH_INITIATOR, the first register of the compute range.
    private const uint ComputeRangeStart = 0x200;

    // A stage is copied without its user scalar sets, and a set only when its window is written:
    // a draw usually rewrites one set of one or two stages.
    private bool _pixelScalarsShared;
    private bool _hullScalarsShared;
    private bool _geometryScalarsShared;
    private bool _legacyVertexScalarsShared;
    private bool _exportScalarsShared;
    private bool _computeScalarsShared;
    private const uint UserScalarWindow = UserScalarRegisters.Capacity;

    private static bool Overlaps(uint first, uint last, uint windowStart) =>
        first < windowStart + UserScalarWindow && last >= windowStart;

    // The shader bank's offsets: pixel below SPI_SHADER_PGM_LO_VS, the geometry stages up to the
    // compute range, compute from there.
    public void PrepareShaderWrite(uint offset, uint count)
    {
        var last = offset + Math.Max(count, 1) - 1;
        if (offset < ShaderRegisterOffset.SpiShaderPgmLoVs)
        {
            if (_pixelShared)
            {
                Shader.Pixel = Shader.Pixel.ShallowCopy();
                _pixelShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteStage();
            }

            if (_pixelScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataPs0))
            {
                Shader.Pixel.UserScalars = Shader.Pixel.UserScalars.Copy();
                _pixelScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }
        }

        if (offset < ComputeRangeStart && last >= ShaderRegisterOffset.SpiShaderPgmLoVs)
        {
            if (_vertexShared)
            {
                Shader.Vertex = Shader.Vertex.ShallowCopy();
                _vertexShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteStage();
            }

            var vertex = Shader.Vertex;
            if (_hullScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataHs0))
            {
                vertex.HullUserScalars = vertex.HullUserScalars.Copy();
                _hullScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }

            if (_geometryScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataGs0))
            {
                vertex.GeometryUserScalars = vertex.GeometryUserScalars.Copy();
                _geometryScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }

            if (_legacyVertexScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataVs0))
            {
                vertex.LegacyVertexUserScalars = vertex.LegacyVertexUserScalars.Copy();
                _legacyVertexScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }

            if (_exportScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.SpiShaderUserDataEs0))
            {
                vertex.ExportUserScalars = vertex.ExportUserScalars.Copy();
                _exportScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }
        }

        if (last >= ComputeRangeStart)
        {
            if (_computeShared)
            {
                Shader.Compute = Shader.Compute.ShallowCopy();
                _computeShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteStage();
            }

            if (_computeScalarsShared && Overlaps(offset, last, ShaderRegisterOffset.ComputeUserData0))
            {
                Shader.Compute.UserScalars = Shader.Compute.UserScalars.Copy();
                _computeScalarsShared = false;
                SharpEmu.Libs.Diagnostics.RegisterCopyTally.NoteScalars();
            }
        }
    }

    public void Reset()
    {
        _contextShared = _vertexShared = _pixelShared = _computeShared = _userConfigShared = false;
        _pixelScalarsShared = _hullScalarsShared = _geometryScalarsShared = _legacyVertexScalarsShared = _exportScalarsShared = _computeScalarsShared = false;
        OwnContext(new ContextRegisters());
        Shader = new ShaderProgramRegisters();
        UserConfig = new UserConfigRegisters();
        _savedContext = null;
        _savedCompositeDepthSizeXy = null;
        CompositeDepthSizeXy = null;
        UserDataMarker = UserScalarKind.Unknown;
        IndexTypeAndSize = 0;
        if (StampProbe) ProbeForget(); // [local]
        ForgetRaw(); // [local] SHARPEMU_STAMP_ON_CHANGE
        _savedPlanShadow = null; // [local] SHARPEMU_PLAN_PROBE
        ClearPlanShadow();
        MarkWritten(RegisterStamps.AllBits);
    }

    public void ApplyContextState(ContextStateOperation operation)
    {
        var previousViewport = Context.ScreenViewport.Primary;
        switch (operation)
        {
            case ContextStateOperation.Clear:
                OwnContext(new ContextRegisters());
                CompositeDepthSizeXy = null;
                ClearPlanShadow(); // [local] SHARPEMU_PLAN_PROBE
                break;
            case ContextStateOperation.Push:
                Push();
                break;
            case ContextStateOperation.Pop:
                if (_savedContext is null)
                {
                    throw Fatal("The context state is not pushed.");
                }

                OwnContext(_savedContext);
                CompositeDepthSizeXy = _savedCompositeDepthSizeXy;
                _savedContext = null;
                _savedCompositeDepthSizeXy = null;
                _planShadow = _savedPlanShadow; // [local] SHARPEMU_PLAN_PROBE
                _planHashes = _savedPlanHashes;
                _savedPlanShadow = null;
                break;
            case ContextStateOperation.PushClear:
                Push();
                OwnContext(new ContextRegisters());
                CompositeDepthSizeXy = null;
                _planShadow = null; // [local] SHARPEMU_PLAN_PROBE: the saved one is the pushed copy
                ClearPlanShadow();
                break;
            default:
                throw Fatal($"The context state operation is unknown: operation={(uint)operation}.");
        }

        if (StampProbe) ProbeForget(); // [local]
        ForgetRaw(); // [local] SHARPEMU_STAMP_ON_CHANGE
        MarkWritten(RegisterStamps.ContextBits);
        if (Rendering.RenderTrace.Enabled)
        {
            var currentViewport = Context.ScreenViewport.Primary;
            Rendering.RenderTrace.Write(
                $"ContextState operation={operation} previousZScale={previousViewport.ZScale} previousZOffset={previousViewport.ZOffset} " +
                $"zScale={currentViewport.ZScale} zOffset={currentViewport.ZOffset}");
        }
    }

    private void Push()
    {
        if (_savedContext is not null)
        {
            throw Fatal("The context state is already pushed.");
        }

        _savedContext = Context.Copy();
        _savedCompositeDepthSizeXy = CompositeDepthSizeXy;
        _savedPlanShadow = _planShadow?.Copy(); // [local] SHARPEMU_PLAN_PROBE
        _savedPlanHashes = _planHashes;
    }
}
