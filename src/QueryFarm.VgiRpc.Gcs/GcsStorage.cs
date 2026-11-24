using Apache.Arrow;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Http;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace QueryFarm.VgiRpc.Gcs;

/// <summary>
/// <see cref="IExternalStorage"/>/<see cref="IUploadUrlProvider"/> implementation backed by
/// Google Cloud Storage. Structurally mirrors vgi-rpc-java's <c>GcsStorage</c> (fluent builder,
/// disposable, caller-suppliable client for custom credentials/emulators) — see
/// docs/roadmap.md M19.
///
/// <para>Uploads under <c>&lt;keyPrefix&gt;&lt;uuid&gt;.arrow</c> and returns a V4 signed GET
/// URL. <see cref="StorageClient"/> is documented safe for concurrent use, satisfying both
/// interfaces' thread-safety requirement.</para>
/// </summary>
public sealed class GcsStorage : IExternalStorage, IUploadUrlProvider, IObjectStore, IDisposable
{
    private const string ContentType = "application/vnd.apache.arrow.stream";

    private readonly StorageClient _client;
    private readonly UrlSigner _signer;
    private readonly bool _ownsClient;
    private readonly string _bucket;
    private readonly string _keyPrefix;
    private readonly TimeSpan _signDuration;
    private int _disposed;

    private GcsStorage(Builder builder)
    {
        _bucket = builder.Bucket;
        _keyPrefix = builder.KeyPrefix.EndsWith('/') ? builder.KeyPrefix : builder.KeyPrefix + "/";
        _signDuration = builder.SignDuration;

        if (builder.Client is not null && builder.Signer is not null)
        {
            _client = builder.Client;
            _signer = builder.Signer;
            _ownsClient = false;
        }
        else
        {
            var credential = builder.Credential ?? GoogleCredential.GetApplicationDefault();
            _client = StorageClient.Create(credential);
            _signer = UrlSigner.FromCredential(credential);
            _ownsClient = true;
        }
    }

    /// <summary>Starts building a <see cref="GcsStorage"/> that uploads into <paramref name="bucket"/>.</summary>
    public static Builder CreateBuilder(string bucket) => new(bucket);

