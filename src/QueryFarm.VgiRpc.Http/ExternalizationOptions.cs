using QueryFarm.VgiRpc.External;

namespace QueryFarm.VgiRpc.Http;

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

    /// <summary>Advertised via <c>VGI-Max-Upload-Bytes</c>. <c>__upload_url__/init</c> refuses
    /// any entry whose declared <c>size</c> exceeds it, before minting a URL; the vended URL is
    /// signed for that exact size where the backend can, so storage rejects a longer body.</summary>
    /// <remarks>With an <see cref="UploadUrlProvider"/> it is always advertised and never exceeds the
    /// inbound fetch cap (<see cref="ServerExternalConfig.FetchConfig"/>): unset, the fetch cap is
    /// advertised; above an explicit fetch cap, <c>MapVgiRpc</c> refuses to start; above the
    /// default fetch cap, the fetch cap is raised to it.</remarks>
    public long? MaxUploadBytes { get; init; }

    /// <summary>The object store that resolves argument references (<c>STATE_REF_V1.md</c> §10).
    /// <see langword="null"/> (the default) uses <see cref="ServerExternalConfig.Storage"/>, else
    /// <see cref="UploadUrlProvider"/>, whichever implements <see cref="IObjectStore"/>.</summary>
    public IObjectStore? ObjectStore { get; init; }

    /// <summary>The object cache argument references resolve through; <see langword="null"/> (the
    /// default) is the process-wide <see cref="External.ObjectCache.Default"/>.</summary>
    public ObjectCache? ObjectCache { get; init; }

    /// <summary>The object store in effect (see <see cref="ObjectStore"/>).</summary>
    public IObjectStore? EffectiveObjectStore =>
        ObjectStore ?? External?.Storage as IObjectStore ?? UploadUrlProvider as IObjectStore;

    /// <summary>Further capability headers stamped on every response beside vgi-rpc's own (e.g. an
    /// application protocol's <c>VGI-Arguments-Ref-Bytes</c>). Names must not collide with
    /// vgi-rpc's headers.</summary>
    public IReadOnlyDictionary<string, string>? CapabilityHeaders { get; init; }

    /// <summary>
    /// The options the blob-vendor environment describes (vgi-rpc-go
    /// <c>ConfigureHTTPServerFromEnv</c>): <c>VGI_RPC_MAX_REQUEST_BYTES</c> is advertised and
    /// enforced whenever set; with <c>VGI_BLOB_VENDOR_URL</c>, large results are externalized
    /// through a <see cref="VendorStorage"/>
    /// (<see cref="BlobVendorEnvironment.ServerConfigFrom"/>), which also vends client upload URLs
    /// and resolves argument references, and <c>VGI-Max-Upload-Bytes</c> is advertised
    /// (<c>VGI_RPC_MAX_UPLOAD_BYTES</c>, else the inbound fetch cap). <see langword="null"/> when
    /// neither is set.
    /// </summary>
    /// <param name="environment">Variable lookup; defaults to the process environment.</param>
    /// <remarks>The backend follows <see cref="StorageBackendEnvironment.Select"/>: an explicit
    /// <c>VGI_STORAGE_BACKEND=none</c> ignores a vendor URL, and <c>s3</c> / <c>gcs</c> must be built
    /// with their packages' <c>ExternalizationFromEnvironment</c>.</remarks>
    /// <exception cref="ArgumentException">A malformed or contradictory value, or an s3/gcs backend.</exception>
    public static ExternalizationOptions? FromEnvironment(Func<string, string?>? environment = null)
    {
        var maxRequest = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxRequestBytes, environment);
        var backend = StorageBackendEnvironment.Select(environment);
        if (backend is StorageBackend.S3 or StorageBackend.Gcs)
        {
            throw new ArgumentException(
                $"{StorageBackendEnvironment.Backend}={backend.ToString().ToLowerInvariant()} needs the QueryFarm.VgiRpc.{backend} package: "
                + $"build the options with {(backend == StorageBackend.S3 ? "S3StorageEnvironment" : "GcsStorageEnvironment")}.ExternalizationFromEnvironment");
        }

        var external = backend == StorageBackend.Vendor ? BlobVendorEnvironment.ServerConfigFrom(environment) : null;
        if (backend == StorageBackend.Vendor && external is null)
        {
            throw new ArgumentException($"{StorageBackendEnvironment.Backend}=vendor needs {BlobVendorEnvironment.Url}");
        }

        if (external is null)
        {
            return maxRequest is null ? null : new ExternalizationOptions { MaxRequestBytes = maxRequest };
        }

        return new ExternalizationOptions
        {
            External = external,
            UploadUrlProvider = external.Storage as IUploadUrlProvider,
            MaxRequestBytes = maxRequest,
            MaxUploadBytes = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxUploadBytes, environment),
        };
    }

    /// <summary>A copy with <see cref="CapabilityHeaders"/> replaced.</summary>
    public ExternalizationOptions WithCapabilityHeaders(IReadOnlyDictionary<string, string>? headers) => new()
    {
        External = External,
        UploadUrlProvider = UploadUrlProvider,
        MaxRequestBytes = MaxRequestBytes,
        MaxUploadBytes = MaxUploadBytes,
        MaxExternalizedResponseBytes = MaxExternalizedResponseBytes,
        ObjectStore = ObjectStore,
        ObjectCache = ObjectCache,
        CapabilityHeaders = headers,
    };

    internal ExternalizationOptions With(long? maxUploadBytes = null, ServerExternalConfig? external = null) => new()
    {
        External = external ?? External,
        UploadUrlProvider = UploadUrlProvider,
        MaxRequestBytes = MaxRequestBytes,
        MaxUploadBytes = maxUploadBytes ?? MaxUploadBytes,
        MaxExternalizedResponseBytes = MaxExternalizedResponseBytes,
        ObjectStore = ObjectStore,
        ObjectCache = ObjectCache,
        CapabilityHeaders = CapabilityHeaders,
    };

    /// <summary>Hard cap on the raw (pre-compression) byte count of any single externalized
    /// upload — unlike <c>max_response_bytes</c>, this has no soft/continuation escape valve
    /// (see <see cref="ExternalLocation.PredictExternalizeBytes"/>'s doc comment). Advertised via
    /// <c>VGI-Max-Externalized-Response-Bytes</c>.</summary>
    public long? MaxExternalizedResponseBytes { get; init; }
}
