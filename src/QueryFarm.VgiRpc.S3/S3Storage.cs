using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using Amazon.S3.Model;
using Apache.Arrow;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Http;

namespace QueryFarm.VgiRpc.S3;

/// <summary>Whether PUTs carry <c>x-amz-checksum-sha256</c> (<c>VGI_STORAGE_S3_CHECKSUM</c>).</summary>
public enum S3ChecksumMode
{
    /// <summary>Every PUT carries the body's SHA-256, so storage refuses any other body; upload grants
    /// are signed for it (upload v2). The default.</summary>
    Sha256,

    /// <summary>For services that do not enforce <c>x-amz-checksum-sha256</c>: no checksum header and
    /// no upload grants (<see cref="S3Storage.IssuesUploadGrants"/> is false). Results and objects are
    /// still stored and verified by their readers.</summary>
    None,
}

/// <summary>
/// <see cref="IExternalStorage"/>/<see cref="IUploadUrlProvider"/>/<see cref="IObjectStore"/> backed by
/// Amazon S3 or any S3-compatible store (MinIO, Ceph, Cloudflare R2's S3 API, Wasabi, Backblaze B2,
/// LocalStack, RustFS). Structurally mirrors vgi-rpc-java's <c>S3Storage</c> — see docs/roadmap.md M19.
/// Configure it with <see cref="CreateBuilder"/> or from the environment with
/// <see cref="S3StorageEnvironment"/> (direct-storage contract v1).
///
/// <para>Keys: results and client uploads under <c>&lt;keyPrefix&gt;rpc/&lt;uuid&gt;.arrow</c>, objects
/// (<c>StateRef</c>, argument references) under <c>&lt;keyPrefix&gt;obj/&lt;uuid&gt;.bin</c>, function
/// storage under <c>&lt;keyPrefix&gt;fs/</c>. vgi-rpc never deletes: add a lifecycle rule expiring
/// <c>rpc/</c> and <c>obj/</c> after 1–2 days.</para>
/// <para>Credentials: <see cref="Builder.WithCredentials"/> (e.g. a <see cref="SessionAWSCredentials"/>
/// with a session token), else the SDK's default chain (environment, shared profile with
/// <c>role_arn</c>/<c>credential_process</c>/SSO, web identity, ECS/EKS Pod Identity, EC2 IMDSv2),
/// resolved on first use. Temporary credentials are refreshed at least 5 minutes before they expire,
/// and every presigned URL's lifetime is capped at <c>expiry − now − 60 s</c> (refreshing first when
/// that is below what the URL needs: 15 minutes for an upload grant).</para>
/// <para>Transfers have no whole-request deadline (<c>AmazonS3Config.Timeout</c> is infinite); each
/// PUT/GET runs under a stall watchdog (<see cref="StorageTimeouts"/>: 30 s with no bytes moving, or
/// <c>VGI_RPC_STORAGE_STALL_TIMEOUT_SECONDS</c>), retried up to 3 attempts on a stall. The SDK's own
/// retry policy still handles 5xx, throttling and connection errors. The SDK's default CRC32
/// trailing checksum (aws-chunked uploads, which several S3-compatible services reject) is off by
/// default (<see cref="Builder.WithRequestChecksumCalculation"/>): PUTs carry an explicit
/// <c>x-amz-checksum-sha256</c> instead, or no checksum with <see cref="S3ChecksumMode.None"/>.</para>
/// </summary>
public sealed class S3Storage : IExternalStorage, IUploadUrlProvider, IObjectStore, IAsyncDisposable
{
    private const string ContentType = "application/vnd.apache.arrow.stream";
    private const string ChecksumSha256Header = "x-amz-checksum-sha256";
    private const int StallAttempts = 3;
    private static readonly TimeSpan s_refreshAhead = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_expiryMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_grantNeed = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_resultNeed = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_ownNeed = TimeSpan.FromMinutes(2);

