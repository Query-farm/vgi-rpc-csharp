using System.Buffers;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using QueryFarm.VgiRpc.Errors;

namespace QueryFarm.VgiRpc.Wire;

/// <summary>
/// Reads one Arrow IPC stream (a schema message followed by zero or more record-batch messages,
/// terminated by the 8-byte EOS marker) from a shared byte channel — never disposing that
/// channel itself. See <see cref="WireWriter"/> for the corresponding writer and the "one stream
/// per request/response/turn" framing this pair implements.
///
/// Thin wrapper over <see cref="ArrowStreamReader"/> from the vendored, patched Apache.Arrow
/// (see third_party/apache-arrow-dotnet/README.md) — this type adds only the
/// <see cref="AnnotatedBatch"/> metadata plumbing, plus draining an oversized message body on
/// behalf of <see cref="PayloadTooLargeException"/> (see that type's doc comment).
/// </summary>
public sealed class WireReader : IDisposable
{
    private readonly Stream _stream;
    private readonly ArrowStreamReader _reader;

    public WireReader(Stream stream)
    {
        _stream = stream;
        _reader = new ArrowStreamReader(stream, leaveOpen: true);
    }

    /// <summary>The stream's schema. Reads the schema message on first access if not already read.</summary>
    public async Task<Schema> ReadSchemaAsync(CancellationToken cancellationToken = default) =>
        await _reader.GetSchema(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads the next record-batch message, or <see langword="null"/> once the EOS marker is
    /// reached. Callers must have consumed the schema (via <see cref="ReadSchemaAsync"/>) first.
    /// The caller owns a non-null result and must dispose its <see cref="AnnotatedBatch.Batch"/>.
    ///
    /// Throws <see cref="PayloadTooLargeException"/> — rather than letting
    /// <see cref="ArrowIpcBodyTooLargeException"/> propagate directly — for a message whose
    /// declared body exceeds what this reader can materialize: the oversized body is drained
    /// (discarded) from the stream first, so by the time this throws the stream is back in sync
    /// and a caller can safely reply with a typed error and keep serving, instead of having to
    /// assume the connection is unrecoverable.
    /// </summary>
    public async Task<AnnotatedBatch?> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        RecordBatch? batch;
        try
        {
            batch = await _reader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArrowIpcBodyTooLargeException exc)
        {
            await DrainAsync(_stream, exc.DeclaredBodyLength, cancellationToken).ConfigureAwait(false);
            throw new PayloadTooLargeException(exc.DeclaredBodyLength);
        }

        return batch is null ? null : new AnnotatedBatch(batch, _reader.LastBatchCustomMetadata);
    }

    /// <summary>
    /// Reads and discards any batches still remaining on this logical IPC stream, through its
    /// EOS marker. Required after reading the one batch a caller actually wanted (a unary/stream-
    /// init request, one exchange turn) and before doing anything else with the shared underlying
    /// channel — <see cref="ArrowStreamReader"/> (like every other Arrow IPC reader this port's
    /// sibling ports use — confirmed against both <c>vgi-rpc-go</c>'s <c>drainInputStream</c>/
    /// <c>ipc.NewReader(...).Next()</c> loop and <c>vgi-rpc-java</c>'s <c>IpcStreamReader.drain()</c>,
    /// both called unconditionally at the equivalent point) stops as soon as it has the one batch
    /// a caller asked for; it does NOT consume the trailing EOS marker (or any further batches) on
    /// its own. Leaving those bytes unread means the NEXT reader constructed over the same channel
    /// — e.g. <see cref="Server.RpcServer.ServeStreamAsync"/>'s own input reader, built fresh right
    /// after the stream-init request is read — starts misaligned, no longer at a real message
    /// boundary. Safe to call even when the stream is already fully consumed (a no-op).
    /// </summary>
    public async Task DrainRemainingBatchesAsync(CancellationToken cancellationToken = default)
    {
        while (await ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } batch)
        {
            batch.Batch.Dispose();
        }
    }

    /// <summary>Reads and discards exactly <paramref name="byteCount"/> bytes so a stream stays
    /// in sync after refusing a message without materializing its body.</summary>
    private static async Task DrainAsync(Stream stream, long byteCount, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (byteCount > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, byteCount);
                var read = await stream.ReadAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // The sender hung up mid-write — nothing left to drain, and there's no
                    // connection left to keep in sync either; let the caller's outer catch-all
                    // treat this the same as any other "channel closed" case.
                    throw new EndOfStreamException("Stream ended while draining an oversized message body.");
                }

                byteCount -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose() => _reader.Dispose();
}
