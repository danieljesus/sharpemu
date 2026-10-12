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

    public static void Copy<T>(T source, T destination) where T : class => (Direct ? DirectCache<T>.Copier : For(typeof(T)))(source, destination);

    // Both copiers by name, for the tests that compare them.
    internal static void CopyDirect<T>(T source, T destination) where T : class => DirectCache<T>.Copier(source, destination);

    internal static void CopyByLookup<T>(T source, T destination) where T : class => For(typeof(T))(source, destination);

    private static class DirectCache<T>
    {
        internal static readonly Action<object, object> Copier = DirectFor(typeof(T));
    }

    // [local] SHARPEMU_REGISTER_COPY_DIRECT=1: copiers that call the copier of a sealed nested type directly and
    // copy value-type arrays through spans, instead of looking the nested copier up and creating the nested
    // object by reflection on every copy. Same fields, same order, same results.
    internal static readonly bool Direct = Environment.GetEnvironmentVariable("SHARPEMU_REGISTER_COPY_DIRECT") == "1";

    private sealed class Holder
    {
        public Action<object, object>? Copier;
    }

    private static readonly Dictionary<Type, Holder> DirectCopiers = new();
    private static readonly MethodInfo CopyTypedArrayMethod = typeof(RegisterBankCopier).GetMethod(nameof(CopyTypedArray), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo HolderCopierField = typeof(Holder).GetField(nameof(Holder.Copier))!;
    private static readonly MethodInfo InvokeCopierMethod = typeof(Action<object, object>).GetMethod(nameof(Action<object, object>.Invoke))!;

    private static T[]? CopyTypedArray<T>(T[]? source, T[]? destination) where T : struct
    {
        if (source is null) return null;
        if (destination is null || destination.Length != source.Length) return (T[])source.Clone();
        source.AsSpan().CopyTo(destination);
        return destination;
    }

    private static Action<object, object> DirectFor(Type type)
    {
        lock (DirectCopiers)
        {
            return DirectHolder(type).Copier!;
        }
    }

    // Called with the lock held; a holder is registered before its copier is built, so a type that
    // reaches itself would find the holder (its copier is set before any copy runs).
    private static Holder DirectHolder(Type type)
    {
        if (DirectCopiers.TryGetValue(type, out var holder))
            return holder;
        holder = new Holder();
        DirectCopiers.Add(type, holder);
        holder.Copier = BuildDirect(type);
        return holder;
    }

    private static Action<object, object> BuildDirect(Type type)
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
                    var element = fieldType.GetElementType()!;
                    if (!element.IsValueType)
                    {
                        throw new NotSupportedException($"A register bank holds an array of objects: {type.Name}.{field.Name}.");
                    }

                    if (fieldType.GetArrayRank() == 1 && fieldType == element.MakeArrayType())
                    {
                        body.Add(Expression.Assign(to, Expression.Call(CopyTypedArrayMethod.MakeGenericMethod(element), from, to)));
                    }
                    else
                    {
                        body.Add(Expression.Assign(to, Expression.Convert(
                            Expression.Call(CopyArrayMethod, Expression.Convert(from, typeof(Array)), Expression.Convert(to, typeof(Array))), fieldType)));
                    }
                }
                else if (fieldType.IsSealed && fieldType.GetConstructor(Type.EmptyTypes) is { } constructor)
                {
                    // from null: null. Else the destination object (a new one when it has none), filled by the
                    // nested type's copier, called through its holder.
                    var nested = DirectHolder(fieldType);
                    var target = Expression.Variable(fieldType, "t");
                    body.Add(Expression.IfThenElse(
                        Expression.Equal(from, Expression.Constant(null, fieldType)),
                        Expression.Assign(to, Expression.Constant(null, fieldType)),
                        Expression.Block([target],
                            Expression.Assign(target, Expression.Coalesce(to, Expression.New(constructor))),
                            Expression.Call(Expression.Field(Expression.Constant(nested), HolderCopierField), InvokeCopierMethod,
                                Expression.Convert(from, typeof(object)), Expression.Convert(target, typeof(object))),
                            Expression.Assign(to, target))));
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
