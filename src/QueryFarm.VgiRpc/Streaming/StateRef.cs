using System.Runtime.CompilerServices;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;

namespace QueryFarm.VgiRpc.Streaming;

/// <summary>
/// A stream-state reference (<c>STATE_REF_V1.md</c>): "this part of my stream state does not
/// change". Created once by <see cref="Server.ICallContext.StateRef(byte[])"/> in a stream
/// method's init handler, held in the stream's state, and read back on any turn with
/// <see cref="Server.ICallContext.Resolve(StateRef)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-replica in 3.4 (§5.4).</b> vgi-rpc-csharp keeps every stream's state in server
/// memory, on HTTP as on pipe, so a <see cref="StateRef"/> created by a worker is always held in
/// memory (<see cref="Form"/> <c>"memory"</c>): it is never encoded, stored, sealed or fetched,
/// and resolving it costs nothing. The API matches the spec's so worker code is portable to the
/// SDKs whose state is serialized (where the same call may store the value once and carry a small
/// sealed tag).
/// </para>
/// <para>
/// A reference received as bytes (<see cref="FromEncoded"/>) is resolved by its form: the inline
/// form (<c>0x01 || codec || body</c>) decodes; the stored form (<c>0x02</c>) is refused with
/// <c>invalid_state_ref</c>, since this server never stores one; any other form byte is
/// <c>invalid_state_ref</c>.
/// </para>
/// <para>The value must be treated as immutable after it is wrapped: resolve may hand the same
/// instance to concurrent turns.</para>
/// </remarks>
public sealed class StateRef
{
    /// <summary>The inline form byte (v1).</summary>
    public const byte InlineForm = 0x01;

    /// <summary>The stored form byte (v1).</summary>
    public const byte StoredForm = 0x02;

    private readonly byte[]? _value;
    private readonly byte[]? _encoded;
    private object? _record;

    private StateRef(byte[]? value, byte[]? encoded, object? record)
    {
        _value = value;
        _encoded = encoded;
        _record = record;
    }

    /// <summary><c>"memory"</c> for a reference held in process memory (every reference this
    /// port creates), else the form of the encoded bytes: <c>"inline"</c> or <c>"stored"</c>.</summary>
    public string Form => _encoded is null
        ? "memory"
        : _encoded.Length > 0 && _encoded[0] == StoredForm ? "stored" : "inline";

    /// <summary>The encoded bytes, or <see langword="null"/> while the reference is held in
    /// memory.</summary>
    public byte[]? Encoded => _encoded?.ToArray();

    /// <summary>A reference as it appears in serialized state (§3): the form byte, then the
    /// form's payload. Not validated until it is resolved.</summary>
    public static StateRef FromEncoded(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return new StateRef(null, encoded.ToArray(), null);
    }

    /// <summary>The inline encoding of this reference, <c>0x01 || codec || body</c>, for a caller
    /// serializing state for its own reasons. Not cached on the reference (the reference's own
    /// form is unchanged), as in the reference implementation.</summary>
    public byte[] ToInlineBytes()
    {
        if (_encoded is not null)
        {
            return _encoded.ToArray();
        }

        var payload = TokenCodec.Pack(_value!);
        var encoded = new byte[payload.Length + 1];
        encoded[0] = InlineForm;
        payload.CopyTo(encoded, 1);
        return encoded;
    }

    internal static StateRef InMemory(byte[] value, object? record) => new(value, null, record);

    /// <summary>The value's bytes (§5.3): the in-memory value itself, or the decoded inline
    /// payload.</summary>
    /// <param name="maxBytes">Decompression bound for an encoded inline payload.</param>
    internal byte[] ResolveBytes(long maxBytes)
    {
        if (_value is not null)
        {
            return _value;
        }

        var encoded = _encoded!;
        if (encoded.Length == 0)
        {
            throw StateRefErrors.InvalidStateRef("empty StateRef");
        }

        switch (encoded[0])
        {
            case InlineForm:
                try
                {
                    return TokenCodec.Unpack(encoded.AsSpan(1), maxBytes);
                }
                catch (InvalidDataException)
                {
                    throw StateRefErrors.InvalidStateRef("StateRef payload does not decode (unknown codec or corrupt body)");
                }

            case StoredForm:
                throw StateRefErrors.InvalidStateRef(
                    "this server never stores StateRefs (single-replica, STATE_REF_V1.md §5.4), so a stored tag cannot be its own");
            default:
                throw StateRefErrors.InvalidStateRef("unknown StateRef form");
        }
    }

    /// <summary>The value as <typeparamref name="T"/>: the record instance this reference was
    /// created from when it is one, else the bytes decoded as a record.</summary>
    internal T ResolveRecord<T>(long maxBytes)
        where T : class
    {
        if (_record is T record)
        {
            return record;
        }

        var bytes = ResolveBytes(maxBytes);
        try
        {
            return (T)ValueCodec.DecodeRecord(bytes, typeof(T));
        }
        catch (Exception exc) when (exc is not RpcException)
        {
            throw StateRefErrors.InvalidStateRef($"StateRef value does not decode as {typeof(T).Name}");
        }
    }

    internal void AttachRecord(object record) => _record ??= record;
}

/// <summary>
/// The <see cref="StateRef"/> surface of one call context: refuses outside a stream's init
/// request, and memoizes within it (§2.2: the same object, or byte-equal values, give one
/// reference).
/// </summary>
internal sealed class StateRefScope(string protocol, string method)
{
    private readonly object _lock = new();
    private readonly Dictionary<byte[], StateRef> _byBytes = new(ByteArrayComparer.Instance);
    private readonly ConditionalWeakTable<object, StateRef> _byIdentity = new();

    public string Protocol { get; } = protocol;

    public string Method { get; } = method;

    /// <summary><see cref="Server.ICallContext.StateRef(byte[])"/> on a context with this scope
    /// (<paramref name="scope"/> non-null only on a stream's init context).</summary>
    public static StateRef Create(StateRefScope? scope, byte[] value, string? protocol, string? method)
    {
        ArgumentNullException.ThrowIfNull(value);
        return scope is null ? throw StateRefErrors.StateRefMisuse(protocol, method) : scope.Create(value);
    }

    /// <summary><see cref="Server.ICallContext.StateRef{T}(T)"/> on a context with this scope.</summary>
    public static StateRef Create<T>(StateRefScope? scope, T record, string? protocol, string? method)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        if (scope is null)
        {
            throw StateRefErrors.StateRefMisuse(protocol, method);
        }

        lock (scope._lock)
        {
            if (scope._byIdentity.TryGetValue(record, out var known))
            {
                return known;
            }
        }

        var reference = scope.Create(ValueCodec.EncodeRecord(record));
        reference.AttachRecord(record);
        lock (scope._lock)
        {
            scope._byIdentity.AddOrUpdate(record, reference);
        }

        return reference;
    }

    private StateRef Create(byte[] value)
    {
        lock (_lock)
        {
            if (!_byBytes.TryGetValue(value, out var reference))
            {
                reference = StateRef.InMemory(value, null);
                _byBytes[value] = reference;
            }

            return reference;
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
