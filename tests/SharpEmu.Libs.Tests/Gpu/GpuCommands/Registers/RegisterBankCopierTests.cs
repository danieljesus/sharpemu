// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Text;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

// The direct copier gives the same banks as the copier that looks nested types up on every copy.
public sealed class RegisterBankCopierTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void DirectCopy_MatchesTheLookupCopy(int seed)
    {
        var random = new System.Random(seed);
        var source = new RegisterBanks(static message => new InvalidOperationException(message));
        Fill(source.Context, random);
        Fill(source.Shader, random);
        Fill(source.UserConfig, random);

        var byLookup = new RegisterBanks(static message => new InvalidOperationException(message));
        var direct = new RegisterBanks(static message => new InvalidOperationException(message));
        Fill(direct.Context, new System.Random(seed + 1000));
        RegisterBankCopier.CopyByLookup(source.Context, byLookup.Context);
        RegisterBankCopier.CopyByLookup(source.Shader, byLookup.Shader);
        RegisterBankCopier.CopyByLookup(source.UserConfig, byLookup.UserConfig);
        RegisterBankCopier.CopyDirect(source.Context, direct.Context);
        RegisterBankCopier.CopyDirect(source.Shader, direct.Shader);
        RegisterBankCopier.CopyDirect(source.UserConfig, direct.UserConfig);

        var expected = Describe(source);
        Assert.Equal(expected, Describe(byLookup));
        Assert.Equal(expected, Describe(direct));
    }

    [Fact]
    public void DirectCopy_DoesNotShareArraysOrNestedObjects()
    {
        var source = new RegisterBanks(static message => new InvalidOperationException(message));
        var destination = new RegisterBanks(static message => new InvalidOperationException(message));
        RegisterBankCopier.CopyDirect(source.Context, destination.Context);
        RegisterBankCopier.CopyDirect(source.Shader, destination.Shader);

        Assert.NotSame(source.Context.ScreenViewport, destination.Context.ScreenViewport);
        Assert.NotSame(source.Context.ColorTargets, destination.Context.ColorTargets);
        Assert.NotSame(source.Shader.Pixel.UserScalars.Values, destination.Shader.Pixel.UserScalars.Values);
    }

    private static void Fill(object target, System.Random random)
    {
        for (var type = target.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(Fields | BindingFlags.DeclaredOnly))
            {
                if (field.IsInitOnly || typeof(Delegate).IsAssignableFrom(field.FieldType))
                    continue;
                var value = field.GetValue(target);
                field.SetValue(target, Randomized(field.FieldType, value, random));
            }
        }
    }

    private static object? Randomized(Type type, object? value, System.Random random)
    {
        if (type == typeof(bool)) return random.Next(2) == 1;
        if (type.IsEnum) return Enum.ToObject(type, random.Next(4));
        if (type.IsPrimitive) return Convert.ChangeType(random.Next(0, 100), type);
        if (type.IsArray && value is Array array)
        {
            for (var index = 0; index < array.Length; index++)
                array.SetValue(Randomized(type.GetElementType()!, array.GetValue(index), random), index);
            return array;
        }

        if (type.IsValueType && value is not null)
        {
            Fill(value, random);
            return value;
        }

        if (value is not null && type != typeof(string))
            Fill(value, random);
        return value;
    }

    private static string Describe(RegisterBanks banks)
    {
        var text = new StringBuilder();
        Describe(banks.Context, "context", text);
        Describe(banks.Shader, "shader", text);
        Describe(banks.UserConfig, "user", text);
        return text.ToString();
    }

    private static void Describe(object? value, string path, StringBuilder text)
    {
        if (value is null)
        {
            text.Append(path).Append("=null\n");
            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string)
        {
            text.Append(path).Append('=').Append(value).Append('\n');
            return;
        }

        if (value is Array array)
        {
            for (var index = 0; index < array.Length; index++)
                Describe(array.GetValue(index), $"{path}[{index}]", text);
            return;
        }

        if (value is Delegate)
            return;

        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(Fields | BindingFlags.DeclaredOnly))
                Describe(field.GetValue(value), $"{path}.{field.Name}", text);
        }
    }
}
