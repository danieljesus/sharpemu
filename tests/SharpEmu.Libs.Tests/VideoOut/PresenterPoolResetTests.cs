// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// [local] SHARPEMU_RENDER_POOLS: a pooled object's Reset leaves every field as a new instance has it, so a reused
// image binding, prepared stage or descriptor set cannot carry state into the next draw. Every field is set to a
// non-default value first; a field added without its reset fails here.
public sealed class PresenterPoolResetTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Presenter = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;

    private static Type Nested(Type owner, string name) => owner.GetNestedType(name, BindingFlags.NonPublic)!;

    [Fact]
    public void TextureResourceResetMatchesANewInstance()
    {
        var type = Nested(typeof(VulkanVideoPresenter), "TextureResource");
        var fresh = Activator.CreateInstance(type, nonPublic: true)!;
        var used = Activator.CreateInstance(type, nonPublic: true)!;
        Scramble(used);
        type.GetMethod("Reset")!.Invoke(used, null);
        AssertSameFields(fresh, used);
    }

    [Fact]
    public void StageDescriptorsResetMatchesANewInstance()
    {
        var type = Nested(Presenter, "StageDescriptors");
        var fresh = Activator.CreateInstance(type, nonPublic: true)!;
        var used = Activator.CreateInstance(type, nonPublic: true)!;
        Scramble(used);
        type.GetMethod("Reset")!.Invoke(used, null);
        AssertSameFields(fresh, used);
    }

    [Fact]
    public void PreparedStageBindingsResetMatchesANewInstanceForTheNewStage()
    {
        var type = Nested(Presenter, "PreparedStageBindings");
        var program = new ShaderProgramInfo { Stage = ShaderStageKind.Pixel, Hash = 7 };
        var stage = new ShaderStageResources(program, new ResourceSnapshot(), 0x1000);
        var other = new ShaderStageResources(new ShaderProgramInfo { Stage = ShaderStageKind.Vertex, Hash = 9 }, new ResourceSnapshot(), 0x2000);
        var fresh = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [stage, program], null)!;
        var used = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [other, other.Program!], null)!;
        Scramble(used, skip: "<Descriptors>k__BackingField");
        Scramble(type.GetProperty("Descriptors")!.GetValue(used)!);
        type.GetMethod("Release")!.Invoke(used, null);
        type.GetMethod("Reset")!.Invoke(used, [stage, program]);
        AssertSameFields(fresh, used, skip: "<Descriptors>k__BackingField");
        AssertSameFields(type.GetProperty("Descriptors")!.GetValue(fresh)!, type.GetProperty("Descriptors")!.GetValue(used)!);
    }

    // Sets every field to a value that differs from its current one.
    private static void Scramble(object target, string? skip = null)
    {
        foreach (var field in target.GetType().GetFields(Fields))
        {
            if (field.Name == skip)
            {
                continue;
            }

            // GetValue boxes a copy each call: the scrambled struct does not alias before.
            var before = field.GetValue(target);
            var value = NonDefault(field.FieldType, field.GetValue(target), depth: 0);
            field.SetValue(target, value);
            Assert.False(SameValue(value, before), $"{target.GetType().Name}.{field.Name} could not be scrambled.");
        }
    }

    private static object? NonDefault(Type type, object? current, int depth)
    {
        if (type == typeof(bool)) return !(bool)(current ?? false);
        if (type.IsEnum) return Enum.ToObject(type, Convert.ToInt64(current ?? 0) + 3);
        if (type == typeof(nint)) return (nint)3 + (nint)(current ?? (nint)0);
        if (type == typeof(nuint)) return (nuint)3 + (nuint)(current ?? (nuint)0);
        if (type.IsPrimitive) return Convert.ChangeType(Convert.ToInt64(current ?? 0) + 3, type);
        if (type == typeof(string)) return "scrambled";
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 2);
        if (type.IsValueType)
        {
            var boxed = current ?? Activator.CreateInstance(type)!;
            if (depth < 6)
            {
                foreach (var field in type.GetFields(Fields))
                {
                    var before = field.GetValue(boxed);
                    var value = NonDefault(field.FieldType, field.GetValue(boxed), depth + 1);
                    if (value is null || SameValue(value, before)) continue;
                    field.SetValue(boxed, value);
                    break;
                }
            }

            return boxed;
        }

        return type.IsAbstract || type.IsInterface ? null : RuntimeHelpers.GetUninitializedObject(type);
    }

    // Structs field by field (an inline array, which has no Equals, by its bytes); arrays by length; the rest Equals.
    private static bool SameValue(object? left, object? right)
    {
        if (left is Array a && right is Array b) return a.Length == b.Length;
        if (left is null || right is null || left.GetType() != right.GetType()) return Equals(left, right);
        var type = left.GetType();
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum) return Equals(left, right);
        if (type.IsDefined(typeof(InlineArrayAttribute), false))
        {
            return (bool)typeof(PresenterPoolResetTests).GetMethod(nameof(SameBytes), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type).Invoke(null, [left, right])!;
        }

        foreach (var field in type.GetFields(Fields))
        {
            if (!SameValue(field.GetValue(left), field.GetValue(right))) return false;
        }

        return true;
    }

    private static bool SameBytes<T>(object left, object right) where T : struct
    {
        var x = (T)left;
        var y = (T)right;
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref x))
            .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref y)));
    }

    private static void AssertSameFields(object expected, object actual, string? skip = null)
    {
        foreach (var field in expected.GetType().GetFields(Fields))
        {
            if (field.Name == skip)
            {
                continue;
            }

            var want = field.GetValue(expected);
            var got = field.GetValue(actual);
            Assert.True(SameValue(want, got), $"{expected.GetType().Name}.{field.Name}: expected {want ?? "null"}, got {got ?? "null"}.");
        }
    }
}
