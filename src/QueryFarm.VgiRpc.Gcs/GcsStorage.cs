using Apache.Arrow;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
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
public sealed class GcsStorage : IExternalStorage, IUploadUrlProvider, IDisposable
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
    /// <see cref="UploadUrl.UploadUrlValue"/> and later GET from <see cref="UploadUrl.DownloadUrl"/>.</summary>
    public async Task<UploadUrl> GenerateUploadUrlAsync(Schema schema, CancellationToken cancellationToken)
    {
        var key = _keyPrefix + Guid.NewGuid().ToString("D") + ".arrow";
        var expiresAt = DateTimeOffset.UtcNow.Add(_signDuration);

        var uploadUrl = await _signer.SignAsync(_bucket, key, _signDuration, HttpMethod.Put, signingVersion: SigningVersion.V4, cancellationToken: cancellationToken).ConfigureAwait(false);
        var downloadUrl = await _signer.SignAsync(_bucket, key, _signDuration, HttpMethod.Get, signingVersion: SigningVersion.V4, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new UploadUrl(uploadUrl, downloadUrl, expiresAt);
    }

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
