using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace QueryFarm.VgiRpc.Wire;

/// <summary>
/// Writes one Arrow IPC stream (a schema message, zero or more <see cref="AnnotatedBatch"/>
/// record-batch messages, then the 8-byte EOS marker) onto a shared byte channel — never
/// disposing that channel itself. A request, a unary response, and each turn of a lockstep
/// streaming exchange are each exactly one such stream; a fresh <see cref="WireWriter"/> is
/// created per stream on the same underlying <see cref="Stream"/>.
///
/// This is a thin wrapper over <see cref="ArrowStreamWriter"/> from the vendored, patched
/// Apache.Arrow (see third_party/apache-arrow-dotnet/README.md) — all buffer layout, alignment,
/// and dictionary-encoding correctness is Apache.Arrow's, not reimplemented here. The only thing
/// this type adds is the <see cref="AnnotatedBatch"/> metadata plumbing and an
/// <see cref="IAsyncDisposable"/> shape that always leaves the underlying stream open.
/// </summary>
public sealed class WireWriter : IAsyncDisposable
{
    private readonly ArrowStreamWriter _writer;
    private readonly Stream _stream;
    private bool _ended;

    public WireWriter(Stream stream, Schema schema)
    {
        _stream = stream;
        // leaveOpen: true — the underlying transport stream is shared across many WireWriter/
        // WireReader instances over the lifetime of a connection; only *this* IPC stream's
        // framing (schema..EOS) belongs to us.
        _writer = new ArrowStreamWriter(stream, schema, leaveOpen: true);
    }

    /// <summary>Writes the schema message. Idempotent; also called implicitly by the first batch write.</summary>
    public async Task WriteStartAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WriteStartAsync(cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task FlushAsync(CancellationToken cancellationToken = default) =>
        _stream.FlushAsync(cancellationToken);

    public Task WriteBatchAsync(AnnotatedBatch batch, CancellationToken cancellationToken = default) =>
        _writer.WriteRecordBatchAsync(batch.Batch, batch.Metadata, cancellationToken);

    /// <summary>
    /// Writes a framework-owned batch and disposes it after the asynchronous write completes.
    /// </summary>
    internal async Task WriteOwnedBatchAsync(
        RecordBatch batch,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        using (batch)
        {
            await _writer.WriteRecordBatchAsync(batch, metadata, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the 8-byte EOS marker, closing this logical IPC stream (not the channel).</summary>
    public async Task WriteEosAsync(CancellationToken cancellationToken = default)
    {
        if (_ended)
        {
            return;
        }

        await _writer.WriteEndAsync(cancellationToken).ConfigureAwait(false);
        // This is required when the transport coalesces Arrow's many small writes (notably
        // buffered stdio): one complete logical IPC response must be visible before its peer can
        // issue the next request. Flush is a no-op for MemoryStream and NetworkStream.
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _ended = true;
    }

    /// <summary>
    /// Writes the EOS marker if it hasn't been written yet (a safety net for callers who forget
    /// to call <see cref="WriteEosAsync"/> explicitly on the success path), then disposes the
    /// wrapped writer without touching the underlying channel. Failures while writing that
    /// trailing EOS (e.g. the channel is already broken) are swallowed — Dispose must not throw,
    /// and the caller has already gotten to decide whether the exchange succeeded.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await WriteEosAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort only — see remarks above.
        }

        _writer.Dispose();
    }
}
