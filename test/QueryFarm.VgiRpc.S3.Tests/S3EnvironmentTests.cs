using System.Security.Cryptography;
using System.Text;
using Apache.Arrow;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.S3.Tests;

/// <summary>Direct-storage contract v1: backend selection and the <c>VGI_STORAGE_S3_*</c> wiring.
/// The MinIO cases skip unless <c>VGI_TEST_S3_ENDPOINT</c> is set (the lane sets it, plus
/// <c>VGI_TEST_S3_BUCKET</c>, <c>VGI_TEST_S3_ACCESS_KEY_ID</c>, <c>VGI_TEST_S3_SECRET_ACCESS_KEY</c>,
/// optionally <c>VGI_TEST_S3_SESSION_TOKEN</c>, and <c>SSL_CERT_FILE</c> for a private CA).</summary>
public sealed class S3EnvironmentTests
{
    private static readonly Schema s_schema = new([], null);
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static Func<string, string?> Env(params (string Name, string? Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void BackendSelection()
    {
        Assert.Equal(StorageBackend.None, StorageBackendEnvironment.Select(Env()));
        Assert.Equal(StorageBackend.Vendor, StorageBackendEnvironment.Select(Env(("VGI_BLOB_VENDOR_URL", "https://v"))));
        Assert.Equal(StorageBackend.S3, StorageBackendEnvironment.Select(Env(("VGI_STORAGE_S3_BUCKET", "b"))));
        Assert.Equal(StorageBackend.Gcs, StorageBackendEnvironment.Select(Env(("VGI_STORAGE_GCS_BUCKET", "b"))));
        var conflict = Assert.Throws<ArgumentException>(() => StorageBackendEnvironment.Select(Env(("VGI_BLOB_VENDOR_URL", "https://v"), ("VGI_STORAGE_S3_BUCKET", "b"))));
        Assert.Contains("VGI_BLOB_VENDOR_URL, VGI_STORAGE_S3_BUCKET", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(StorageBackend.S3, StorageBackendEnvironment.Select(Env(("VGI_BLOB_VENDOR_URL", "https://v"), ("VGI_STORAGE_S3_BUCKET", "b"), ("VGI_STORAGE_BACKEND", "S3"))));
        Assert.Equal(StorageBackend.None, StorageBackendEnvironment.Select(Env(("VGI_BLOB_VENDOR_URL", "https://v"), ("VGI_STORAGE_BACKEND", "none"))));
        Assert.Throws<ArgumentException>(() => StorageBackendEnvironment.Select(Env(("VGI_STORAGE_BACKEND", "r2"))));
        // The plain HTTP entry point refuses s3 instead of silently serving inline.
        Assert.Throws<ArgumentException>(() => ExternalizationOptions.FromEnvironment(Env(("VGI_STORAGE_S3_BUCKET", "b"))));
        Assert.Null(ExternalizationOptions.FromEnvironment(Env(("VGI_BLOB_VENDOR_URL", "https://v.example.com"), ("VGI_BLOB_VENDOR_TOKEN", "t"), ("VGI_STORAGE_BACKEND", "none"))));
        Assert.Equal(TimeSpan.FromHours(1), StorageBackendEnvironment.UrlTtlFrom(Env()));
        Assert.Equal(TimeSpan.FromDays(7), StorageBackendEnvironment.UrlTtlFrom(Env(("VGI_STORAGE_URL_TTL_SECONDS", "99999999"))));
        Assert.Throws<ArgumentException>(() => StorageBackendEnvironment.UrlTtlFrom(Env(("VGI_STORAGE_URL_TTL_SECONDS", "5"))));
    }

    [Fact]
    public async Task EnvironmentParsingAndOfflinePresigning()
    {
        foreach (var bad in new[]
                 {
                     Env(),
                     Env(("VGI_STORAGE_S3_BUCKET", "b"), ("VGI_STORAGE_S3_CHECKSUM", "md5")),
                     Env(("VGI_STORAGE_S3_BUCKET", "b"), ("VGI_STORAGE_S3_ENDPOINT", "ftp://x")),
                     Env(("VGI_STORAGE_S3_BUCKET", "b"), ("VGI_STORAGE_S3_ENDPOINT", "https://u:p@x")),
                     Env(("VGI_STORAGE_S3_BUCKET", "b"), ("VGI_STORAGE_S3_ACCESS_KEY_ID", "a")),
                     Env(("VGI_STORAGE_S3_BUCKET", "b"), ("VGI_STORAGE_S3_SESSION_TOKEN", "t")),
                     Env(("VGI_STORAGE_S3_BUCKET", "b"), ("VGI_STORAGE_S3_FORCE_PATH_STYLE", "maybe")),
                 })
        {
            Assert.Throws<ArgumentException>(() => S3StorageEnvironment.BuilderFrom(bad));
        }

        // Endpoint set: path style on by default; region "auto"; the public endpoint carries the
        // handed-out URLs; the session token rides in the URL; credentials never appear in it.
        await using var storage = S3StorageEnvironment.BuilderFrom(Env(
            ("VGI_STORAGE_S3_BUCKET", "my-bucket"),
            ("VGI_STORAGE_S3_PREFIX", "vgi"),
            ("VGI_STORAGE_S3_ENDPOINT", "http://minio.internal:9000"),
            ("VGI_STORAGE_S3_PUBLIC_ENDPOINT", "https://s3.example.com"),
            ("VGI_STORAGE_S3_REGION", "auto"),
            ("VGI_STORAGE_URL_TTL_SECONDS", "600"),
            ("VGI_STORAGE_S3_ACCESS_KEY_ID", "AKIDEXAMPLE"),
            ("VGI_STORAGE_S3_SECRET_ACCESS_KEY", "SECRETSECRET"),
            ("VGI_STORAGE_S3_SESSION_TOKEN", "TOKEN/abc+=")))
            .Build();
        storage.ValidateUrl("https://s3.example.com/my-bucket/vgi/rpc/x.arrow?X-Amz-Signature=1");
        Assert.Throws<ArgumentException>(() => storage.ValidateUrl("http://minio.internal:9000/my-bucket/vgi/rpc/x.arrow"));
        Assert.Throws<ArgumentException>(() => storage.ValidateUrl("https://s3.example.com/other/x"));
        Assert.True(storage.IssuesUploadGrants);

        await using var none = S3StorageEnvironment.BuilderFrom(Env(
            ("VGI_STORAGE_S3_BUCKET", "b"), ("VGI_STORAGE_S3_CHECKSUM", "none"),
            ("VGI_STORAGE_S3_ACCESS_KEY_ID", "a"), ("VGI_STORAGE_S3_SECRET_ACCESS_KEY", "s"))).Build();
        Assert.False(none.IssuesUploadGrants);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.GenerateUploadUrlAsync(10, new string('a', 64), CancellationToken.None));
    }

    private static Func<string, string?>? MinioEnv(params (string Name, string? Value)[] extra)
    {
        var endpoint = Environment.GetEnvironmentVariable("VGI_TEST_S3_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
        {
            return null;
        }

        var map = new Dictionary<string, string?>
        {
            ["VGI_STORAGE_S3_ENDPOINT"] = endpoint,
            ["VGI_STORAGE_S3_BUCKET"] = Environment.GetEnvironmentVariable("VGI_TEST_S3_BUCKET"),
            ["VGI_STORAGE_S3_PREFIX"] = "vgi-csharp-test/",
            ["VGI_STORAGE_S3_ACCESS_KEY_ID"] = Environment.GetEnvironmentVariable("VGI_TEST_S3_ACCESS_KEY_ID"),
            ["VGI_STORAGE_S3_SECRET_ACCESS_KEY"] = Environment.GetEnvironmentVariable("VGI_TEST_S3_SECRET_ACCESS_KEY"),
            ["VGI_STORAGE_S3_SESSION_TOKEN"] = Environment.GetEnvironmentVariable("VGI_TEST_S3_SESSION_TOKEN"),
            ["VGI_STORAGE_S3_VERIFY"] = "1",
        };
        foreach (var (name, value) in extra)
        {
            map[name] = value;
        }

        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public async Task Minio_ObjectsResultsAndGrants()
    {
        if (MinioEnv() is not { } env)
        {
            Assert.Skip("VGI_TEST_S3_ENDPOINT is not set");
            return;
        }

        var options = S3StorageEnvironment.ExternalizationFromEnvironment(env);
        Assert.NotNull(options.UploadUrlProvider);
        await using var storage = (S3Storage)options.External!.Storage;

        var obj = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("state ref object ", 1000)));
        var id = await storage.PutObjectAsync(obj, CancellationToken.None);
        Assert.StartsWith("vgi-csharp-test/obj/", id, StringComparison.Ordinal);
        Assert.Equal(obj, await storage.GetAsync(id, null, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectMissingException>(() => storage.GetAsync("vgi-csharp-test/obj/nope.bin", null, CancellationToken.None));

        var result = Encoding.UTF8.GetBytes("arrow result bytes");
        var url = await storage.UploadAsync(result, s_schema, null, CancellationToken.None);
        options.External.UrlValidator!(url);
        Assert.Equal(result, await s_http.GetByteArrayAsync(url, TestContext.Current.CancellationToken));

        var body = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("client upload ", 64)));
        var grant = await storage.GenerateUploadUrlAsync(body.Length, Convert.ToHexStringLower(SHA256.HashData(body)), CancellationToken.None);
        async Task<int> Put(byte[] data, bool withHeader)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, grant.UploadUrlValue) { Content = new ByteArrayContent(data) };
            if (withHeader)
            {
                foreach (var (k, v) in grant.Headers)
                {
                    request.Headers.TryAddWithoutValidation(k, v);
                }
            }

            using var response = await s_http.SendAsync(request);
            return (int)response.StatusCode;
        }

        var flipped = (byte[])body.Clone();
        flipped[3] ^= 1;
        Assert.Contains(await Put(flipped, true), new[] { 400, 403 });
        Assert.Contains(await Put(body[1..], true), new[] { 400, 403 });
        Assert.Contains(await Put(body, false), new[] { 400, 403 });
        Assert.Equal(200, await Put(body, true));
        Assert.Equal(body, await s_http.GetByteArrayAsync(grant.DownloadUrl, TestContext.Current.CancellationToken));
        Assert.Equal(body, await storage.GetAsync(grant.ObjectId!, null, CancellationToken.None));
    }

