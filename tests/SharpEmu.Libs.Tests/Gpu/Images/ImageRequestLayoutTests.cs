// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// [local] Image requests carry a whole mip layout and are copied by value on every texture binding. A field that
// holds a reference makes the JIT zero each request-sized local on entry, so they stay reference-free.
// DarthPotato 8112fc7f.
public sealed class ImageRequestLayoutTests
{
    [Fact]
    public void ImageRequestsHoldNoReferences()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ImageDescription>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ImageRequest>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<TextureRequestResolution>());
    }

    [Fact]
    public void TraceDescriptorTextRoundTripsByIdentity()
    {
        var request = default(ImageRequest);
        Assert.Null(request.TraceTextureDescriptor);
        request.TraceTextureDescriptorId = TextureDescriptorTraceText.Intern("00000001,00000002");
        Assert.Equal("00000001,00000002", request.TraceTextureDescriptor);
        Assert.Equal(request.TraceTextureDescriptorId, TextureDescriptorTraceText.Intern(new string("00000001,00000002".ToCharArray())));
        Assert.NotEqual(request.TraceTextureDescriptorId, TextureDescriptorTraceText.Intern("00000003"));
        Assert.Equal("00000003", TextureDescriptorTraceText.Get(TextureDescriptorTraceText.Intern("00000003")));
    }
}
