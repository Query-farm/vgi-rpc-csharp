using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Apache.Arrow;
using QueryFarm.VgiRpc.Http;

namespace QueryFarm.VgiRpc.S3;

/// <summary>
/// <see cref="IExternalStorage"/>/<see cref="IUploadUrlProvider"/> implementation backed by
/// Amazon S3 (or any S3-compatible object store — endpoint/path-style are configurable for
/// MinIO/LocalStack/RustFS). Structurally mirrors vgi-rpc-java's <c>S3Storage</c> (fluent
/// builder, disposable, endpoint-override + path-style knobs) — see docs/roadmap.md M19.
///
/// <para>Uploads under <c>&lt;keyPrefix&gt;&lt;uuid&gt;.arrow</c> and returns a V4 pre-signed GET
/// URL. <see cref="AmazonS3Client"/> is documented thread-safe for concurrent calls, satisfying
/// both interfaces' thread-safety requirement.</para>
/// </summary>
public sealed class S3Storage : IExternalStorage, IUploadUrlProvider, IAsyncDisposable
{
    private const string ContentType = "application/vnd.apache.arrow.stream";

    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _keyPrefix;
    private readonly TimeSpan _presignDuration;
    private readonly Protocol? _presignProtocol;
    private int _disposed;

    private S3Storage(Builder builder)
    {
        _bucket = builder.Bucket;
        _keyPrefix = builder.KeyPrefix.EndsWith('/') ? builder.KeyPrefix : builder.KeyPrefix + "/";
        _presignDuration = builder.PresignDuration;

        var config = new AmazonS3Config
        {
            ForcePathStyle = builder.ForcePathStyle,
        };

        // Setting RegionEndpoint together with a custom ServiceURL is a real AWSSDK.S3 v4
        // interop trap: every request comes back 403 InvalidAccessKeyId even with genuinely
        // correct credentials (confirmed directly against RustFS — a raw curl --aws-sigv4 request
        // with the identical credentials succeeds, so the credentials were never the problem).
        // AuthenticationRegion is the SDK's own escape hatch for exactly this "custom S3-compatible
        // endpoint" scenario: it supplies the region string SigV4 needs without engaging whatever
        // RegionEndpoint-driven resolution conflicts with an explicit ServiceURL. Only reachable
        // when RegionEndpoint alone (no ServiceUrl) still works normally for real AWS S3.
        if (builder.ServiceUrl is not null)
        {
            config.ServiceURL = builder.ServiceUrl;
            config.AuthenticationRegion = builder.RegionEndpoint?.SystemName ?? "us-east-1";

            // Presigned URLs default to https regardless of ServiceURL's own scheme unless each
            // GetPreSignedUrlRequest is told otherwise via its own Protocol property (AmazonS3Config
            // .UseHttp does NOT affect presigned-URL generation, only which scheme the client
            // itself uses to reach ServiceURL) — without this, a plain-http endpoint (MinIO/
            // LocalStack/RustFS without TLS in front) still gets an https:// presigned URL, which
            // then fails the TLS handshake against a server that was never speaking TLS at all.
            config.UseHttp = builder.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            _presignProtocol = config.UseHttp ? Protocol.HTTP : Protocol.HTTPS;
        }
        else if (builder.RegionEndpoint is not null)
        {
            config.RegionEndpoint = builder.RegionEndpoint;
        }

        _client = builder.Credentials is not null
            ? new AmazonS3Client(builder.Credentials, config)
            : new AmazonS3Client(config);
    }

    /// <summary>Starts building an <see cref="S3Storage"/> that uploads into <paramref name="bucket"/>.</summary>
    public static Builder CreateBuilder(string bucket) => new(bucket);

    /// <summary>Uploads under <c>&lt;keyPrefix&gt;&lt;uuid&gt;.arrow</c> with content type
    /// <c>application/vnd.apache.arrow.stream</c>, then returns a V4 pre-signed GET URL valid for
    /// the configured <see cref="Builder.WithPresignDuration"/> window.</summary>
    public async Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
    {
        var key = _keyPrefix + Guid.NewGuid().ToString("D") + ".arrow";
        using var stream = new MemoryStream(data);
        var putRequest = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            ContentType = ContentType,
            AutoCloseStream = false,
        };
        if (contentEncoding is not null)
        {
            putRequest.Headers.ContentEncoding = contentEncoding;
        }

        _ = await _client.PutObjectAsync(putRequest, cancellationToken).ConfigureAwait(false);

