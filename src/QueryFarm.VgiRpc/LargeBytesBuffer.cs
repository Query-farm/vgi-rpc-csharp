using Apache.Arrow;

namespace QueryFarm.VgiRpc;

/// <summary>
/// An owned, zero-copy large-binary value backed by an Arrow buffer.
/// </summary>
/// <remarks>
/// Use this type instead of <c>byte[]</c> for <c>large_binary</c> RPC parameters and results
/// when payload copies matter. Values decoded by vgi-rpc own a reference to the incoming Arrow
/// allocation and remain valid after the source record batch is disposed. Dispose values when
/// they are no longer needed; <see cref="Retain"/> creates an independent owner. Server method
/// parameters are borrowed for the duration of dispatch and disposed by the framework after the
/// result is encoded. A handler which stores one or captures it in a returned stream must retain
/// it first. Client result values are owned by the caller.
/// </remarks>
public sealed class LargeBytesBuffer : IDisposable
{
    private readonly object _gate = new();
    private ArrowBuffer _buffer;
    private bool _disposed;

    /// <summary>
    /// Wraps managed memory without copying it. The memory remains referenced for this value's
    /// lifetime; callers must not mutate it while an RPC write is in progress.
    /// </summary>
    public LargeBytesBuffer(ReadOnlyMemory<byte> memory)
        : this(new ArrowBuffer(memory))
    {
    }

    private LargeBytesBuffer(ArrowBuffer buffer)
    {
        _buffer = buffer;
    }

    /// <summary>The number of bytes in this value.</summary>
    public int Length
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _buffer.Length;
            }
        }
    }

    /// <summary>A read-only view of this value's bytes.</summary>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _buffer.Memory;
            }
        }
    }

    /// <summary>Copies this value into a new managed array.</summary>
    public byte[] ToArray() => Memory.ToArray();

    /// <summary>Creates an independently disposable owner of the same bytes.</summary>
    public LargeBytesBuffer Retain()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new LargeBytesBuffer(_buffer.Retain());
        }
    }

    internal static LargeBytesBuffer FromArray(LargeBinaryArray array, int index)
    {
        var offsets = array.ValueOffsets;
        var start = checked((int)offsets[index]);
        var length = checked((int)(offsets[index + 1] - offsets[index]));
        return new LargeBytesBuffer(array.ValueBuffer.Slice(start, length));
    }

    internal ArrowBuffer RetainBuffer()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _buffer.Retain();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _buffer.Dispose();
            _disposed = true;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Deterministically releases large-binary values extracted for one dispatch.</summary>
internal sealed class LargeBytesBufferArgumentsOwner(IEnumerable<object?> arguments) : IDisposable
{
    private LargeBytesBuffer[]? _values = Enumerable
        .Distinct<LargeBytesBuffer>(
            arguments.OfType<LargeBytesBuffer>(),
            ReferenceEqualityComparer.Instance)
        .ToArray();

    public void Dispose()
    {
        var values = Interlocked.Exchange(ref _values, null);
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            value.Dispose();
        }
    }
}
