using System.Security.Cryptography;
using Apache.Arrow;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// ExternalLocation batch support for large data batches — a port of the canonical Python repo's
/// <c>vgi_rpc.external</c>. When a record batch exceeds a configurable size threshold, it can be
/// externalized to remote storage (S3, GCS, or any HTTP-addressable backend) and replaced with a
/// zero-row pointer batch carrying a <c>vgi_rpc.location</c> metadata key. Readers resolve the
/// pointer transparently; writers externalize batches above the threshold. See
/// <c>docs/roadmap.md</c> M13 for what this port implements and what it deliberately doesn't yet.
///
/// <para><b>Object lifecycle</b>: vgi-rpc does not delete uploaded objects. Every
/// <see cref="IExternalStorage.UploadAsync"/>/<see cref="IUploadUrlProvider.GenerateUploadUrlAsync"/>
/// call creates a new object that persists until the operator removes it — configure
/// storage-level lifecycle rules (S3 Lifecycle Policy, GCS Object Lifecycle Management).</para>
///
/// <para><b>Scope narrower than Python here</b>: this port externalizes the unary <i>result</i>
/// batch and each producer/exchange turn's single emitted data batch. Python's
/// <c>maybe_externalize_collector</c> additionally externalizes the turn's <i>log</i> batches
/// alongside the data batch when the data batch crosses the threshold — this port's log batches
/// always stay inline (they're small, zero-row control messages; only the data batch is ever a
/// candidate). <c>max_externalized_response_bytes</c> is still enforced hard on every method type
/// with no continuation escape valve, matching the spec, even though the unrelated
/// <c>max_response_bytes</c> wire cap stays soft/unenforced for producer turns (see
/// <c>docs/roadmap.md</c> M7). Request-side pointer <i>resolution</i> (an incoming request/
/// exchange-turn batch that is itself a pointer) is fully wired for unary, stream init, and
/// exchange.</para>
/// </summary>
public static class ExternalLocation
{
    /// <summary>Checks whether <paramref name="batch"/> is an ExternalLocation pointer: a
    /// zero-row batch whose custom metadata contains <see cref="MetadataKeys.Location"/> and does
    /// NOT contain <see cref="MetadataKeys.LogLevel"/> (which would make it a log batch).</summary>
    public static bool IsExternalLocationBatch(RecordBatch batch, IReadOnlyDictionary<string, string>? metadata)
    {
        if (batch.Length != 0 || metadata is null)
        {
            return false;
        }

        return metadata.ContainsKey(MetadataKeys.Location) && !metadata.ContainsKey(MetadataKeys.LogLevel);
    }

    /// <summary>Creates a zero-row pointer batch for an externalized location.</summary>
    /// <param name="schema">The schema the pointer batch should conform to.</param>
    /// <param name="url">The URL where the actual data resides.</param>
    /// <param name="sha256">Optional hex-encoded SHA-256 of the raw IPC bytes (pre-compression),
    /// included as <see cref="MetadataKeys.LocationSha256"/> so consumers can verify integrity on
    /// fetch.</param>
    public static (RecordBatch Batch, Dictionary<string, string> Metadata) MakePointerBatch(Schema schema, string url, string? sha256 = null)
    {
        var batch = ValueCodec.EmptyRow(schema);
        var metadata = new Dictionary<string, string> { [MetadataKeys.Location] = url };
        if (sha256 is not null)
        {
            metadata[MetadataKeys.LocationSha256] = sha256;
        }

        return (batch, metadata);
    }

