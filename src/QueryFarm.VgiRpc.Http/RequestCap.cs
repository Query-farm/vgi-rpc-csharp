namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Enforces <c>max_request_bytes</c> — the advertised cap on inbound request body size (spec
/// source: <c>docs/porting-guide.md</c>'s externalization coverage is implicit in the reference
/// Falcon middleware at <c>vgi_rpc/http/server/_middleware.py</c>; there is no standalone doc).
/// Real deployments use this to push large *requests* out of band via the upload-URL flow
/// (<c>docs/roadmap.md</c> M13) rather than inlining megabytes into the RPC body.
///
/// <para>A <c>Content-Length</c> header check alone is insufficient — a chunked request carries
/// no declared length at all, so the cap must also be enforced while the body is actually being
/// read. <see cref="CappedStream"/> does that. HTTP dispatch wraps both the raw request stream and,
/// for compressed requests, the decoded stream so the configured limit applies independently to
/// bytes received on the wire and bytes produced by decompression. This prevents a small
/// gzip/zstd body from expanding beyond the advertised allocation limit.</para>
/// </summary>
public static class RequestCap
{
    /// <summary>Checks <c>HttpRequest.ContentLength</c> against <paramref name="maxRequestBytes"/>
    /// as a fast pre-read rejection, then returns a stream reading <paramref name="request"/>'s
    /// raw body that will throw <see cref="RequestTooLargeException"/> if actually reading it
    /// exceeds the cap — catching chunked bodies with no declared length.</summary>
    /// <exception cref="RequestTooLargeException">The declared <c>Content-Length</c> alone
    /// already exceeds the cap.</exception>
    public static Stream Enforce(Microsoft.AspNetCore.Http.HttpRequest request, long? maxRequestBytes)
    {
        if (maxRequestBytes is not { } cap)
        {
            return request.Body;
        }

        if (request.ContentLength is { } declared && declared > cap)
        {
            throw new RequestTooLargeException(declared, cap);
        }

        return new CappedStream(request.Body, cap);
    }
}

/// <summary>Thrown by <see cref="RequestCap.Enforce"/>/<see cref="CappedStream"/> when a request
/// body exceeds the configured cap — callers should catch this and answer a real HTTP 413,
/// distinct from every other in-band RPC error this port folds into 200 (this one is a genuine
/// transport-level rejection, matching the porting guide's own reasoning: a caller must be able
/// to distinguish "your payload is too big" from an application-level RPC error).</summary>
public sealed class RequestTooLargeException(long actualOrObservedBytes, long maxRequestBytes) : Exception(
    $"Request body exceeds max_request_bytes ({actualOrObservedBytes} > {maxRequestBytes}). " +
    "Use the upload-URL flow (POST {prefix}/__upload_url__/init) to send large payloads out of band.")
{
    public long ActualOrObservedBytes { get; } = actualOrObservedBytes;

    public long MaxRequestBytes { get; } = maxRequestBytes;
}

/// <summary>A read-only stream wrapper that throws <see cref="RequestTooLargeException"/> once
/// more than <paramref name="capBytes"/> total bytes have been read from <paramref name="inner"/>.
/// When <paramref name="leaveOpen"/> is false, disposing this wrapper also disposes the wrapped
/// stream (used by the decoded cap to release its decompressor).</summary>
internal sealed class CappedStream(Stream inner, long capBytes, bool leaveOpen = true) : Stream
{
    private long _totalRead;
    private bool _disposed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        _totalRead += read;
        if (_totalRead > capBytes)
        {
            throw new RequestTooLargeException(_totalRead, capBytes);
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _totalRead += read;
        if (_totalRead > capBytes)
        {
            throw new RequestTooLargeException(_totalRead, capBytes);
        }

        return read;
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (!leaveOpen)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
