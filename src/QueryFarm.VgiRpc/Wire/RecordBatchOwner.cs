using Apache.Arrow;

namespace QueryFarm.VgiRpc.Wire;

/// <summary>
/// Owns exactly one <see cref="RecordBatch"/> at a time and disposes it deterministically.
/// Replacing the batch transfers ownership to the replacement and disposes the previous batch.
/// </summary>
internal sealed class RecordBatchOwner(RecordBatch batch) : IDisposable
{
    private RecordBatch? _batch = batch ?? throw new ArgumentNullException(nameof(batch));
    private List<RecordBatch>? _retiredSharedBatches;

    public RecordBatch Batch => _batch ?? throw new ObjectDisposedException(nameof(RecordBatchOwner));

    /// <summary>
    /// Takes ownership of <paramref name="replacement"/>. The replaced batch is disposed unless
    /// the caller returned the same instance (the common no-op fast path for SHM/externalization).
    /// </summary>
    public RecordBatch Replace(RecordBatch replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = Batch;
        if (ReferenceEquals(previous, replacement))
        {
            return replacement;
        }

        _batch = replacement;
        previous.Dispose();
        return replacement;
    }

    /// <summary>
    /// Replaces the current batch while retaining the previous wrapper until disposal. Use this
    /// when the replacement may share Arrow arrays with the source (for example schema coercion);
    /// disposing the source immediately would invalidate the replacement's shared buffers.
    /// </summary>
    public RecordBatch ReplaceShared(RecordBatch replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = Batch;
        if (ReferenceEquals(previous, replacement))
        {
            return replacement;
        }

        _retiredSharedBatches ??= [];
        _retiredSharedBatches.Add(previous);
        _batch = replacement;
        return replacement;
    }

    public void Dispose()
    {
        _batch?.Dispose();
        _batch = null;
        if (_retiredSharedBatches is not null)
        {
            foreach (var retired in _retiredSharedBatches)
            {
                retired.Dispose();
            }

            _retiredSharedBatches = null;
        }
    }
}