    private readonly Builder _builder;
    private readonly string _bucket;
    private readonly string _keyPrefix;
    private readonly TimeSpan _urlTtl;
    private readonly Protocol? _presignProtocol;
    private readonly Protocol? _publicPresignProtocol;
    private readonly StorageTimeouts _timeouts;
    private readonly Lazy<(AWSCredentials Credentials, AmazonS3Client Client, AmazonS3Client Presigner)> _clients;
    private readonly SemaphoreSlim _verifyGate = new(1, 1);
    private Action<string>? _validator;
    private bool _verified;
    private int _disposed;

    private S3Storage(Builder builder)
    {
        _builder = builder;
        _bucket = builder.Bucket;
        _keyPrefix = builder.KeyPrefix.Length == 0 || builder.KeyPrefix.EndsWith('/') ? builder.KeyPrefix : builder.KeyPrefix + "/";
        _urlTtl = builder.PresignDuration;
        _timeouts = builder.Timeouts ?? StorageTimeouts.FromEnvironment();
        Checksum = builder.Checksum;
        _presignProtocol = ProtocolOf(builder.ServiceUrl);
        _publicPresignProtocol = ProtocolOf(builder.PublicServiceUrl ?? builder.ServiceUrl);
        _clients = new Lazy<(AWSCredentials, AmazonS3Client, AmazonS3Client)>(CreateClients, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The checksum mode.</summary>
    public S3ChecksumMode Checksum { get; }

    /// <summary>Whether this storage vends upload grants (<see cref="S3ChecksumMode.Sha256"/>). With
    /// <see cref="S3ChecksumMode.None"/> a server must not register it as an upload-URL provider nor
    /// advertise argument references.</summary>
    public bool IssuesUploadGrants => Checksum == S3ChecksumMode.Sha256;

    /// <summary>The bucket.</summary>
    public string Bucket => _bucket;

    /// <summary>Starts building an <see cref="S3Storage"/> that uploads into <paramref name="bucket"/>.</summary>
    public static Builder CreateBuilder(string bucket) => new(bucket);

    private static Protocol? ProtocolOf(string? serviceUrl) =>
        serviceUrl is null ? null : serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;

    private AmazonS3Config NewConfig(string? serviceUrl)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = _builder.ForcePathStyle,

            // No total deadline: each transfer is bounded by a stall watchdog (WithStallRetriesAsync).
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            RequestChecksumCalculation = _builder.RequestChecksumCalculation,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        // Setting RegionEndpoint together with a custom ServiceURL is a real AWSSDK.S3 v4
        // interop trap: every request comes back 403 InvalidAccessKeyId even with genuinely
        // correct credentials (confirmed directly against RustFS — a raw curl --aws-sigv4 request
        // with the identical credentials succeeds, so the credentials were never the problem).
        // AuthenticationRegion is the SDK's own escape hatch for exactly this "custom S3-compatible
        // endpoint" scenario: it supplies the region string SigV4 needs without engaging whatever
        // RegionEndpoint-driven resolution conflicts with an explicit ServiceURL (and it takes any
        // string, e.g. R2's "auto").
        if (serviceUrl is not null)
        {
            config.ServiceURL = serviceUrl;
            config.AuthenticationRegion = _builder.Region ?? _builder.RegionEndpoint?.SystemName ?? "us-east-1";

            // Presigned URLs default to https regardless of ServiceURL's own scheme unless each
            // GetPreSignedUrlRequest is told otherwise via its own Protocol property (AmazonS3Config
            // .UseHttp does NOT affect presigned-URL generation, only which scheme the client
            // itself uses to reach ServiceURL) — without this, a plain-http endpoint (MinIO/
            // LocalStack/RustFS without TLS in front) still gets an https:// presigned URL, which
            // then fails the TLS handshake against a server that was never speaking TLS at all.
            config.UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        }
        else if (_builder.RegionEndpoint is not null)
        {
            config.RegionEndpoint = _builder.RegionEndpoint;
        }
        else if (_builder.Region is not null)
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_builder.Region);
        }

