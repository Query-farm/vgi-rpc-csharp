using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Http;

namespace QueryFarm.VgiRpc.Gcs;

/// <summary>
/// Direct GCS storage from the environment (direct-storage contract v1, §5):
/// <c>VGI_STORAGE_GCS_BUCKET</c> (required), <c>VGI_STORAGE_GCS_PREFIX</c>,
/// <c>VGI_STORAGE_GCS_ENDPOINT</c> (emulator or custom endpoint),
/// <c>VGI_STORAGE_GCS_CREDENTIALS_FILE</c> (else Application Default Credentials:
/// <c>GOOGLE_APPLICATION_CREDENTIALS</c>, workload identity, the metadata server) and
/// <c>VGI_STORAGE_URL_TTL_SECONDS</c>. URLs are V4-signed (at most 7 days).
/// </summary>
public static class GcsStorageEnvironment
{
    /// <summary>Bucket name.</summary>
    public const string Bucket = "VGI_STORAGE_GCS_BUCKET";

    /// <summary>Key prefix.</summary>
    public const string Prefix = "VGI_STORAGE_GCS_PREFIX";

    /// <summary>Custom endpoint (emulator).</summary>
    public const string Endpoint = "VGI_STORAGE_GCS_ENDPOINT";

    /// <summary>Service-account or external-account credentials file.</summary>
    public const string CredentialsFile = "VGI_STORAGE_GCS_CREDENTIALS_FILE";

    private static string? Read(Func<string, string?> env, string name) =>
        env(name)?.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>A <see cref="GcsStorage"/> configured from the environment.</summary>
    /// <exception cref="ArgumentException">The bucket is unset or a variable is malformed.</exception>
    public static GcsStorage FromEnvironment(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var bucket = Read(env, Bucket) ?? throw new ArgumentException($"{Bucket} is required for the gcs backend");
        var builder = GcsStorage.CreateBuilder(bucket)
            .WithKeyPrefix(Read(env, Prefix) ?? "")
            .WithSignDuration(StorageBackendEnvironment.UrlTtlFrom(env));
        var credential = Read(env, CredentialsFile) is { } file
            ? CredentialFactory.FromFile<ServiceAccountCredential>(file).ToGoogleCredential()
            : null;
        if (Read(env, Endpoint) is { } endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException($"{Endpoint} must be an absolute http(s) URL: {ExternalFetch.RedactUrl(endpoint)}");
            }

            credential ??= GoogleCredential.GetApplicationDefault();
            var client = new StorageClientBuilder { BaseUri = endpoint.TrimEnd('/') + "/storage/v1/", Credential = credential }.Build();
            builder.WithClient(client, UrlSigner.FromCredential(credential));
        }
        else if (credential is not null)
        {
            builder.WithCredential(credential);
        }

        return builder.Build();
    }

    /// <summary>The server options for the gcs backend: results externalized to the bucket, inbound
    /// pointers fetched only from <c>storage.googleapis.com</c> (or the custom endpoint) under the
    /// bucket, objects stored by name, and client upload grants signed for the exact length (GCS
    /// cannot verify a SHA-256 at storage; the server checks the pointer's digest).</summary>
    /// <exception cref="ArgumentException">A malformed or contradictory value.</exception>
    public static ExternalizationOptions ExternalizationFromEnvironment(Func<string, string?>? environment = null)
    {
        var env = environment ?? Environment.GetEnvironmentVariable;
        var storage = FromEnvironment(env);
        var bucket = Read(env, Bucket)!;
        var endpoint = Read(env, Endpoint) is { } e ? new Uri(e) : new Uri("https://storage.googleapis.com");
        var validator = PinnedHosts.Validator(
            [$"{endpoint.Host}:{endpoint.Port}"], endpoint.Scheme == "http", [$"{endpoint.Scheme}://{endpoint.Authority}/{bucket}/"]);
        var fetch = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxFetchBytes, env) is not null
                    || BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxUploadBytes, env) is not null
            ? new FetchConfig().WithMaxFetchBytes(BlobVendorEnvironment.MaxFetchBytesFrom(env))
            : new FetchConfig();
        return new ExternalizationOptions
        {
            External = new ServerExternalConfig
            {
                Storage = storage,
                ExternalizeThresholdBytes = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.ExternalizeThreshold, env) ?? 1_048_576,
                Compression = BlobVendorEnvironment.CompressionFrom(env),
                FetchConfig = fetch,
                UrlValidator = validator,
            },
            UploadUrlProvider = storage,
            ObjectStore = storage,
            MaxRequestBytes = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxRequestBytes, env),
            MaxUploadBytes = BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxUploadBytes, env),
        };
    }
}
