// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IndirectImageDimensionCollection
{
    public const string Name = "IndirectImageDimension";

    // [local] Runs body with SHARPEMU_INDIRECT_IMAGE_PAD at mode, for tests of the unpadded table layout.
    internal static void WithPadMode(int mode, Action body)
    {
        var previous = ResourceMaterializer.IndirectImagePadMode;
        ResourceMaterializer.IndirectImagePadMode = mode;
        try
        {
            body();
        }
        finally
        {
            ResourceMaterializer.IndirectImagePadMode = previous;
        }
    }
}

// [local] SHARPEMU_INDIRECT_IMAGE_DIM: a 3D candidate in a 2D sampled indirect table (GTA V's 0xD44E).
[Collection(IndirectImageDimensionCollection.Name)]
public sealed class IndirectImageDimensionTests
{
    private const uint ImageType2D = 9;
    private const uint ImageType3D = 10;
    private const uint ImageTypeCube = 11;

    private static T WithMode<T>(int mode, Func<T> body)
    {
        var previous = ResourceMaterializer.IndirectImageDimMode;
        ResourceMaterializer.IndirectImageDimMode = mode;
        try
        {
            return body();
        }
        finally
        {
            ResourceMaterializer.IndirectImageDimMode = previous;
        }
    }

    private static (bool Success, ResourceMaterializationFailure Failure, ResourceSpecialization Specialization) Materialize(uint secondImageType)
    {
        var plan = Extract(SpirvBindingDeclarationTests.IndirectImageAfterPlainImageProgram(indirectDimension: 1));
        var (userData, memory) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageInputs(
            64, false, firstImageType: ImageType2D, secondImageType: secondImageType);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var success = ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
            ref snapshot, ref specialization, out var failure);
        return (success, failure, specialization);
    }

    [Fact]
    public void Off_A3DCandidateIn2DTableIsIncompatible()
    {
        var (success, failure, _) = WithMode(0, () => Materialize(ImageType3D));
        Assert.False(success);
        Assert.Equal(ResourceMaterializationFailure.IncompatibleImageCandidates, failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void On_A3DCandidateKeepsItsOwnDimensionAndCompiles(int mode)
    {
        WithMode(mode, () =>
        {
            var (success, failure, specialization) = Materialize(ImageType3D);
            Assert.True(success, failure.ToString());
            Assert.Equal(ImageDimension.Dim2D, specialization.Images[1].Dimension);
            Assert.Equal(ImageDimension.Dim3D, specialization.Images[2].Dimension);

            var (request, _) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageRequest(
                firstImageType: ImageType2D, secondImageType: ImageType3D);
            var root = request.Resources.Info.Images[1];
            var candidate = request.Resources.Info.Images[2];
            Assert.Equal(ImageDimension.Dim3D, candidate.Dimension);
            Assert.NotEqual(ImageDescriptorBinding.ForImage(root), ImageDescriptorBinding.ForImage(candidate));
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
            return 0;
        });
    }

    // GTA V's other placeholder: a 1x1 8_8_8_8 UINT texture (unified format 60) among float ones.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void AUintCandidateIn2DFloatTableNeedsTheSwitch(int mode, bool accepted)
    {
        var plan = Extract(SpirvBindingDeclarationTests.IndirectImageAfterPlainImageProgram(indirectDimension: 1));
        var (userData, memory) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageInputs(
            64, false, firstImageType: ImageType2D, secondImageType: ImageType2D);
        memory.At(0x2024) = (memory.At(0x2024) & ~(0x1FFu << 20)) | (60u << 20);
        WithMode(mode, () =>
        {
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            var success = ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
                ref snapshot, ref specialization, out var failure);
            Assert.Equal(accepted, success);
            if (!accepted)
            {
                Assert.Equal(ResourceMaterializationFailure.IncompatibleImageCandidates, failure);
                return 0;
            }

            Assert.Equal(ImageNumericClass.Float, specialization.Images[1].NumericClass);
            Assert.Equal(ImageNumericClass.Uint, specialization.Images[2].NumericClass);
            var resources = ResourceMaterializer.ApplyTo(plan, specialization);
            var layout = BindingLayout.Allocate(
                resources.Info,
                BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64),
                false,
                ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
                false);
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out _, out var error), error);
            return 0;
        });
    }

    // The material table puts the 3D candidate at another position: the specialization is the same, and the key of
    // the volume still selects the volume's descriptor.
    [Fact]
    public void CandidatesAreGroupedByClassSoTheirOrderDoesNotMakeAPermutation()
    {
        var plan = Extract(ResourceTrackerTests.IndirectImageProgram(false));
        (ResourceSpecialization Specialization, ResourceSnapshot Snapshot) Specialize(int volumeIndex)
        {
            const int distinctCount = 4;
            uint[] userData = [0x1000, 224 << 16, distinctCount, 0, 0x10000, 16 << 16, distinctCount * 2, 0, 7];
            var memory = new TestWordMemory { Words = new uint[0x11000 / 4] };
            for (var index = 0; index < distinctCount; index++)
            {
                memory.At(0x1000 + (ulong)index * 224 + 4) = (uint)index;
                var descriptor = ResourceTrackerTests.ImageDescriptor();
                descriptor[0] += (uint)index;
                if (index == volumeIndex)
                    descriptor[3] = (descriptor[3] & 0x0FFFFFFF) | (ImageType3D << 28);
                ResourceTrackerTests.WriteImage(memory, 0x10000 + (ulong)index * 32, descriptor);
            }

            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
                ref snapshot, ref specialization, out var failure), failure.ToString());
            return (specialization, snapshot);
        }

        var (first, second) = WithMode(1, () => (Specialize(1), Specialize(3)));
        Assert.Equal(first.Specialization, second.Specialization);
        foreach (var (result, volumeKey) in new[] { (first, 1u), (second, 3u) })
        {
            var images = result.Specialization.Images;
            var root = images.FindIndex(image => image.IndirectRoot != DescriptorConstants.NoIndex);
            var table = result.Snapshot.FlattenedResourceTable;
            var mapping = (int)images[root].IndirectMappingOffset;
            var keys = table[mapping];
            var members = Enumerable.Range(0, images.Count).Where(index => images[index].IndirectRoot == (uint)root).ToArray();
            for (var entry = 0; entry < keys; entry++)
            {
                var key = table[mapping + 1 + entry * 2];
                var image = members[(int)table[mapping + 2 + entry * 2]];
                Assert.Equal(key == volumeKey ? ImageDimension.Dim3D : ImageDimension.Dim2D, images[image].Dimension);
                Assert.Equal(0x20u + key, result.Snapshot.Images[image][0]);
            }
        }
    }

    // [local] SHARPEMU_INDIRECT_IMAGE_PAD: tables with 2, 8 and 6 float candidates after the root plus one volume, the volume at different
    // places, give one specialization; every key still selects its own descriptor, never a padding slot.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PaddedClassRunsMakeTheCandidateCountsOnePermutation(int padMode)
    {
        var plan = Extract(ResourceTrackerTests.IndirectImageProgram(false));
        (ResourceSpecialization Specialization, ResourceSnapshot Snapshot) Specialize(int distinctCount, int volumeIndex)
        {
            uint[] userData = [0x1000, 224 << 16, (uint)distinctCount, 0, 0x10000, 16 << 16, (uint)distinctCount * 2, 0, 7];
            var memory = new TestWordMemory { Words = new uint[0x11000 / 4] };
            for (var index = 0; index < distinctCount; index++)
            {
                memory.At(0x1000 + (ulong)index * 224 + 4) = (uint)index;
                var descriptor = ResourceTrackerTests.ImageDescriptor();
                descriptor[0] += (uint)index;
                if (index == volumeIndex)
                    descriptor[3] = (descriptor[3] & 0x0FFFFFFF) | (ImageType3D << 28);
                ResourceTrackerTests.WriteImage(memory, 0x10000 + (ulong)index * 32, descriptor);
            }

            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
                ref snapshot, ref specialization, out var failure), failure.ToString());
            return (specialization, snapshot);
        }

        var previous = ResourceMaterializer.IndirectImagePadMode;
        ResourceMaterializer.IndirectImagePadMode = padMode;
        var mismatchesBefore = ResourceMaterializer.IndirectImagePadCounters.Mismatches;
        try
        {
            var results = WithMode(1, () => new[] { (Specialize(4, 1), 1u), (Specialize(10, 9), 9u), (Specialize(8, 2), 2u) });
            Assert.Equal(results[0].Item1.Specialization, results[1].Item1.Specialization);
            Assert.Equal(results[0].Item1.Specialization, results[2].Item1.Specialization);
            foreach (var (result, volumeKey) in results)
            {
                var images = result.Specialization.Images;
                var root = images.FindIndex(image => image.IndirectRoot != DescriptorConstants.NoIndex);
                var table = result.Snapshot.FlattenedResourceTable;
                var mapping = (int)images[root].IndirectMappingOffset;
                var keys = table[mapping];
                var members = Enumerable.Range(0, images.Count).Where(index => images[index].IndirectRoot == (uint)root).ToArray();
                Assert.Equal(1 + 16 + 16, members.Length);
                for (var entry = 0; entry < keys; entry++)
                {
                    var key = table[mapping + 1 + entry * 2];
                    var image = members[(int)table[mapping + 2 + entry * 2]];
                    Assert.Equal(key == volumeKey ? ImageDimension.Dim3D : ImageDimension.Dim2D, images[image].Dimension);
                    Assert.Equal(0x20u + key, result.Snapshot.Images[image][0]);
                }
            }

            var resources = ResourceMaterializer.ApplyTo(plan, results[0].Item1.Specialization);
            var layout = BindingLayout.Allocate(
                resources.Info,
                BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64),
                false,
                ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
                false);
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out _, out var error), error);
            Assert.Equal(mismatchesBefore, ResourceMaterializer.IndirectImagePadCounters.Mismatches);
        }
        finally
        {
            ResourceMaterializer.IndirectImagePadMode = previous;
        }
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(4, 16)]
    [InlineData(5, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 32)]
    public void PaddedRunCapacityIsFixedPerBucket(int count, int capacity) =>
        Assert.Equal(capacity, ResourceMaterializer.PaddedRunCapacity(count));

    // Graphics stages keep refusing the mix unless the switch is 3.
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void APixelProgramMixesClassesOnlyWithMode3(int mode, bool accepted)
    {
        var plan = Extract(SpirvBindingDeclarationTests.IndirectImageAfterPlainImageProgram(indirectDimension: 1), stage: ShaderStage.Pixel);
        var (userData, memory) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageInputs(
            64, false, firstImageType: ImageType2D, secondImageType: ImageType3D);
        var (success, failure) = WithMode(mode, () =>
        {
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            var ok = ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
                ref snapshot, ref specialization, out var kind);
            return (ok, kind);
        });
        Assert.Equal(accepted, success);
        if (!accepted)
            Assert.Equal(ResourceMaterializationFailure.IncompatibleImageCandidates, failure);
    }

    [Fact]
    public void On_ACubeCandidateIn2DTableStaysIncompatible()
    {
        var plan = Extract(SpirvBindingDeclarationTests.IndirectImageAfterPlainImageProgram(indirectDimension: 3));
        var (userData, memory) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageInputs(
            64, false, firstImageType: ImageType2D, secondImageType: ImageTypeCube);
        var failure = WithMode(1, () =>
        {
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.False(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
                ref snapshot, ref specialization, out var kind));
            return kind;
        });
        Assert.Equal(ResourceMaterializationFailure.IncompatibleImageCandidates, failure);
    }
}