        return config;
    }

    private (AWSCredentials, AmazonS3Client, AmazonS3Client) CreateClients()
    {
        var config = NewConfig(_builder.ServiceUrl);
        var credentials = _builder.Credentials ?? DefaultAWSCredentialsIdentityResolver.GetCredentials(config);
        if (credentials is RefreshingAWSCredentials refreshing && refreshing.PreemptExpiryTime < s_refreshAhead)
        {
            refreshing.PreemptExpiryTime = s_refreshAhead;
        }

        var client = new AmazonS3Client(credentials, config);
        var presigner = _builder.PublicServiceUrl is null ? client : new AmazonS3Client(credentials, NewConfig(_builder.PublicServiceUrl));
        return (credentials, client, presigner);
    }

    private AmazonS3Client Client => _clients.Value.Client;

    /// <summary>The URL lifetime the current credentials allow for a URL that must live at least
    /// <paramref name="need"/>, refreshing them first when they expire too soon.</summary>
    private async Task<TimeSpan> SigningTtlAsync(TimeSpan need)
    {
        var credentials = _clients.Value.Credentials;
        _ = await credentials.GetCredentialsAsync().ConfigureAwait(false);
        if (credentials.Expiration is not { } expiration)
        {
            return _urlTtl;
        }

        var left = expiration.ToUniversalTime() - DateTime.UtcNow - s_expiryMargin;
        if (left < need && credentials is RefreshingAWSCredentials refreshing)
        {
            refreshing.ClearCredentials();
            _ = await refreshing.GetCredentialsAsync().ConfigureAwait(false);
            left = (refreshing.Expiration ?? DateTime.UtcNow).ToUniversalTime() - DateTime.UtcNow - s_expiryMargin;
        }

        if (left < TimeSpan.FromSeconds(1))
        {
            throw new InvalidOperationException("S3: the AWS credentials expire too soon to sign a URL");
        }

        return left < _urlTtl ? left : _urlTtl;
    }

    /// <summary>HEADs the bucket once (on first use, or at startup via
    /// <see cref="S3StorageEnvironment"/> with <c>VGI_STORAGE_S3_VERIFY=1</c>).</summary>
    /// <exception cref="InvalidOperationException">Bucket missing, access denied, wrong region, or
    /// storage unreachable, with an actionable message.</exception>
    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _verified))
        {
            return;
        }

        await _verifyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_verified)
            {
                return;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeouts.Connect + _timeouts.Stall);
                _ = await Client.HeadBucketAsync(new HeadBucketRequest { BucketName = _bucket }, cts.Token).ConfigureAwait(false);
            }
            catch (AmazonS3Exception exc)
            {
                var region = _builder.Region ?? _builder.RegionEndpoint?.SystemName ?? "us-east-1";
                throw new InvalidOperationException(exc.StatusCode switch
                {
                    System.Net.HttpStatusCode.NotFound => $"S3: bucket '{_bucket}' does not exist (HTTP 404); check {S3StorageEnvironment.Bucket}",
                    System.Net.HttpStatusCode.Forbidden => $"S3: access denied to bucket '{_bucket}' (HTTP 403); check the credentials and that they allow s3:ListBucket, s3:GetObject and s3:PutObject on it",
                    System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.BadRequest =>
                        $"S3: bucket '{_bucket}' is not in region '{region}' (HTTP {(int)exc.StatusCode} {exc.ErrorCode}); set {S3StorageEnvironment.Region}",
                    _ => $"S3: bucket check for '{_bucket}' failed: HTTP {(int)exc.StatusCode} {exc.ErrorCode}",
                }, exc);
            }
            catch (Exception exc) when (exc is HttpRequestException or OperationCanceledException or AmazonClientException or IOException)
            {
                throw new InvalidOperationException(
                    $"S3: cannot reach bucket '{_bucket}' ({exc.GetType().Name}); check {S3StorageEnvironment.Endpoint}", exc);
            }

            _verified = true;
        }
        finally
        {
            _verifyGate.Release();
        }
    }

    /// <summary>Accepts only this bucket's URLs on the public endpoint (for
    /// <see cref="ServerExternalConfig.UrlValidator"/>: inbound upload pointers).</summary>
    /// <exception cref="ArgumentException">Any other URL.</exception>
    public void ValidateUrl(string url)
    {
        if (_validator is null)
        {
            // Derive the pin from a real presigned URL so it matches the SDK's own addressing.
            var sample = new Uri(_clients.Value.Presigner.GetPreSignedURL(NewPresignRequest(_keyPrefix + "rpc/x", HttpVerb.GET, DateTime.UtcNow.AddMinutes(5), _publicPresignProtocol)));
            var path = sample.AbsolutePath;
            var bucketBase = path[..(path.LastIndexOf("/rpc/x", StringComparison.Ordinal) + 1)];
            var prefix = $"{sample.Scheme}://{sample.Authority}{bucketBase}";
            _validator = PinnedHosts.Validator([$"{sample.Host}:{sample.Port}"], sample.Scheme == "http", [prefix]);
        }

        _validator(url);
    }

    /// <summary>Uploads under <c>&lt;keyPrefix&gt;rpc/&lt;uuid&gt;.arrow</c> with content type
    /// <c>application/vnd.apache.arrow.stream</c> (and its SHA-256 with
    /// <see cref="S3ChecksumMode.Sha256"/>), then returns a V4 pre-signed GET URL on the public
    /// endpoint, valid for the configured window capped by the credentials' expiry.</summary>
    public async Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
    {
        var key = _keyPrefix + "rpc/" + Guid.NewGuid().ToString("D") + ".arrow";
        await PutKeyAsync(key, data, ContentType, contentEncoding, cancellationToken).ConfigureAwait(false);
        var ttl = await SigningTtlAsync(s_resultNeed).ConfigureAwait(false);
        var presignRequest = NewPresignRequest(key, HttpVerb.GET, DateTime.UtcNow.Add(ttl), _publicPresignProtocol);
        return await _clients.Value.Presigner.GetPreSignedURLAsync(presignRequest).ConfigureAwait(false);
    }

    /// <summary>Vends a pre-signed PUT/GET URL pair for the same new object on the public endpoint —
    /// S3 pre-signed URLs are method-bound (see <see cref="UploadUrl"/>'s doc comment), so a caller
    /// must PUT to <see cref="UploadUrl.UploadUrlValue"/> and later GET from
    /// <see cref="UploadUrl.DownloadUrl"/>. The PUT signs <c>Content-Length</c> and
    /// <c>x-amz-checksum-sha256</c>, so S3/R2 itself rejects a body of another length or digest; the
    /// checksum header is returned in <see cref="UploadUrl.Headers"/> for the client to send
    /// verbatim. The <see cref="UploadUrl.ObjectId"/> is the key, fetchable with
    /// <see cref="GetAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">With <see cref="S3ChecksumMode.None"/>.</exception>
    public async Task<UploadUrl> GenerateUploadUrlAsync(long size, string sha256Hex, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        if (!IssuesUploadGrants)
        {
            throw new InvalidOperationException($"S3: upload grants are disabled ({S3StorageEnvironment.Checksum}=none)");
        }

        var checksum = UploadDigest.HexToBase64(sha256Hex);
        await VerifyAsync(cancellationToken).ConfigureAwait(false);
        var key = _keyPrefix + "rpc/" + Guid.NewGuid().ToString("D") + ".arrow";
        var ttl = await SigningTtlAsync(s_grantNeed).ConfigureAwait(false);
        var expires = DateTime.UtcNow.Add(ttl);

        // Deliberately NOT setting ContentType here (unlike UploadAsync's own PUT, which this
        // class fully controls): GetPreSignedUrlRequest.ContentType becomes part of what's
        // signed, so it would force whoever PUTs to uploadUrl to send that exact Content-Type
        // header too. Content-Length and the checksum ARE signed (X-Amz-SignedHeaders
        // content-length;host;x-amz-checksum-sha256): that is the point of upload-URL v2.
        var putRequest = NewPresignRequest(key, HttpVerb.PUT, expires, _publicPresignProtocol);
        putRequest.Headers[ChecksumSha256Header] = checksum;
        putRequest.Headers.ContentLength = size;
        var getRequest = NewPresignRequest(key, HttpVerb.GET, expires, _publicPresignProtocol);

        var presigner = _clients.Value.Presigner;
        var uploadUrl = await presigner.GetPreSignedURLAsync(putRequest).ConfigureAwait(false);
        var downloadUrl = await presigner.GetPreSignedURLAsync(getRequest).ConfigureAwait(false);
        return new UploadUrl(uploadUrl, downloadUrl, new DateTimeOffset(expires, TimeSpan.Zero),
            new Dictionary<string, string> { [ChecksumSha256Header] = checksum })
        {
            ObjectId = key,
        };
    }

    /// <summary>The object API's <c>put_object</c> (<c>STATE_REF_V1.md</c> §10.1): a SigV4
    /// <c>PutObject</c> under <c>&lt;keyPrefix&gt;obj/&lt;uuid&gt;.bin</c> (carrying
    /// <c>ChecksumSHA256</c> with <see cref="S3ChecksumMode.Sha256"/>, so S3/R2 verify the body).
    /// Returns the key. Objects carry no TTL: set a lifecycle rule of at least one day on the key
    /// prefix.</summary>
    public async Task<string> PutObjectAsync(byte[] data, CancellationToken cancellationToken)
    {
        var key = _keyPrefix + "obj/" + Guid.NewGuid().ToString("N") + ".bin";
        await PutKeyAsync(key, data, "application/octet-stream", null, cancellationToken).ConfigureAwait(false);
        return key;
    }

    /// <summary>The full key of a function-storage blob named <paramref name="name"/>
    /// (<c>&lt;keyPrefix&gt;fs/&lt;name&gt;</c>).</summary>
    public string FunctionStorageKey(string name) => _keyPrefix + "fs/" + name;

    /// <summary>Stores <paramref name="data"/> under exactly <paramref name="key"/> (the worker's own
    /// PUT, under the stall watchdog), with its SHA-256 in <see cref="S3ChecksumMode.Sha256"/>.</summary>
    public async Task PutKeyAsync(string key, byte[] data, string contentType, string? contentEncoding, CancellationToken cancellationToken)
    {
        await VerifyAsync(cancellationToken).ConfigureAwait(false);
        var checksum = Checksum == S3ChecksumMode.Sha256
            ? Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(data))
            : null;
        await WithStallRetriesAsync("PUT", async watchdog =>
        {
            using var stream = new ProgressStream(new MemoryStream(data), watchdog);
            var putRequest = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = stream,
                ContentType = contentType,
                AutoCloseStream = false,
            };
            if (checksum is not null)
            {
                putRequest.ChecksumSHA256 = checksum;
            }

            if (contentEncoding is not null)
            {
                putRequest.Headers.ContentEncoding = contentEncoding;
            }

            _ = await Client.PutObjectAsync(putRequest, watchdog.Token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The object API's <c>get(object_id)</c>: a <c>GetObject</c> of the key, capped at
    /// <paramref name="maxBytes"/> (default 256 MiB). <c>NoSuchKey</c>/404 is
    /// <see cref="ObjectMissingException"/>. Re-signed on every call, so stored objects survive
    /// credential rotation.</summary>
    public async Task<byte[]> GetAsync(string objectId, long? maxBytes, CancellationToken cancellationToken)
    {
        var limit = maxBytes ?? FetchConfig.DefaultMaxFetchBytes;
        await VerifyAsync(cancellationToken).ConfigureAwait(false);
        return await WithStallRetriesAsync("GET", async watchdog =>
        {
            GetObjectResponse response;
            try
            {
                response = await Client.GetObjectAsync(_bucket, objectId, watchdog.Token).ConfigureAwait(false);
            }
            catch (AmazonS3Exception exc) when (exc.StatusCode == System.Net.HttpStatusCode.NotFound || exc.ErrorCode == "NoSuchKey")
            {
                throw new ObjectMissingException("S3 object is gone");
            }

            using (response)
            {
                watchdog.Progress();
                if (response.ContentLength > limit)
                {
                    throw new InvalidOperationException($"S3 object is {response.ContentLength} bytes, over max_fetch_bytes ({limit})");
                }

                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await response.ResponseStream.ReadAsync(chunk, watchdog.Token).ConfigureAwait(false)) > 0)
                {
                    watchdog.Progress();
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > limit)
                    {
                        throw new InvalidOperationException($"S3 object exceeds max_fetch_bytes ({limit})");
                    }
                }

                return buffer.ToArray();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one SDK transfer per attempt under a fresh <see cref="StallWatchdog"/>; an
    /// attempt the watchdog cancelled (no bytes moving for the stall timeout) is retried, up to
    /// <see cref="StallAttempts"/> in all, with jittered exponential backoff. Anything else
    /// propagates (the SDK has already applied its own retry policy).</summary>
    private async Task<T> WithStallRetriesAsync<T>(string op, Func<StallWatchdog, Task<T>> attempt, CancellationToken cancellationToken)
    {
        for (var n = 0; ; n++)
        {
            if (n > 0)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(250 * (1 << (n - 1)), 5000) * (0.5 + (Random.Shared.NextDouble() / 2)));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            using var watchdog = new StallWatchdog(_timeouts.Stall, _timeouts.Connect, cancellationToken);
            try
            {
                return await attempt(watchdog).ConfigureAwait(false);
            }
            catch (Exception exc) when (watchdog.Stalled && !cancellationToken.IsCancellationRequested
                && exc is OperationCanceledException or IOException or HttpRequestException or AmazonServiceException)
            {
                if (n + 1 >= StallAttempts)
                {
                    throw new TimeoutException($"S3 {op} stalled (no bytes moved for {_timeouts.Stall.TotalSeconds:0.###} s) on each of {StallAttempts} attempts");
                }
            }
        }
    }

    /// <summary>Builds a <see cref="GetPreSignedUrlRequest"/> for <paramref name="key"/>, applying
    /// <paramref name="protocol"/> only when a custom endpoint set it — <see cref="Protocol"/>
    /// isn't nullable on the request itself, so leaving it unset for real AWS S3 keeps the
    /// request's own default rather than forcing a value.</summary>
    private GetPreSignedUrlRequest NewPresignRequest(string key, HttpVerb verb, DateTime expires, Protocol? protocol)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = verb,
            Expires = expires,
        };
        if (protocol is { } p)
        {
            request.Protocol = p;
        }

        return request;
    }

    /// <summary>Disposes the underlying <see cref="AmazonS3Client"/>s. Idempotent.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_clients.IsValueCreated)
            {
                var (_, client, presigner) = _clients.Value;
                client.Dispose();
                if (!ReferenceEquals(client, presigner))
                {
                    presigner.Dispose();
                }
            }

            _verifyGate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Fluent builder for <see cref="S3Storage"/>.</summary>
    public sealed class Builder
    {
        internal string Bucket { get; }
        internal string KeyPrefix { get; private set; } = "vgi-rpc/";
        internal Amazon.RegionEndpoint? RegionEndpoint { get; private set; }
        internal string? Region { get; private set; }
        internal string? ServiceUrl { get; private set; }
        internal string? PublicServiceUrl { get; private set; }
        internal bool ForcePathStyle { get; private set; }
        internal TimeSpan PresignDuration { get; private set; } = TimeSpan.FromHours(1);
        internal AWSCredentials? Credentials { get; private set; }
        internal StorageTimeouts? Timeouts { get; private set; }
        internal S3ChecksumMode Checksum { get; private set; } = S3ChecksumMode.Sha256;
        internal RequestChecksumCalculation RequestChecksumCalculation { get; private set; } = RequestChecksumCalculation.WHEN_REQUIRED;

        internal Builder(string bucket) => Bucket = bucket;

        /// <summary>Key prefix for uploaded objects; a trailing slash is added if missing (default
        /// <c>"vgi-rpc/"</c>; empty puts the <c>rpc/</c>, <c>obj/</c> and <c>fs/</c> areas at the
        /// bucket root).</summary>
        public Builder WithKeyPrefix(string keyPrefix) { KeyPrefix = keyPrefix; return this; }

        /// <summary>Sets the AWS region (default: the SDK's own default resolution).</summary>
        public Builder WithRegion(Amazon.RegionEndpoint region) { RegionEndpoint = region; return this; }

        /// <summary>Sets the signing region by name — any string, e.g. <c>auto</c> for Cloudflare R2.
        /// Takes precedence over <see cref="WithRegion(Amazon.RegionEndpoint)"/>.</summary>
        public Builder WithRegion(string region) { Region = region; return this; }

        /// <summary>Overrides the S3 endpoint — e.g. for MinIO, LocalStack, or RustFS.</summary>
        public Builder WithServiceUrl(string serviceUrl) { ServiceUrl = serviceUrl; return this; }

        /// <summary>The endpoint used only for the presigned URLs handed to clients, when the worker
        /// reaches storage through a different address (e.g. Docker-internal
        /// <c>http://minio:9000</c> vs public <c>https://s3.example.com</c>).</summary>
        public Builder WithPublicServiceUrl(string publicServiceUrl) { PublicServiceUrl = publicServiceUrl; return this; }

        /// <summary>Enables path-style addressing — required by most S3-compatible stores.</summary>
        public Builder WithForcePathStyle(bool forcePathStyle) { ForcePathStyle = forcePathStyle; return this; }

        /// <summary>Validity window of pre-signed URLs (default one hour; capped by the credentials'
        /// expiry).</summary>
        public Builder WithPresignDuration(TimeSpan duration) { PresignDuration = duration; return this; }

        /// <summary>Overrides the credentials (default: the SDK's own default credential chain).</summary>
        public Builder WithCredentials(AWSCredentials credentials) { Credentials = credentials; return this; }

        /// <summary>Connect and stall timeouts of each transfer (default
        /// <see cref="StorageTimeouts.FromEnvironment"/>: 30 s / 30 s).</summary>
        public Builder WithTransferTimeouts(StorageTimeouts timeouts) { Timeouts = timeouts; return this; }

        /// <summary>The checksum mode (default <see cref="S3ChecksumMode.Sha256"/>).</summary>
        public Builder WithChecksum(S3ChecksumMode checksum) { Checksum = checksum; return this; }

        /// <summary>The SDK's automatic request checksum (default <c>WHEN_REQUIRED</c>: no CRC32
        /// trailing checksum, which several S3-compatible services reject; PUTs carry an explicit
        /// SHA-256 instead). <c>WHEN_SUPPORTED</c> restores the SDK default.</summary>
        public Builder WithRequestChecksumCalculation(RequestChecksumCalculation calculation) { RequestChecksumCalculation = calculation; return this; }

        /// <summary>Builds the configured <see cref="S3Storage"/>.</summary>
        public S3Storage Build() => new(this);
    }
}

/// <summary>A read-through stream that reports each read as transfer progress (the SDK reads the
/// request body from it as it sends).</summary>
internal sealed class ProgressStream(Stream inner, StallWatchdog watchdog) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        watchdog.Progress();
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        watchdog.Progress();
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
