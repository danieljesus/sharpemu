// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// Copies the contents of one register bank object into another of the same type without
// allocating: values and delegates are assigned, arrays are copied element by element into
// the destination's arrays, nested bank objects are copied the same way. One copier per
// type is built on first use from the type's fields, so a new register needs no code here.
internal static class RegisterBankCopier
{
    private static readonly ConcurrentDictionary<Type, Action<object, object>> Copiers = new();
    private static readonly MethodInfo CopyArrayMethod = typeof(RegisterBankCopier).GetMethod(nameof(CopyArray), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo CopyObjectMethod = typeof(RegisterBankCopier).GetMethod(nameof(CopyObject), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static void Copy<T>(T source, T destination) where T : class => For(typeof(T))(source, destination);

    // The destination array to keep: the existing one when it has the source's shape, else a clone.
    private static Array? CopyArray(Array? source, Array? destination)
    {
        if (source is null) return null;
        if (destination is null || destination.Length != source.Length || destination.GetType() != source.GetType()) return (Array)source.Clone();
        Array.Copy(source, destination, source.Length);
        return destination;
    }

    // The destination object to keep: the existing one filled from the source, else a new one.
    private static object? CopyObject(object? source, object? destination, Type type)
    {
        if (source is null) return null;
        destination ??= Activator.CreateInstance(source.GetType()) ?? throw new InvalidOperationException($"A register bank object cannot be created: type={type.Name}.");
        For(source.GetType())(source, destination);
        return destination;
    }

    private static Action<object, object> For(Type type) => Copiers.GetOrAdd(type, Build);

    private static Action<object, object> Build(Type type)
    {
        var sourceParameter = Expression.Parameter(typeof(object), "source");
        var destinationParameter = Expression.Parameter(typeof(object), "destination");
        var source = Expression.Variable(type, "s");
        var destination = Expression.Variable(type, "d");
        var body = new List<Expression>
        {
            Expression.Assign(source, Expression.Convert(sourceParameter, type)),
            Expression.Assign(destination, Expression.Convert(destinationParameter, type)),
        };
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var fieldType = field.FieldType;
                var from = Expression.Field(source, field);
                var to = Expression.Field(destination, field);
                if (fieldType.IsValueType || fieldType == typeof(string) || typeof(Delegate).IsAssignableFrom(fieldType))
                {
                    body.Add(Expression.Assign(to, from));
                }
                else if (fieldType.IsArray)
                {
                    if (!fieldType.GetElementType()!.IsValueType)
                    {
                        throw new NotSupportedException($"A register bank holds an array of objects: {type.Name}.{field.Name}.");
                    }

                    body.Add(Expression.Assign(to, Expression.Convert(
                        Expression.Call(CopyArrayMethod, Expression.Convert(from, typeof(Array)), Expression.Convert(to, typeof(Array))), fieldType)));
                }
                else
                {
                    body.Add(Expression.Assign(to, Expression.Convert(
                        Expression.Call(CopyObjectMethod, Expression.Convert(from, typeof(object)), Expression.Convert(to, typeof(object)),
                            Expression.Constant(fieldType, typeof(Type))), fieldType)));
                }
            }
        }

        var block = Expression.Block([source, destination], body);
        return Expression.Lambda<Action<object, object>>(block, sourceParameter, destinationParameter).Compile();
    }
}
