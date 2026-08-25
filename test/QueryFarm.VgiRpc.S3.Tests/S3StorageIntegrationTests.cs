using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Apache.Arrow;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using QueryFarm.VgiRpc.S3;
using Xunit;

namespace QueryFarm.VgiRpc.S3.Tests;

/// <summary>
/// Starts one RustFS (S3-compatible object store) container for the whole test class — a fresh
/// container per <c>[Fact]</c> (xUnit's default: a new test-class instance per test method) would
/// pay full Testcontainers startup cost three times over for no isolation benefit these tests
/// actually need, since each test uses its own object key.
/// </summary>
public sealed class S3Fixture : IAsyncLifetime
{
    // Multi-architecture manifest for the 1.0.0-beta.12-glibc release — same digest vgi-rpc-java pins.
    private const string Image = "ghcr.io/rustfs/rustfs@sha256:29c02251c085cb04edce556304a9ec0f8fba0c40300266cf4f3d953783fe2450";
    public const string AccessKey = "vgi-integration-access";
    public const string SecretKey = "vgi-integration-secret-key";
    public const string Bucket = "vgi-rpc-integration";
    private const int S3Port = 9000;

    private readonly IContainer _rustfs = new ContainerBuilder()
        .WithImage(Image)
        .WithPortBinding(S3Port, true)
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithEnvironment("RUSTFS_CONSOLE_ENABLE", "false")
        .WithEnvironment("RUSTFS_OBS_LOGGER_LEVEL", "error")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(S3Port)))
        .Build();

    public Uri Endpoint { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _rustfs.StartAsync();
        Endpoint = new Uri($"http://{_rustfs.Hostname}:{_rustfs.GetMappedPublicPort(S3Port)}");

        // AuthenticationRegion, NOT RegionEndpoint, alongside a custom ServiceURL — see
        // S3Storage's constructor comment: setting both together is a real AWSSDK.S3 v4 interop
        // trap that makes every request fail 403 InvalidAccessKeyId even with correct credentials.
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config { ServiceURL = Endpoint.ToString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, cts.Token);
    }

    public async ValueTask DisposeAsync() => await _rustfs.DisposeAsync();
}

/// <summary>
/// Real S3-protocol integration tests against RustFS, run via Testcontainers — no cloud
/// credentials, no network egress. RustFS is deliberately used instead of a request stub: the
/// rejected requests below prove the presigned URL is genuinely bound to its SigV4 signature and
/// HTTP method, mirroring vgi-rpc-java's own <c>S3StorageIntegrationTest</c> (same image, same
/// assertions).
/// </summary>
public sealed class S3StorageIntegrationTests(S3Fixture fixture) : IClassFixture<S3Fixture>
{
    private const string Prefix = "csharp-integration/";
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Schema s_dummySchema = new([], null);

    [Fact]
    public async Task UploadProducesMethodBoundSigV4GetAndRecoversAfterRejections()
    {
        var expected = Encoding.UTF8.GetBytes("real RustFS payload: \0");

        await using var storage = NewStorage();
        var signedGet = await storage.UploadAsync(expected, s_dummySchema, null, CancellationToken.None);
        AssertSignedObjectPath(signedGet);

        var fetched = await s_http.GetAsync(signedGet);
        Assert.Equal(System.Net.HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(expected, await fetched.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/vnd.apache.arrow.stream", fetched.Content.Headers.ContentType?.ToString());

        var mutated = MutateSignature(signedGet);
        using var mutatedResponse = await s_http.GetAsync(mutated);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, mutatedResponse.StatusCode);

        using var wrongMethodResponse = await s_http.PutAsync(signedGet, new ByteArrayContent([]));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, wrongMethodResponse.StatusCode);

        // Storage remains usable after the rejected requests above.
        var recoveryBody = Encoding.UTF8.GetBytes("storage remains usable");
        var recoveryUrl = await storage.UploadAsync(recoveryBody, s_dummySchema, null, CancellationToken.None);
        using var recovered = await s_http.GetAsync(recoveryUrl);
        Assert.Equal(System.Net.HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(recoveryBody, await recovered.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ZstdObjectRetainsContentEncoding()
    {
        var decoded = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Arrow-compatible bytes should survive external zstd.\n", 256)));
        using var compressor = new ZstdSharp.Compressor(3);
        var encoded = compressor.Wrap(decoded).ToArray();

        await using var storage = NewStorage();
        var signedGet = await storage.UploadAsync(encoded, s_dummySchema, "zstd", CancellationToken.None);

        using var raw = await s_http.GetAsync(signedGet);
        Assert.Equal(System.Net.HttpStatusCode.OK, raw.StatusCode);
        Assert.Equal(encoded, await raw.Content.ReadAsByteArrayAsync());
        Assert.Equal("zstd", raw.Content.Headers.ContentEncoding.FirstOrDefault());
    }

    [Fact]
    public async Task GenerateUploadUrlVendsMethodBoundPutAndGetForTheSameObject()
    {
        var payload = Encoding.UTF8.GetBytes("client-vended upload");

        await using var storage = NewStorage();
        var uploadUrl = await storage.GenerateUploadUrlAsync(s_dummySchema, CancellationToken.None);

        using var put = await s_http.PutAsync(new Uri(uploadUrl.UploadUrlValue), new ByteArrayContent(payload));
        Assert.Equal(System.Net.HttpStatusCode.OK, put.StatusCode);

        using var get = await s_http.GetAsync(uploadUrl.DownloadUrl);
        Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(payload, await get.Content.ReadAsByteArrayAsync());

        // The GET url is not authorized for PUT and vice versa — each is bound to its own method.
        using var getUsedAsPut = await s_http.PutAsync(new Uri(uploadUrl.DownloadUrl), new ByteArrayContent(payload));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, getUsedAsPut.StatusCode);
    }

    private S3Storage NewStorage() =>
        S3Storage.CreateBuilder(S3Fixture.Bucket)
            .WithServiceUrl(fixture.Endpoint.ToString())
            .WithForcePathStyle(true)
            .WithCredentials(new BasicAWSCredentials(S3Fixture.AccessKey, S3Fixture.SecretKey))
            .WithRegion(RegionEndpoint.USEast1)
            .WithKeyPrefix(Prefix)
            .WithPresignDuration(TimeSpan.FromMinutes(5))
            .Build();

    private void AssertSignedObjectPath(string signedUrl)
    {
        var uri = new Uri(signedUrl);
        Assert.Equal(fixture.Endpoint.Scheme, uri.Scheme);
        Assert.Equal(fixture.Endpoint.Host, uri.Host);
        Assert.Equal(fixture.Endpoint.Port, uri.Port);
        Assert.StartsWith($"/{S3Fixture.Bucket}/{Prefix}", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith(".arrow", uri.AbsolutePath, StringComparison.Ordinal);
        var query = uri.Query.ToLowerInvariant();
        Assert.Contains("x-amz-algorithm=aws4-hmac-sha256", query, StringComparison.Ordinal);
        Assert.Contains("x-amz-signature=", query, StringComparison.Ordinal);
    }

    private static string MutateSignature(string signedUrl) =>
        System.Text.RegularExpressions.Regex.Replace(
            signedUrl, "(?i)(X-Amz-Signature=)[0-9a-f]+", "${1}" + new string('0', 64));
}
