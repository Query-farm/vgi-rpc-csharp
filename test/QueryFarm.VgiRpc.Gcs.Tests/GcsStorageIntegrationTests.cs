using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Apache.Arrow;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using QueryFarm.VgiRpc.Gcs;
using Xunit;

namespace QueryFarm.VgiRpc.Gcs.Tests;

/// <summary>
/// Starts one fake-gcs-server container, one <see cref="StorageClient"/>, and one signer for the
/// whole test class — see <c>S3Fixture</c>'s matching doc comment for why a shared class fixture
/// beats a fresh container per <c>[Fact]</c> here.
/// </summary>
public sealed class GcsFixture : IAsyncLifetime
{
    // Multi-architecture manifest for the 1.54.0 release — same digest vgi-rpc-java pins.
    private const string Image = "fsouza/fake-gcs-server@sha256:3730da0e31f7e5186a90ec4899dc2c336104e7599df400411392ef17e684c31f";
    private const string Project = "vgi-rpc-integration";
    public const string Bucket = "vgi-rpc-integration";
    private const int GcsPort = 4443;

    private readonly IContainer _fakeGcs = new ContainerBuilder()
        .WithImage(Image)
        .WithPortBinding(GcsPort, true)
        .WithCommand("-scheme", "http", "-port", GcsPort.ToString(), "-public-host", "localhost", "-backend", "memory")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/_internal/healthcheck").ForPort(GcsPort)))
        .Build();

    // Kept alive for the fixture's lifetime, disposed in DisposeAsync — the signer holds onto
    // this key and signs lazily on each SignAsync call, so disposing it at the end of
    // InitializeAsync (e.g. via a `using` local) throws ObjectDisposedException the first time an
    // actual test tries to sign a URL.
    private RSA? _rsa;

    public Uri Endpoint { get; private set; } = null!;
    public StorageClient Sdk { get; private set; } = null!;
    public UrlSigner Signer { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _fakeGcs.StartAsync();
        // "localhost", not _fakeGcs.Hostname ("127.0.0.1") — fake-gcs-server's "-public-host
        // localhost" flag makes its XML-API-style flat routing (/{bucket}/{object}, what a V4
        // signed URL's path targets) key off the request's Host header matching that value
        // exactly; connecting via the raw IP still reaches the same container but gets a 404,
        // confirmed directly against the emulator (its JSON API paths are unaffected either way).
        Endpoint = new Uri($"http://localhost:{_fakeGcs.GetMappedPublicPort(GcsPort)}");

        // The emulator needs no authentication, while V4 URL generation needs a signer. An
        // ephemeral RSA test credential supplies both contracts without checking a private key
        // or cloud credential into the repository — fake-gcs-server ignores the signature anyway
        // (see S3StorageIntegrationTests' sibling class doc comment for the analogous S3 case,
        // and this file's own test doc comment for why the GCS emulator can't prove as much).
        _rsa = RSA.Create(2048);
        var credential = new ServiceAccountCredential(
            new ServiceAccountCredential.Initializer("vgi-rpc-integration@local.invalid") { Key = _rsa });
        Signer = UrlSigner.FromServiceAccountCredential(credential);

        Sdk = new StorageClientBuilder
        {
            BaseUri = Endpoint + "storage/v1/",
            UnauthenticatedAccess = true,
        }.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Sdk.CreateBucketAsync(Project, Bucket, cancellationToken: cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        Sdk.Dispose();
        _rsa?.Dispose();
        await _fakeGcs.DisposeAsync();
    }
}

/// <summary>
/// GCS SDK integration against fake-gcs-server, run via Testcontainers. The emulator exercises
/// the real JSON upload and signed-URL download routes, but — like vgi-rpc-java's own
/// <c>GcsStorageIntegrationTest</c>, which this mirrors (same image, same emulator quirks) —
/// intentionally does NOT validate signed-URL query parameters; the last test makes that
/// limitation executable rather than letting this lane imply stronger coverage than it has.
/// </summary>
public sealed class GcsStorageIntegrationTests(GcsFixture fixture) : IClassFixture<GcsFixture>
{
    private const string Prefix = "csharp-integration/";
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Schema s_dummySchema = new([], null);

