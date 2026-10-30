// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_INDIRECT_IMAGE_DIM=2 (verify; needs the ray query area): at every indirect image access whose
// candidates differ in dimension or numeric class, ray query counter word 122 sums the lanes that select a candidate
// of another class than the first case's, and word 123 the lanes of all such accesses (124 and 125: see
// CountIndirectProgramImage). The host prints them
// ([GPU][INDIRECT_DIM]).
public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        internal const uint IndirectDimOtherWord = 122;
        internal const uint IndirectDimTotalWord = 123;

        private bool OtherIndirectClass((uint Resource, uint Element) first, (uint Resource, uint Element) candidate)
        {
            var images = _request.Resources.Info.Images;
            var left = images[(int)first.Resource];
            var right = images[(int)candidate.Resource];
            return left.Dimension != right.Dimension || left.NumericClass != right.NumericClass;
        }

        private bool CountsIndirectDimensions(IReadOnlyList<(uint Resource, uint Element)> elements)
        {
            if (ResourceMaterializer.IndirectImageDimMode != 2 || elements.Count < 2)
                return false;
            var other = elements.Count(element => OtherIndirectClass(elements[0], element));
            if (other == 0)
                return false;
            Console.Error.WriteLine($"[GPU][INDIRECT_DIM] hash=0x{_request.Hash:X16} cases={elements.Count} other_class={other} counted={RqAreaUsable}");
            return RqAreaUsable;
        }

        internal const uint IndirectCaseWord = 124;
        internal const uint IndirectProgramImageWord = 125;

        // Words 124 (accesses over several descriptors) and 125 (every image access) of a program with an indirect
        // image table: they show that the counters reach the host from this program at all.
        private void CountIndirectProgramImage(bool hasCases)
        {
            if (ResourceMaterializer.IndirectImageDimMode != 2 || !RqAreaUsable ||
                !_request.Resources.Info.Images.Any(image => image.IndirectRoot != DescriptorConstants.NoIndex))
                return;
            CountIndirectDimensionLanes(IndirectProgramImageWord);
            if (hasCases)
                CountIndirectDimensionLanes(IndirectCaseWord);
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