    /// <summary>Uploads under <c>&lt;keyPrefix&gt;&lt;uuid&gt;.arrow</c> with content type
    /// <c>application/vnd.apache.arrow.stream</c>, then returns a V4 signed GET URL valid for the
    /// configured <see cref="Builder.WithSignDuration"/> window.</summary>
    public async Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
    {
        var key = _keyPrefix + Guid.NewGuid().ToString("D") + ".arrow";
        var gcsObject = new GcsObject
        {
            Bucket = _bucket,
            Name = key,
            ContentType = ContentType,
            ContentEncoding = contentEncoding,
        };

        using var stream = new MemoryStream(data);
        _ = await _client.UploadObjectAsync(gcsObject, stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return await _signer.SignAsync(_bucket, key, _signDuration, HttpMethod.Get, signingVersion: SigningVersion.V4, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Vends a signed PUT/GET URL pair for the same new object — GCS signed URLs are
    /// method-bound (see <see cref="UploadUrl"/>'s doc comment), so a caller must PUT to
    /// <see cref="UploadUrl.UploadUrlValue"/> and later GET from <see cref="UploadUrl.DownloadUrl"/>.
    /// GCS cannot verify a SHA-256 at storage, so the PUT signs the exact length
    /// (<c>x-goog-content-length-range: N,N</c>) and an <c>application/octet-stream</c>
    /// Content-Type, both returned in <see cref="UploadUrl.Headers"/>; the server's
    /// <c>vgi_rpc.location.sha256</c> check on the request pointer catches a wrong body.</summary>
    public async Task<UploadUrl> GenerateUploadUrlAsync(long size, string sha256Hex, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        if (!UploadDigest.IsLowerHexSha256(sha256Hex))
        {
            throw new ArgumentException("sha256 must be 64 lowercase hex characters", nameof(sha256Hex));
        }

        var key = _keyPrefix + Guid.NewGuid().ToString("D") + ".arrow";
        var expiresAt = DateTimeOffset.UtcNow.Add(_signDuration);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = UploadContentType,
            ["x-goog-content-length-range"] = $"{size},{size}",
        };

        var putTemplate = UrlSigner.RequestTemplate.FromBucket(_bucket).WithObjectName(key)
            .WithHttpMethod(HttpMethod.Put)
            .WithContentHeaders(new Dictionary<string, IEnumerable<string>> { ["Content-Type"] = [UploadContentType] })
            .WithRequestHeaders(new Dictionary<string, IEnumerable<string>> { ["x-goog-content-length-range"] = [headers["x-goog-content-length-range"]] });
        var options = UrlSigner.Options.FromDuration(_signDuration).WithSigningVersion(SigningVersion.V4);
        var uploadUrl = await _signer.SignAsync(putTemplate, options, cancellationToken).ConfigureAwait(false);
        var downloadUrl = await _signer.SignAsync(_bucket, key, _signDuration, HttpMethod.Get, signingVersion: SigningVersion.V4, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new UploadUrl(uploadUrl, downloadUrl, expiresAt, headers) { ObjectId = key };
    }

    /// <summary>The object API's <c>put_object</c> (<c>STATE_REF_V1.md</c> §10.1): uploads under
    /// <c>&lt;keyPrefix&gt;&lt;uuid&gt;.bin</c> (create-only) and returns the name. GCS cannot verify
    /// a SHA-256 at storage; callers verify the digest on read. Objects carry no TTL: set Object
    /// Lifecycle Management of at least one day on the key prefix.</summary>
    public async Task<string> PutObjectAsync(byte[] data, CancellationToken cancellationToken)
    {
        var key = _keyPrefix + Guid.NewGuid().ToString("N") + ".bin";
        using var stream = new MemoryStream(data);
        _ = await _client.UploadObjectAsync(
            new GcsObject { Bucket = _bucket, Name = key, ContentType = UploadContentType },
            stream,
            new UploadObjectOptions { IfGenerationMatch = 0 },
            cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <summary>The object API's <c>get(object_id)</c>: downloads the named object, capped at
    /// <paramref name="maxBytes"/> (default 256 MiB). A 404 is
    /// <see cref="ObjectMissingException"/>.</summary>
    public async Task<byte[]> GetAsync(string objectId, long? maxBytes, CancellationToken cancellationToken)
    {
        var limit = maxBytes ?? FetchConfig.DefaultMaxFetchBytes;
        try
        {
            var metadata = await _client.GetObjectAsync(_bucket, objectId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (metadata.Size is { } size && (long)size > limit)
            {
                throw new InvalidOperationException($"GCS object is {size} bytes, over max_fetch_bytes ({limit})");
            }

            using var buffer = new MemoryStream();
            _ = await _client.DownloadObjectAsync(metadata, buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
            return buffer.Length > limit
                ? throw new InvalidOperationException($"GCS object exceeds max_fetch_bytes ({limit})")
                : buffer.ToArray();
        }
        catch (Google.GoogleApiException exc) when (exc.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new ObjectMissingException("GCS object is gone");
        }
    }

    /// <summary>Checks the bucket exists and the credentials can read it (<c>VGI_STORAGE_GCS_VERIFY</c>).</summary>
    /// <exception cref="InvalidOperationException">Bucket missing or access denied, with an actionable
    /// message.</exception>
    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await _client.GetBucketAsync(_bucket, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Google.GoogleApiException exc)
        {
            throw new InvalidOperationException(exc.HttpStatusCode switch
            {
                System.Net.HttpStatusCode.NotFound => $"GCS: bucket '{_bucket}' does not exist (HTTP 404); check VGI_STORAGE_GCS_BUCKET",
                System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized =>
                    $"GCS: access denied to bucket '{_bucket}' (HTTP {(int)exc.HttpStatusCode}); check the credentials (storage.buckets.get, storage.objects.create/get)",
                _ => $"GCS: bucket check for '{_bucket}' failed: HTTP {(int)exc.HttpStatusCode}",
            }, exc);
        }
    }

    private const string UploadContentType = "application/octet-stream";

    /// <summary>Disposes the underlying <see cref="StorageClient"/>, but only when it was created
    /// internally — a caller-supplied client (via <see cref="Builder.WithClient"/>) is left open.
    /// Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsClient)
        {
            _client.Dispose();
        }
    }

    /// <summary>Fluent builder for <see cref="GcsStorage"/>.</summary>
    public sealed class Builder
    {
        internal string Bucket { get; }
        internal string KeyPrefix { get; private set; } = "vgi-rpc/";
        internal TimeSpan SignDuration { get; private set; } = TimeSpan.FromHours(1);
        internal GoogleCredential? Credential { get; private set; }
        internal StorageClient? Client { get; private set; }
        internal UrlSigner? Signer { get; private set; }

        internal Builder(string bucket) => Bucket = bucket;

        /// <summary>Key prefix for uploaded objects; a trailing slash is added if missing (default <c>"vgi-rpc/"</c>).</summary>
        public Builder WithKeyPrefix(string keyPrefix) { KeyPrefix = keyPrefix; return this; }

        /// <summary>Validity window of the signed GET URLs (default one hour).</summary>
        public Builder WithSignDuration(TimeSpan duration) { SignDuration = duration; return this; }

        /// <summary>Overrides the credential used for both the storage client and URL signing
        /// (default: Application Default Credentials).</summary>
        public Builder WithCredential(GoogleCredential credential) { Credential = credential; return this; }

        /// <summary>Supplies a pre-configured client and signer directly — e.g. for a test
        /// emulator. Not disposed by <see cref="GcsStorage.Dispose"/>.</summary>
        public Builder WithClient(StorageClient client, UrlSigner signer) { Client = client; Signer = signer; return this; }

        /// <summary>Builds the configured <see cref="GcsStorage"/>.</summary>
        public GcsStorage Build() => new(this);
    }
}