    /// <summary>Serializes a single batch (schema + one batch + EOS) as a standalone IPC stream —
    /// the exact bytes uploaded to storage or fetched back on resolution.</summary>
    public static async Task<byte[]> SerializeBatchAsync(RecordBatch batch, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await using (var writer = new WireWriter(buffer, batch.Schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, metadata), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Possibly externalizes a single batch — for unary results. Below-threshold or zero-row
    /// batches, or a <see langword="null"/> <see cref="ServerExternalConfig.Storage"/>, are
    /// returned unchanged with <c>ExternalBytes: 0</c>. Externalized batches return the pointer
    /// batch and the raw (pre-compression) IPC byte count — callers enforcing
    /// <c>max_externalized_response_bytes</c> should predict against that count via
    /// <see cref="PredictExternalizeBytes"/> <i>before</i> calling this (the whole point of a
    /// predict/refuse split — see that method's doc comment).
    /// </summary>
    public static async Task<(RecordBatch Batch, IReadOnlyDictionary<string, string>? Metadata, int ExternalBytes)> MaybeExternalizeAsync(
        RecordBatch batch, IReadOnlyDictionary<string, string>? metadata, ServerExternalConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Storage is null || batch.Length == 0)
        {
            return (batch, metadata, 0);
        }

        if (batch.GetTotalBufferSize() < config.ExternalizeThresholdBytes)
        {
            return (batch, metadata, 0);
        }

        var ipcBytes = await SerializeBatchAsync(batch, metadata, cancellationToken).ConfigureAwait(false);
        var dataSha256 = Convert.ToHexStringLower(SHA256.HashData(ipcBytes));

        string? contentEncoding = null;
        var rawSize = ipcBytes.Length;
        if (config.Compression is { } compression)
        {
            ipcBytes = ExternalCompression.Compress(compression.Algorithm, ipcBytes, compression.Level);
            contentEncoding = compression.Algorithm;
        }

        var url = await config.Storage.UploadAsync(ipcBytes, batch.Schema, contentEncoding, cancellationToken).ConfigureAwait(false);
        var (pointerBatch, pointerMetadata) = MakePointerBatch(batch.Schema, url, dataSha256);
        return (pointerBatch, pointerMetadata, rawSize);
    }

    /// <summary>
    /// Predicts the external upload size if <see cref="MaybeExternalizeAsync"/> ran now on
    /// <paramref name="batch"/> — <c>0</c> when externalization wouldn't fire. Lets HTTP dispatch
    /// refuse a cap-violating upload <i>before</i> paying for the storage round-trip: the
    /// operator's intent in setting <c>max_externalized_response_bytes</c> is "don't emit data
    /// beyond this per call", not "emit and then complain" (once bytes are uploaded, they cannot
    /// be un-uploaded — the cap has no soft/continuation escape valve the way
    /// <c>max_response_bytes</c> does for producer streams).
    /// </summary>
    public static long PredictExternalizeBytes(RecordBatch batch, ServerExternalConfig? config)
    {
        if (config?.Storage is null || batch.Length == 0)
        {
            return 0;
        }

        var size = batch.GetTotalBufferSize();
        return size < config.ExternalizeThresholdBytes ? 0 : size;
    }

    /// <summary>
    /// Resolves an ExternalLocation pointer batch by fetching its URL, or returns
    /// <paramref name="batch"/>/<paramref name="metadata"/> unchanged if it isn't one (or
    /// <paramref name="config"/> is <see langword="null"/>). Safe to call on any batch.
    /// </summary>
    /// <param name="batch">The inline data batch or external-location pointer.</param>
    /// <param name="metadata">Custom metadata associated with <paramref name="batch"/>.</param>
    /// <param name="config">External fetch policy, or <see langword="null"/> to leave pointers unresolved.</param>
    /// <param name="cancellationToken">Cancels fetching and Arrow IPC decoding.</param>
    /// <param name="onLog">Optional callback for log or error batches embedded in the fetched
    /// IPC stream. The callback must not retain the batch; its lifetime ends when the callback
    /// returns.</param>
    /// <exception cref="Errors.RpcException">On fetch failure, checksum mismatch, redirect loop, a
    /// fetched stream with no data batch (or more than one), or a schema mismatch — mirrors
    /// Python's <c>resolve_external_location</c> raising <c>RuntimeError</c>/<c>ValueError</c>,
    /// translated to this port's uniform wire-exception type.</exception>
    public static async Task<(RecordBatch Batch, IReadOnlyDictionary<string, string>? Metadata)> ResolveAsync(
        RecordBatch batch,
        IReadOnlyDictionary<string, string>? metadata,
        ClientExternalConfig? config,
        CancellationToken cancellationToken = default,
        Action<AnnotatedBatch>? onLog = null)
    {
        if (config is null || !IsExternalLocationBatch(batch, metadata))
        {
            return (batch, metadata);
        }

        var url = metadata![MetadataKeys.Location];
        var expectedSha256 = metadata.GetValueOrDefault(MetadataKeys.LocationSha256);

        var data = await ExternalFetch.FetchUrlAsync(url, config.FetchConfig, config.UrlValidator, cancellationToken).ConfigureAwait(false);

        if (expectedSha256 is not null)
        {
            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new Errors.RpcException("RuntimeError", $"SHA-256 checksum mismatch for {ExternalFetch.RedactUrl(url)}: expected {expectedSha256}, got {actualSha256}");
            }
        }

        using var reader = new WireReader(new MemoryStream(data));
        _ = await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        var dataBatches = new List<AnnotatedBatch>();
        try
        {
            while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } fetched)
            {
                if (fetched.Metadata?.ContainsKey(MetadataKeys.Location) == true)
                {
                    fetched.Batch.Dispose();
                    throw new Errors.RpcException("RuntimeError", $"Redirect loop detected: fetched batch from {ExternalFetch.RedactUrl(url)} contains vgi_rpc.location");
                }

                if (fetched.Batch.Length == 0 && fetched.Metadata?.ContainsKey(MetadataKeys.LogLevel) == true)
                {
                    try
                    {
                        onLog?.Invoke(fetched);
                    }
                    finally
                    {
                        fetched.Batch.Dispose();
                    }

                    continue;
                }

                dataBatches.Add(fetched);
            }