    [Fact]
    public async Task UploadPersistsBytesMetadataAndProducesFetchableRewrittenV4Url()
    {
        var decoded = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("real fake-gcs-server payload: \0\n", 256)));
        using var compressor = new ZstdSharp.Compressor(3);
        var encoded = compressor.Wrap(decoded).ToArray();

        using var storage = NewStorage();
        var signedGoogleUrl = await storage.UploadAsync(encoded, s_dummySchema, "zstd", CancellationToken.None);
        var uri = new Uri(signedGoogleUrl);

        Assert.Equal("storage.googleapis.com", uri.Host);
        Assert.StartsWith($"/{GcsFixture.Bucket}/{Prefix}", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith(".arrow", uri.AbsolutePath, StringComparison.Ordinal);
        var query = uri.Query.ToLowerInvariant();
        Assert.Contains("x-goog-algorithm=goog4-rsa-sha256", query, StringComparison.Ordinal);
        Assert.Contains("x-goog-signature=", query, StringComparison.Ordinal);

        var objectName = uri.AbsolutePath[$"/{GcsFixture.Bucket}/".Length..];
        var blob = await fixture.Sdk.GetObjectAsync(GcsFixture.Bucket, objectName);
        Assert.Equal("application/vnd.apache.arrow.stream", blob.ContentType);
        Assert.Equal("zstd", blob.ContentEncoding);

        var emulatorUrl = RewriteToEmulator(signedGoogleUrl);
        using var downloaded = await s_http.GetAsync(emulatorUrl);
        Assert.Equal(System.Net.HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(encoded, await downloaded.Content.ReadAsByteArrayAsync());
        Assert.Equal("zstd", downloaded.Content.Headers.ContentEncoding.FirstOrDefault());
    }

    [Fact]
    public async Task EmulatorExplicitlyDoesNotValidateSignedUrlQueryParameters()
    {
        var expected = Encoding.UTF8.GetBytes("signature limitation sentinel");

        using var storage = NewStorage();
        var signedGoogleUrl = await storage.UploadAsync(expected, s_dummySchema, null, CancellationToken.None);

        var valid = RewriteToEmulator(signedGoogleUrl);
        var mutated = MutateSignature(valid);

        using var validResponse = await s_http.GetAsync(valid);
        Assert.Equal(System.Net.HttpStatusCode.OK, validResponse.StatusCode);

        // fake-gcs-server intentionally does not validate signed-URL query parameters; real GCS
        // is still required to prove signer correctness — this test documents that gap rather
        // than implying this lane covers it.
        using var mutatedResponse = await s_http.GetAsync(mutated);
        Assert.Equal(System.Net.HttpStatusCode.OK, mutatedResponse.StatusCode);
        Assert.Equal(expected, await mutatedResponse.Content.ReadAsByteArrayAsync());
    }

    private GcsStorage NewStorage() =>
        GcsStorage.CreateBuilder(GcsFixture.Bucket)
            .WithClient(fixture.Sdk, fixture.Signer)
            .WithKeyPrefix(Prefix)
            .WithSignDuration(TimeSpan.FromMinutes(5))
            .Build();

    private string RewriteToEmulator(string signedGoogleUrl)
    {
        var uri = new Uri(signedGoogleUrl);
        return $"{fixture.Endpoint}{uri.AbsolutePath.TrimStart('/')}{uri.Query}";
    }

    private static string MutateSignature(string signedUrl)
    {
        var mutated = Regex.Replace(signedUrl, "(?i)(X-Goog-Signature=)[0-9a-f]+", "${1}" + new string('0', 64));
        Assert.NotEqual(signedUrl, mutated);
        return mutated;
    }
}
