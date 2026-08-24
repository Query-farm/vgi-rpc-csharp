using Apache.Arrow;
using Apache.Arrow.Ipc;

namespace QueryFarm.VgiRpc.Wire;

/// <summary>
/// Reads one Arrow IPC stream (a schema message followed by zero or more record-batch messages,
/// terminated by the 8-byte EOS marker) from a shared byte channel — never disposing that
/// channel itself. See <see cref="WireWriter"/> for the corresponding writer and the "one stream
/// per request/response/turn" framing this pair implements.
///
/// Thin wrapper over <see cref="ArrowStreamReader"/> from the vendored, patched Apache.Arrow
/// (see third_party/apache-arrow-dotnet/README.md) — this type adds only the
/// <see cref="AnnotatedBatch"/> metadata plumbing.
/// </summary>
public sealed class WireReader : IDisposable
{
    private readonly ArrowStreamReader _reader;

    public WireReader(Stream stream)
    {
        _reader = new ArrowStreamReader(stream, leaveOpen: true);
    }

    /// <summary>The stream's schema. Reads the schema message on first access if not already read.</summary>
    public async Task<Schema> ReadSchemaAsync(CancellationToken cancellationToken = default) =>
        await _reader.GetSchema(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads the next record-batch message, or <see langword="null"/> once the EOS marker is
    /// reached. Callers must have consumed the schema (via <see cref="ReadSchemaAsync"/>) first.
    /// </summary>
    public async Task<AnnotatedBatch?> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        var batch = await _reader.ReadNextRecordBatchAsync(cancellationToken).ConfigureAwait(false);
        return batch is null ? null : new AnnotatedBatch(batch, _reader.LastBatchCustomMetadata);
    }

    public void Dispose() => _reader.Dispose();
}