            if (dataBatches.Count == 0)
            {
                throw new Errors.RpcException("RuntimeError", $"No data batch found in ExternalLocation stream from {ExternalFetch.RedactUrl(url)}");
            }

            if (dataBatches.Count > 1)
            {
                throw new Errors.RpcException("RuntimeError", $"Multiple data batches ({dataBatches.Count}) found in ExternalLocation stream from {ExternalFetch.RedactUrl(url)}");
            }

            var resolved = dataBatches[0];
            // Schema (Apache.Arrow) never overrides object.Equals — it's reference equality by
            // default, which would always fail here since the fetched schema is a distinct instance
            // from the original pointer's schema even when structurally identical. Use the same
            // structural (name/type-id) comparison ValueCodec.CoerceBatch's own fast path uses.
            if (!Reflection.ValueCodec.SchemasEqual(resolved.Batch.Schema, batch.Schema))
            {
                throw new Errors.RpcException("ValueError", $"Schema mismatch in ExternalLocation: expected {batch.Schema}, got {resolved.Batch.Schema}");
            }

            dataBatches.Clear(); // ownership transfers to the caller
            return (resolved.Batch, resolved.Metadata);
        }
        finally
        {
            foreach (var unclaimed in dataBatches)
            {
                unclaimed.Batch.Dispose();
            }
        }
    }
}

/// <summary>Pluggable storage interface for externalizing large batches. Implementations must be
/// thread-safe — <see cref="UploadAsync"/> may be called concurrently from different requests.</summary>
public interface IExternalStorage
{
    /// <summary>Uploads serialized IPC data and returns a URL for retrieval. The uploaded object
    /// is not automatically deleted — see <see cref="ExternalLocation"/>'s class doc comment.</summary>
    /// <param name="data">Complete Arrow IPC stream bytes.</param>
    /// <param name="schema">The schema of the data being uploaded — backends may use this for
    /// content-type or metadata hints.</param>
    /// <param name="contentEncoding">Optional encoding applied to <paramref name="data"/> (e.g.
    /// <c>"zstd"</c>) — backends should store this so fetchers can decompress correctly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken);
}

/// <summary>Pre-signed URL pair for client-side data upload. S3/GCS pre-signed URLs are signed
/// per HTTP method, so a PUT URL cannot be used for GET — hence two URLs for the same object.</summary>
public sealed record UploadUrl(string UploadUrlValue, string DownloadUrl, DateTimeOffset ExpiresAt);

/// <summary>Generates pre-signed upload URL pairs. Implementations must be thread-safe.</summary>
public interface IUploadUrlProvider
{
    /// <summary>Generates a pre-signed upload/download URL pair for a new storage object. The
    /// caller receives time-limited PUT and GET URLs — the object is not automatically deleted.</summary>
    /// <param name="schema">The Arrow schema of the data to be uploaded — backends may use this
    /// for content-type or metadata hints.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UploadUrl> GenerateUploadUrlAsync(Schema schema, CancellationToken cancellationToken);
}

/// <summary>
/// Bundles every M13 externalization knob <see cref="RpcHttpEndpoints.MapVgiRpc"/> accepts, so
/// that call site takes one parameter instead of five. All fields are independently optional —
/// an operator wanting only request-side pointer resolution (no response externalization, no
/// upload-URL vending) sets just <see cref="External"/>, for example.
/// </summary>
public sealed class ExternalizationOptions
{
    /// <summary>Drives both directions of server-side externalization: uploading oversized unary
    /// results (<see cref="ServerExternalConfig.Storage"/>) and resolving client-vended pointer
    /// batches on incoming requests/exchange turns (<see cref="ServerExternalConfig.FetchConfig"/>/
    /// <see cref="ServerExternalConfig.UrlValidator"/>). <see langword="null"/> disables both.</summary>
    public ServerExternalConfig? External { get; init; }

    /// <summary>Enables <c>POST {prefix}/__upload_url__/init</c> when non-null — lets a client
    /// externalize an oversized <i>request</i> by vending it a pre-signed upload/download URL
    /// pair to PUT to directly.</summary>
    public IUploadUrlProvider? UploadUrlProvider { get; init; }

    /// <summary>Hard cap on inbound request body size (pre-decompression, on-wire bytes) — see
    /// <see cref="RequestCap"/>. Advertised via <c>VGI-Max-Request-Bytes</c>.</summary>
    public long? MaxRequestBytes { get; init; }