        var presignRequest = NewPresignRequest(key, HttpVerb.GET, DateTime.UtcNow.Add(_presignDuration));
        return await _client.GetPreSignedURLAsync(presignRequest).ConfigureAwait(false);
    }

    /// <summary>Vends a pre-signed PUT/GET URL pair for the same new object — S3 pre-signed URLs
    /// are method-bound (see <see cref="UploadUrl"/>'s doc comment), so a caller must PUT to
    /// <see cref="UploadUrl.UploadUrlValue"/> and later GET from <see cref="UploadUrl.DownloadUrl"/>.</summary>
    public async Task<UploadUrl> GenerateUploadUrlAsync(Schema schema, CancellationToken cancellationToken)
    {
        var key = _keyPrefix + Guid.NewGuid().ToString("D") + ".arrow";
        var expires = DateTime.UtcNow.Add(_presignDuration);

        // Deliberately NOT setting ContentType here (unlike UploadAsync's own PUT, which this
        // class fully controls): GetPreSignedUrlRequest.ContentType becomes part of what's
        // signed, so it would force whoever PUTs to uploadUrl to send that exact Content-Type
        // header too, or SigV4 rejects the request as a signature mismatch — a coupling this
        // hand-off to an external caller shouldn't impose.
        var putRequest = NewPresignRequest(key, HttpVerb.PUT, expires);
        var getRequest = NewPresignRequest(key, HttpVerb.GET, expires);

        var uploadUrl = await _client.GetPreSignedURLAsync(putRequest).ConfigureAwait(false);
        var downloadUrl = await _client.GetPreSignedURLAsync(getRequest).ConfigureAwait(false);
        return new UploadUrl(uploadUrl, downloadUrl, new DateTimeOffset(expires, TimeSpan.Zero));
    }

    /// <summary>Builds a <see cref="GetPreSignedUrlRequest"/> for <paramref name="key"/>, applying
    /// <see cref="_presignProtocol"/> only when a custom endpoint set it — <see cref="Protocol"/>
    /// isn't nullable on the request itself, so leaving it unset for real AWS S3 keeps the
    /// request's own default rather than forcing a value.</summary>
    private GetPreSignedUrlRequest NewPresignRequest(string key, HttpVerb verb, DateTime expires)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = verb,
            Expires = expires,
        };
        if (_presignProtocol is { } protocol)
        {
            request.Protocol = protocol;
        }

        return request;
    }

    /// <summary>Disposes the underlying <see cref="AmazonS3Client"/>. Idempotent.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Fluent builder for <see cref="S3Storage"/>.</summary>
    public sealed class Builder
    {
        internal string Bucket { get; }
        internal string KeyPrefix { get; private set; } = "vgi-rpc/";
        internal Amazon.RegionEndpoint? RegionEndpoint { get; private set; }
        internal string? ServiceUrl { get; private set; }
        internal bool ForcePathStyle { get; private set; }
        internal TimeSpan PresignDuration { get; private set; } = TimeSpan.FromHours(1);
        internal AWSCredentials? Credentials { get; private set; }

        internal Builder(string bucket) => Bucket = bucket;

        /// <summary>Key prefix for uploaded objects; a trailing slash is added if missing (default <c>"vgi-rpc/"</c>).</summary>
        public Builder WithKeyPrefix(string keyPrefix) { KeyPrefix = keyPrefix; return this; }

        /// <summary>Sets the AWS region (default: the SDK's own default resolution).</summary>
        public Builder WithRegion(Amazon.RegionEndpoint region) { RegionEndpoint = region; return this; }

        /// <summary>Overrides the S3 endpoint — e.g. for MinIO, LocalStack, or RustFS.</summary>
        public Builder WithServiceUrl(string serviceUrl) { ServiceUrl = serviceUrl; return this; }

        /// <summary>Enables path-style addressing — required by most S3-compatible stores.</summary>
        public Builder WithForcePathStyle(bool forcePathStyle) { ForcePathStyle = forcePathStyle; return this; }

        /// <summary>Validity window of pre-signed URLs (default one hour).</summary>
        public Builder WithPresignDuration(TimeSpan duration) { PresignDuration = duration; return this; }

        /// <summary>Overrides the credentials (default: the SDK's own default credential chain).</summary>
        public Builder WithCredentials(AWSCredentials credentials) { Credentials = credentials; return this; }

        /// <summary>Builds the configured <see cref="S3Storage"/>.</summary>
        public S3Storage Build() => new(this);
    }
}
