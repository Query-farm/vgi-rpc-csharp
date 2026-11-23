// Copyright 2025, 2026 Query Farm LLC - https://query.farm

using System.Collections.Concurrent;
using System.Reflection;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// A value carried in a <c>binary</c> column as bytes the codec writes and reads verbatim, never
/// re-serialized: the raw-preserving wrapper of a nested record carried as binary (vgi
/// <c>docs/large-values-3.4.md</c> §3.3). <see cref="SchemaDerivation"/> derives <c>binary</c> for
/// it at any nesting level; <see cref="ValueCodec"/> writes <see cref="RawBinary"/> and builds a
/// received value with the implementing type's <c>FromRawBinary</c>.
/// </summary>
public interface IRawBinary
{
    /// <summary>The bytes the column holds, written exactly as they are.</summary>
    ReadOnlyMemory<byte> RawBinary { get; }
}

/// <summary>An <see cref="IRawBinary"/> the codec can construct from the received bytes.</summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface IRawBinary<TSelf> : IRawBinary where TSelf : IRawBinary<TSelf>
{
    /// <summary>Wrap received bytes (the codec passes a private copy) without decoding them.</summary>
    static abstract TSelf FromRawBinary(byte[] bytes);
}

internal static class RawBinaryFactory
{
    private static readonly ConcurrentDictionary<Type, Func<byte[], object>> s_factories = new();

    public static bool IsRawBinary(Type type) => typeof(IRawBinary).IsAssignableFrom(type);

    public static object Create(Type type, byte[] bytes) => s_factories.GetOrAdd(type, static t =>
    {
        var method = t.GetMethod("FromRawBinary", BindingFlags.Public | BindingFlags.Static, [typeof(byte[])])
            ?? throw new InvalidOperationException($"'{t}' implements IRawBinary but has no public static FromRawBinary(byte[]).");
        return bytes => method.Invoke(null, [bytes])!;
    })(bytes);
}
