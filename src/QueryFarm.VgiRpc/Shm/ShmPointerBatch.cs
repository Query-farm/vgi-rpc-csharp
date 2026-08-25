using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Shm;

/// <summary>
/// SHM pointer-batch protocol — a port of the canonical Python repo's
/// <c>vgi_rpc.shm.is_shm_pointer_batch</c>/<c>make_shm_pointer_batch</c>/<c>resolve_shm_batch</c>/
/// <c>maybe_write_to_shm</c>. See <c>docs/WIRE_PROTOCOL.md</c> §11.
/// </summary>
public static class ShmPointerBatch
{
    /// <summary>IPC stream EOS marker: continuation token (<c>0xFFFFFFFF</c>) + zero-length
    /// metadata — the 8 trailing bytes every Arrow IPC stream ends with.</summary>
    internal static readonly byte[] EosMarker = [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Smallest batch (bytes) worth shipping through SHM; below this the pipe wins —
    /// SHM's fixed per-batch cost (slot allocation, pointer round trip, the peer's resolve/free)
    /// only pays off once the copy it avoids is large enough. Matches the canonical Python repo's
    /// crossover values and its <c>VGI_RPC_SHM_MIN_BATCH_BYTES</c> environment override exactly,
    /// so a deployment tuning one language's worker via that variable gets the same behavior from
    /// this one.</summary>
    public static long MinBatchBytes { get; } = ResolveMinBatchBytes();

    private static long ResolveMinBatchBytes()
    {
        var raw = Environment.GetEnvironmentVariable("VGI_RPC_SHM_MIN_BATCH_BYTES");
        if (raw is not null && long.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return OperatingSystem.IsWindows() ? 1024 * 1024 : 128 * 1024;
    }

    /// <summary>Checks whether <paramref name="batch"/> is a shared-memory pointer: a zero-row
    /// batch whose custom metadata contains <see cref="MetadataKeys.ShmOffset"/> and does NOT
    /// contain <see cref="MetadataKeys.LogLevel"/> (which would make it a log batch).</summary>
    public static bool IsShmPointerBatch(RecordBatch batch, IReadOnlyDictionary<string, string>? metadata)
    {
        if (batch.Length != 0 || metadata is null)
        {
            return false;
        }

        return metadata.ContainsKey(MetadataKeys.ShmOffset) && !metadata.ContainsKey(MetadataKeys.LogLevel);
    }

    /// <summary>Creates a zero-row pointer batch for a shared-memory region.</summary>
    public static (RecordBatch Batch, Dictionary<string, string> Metadata) Make(Schema schema, long offset, long length)
    {
        var batch = ValueCodec.EmptyRow(schema);
        var metadata = new Dictionary<string, string>
        {
            [MetadataKeys.ShmOffset] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [MetadataKeys.ShmLength] = length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return (batch, metadata);
    }

    /// <summary>
    /// Resolves a shared-memory pointer batch by reading its region from <paramref name="shm"/>,
    /// or returns <paramref name="batch"/>/<paramref name="metadata"/> unchanged if it isn't one
    /// (or <paramref name="shm"/> is <see langword="null"/>). Safe to call on any batch.
    /// </summary>
    /// <returns>The resolved batch, its metadata (pointer keys stripped, provenance added via
    /// <see cref="MetadataKeys.ShmSource"/>), and a release callback that frees the region — or
    /// <see langword="null"/> release when the batch wasn't a pointer.</returns>
    public static async Task<(RecordBatch Batch, IReadOnlyDictionary<string, string>? Metadata, Action? Release)> ResolveAsync(
        RecordBatch batch, IReadOnlyDictionary<string, string>? metadata, ShmSegment? shm, CancellationToken cancellationToken = default)
    {
        if (shm is null || !IsShmPointerBatch(batch, metadata))
        {
            return (batch, metadata, null);
        }

        var offset = long.Parse(metadata![MetadataKeys.ShmOffset], System.Globalization.CultureInfo.InvariantCulture);
        var length = long.Parse(metadata[MetadataKeys.ShmLength], System.Globalization.CultureInfo.InvariantCulture);

        var buffer = shm.ReadBuffer(offset, length);
        var resolvedBatch = await DeserializeAsync(buffer, batch.Schema, cancellationToken).ConfigureAwait(false);

        var resolvedMetadata = new Dictionary<string, string>(metadata);
        resolvedMetadata.Remove(MetadataKeys.ShmOffset);
        resolvedMetadata.Remove(MetadataKeys.ShmLength);
        resolvedMetadata[MetadataKeys.ShmSource] = shm.Name;

        return (resolvedBatch, resolvedMetadata, () => shm.Free(offset));
    }

    /// <summary>Tries to write <paramref name="batch"/> to <paramref name="shm"/>; falls back to
    /// the original batch/metadata unchanged if it's too small to be worth it, doesn't fit, or no
    /// segment is available.</summary>
    public static async Task<(RecordBatch Batch, IReadOnlyDictionary<string, string>? Metadata)> MaybeWriteAsync(
        RecordBatch batch, IReadOnlyDictionary<string, string>? metadata, ShmSegment? shm, CancellationToken cancellationToken = default)
    {
        if (shm is null || batch.Length == 0 || batch.GetTotalBufferSize() < MinBatchBytes)
        {
            return (batch, metadata);
        }

        var result = await shm.AllocateAndWriteAsync(batch, cancellationToken).ConfigureAwait(false);
        if (result is not { } r)
        {
            return (batch, metadata);
        }

        var (pointerBatch, pointerMetadata) = Make(batch.Schema, r.Offset, r.Length);
        if (metadata is not null)
        {
            foreach (var (key, value) in metadata)
            {
                pointerMetadata[key] = value;
            }
        }

        return (pointerBatch, pointerMetadata);
    }

    private static async Task<RecordBatch> DeserializeAsync(byte[] buffer, Schema schema, CancellationToken cancellationToken)
    {
        if (!schema.FieldsList.Any(f => f.DataType is DictionaryType))
        {
            using var reader = new WireReader(new MemoryStream(buffer));
            _ = await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            var next = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            return next?.Batch ?? throw new InvalidDataException("SHM region carried no batch.");
        }

        // Dictionary path: re-synthesize the schema message ShmSegment stripped on write, then
        // append the EOS marker it also stripped, reconstructing a standalone IPC stream — see
        // ShmSegment.SerializeForShmAsync's doc comment for the write-side half of this.
        var schemaMessage = await ShmSegment.SchemaMessageBytesAsync(schema, cancellationToken).ConfigureAwait(false);
        var combined = new byte[schemaMessage.Length + buffer.Length + EosMarker.Length];
        schemaMessage.CopyTo(combined, 0);
        buffer.CopyTo(combined, schemaMessage.Length);
        EosMarker.CopyTo(combined, schemaMessage.Length + buffer.Length);

        using var combinedReader = new WireReader(new MemoryStream(combined));
        _ = await combinedReader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        var combinedNext = await combinedReader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        return combinedNext?.Batch ?? throw new InvalidDataException("SHM region carried no batch.");
    }
}
