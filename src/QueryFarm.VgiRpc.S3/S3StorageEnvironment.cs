using Amazon.Runtime;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Http;

namespace QueryFarm.VgiRpc.S3;

/// <summary>
/// Direct S3 storage from the environment (direct-storage contract v1, §2–§4): the worker signs its
/// own URLs against the operator's bucket, with no blob vendor.
/// <list type="table">
/// <item><term><c>VGI_STORAGE_S3_BUCKET</c></term><description>bucket (required)</description></item>
/// <item><term><c>VGI_STORAGE_S3_PREFIX</c></term><description>key prefix, e.g. <c>vgi/</c> (default empty)</description></item>
/// <item><term><c>VGI_STORAGE_S3_ENDPOINT</c></term><description>custom endpoint, <c>https://</c> or <c>http://</c> (default AWS)</description></item>
/// <item><term><c>VGI_STORAGE_S3_REGION</c></term><description>signing region, <c>auto</c> for R2 (default <c>AWS_REGION</c>, <c>AWS_DEFAULT_REGION</c>, <c>us-east-1</c>)</description></item>
/// <item><term><c>VGI_STORAGE_S3_FORCE_PATH_STYLE</c></term><description>bucket in the path (default true with an endpoint, else false)</description></item>
/// <item><term><c>VGI_STORAGE_S3_PUBLIC_ENDPOINT</c></term><description>endpoint for the URLs handed to clients (default the endpoint)</description></item>
/// <item><term><c>VGI_STORAGE_S3_CHECKSUM</c></term><description><c>sha256</c> (default) or <c>none</c></description></item>
/// <item><term><c>VGI_STORAGE_URL_TTL_SECONDS</c></term><description>presigned URL lifetime (default 3600, capped by credential expiry)</description></item>
/// <item><term><c>VGI_STORAGE_S3_VERIFY</c></term><description><c>1</c>: HEAD the bucket at startup (otherwise on first use)</description></item>
/// <item><term><c>VGI_STORAGE_S3_ACCESS_KEY_ID</c>, <c>_SECRET_ACCESS_KEY</c>, <c>_SESSION_TOKEN</c></term><description>explicit credentials (default the AWS chain)</description></item>
/// </list>
/// </summary>
public static class S3StorageEnvironment
{
    /// <summary>Bucket name.</summary>
    public const string Bucket = "VGI_STORAGE_S3_BUCKET";

    /// <summary>Key prefix.</summary>
    public const string Prefix = "VGI_STORAGE_S3_PREFIX";

    /// <summary>Custom endpoint URL.</summary>
    public const string Endpoint = "VGI_STORAGE_S3_ENDPOINT";

    /// <summary>Signing region.</summary>
    public const string Region = "VGI_STORAGE_S3_REGION";

    /// <summary>Path-style addressing.</summary>
    public const string ForcePathStyle = "VGI_STORAGE_S3_FORCE_PATH_STYLE";

    /// <summary>Endpoint for URLs handed to clients.</summary>
    public const string PublicEndpoint = "VGI_STORAGE_S3_PUBLIC_ENDPOINT";

    /// <summary><c>sha256</c> or <c>none</c>.</summary>
    public const string Checksum = "VGI_STORAGE_S3_CHECKSUM";

    /// <summary><c>1</c> checks the bucket at startup.</summary>
    public const string Verify = "VGI_STORAGE_S3_VERIFY";

    /// <summary>Explicit access key id.</summary>
    public const string AccessKeyId = "VGI_STORAGE_S3_ACCESS_KEY_ID";

    /// <summary>Explicit secret access key.</summary>
    public const string SecretAccessKey = "VGI_STORAGE_S3_SECRET_ACCESS_KEY";

    /// <summary>Explicit session token.</summary>
    public const string SessionToken = "VGI_STORAGE_S3_SESSION_TOKEN";

    private static string? Read(Func<string, string?> env, string name) =>
        env(name)?.Trim() is { Length: > 0 } v ? v : null;

    private static bool? Bool(Func<string, string?> env, string name) =>
        Read(env, name)?.ToLowerInvariant() switch
        {
            null => null,
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            var other => throw new ArgumentException($"{name} must be true or false, got '{other}'"),
        };

