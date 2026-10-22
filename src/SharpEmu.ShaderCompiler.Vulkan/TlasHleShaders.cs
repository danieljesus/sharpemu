// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// [local] SHARPEMU_TLAS_HLE: the box pass of the host-built TLAS. One invocation walks the nodes from last to first
// (children always come after their parent), takes each child's box from the per-instance world boxes (leaf) or from
// a scratch copy of an already finished node, writes it into the node's child slot and keeps the union for the parent.
//   binding 0: per-instance boxes (6 floats from records[recordOffset + index * recordStride])
//   binding 1: the topology: [0] node count, [1] record offset, [2] output offset (dwords, at the first node),
//              [3] record stride; then 4 entries per node: 0xFFFFFFFF empty, bit 31 set = leaf of instance (low bits),
//              otherwise the child node's index
//   binding 2: the TLAS: node k's child box c at outputOffset + k * 32 + 4 + c * 6
//   binding 3: scratch, node k's box at k * 6
public static class TlasHleShaders
{
    public static byte[] CreateTlasBoxes()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var glsl = module.ImportExtInst("GLSL.std.450");

        var voidType = module.TypeVoid();
        var boolType = module.TypeBool();
        var uintType = module.TypeInt(32, signed: false);
        var floatType = module.TypeFloat(32);
        var runtimeArray = module.TypeRuntimeArray(uintType);
        module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, 4);
        var bufferStruct = module.TypeStruct(runtimeArray);
        module.AddDecoration(bufferStruct, SpirvDecoration.Block);
        module.AddMemberDecoration(bufferStruct, 0, SpirvDecoration.Offset, 0);
        var bufferPtrType = module.TypePointer(SpirvStorageClass.StorageBuffer, bufferStruct);
        var uintStoragePtr = module.TypePointer(SpirvStorageClass.StorageBuffer, uintType);
        var uintFunctionPtr = module.TypePointer(SpirvStorageClass.Function, uintType);
        var floatFunctionPtr = module.TypePointer(SpirvStorageClass.Function, floatType);

        uint MakeBuffer(uint binding, string name)
        {
            var variable = module.AddGlobalVariable(bufferPtrType, SpirvStorageClass.StorageBuffer);
            module.AddName(variable, name);
            module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
            module.AddDecoration(variable, SpirvDecoration.Binding, binding);
            return variable;
        }

        var records = MakeBuffer(0, "records");
        var topology = MakeBuffer(1, "topology");
        var output = MakeBuffer(2, "tlas");
        var scratch = MakeBuffer(3, "scratch");

        uint UInt(uint value) => module.Constant(uintType, value);
        var positiveInfinity = module.ConstantFloat(floatType, float.PositiveInfinity);
        var negativeInfinity = module.ConstantFloat(floatType, float.NegativeInfinity);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        var nodeVar = module.AddFunctionVariable(uintFunctionPtr);
        var unionVars = new uint[6];
        for (var index = 0; index < 6; index++)
            unionVars[index] = module.AddFunctionVariable(floatFunctionPtr);
        var childVars = new uint[6];
        for (var index = 0; index < 6; index++)
            childVars[index] = module.AddFunctionVariable(floatFunctionPtr);

        uint Load(uint buffer, uint index) =>
            module.AddInstruction(SpirvOp.Load, uintType, module.AddInstruction(SpirvOp.AccessChain, uintStoragePtr, buffer, UInt(0), index));

        void StoreWord(uint buffer, uint index, uint value) =>
            module.AddStatement(SpirvOp.Store, module.AddInstruction(SpirvOp.AccessChain, uintStoragePtr, buffer, UInt(0), index), value);

        var nodeCount = Load(topology, UInt(0));
        var recordOffset = Load(topology, UInt(1));
        var outputOffset = Load(topology, UInt(2));
        var recordStride = Load(topology, UInt(3));
        module.AddStatement(SpirvOp.Store, nodeVar, nodeCount);

        var header = module.AllocateId();
        var body = module.AllocateId();
        var continueLabel = module.AllocateId();
        var merge = module.AllocateId();
        module.AddStatement(SpirvOp.Branch, header);

        module.AddLabel(header);
        var remaining = module.AddInstruction(SpirvOp.Load, uintType, nodeVar);
        var more = module.AddInstruction(SpirvOp.UGreaterThan, boolType, remaining, UInt(0));
        module.AddStatement(SpirvOp.LoopMerge, merge, continueLabel, 0);
        module.AddStatement(SpirvOp.BranchConditional, more, body, merge);

        module.AddLabel(body);
        var node = module.AddInstruction(SpirvOp.ISub, uintType, remaining, UInt(1));
        module.AddStatement(SpirvOp.Store, nodeVar, node);
        for (var index = 0; index < 6; index++)
            module.AddStatement(SpirvOp.Store, unionVars[index], index < 3 ? positiveInfinity : negativeInfinity);
        var entryBase = module.AddInstruction(SpirvOp.IAdd, uintType, UInt(4), module.AddInstruction(SpirvOp.IMul, uintType, node, UInt(4)));
        var nodeOutput = module.AddInstruction(SpirvOp.IAdd, uintType, outputOffset, module.AddInstruction(SpirvOp.IMul, uintType, node, UInt(32)));

        for (uint child = 0; child < 4; child++)
        {
            var entry = Load(topology, module.AddInstruction(SpirvOp.IAdd, uintType, entryBase, UInt(child)));
            var present = module.AddInstruction(SpirvOp.INotEqual, boolType, entry, UInt(0xFFFFFFFF));
            var presentLabel = module.AllocateId();
            var afterChild = module.AllocateId();
            module.AddStatement(SpirvOp.SelectionMerge, afterChild, 0);
            module.AddStatement(SpirvOp.BranchConditional, present, presentLabel, afterChild);

            module.AddLabel(presentLabel);
            var isLeaf = module.AddInstruction(SpirvOp.INotEqual, boolType,
                module.AddInstruction(SpirvOp.BitwiseAnd, uintType, entry, UInt(0x80000000)), UInt(0));
            var index = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, entry, UInt(0x7FFFFFFF));
            var leafLabel = module.AllocateId();
            var nodeLabel = module.AllocateId();
            var afterSource = module.AllocateId();
            module.AddStatement(SpirvOp.SelectionMerge, afterSource, 0);
            module.AddStatement(SpirvOp.BranchConditional, isLeaf, leafLabel, nodeLabel);

            module.AddLabel(leafLabel);
            var leafBase = module.AddInstruction(SpirvOp.IAdd, uintType, recordOffset, module.AddInstruction(SpirvOp.IMul, uintType, index, recordStride));
            for (uint component = 0; component < 6; component++)
            {
                var word = Load(records, module.AddInstruction(SpirvOp.IAdd, uintType, leafBase, UInt(component)));
                module.AddStatement(SpirvOp.Store, childVars[component], module.AddInstruction(SpirvOp.Bitcast, floatType, word));
            }

            module.AddStatement(SpirvOp.Branch, afterSource);

            module.AddLabel(nodeLabel);
            var nodeBase = module.AddInstruction(SpirvOp.IMul, uintType, index, UInt(6));
            for (uint component = 0; component < 6; component++)
            {
                var word = Load(scratch, module.AddInstruction(SpirvOp.IAdd, uintType, nodeBase, UInt(component)));
                module.AddStatement(SpirvOp.Store, childVars[component], module.AddInstruction(SpirvOp.Bitcast, floatType, word));
            }

            module.AddStatement(SpirvOp.Branch, afterSource);

            module.AddLabel(afterSource);
            var slot = module.AddInstruction(SpirvOp.IAdd, uintType, nodeOutput, UInt(4 + child * 6));
            for (uint component = 0; component < 6; component++)
            {
                var value = module.AddInstruction(SpirvOp.Load, floatType, childVars[component]);
                StoreWord(output, module.AddInstruction(SpirvOp.IAdd, uintType, slot, UInt(component)), module.AddInstruction(SpirvOp.Bitcast, uintType, value));
                var current = module.AddInstruction(SpirvOp.Load, floatType, unionVars[component]);
                var combined = module.AddInstruction(SpirvOp.ExtInst, floatType, glsl, component < 3 ? 37u : 40u, current, value); // FMin / FMax
                module.AddStatement(SpirvOp.Store, unionVars[component], combined);
            }

            module.AddStatement(SpirvOp.Branch, afterChild);
            module.AddLabel(afterChild);
        }

        var scratchBase = module.AddInstruction(SpirvOp.IMul, uintType, node, UInt(6));
        for (uint component = 0; component < 6; component++)
        {
            var value = module.AddInstruction(SpirvOp.Load, floatType, unionVars[component]);
            StoreWord(scratch, module.AddInstruction(SpirvOp.IAdd, uintType, scratchBase, UInt(component)), module.AddInstruction(SpirvOp.Bitcast, uintType, value));
        }

        module.AddStatement(SpirvOp.Branch, continueLabel);
        module.AddLabel(continueLabel);
        module.AddStatement(SpirvOp.Branch, header);
        module.AddLabel(merge);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 1, 1, 1);
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", [records, topology, output, scratch]);
        return module.Build();
    }
}