    /// <summary>Advertised via <c>VGI-Max-Upload-Bytes</c> — informational only in this port (the
    /// upload-URL provider itself is responsible for enforcing it against what actually lands in
    /// storage; this server never sees the uploaded bytes, since the client PUTs directly to the
    /// vended pre-signed URL).</summary>
    public long? MaxUploadBytes { get; init; }

    /// <summary>Hard cap on the raw (pre-compression) byte count of any single externalized
    /// upload — unlike <c>max_response_bytes</c>, this has no soft/continuation escape valve
    /// (see <see cref="ExternalLocation.PredictExternalizeBytes"/>'s doc comment). Advertised via
    /// <c>VGI-Max-Externalized-Response-Bytes</c>.</summary>
    public long? MaxExternalizedResponseBytes { get; init; }
}

/// <summary>Compression settings for externalized data.</summary>
/// <param name="Algorithm">Either <c>"zstd"</c> or <c>"gzip"</c>.</param>
/// <param name="Level">Codec-specific level.</param>
public sealed record Compression(string Algorithm = "zstd", int Level = 3);

/// <summary>
/// Server-side configuration for ExternalLocation batch support. Owns the trust boundary: the
/// server is the only party that decides where externalized data goes (<see cref="Storage"/>),
/// what compression to apply, and which inbound URLs are fetchable from the server side (via
/// <see cref="UrlValidator"/>, when resolving client-vended pointer batches on <i>requests</i>).
/// </summary>
public sealed class ServerExternalConfig
{
    /// <summary>Storage backend for uploading server-produced batches that exceed
    /// <see cref="ExternalizeThresholdBytes"/>. <see langword="null"/> disables
    /// server-to-client externalization.</summary>
    public IExternalStorage? Storage { get; init; }

    /// <summary>Server-produced batch buffer size above which to externalize. Uses
    /// <c>RecordBatch.GetTotalBufferSize()</c> as a fast O(1) estimate.</summary>
    public long ExternalizeThresholdBytes { get; init; } = 1_048_576;

    /// <summary>Fetch configuration for resolving client-vended pointer batches on requests.</summary>
    public FetchConfig FetchConfig { get; init; } = new();

    /// <summary>Compression settings for externalized data. <see langword="null"/> disables
    /// compression (default).</summary>
    public Compression? Compression { get; init; }

    /// <summary>Callback invoked before the server fetches a client-vended pointer URL — throw to
    /// reject. Defaults to <see cref="ExternalFetch.HttpsOnlyValidator"/>.</summary>
    public Action<string>? UrlValidator { get; init; } = ExternalFetch.HttpsOnlyValidator;
}

/// <summary>
/// Client-side configuration for resolving ExternalLocation pointer batches embedded in
/// responses. Used by both the native HTTP client and server-side conformance helpers; covers
/// only the fetch side.
/// </summary>
public sealed class ClientExternalConfig
{
    public FetchConfig FetchConfig { get; init; } = new();

    public Action<string>? UrlValidator { get; init; } = ExternalFetch.HttpsOnlyValidator;
}

/// <summary>zstd/gzip compression for externalized upload bytes. This is a separate code path
/// from response wire-compression negotiation (<see cref="ContentEncoding"/>/M6-M7):
/// externalized objects are fetched via plain HTTP GET by whatever storage
/// client resolves them, never through this port's own response-negotiation path.</summary>
internal static class ExternalCompression
{
    public static byte[] Compress(string algorithm, byte[] data, int level) => algorithm switch
    {
        "zstd" => CompressZstd(data, level),
        "gzip" => CompressGzip(data, level),
        _ => throw new ArgumentException($"Unsupported compression algorithm: {algorithm}", nameof(algorithm)),
    };

    public static byte[] Decompress(string algorithm, byte[] data) => algorithm switch
    {
        "zstd" => DecompressZstd(data),
        "gzip" => DecompressGzip(data),
        _ => throw new ArgumentException($"Unsupported compression algorithm: {algorithm}", nameof(algorithm)),
    };

    private static byte[] CompressZstd(byte[] data, int level)
    {
        using var compressor = new ZstdSharp.Compressor(level);
        return compressor.Wrap(data).ToArray();
    }

    private static byte[] DecompressZstd(byte[] data)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(data).ToArray();
    }

    private static byte[] CompressGzip(byte[] data, int level)
    {
        using var output = new MemoryStream();
        var compressionLevel = level >= 7 ? System.IO.Compression.CompressionLevel.SmallestSize : System.IO.Compression.CompressionLevel.Optimal;
        using (var gzip = new System.IO.Compression.GZipStream(output, compressionLevel))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