    private static string? EndpointFrom(Func<string, string?> env, string name)
    {
        if (Read(env, name) is not { } raw)
        {
            return null;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? raw.TrimEnd('/')
            : throw new ArgumentException($"{name} must be an absolute http(s) URL without credentials or query: {ExternalFetch.RedactUrl(raw)}");
    }

    /// <summary>An <see cref="S3Storage.Builder"/> configured from the environment.</summary>
    /// <exception cref="ArgumentException">The bucket is unset or a variable is malformed.</exception>
    public static S3Storage.Builder BuilderFrom(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var bucket = Read(env, Bucket) ?? throw new ArgumentException($"{Bucket} is required for the s3 backend");
        var builder = S3Storage.CreateBuilder(bucket).WithKeyPrefix(Read(env, Prefix) ?? "");
        var endpoint = EndpointFrom(env, Endpoint);
        if (endpoint is not null)
        {
            builder.WithServiceUrl(endpoint);
        }

        if (EndpointFrom(env, PublicEndpoint) is { } publicEndpoint)
        {
            builder.WithPublicServiceUrl(publicEndpoint);
        }

        builder.WithRegion(Read(env, Region) ?? Read(env, "AWS_REGION") ?? Read(env, "AWS_DEFAULT_REGION") ?? "us-east-1");
        builder.WithForcePathStyle(Bool(env, ForcePathStyle) ?? endpoint is not null);
        builder.WithChecksum(Read(env, Checksum)?.ToLowerInvariant() switch
        {
            null or "sha256" => S3ChecksumMode.Sha256,
            "none" => S3ChecksumMode.None,
            var other => throw new ArgumentException($"{Checksum} must be sha256 or none, got '{other}'"),
        });
        builder.WithPresignDuration(StorageBackendEnvironment.UrlTtlFrom(env));
        builder.WithTransferTimeouts(StorageTimeouts.FromEnvironment(env));

        var key = Read(env, AccessKeyId);
        var secret = Read(env, SecretAccessKey);
        var token = Read(env, SessionToken);
        switch (key, secret)
        {
            case (not null, not null):
                builder.WithCredentials(token is null ? new BasicAWSCredentials(key, secret) : new SessionAWSCredentials(key, secret, token));
                break;
            case (null, null) when token is not null:
                throw new ArgumentException($"{SessionToken} is set without {AccessKeyId} and {SecretAccessKey}");
            case (null, null):
                break;
            default:
                throw new ArgumentException($"set both {AccessKeyId} and {SecretAccessKey}, or neither");
        }

        return builder;
    }

    /// <summary>An <see cref="S3Storage"/> from the environment, its bucket checked now when
    /// <c>VGI_STORAGE_S3_VERIFY=1</c>.</summary>
    /// <exception cref="ArgumentException">The bucket is unset or a variable is malformed.</exception>
    /// <exception cref="InvalidOperationException">The startup bucket check failed.</exception>
    public static S3Storage FromEnvironment(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var storage = BuilderFrom(env).Build();
        if (Bool(env, Verify) == true)
        {
            storage.VerifyAsync().GetAwaiter().GetResult();
        }

        return storage;
    }

    /// <summary>
    /// The server options for the s3 backend: results over <c>VGI_RPC_EXTERNALIZE_THRESHOLD_BYTES</c>
    /// externalized to the bucket (compressed per <c>VGI_RPC_EXTERNAL_COMPRESSION</c>), inbound
    /// pointers fetched only from the bucket's public endpoint, objects stored by key, and — unless
    /// <c>VGI_STORAGE_S3_CHECKSUM=none</c> — client upload grants with <c>VGI_RPC_MAX_UPLOAD_BYTES</c>
    /// advertised. With <c>none</c> no upload-URL provider is set (no <c>VGI-Upload-URL-Support</c>,
    /// no argument references), and a warning is written to stderr.
    /// </summary>
    /// <exception cref="ArgumentException">A malformed or contradictory value.</exception>
    public static ExternalizationOptions ExternalizationFromEnvironment(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var storage = FromEnvironment(env);
        var fetch = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxFetchBytes, env) is not null
                    || BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxUploadBytes, env) is not null
            ? new FetchConfig().WithMaxFetchBytes(BlobVendorEnvironment.MaxFetchBytesFrom(env))
            : new FetchConfig();
        if (!storage.IssuesUploadGrants)
        {
            Console.Error.WriteLine(
                $"vgi-rpc: {Checksum}=none: no upload grants or argument references (large inputs stay inline); results and objects are still stored in s3");
        }

        return new ExternalizationOptions
        {
            External = new ServerExternalConfig
            {
                Storage = storage,
                ExternalizeThresholdBytes = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.ExternalizeThreshold, env) ?? 1_048_576,
                Compression = BlobVendorEnvironment.CompressionFrom(env),
                FetchConfig = fetch,
                UrlValidator = storage.ValidateUrl,
            },
            UploadUrlProvider = storage.IssuesUploadGrants ? storage : null,
            ObjectStore = storage,
            MaxRequestBytes = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxRequestBytes, env),
            MaxUploadBytes = storage.IssuesUploadGrants ? BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxUploadBytes, env) : null,
        };
    }
}