    [Fact]
    public async Task Minio_ChecksumNoneStoresButGrantsNothing()
    {
        if (MinioEnv(("VGI_STORAGE_S3_CHECKSUM", "none")) is not { } env)
        {
            Assert.Skip("VGI_TEST_S3_ENDPOINT is not set");
            return;
        }

        var options = S3StorageEnvironment.ExternalizationFromEnvironment(env);
        Assert.Null(options.UploadUrlProvider);
        Assert.Null(options.MaxUploadBytes);
        Assert.NotNull(options.EffectiveObjectStore);
        await using var storage = (S3Storage)options.External!.Storage;
        var id = await storage.PutObjectAsync([1, 2, 3], CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, await storage.GetAsync(id, null, CancellationToken.None));
        var url = await storage.UploadAsync([4, 5], s_schema, null, CancellationToken.None);
        Assert.Equal(new byte[] { 4, 5 }, await s_http.GetByteArrayAsync(url, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Minio_PublicEndpointOnlyForHandedOutUrls()
    {
        var endpoint = Environment.GetEnvironmentVariable("VGI_TEST_S3_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint) || MinioEnv(("VGI_STORAGE_S3_PUBLIC_ENDPOINT", endpoint.Replace("127.0.0.1", "localhost", StringComparison.Ordinal))) is not { } env)
        {
            Assert.Skip("VGI_TEST_S3_ENDPOINT is not set");
            return;
        }

        await using var storage = S3StorageEnvironment.FromEnvironment(env);
        var url = await storage.UploadAsync([7, 7], s_schema, null, CancellationToken.None);
        Assert.Contains("://localhost:", url, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 7, 7 }, await s_http.GetByteArrayAsync(url, TestContext.Current.CancellationToken));
        var id = await storage.PutObjectAsync([8], CancellationToken.None);
        Assert.Equal(new byte[] { 8 }, await storage.GetAsync(id, null, CancellationToken.None));
    }

    [Fact]
    public async Task Minio_BucketCheckNamesTheProblem()
    {
        if (MinioEnv(("VGI_STORAGE_S3_BUCKET", "vgi-no-such-bucket-4c1e"), ("VGI_STORAGE_S3_VERIFY", null)) is not { } missingEnv)
        {
            Assert.Skip("VGI_TEST_S3_ENDPOINT is not set");
            return;
        }

        await using var missing = S3StorageEnvironment.FromEnvironment(missingEnv);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.VerifyAsync(TestContext.Current.CancellationToken));
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        await using var denied = S3StorageEnvironment.FromEnvironment(MinioEnv(
            ("VGI_STORAGE_S3_ACCESS_KEY_ID", "AKIDWRONGWRONG"), ("VGI_STORAGE_S3_SECRET_ACCESS_KEY", "wrongwrongwrong"),
            ("VGI_STORAGE_S3_SESSION_TOKEN", null), ("VGI_STORAGE_S3_VERIFY", null))!);
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => denied.VerifyAsync(TestContext.Current.CancellationToken));
        Assert.Contains("access denied", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wrongwrongwrong", error.Message, StringComparison.Ordinal);
    }
}

/// <summary>Credential rotation through the SDK's default chain: a <c>credential_process</c>
/// profile mints MinIO STS AssumeRole credentials and reports them as expiring in about 5 minutes,
/// so they are refreshed on every call; objects stored under one set resolve under the next. Skips
/// unless <c>VGI_TEST_S3_CREDENTIAL_PROCESS</c> is set. Not parallel: it sets <c>AWS_*</c> process
/// variables.</summary>
[Collection("aws-process-environment")]
public sealed class S3CredentialRotationTests
{
    [Fact]
    public async Task ObjectsSurviveCredentialRotation()
    {
        var endpoint = Environment.GetEnvironmentVariable("VGI_TEST_S3_ENDPOINT");
        var process = Environment.GetEnvironmentVariable("VGI_TEST_S3_CREDENTIAL_PROCESS");
        var log = Environment.GetEnvironmentVariable("VGI_TEST_S3_CREDENTIAL_LOG");
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(process) || string.IsNullOrEmpty(log))
        {
            Assert.Skip("VGI_TEST_S3_ENDPOINT / VGI_TEST_S3_CREDENTIAL_PROCESS not set");
            return;
        }

        var dir = Directory.CreateTempSubdirectory("vgi-s3-rotation-");
        var config = Path.Combine(dir.FullName, "config");
        await File.WriteAllTextAsync(config, $"[profile vgi-rotation]\ncredential_process = {process}\nregion = us-east-1\n", TestContext.Current.CancellationToken);
        foreach (var k in new[] { "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" })
        {
            Environment.SetEnvironmentVariable(k, null);
        }

        Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", config);
        Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE", Path.Combine(dir.FullName, "none"));
        Environment.SetEnvironmentVariable("AWS_PROFILE", "vgi-rotation");
        Environment.SetEnvironmentVariable("AWS_EC2_METADATA_DISABLED", "true");
        File.Delete(log);

        var map = new Dictionary<string, string?>
        {
            ["VGI_STORAGE_S3_ENDPOINT"] = endpoint,
            ["VGI_STORAGE_S3_BUCKET"] = Environment.GetEnvironmentVariable("VGI_TEST_S3_BUCKET"),
            ["VGI_STORAGE_S3_PREFIX"] = "vgi-csharp-rotation/",
        };
        await using var storage = S3StorageEnvironment.FromEnvironment(name => map.GetValueOrDefault(name));
        var id = await storage.PutObjectAsync(Encoding.UTF8.GetBytes("stored under the first credentials"), CancellationToken.None);
        var url = await storage.UploadAsync([1], new Schema([], null), null, CancellationToken.None);
        var first = (await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken)).Length;
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal("stored under the first credentials", Encoding.UTF8.GetString(await storage.GetAsync(id, null, CancellationToken.None)));
        var calls = await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken);
        Assert.True(calls.Length > first, $"credentials were not refreshed: {first} calls before, {calls.Length} after");
        Assert.Contains("X-Amz-Security-Token=", url, StringComparison.Ordinal);
        var expires = int.Parse(System.Text.RegularExpressions.Regex.Match(url, "X-Amz-Expires=(\\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(expires <= 300, $"URL lifetime {expires} s exceeds the credentials' remaining lifetime");
        dir.Delete(true);
    }
}
