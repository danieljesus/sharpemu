// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IndirectImageDimensionCollection
{
    public const string Name = "IndirectImageDimension";
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
