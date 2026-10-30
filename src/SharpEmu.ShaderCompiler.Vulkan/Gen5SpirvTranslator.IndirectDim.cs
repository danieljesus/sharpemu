// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_INDIRECT_IMAGE_DIM=2 (verify; needs the ray query area): at every indirect image access whose
// candidates mix 3D with other dimensions, ray query counter word 122 sums the lanes that select a candidate of
// another dimension than the first case's, and word 123 the lanes of all such accesses. The host prints both
// ([GPU][INDIRECT_DIM]).
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        internal const uint IndirectDimOtherWord = 122;
        internal const uint IndirectDimTotalWord = 123;

        private bool CountsIndirectDimensions(IReadOnlyList<(uint Resource, uint Element)> elements)
        {
            if (ResourceMaterializer.IndirectImageDimMode != 2 || !RqAreaUsable || elements.Count < 2)
                return false;
            var images = _request.Resources.Info.Images;
            var first = images[(int)elements[0].Resource].Dimension;
            foreach (var element in elements)
            {
                var dimension = images[(int)element.Resource].Dimension;
                if (dimension != first && (dimension == ImageDimension.Dim3D || first == ImageDimension.Dim3D))
                    return true;
            }

            return false;
        }

        // One lane per subgroup adds the active lanes.
        private void CountIndirectDimensionLanes(uint word)
        {
            var lanes = SubgroupCount(_module.ConstantBool(true));
            EmitConditional(_module.AddInstruction(SpirvOp.GroupNonUniformElect, _boolType, UInt(3)),
                () => AtomicAdd(RqCounter(word), lanes));
        }
    }
}
