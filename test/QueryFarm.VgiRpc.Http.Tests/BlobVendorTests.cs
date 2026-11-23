using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Server;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// The blob-vendor client (<see cref="VendorStorage"/>, <see cref="VendorBlobClient"/>,
/// <see cref="PinnedHosts"/>) and its environment wiring
/// (<see cref="ExternalizationOptions.FromEnvironment"/>). The vendor-backed tests run against
/// vgi-rpc-python's fake vendor: set <c>VGI_RPC_FAKE_VENDOR_BIN</c> to its
/// <c>vgi-rpc-fake-vendor</c> executable (CI's conformance job does).
/// </summary>
public sealed class BlobVendorTests
{
    private const string Token = "test-token";

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return name => map.GetValueOrDefault(name);
    }

    [Fact]
    public void PinnedHosts_AcceptsOnlyTheExactHostsAndPrefixes()
    {
        var validate = PinnedHosts.Validator(["store.example.com", "dl.example.com:8443"], allowHttp: false, ["https://store.example.com/bucket/"]);
        validate("https://store.example.com/bucket/obj?X-Amz-Signature=s");
        Assert.Throws<ArgumentException>(() => validate("https://evil.store.example.com/bucket/obj"));
        Assert.Throws<ArgumentException>(() => validate("http://store.example.com/bucket/obj"));
        Assert.Throws<ArgumentException>(() => validate("https://user@store.example.com/bucket/obj"));
        Assert.Throws<ArgumentException>(() => validate("https://store.example.com/other/obj"));
        Assert.Throws<ArgumentException>(() => validate("https://store.example.com/bucket/../other/obj"));
        Assert.Throws<ArgumentException>(() => validate("https://store.example.com:444/bucket/obj"));
        var noPrefix = PinnedHosts.Validator(["dl.example.com:8443"], allowHttp: false);
        noPrefix("https://dl.example.com:8443/x");
        Assert.Throws<ArgumentException>(() => noPrefix("https://dl.example.com/x"));
        var error = Assert.Throws<ArgumentException>(() => noPrefix("https://other.example.com/x?X-Amz-Signature=secret"));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Vendor_RefusesPlainHttpOffLoopbackAndAnEmptyToken()
    {
        Assert.Throws<ArgumentException>(() => new VendorStorage("http://vendor.example.com", Token));
        Assert.Throws<ArgumentException>(() => new VendorStorage("https://vendor.example.com", ""));
        using var loopback = new VendorStorage("http://127.0.0.1:9", Token);
    }

    [Fact]
    public void Environment_FetchAndUploadCaps()
    {
        Assert.Equal(FetchConfig.DefaultMaxFetchBytes, BlobVendorEnvironment.MaxFetchBytesFrom(Env()));
        Assert.Equal(1L << 30, BlobVendorEnvironment.MaxFetchBytesFrom(Env((BlobVendorEnvironment.MaxUploadBytes, "1073741824"))));
        Assert.Equal(1000, BlobVendorEnvironment.MaxFetchBytesFrom(Env((BlobVendorEnvironment.MaxFetchBytes, "1000"))));
        Assert.Throws<ArgumentException>(() => BlobVendorEnvironment.MaxFetchBytesFrom(
            Env((BlobVendorEnvironment.MaxFetchBytes, "1000"), (BlobVendorEnvironment.MaxUploadBytes, "2000"))));
        Assert.Throws<ArgumentException>(() => BlobVendorEnvironment.PositiveInt(BlobVendorEnvironment.MaxRequestBytes, Env((BlobVendorEnvironment.MaxRequestBytes, "-1"))));
        Assert.Throws<ArgumentException>(() => BlobVendorEnvironment.UrlAndToken(Env((BlobVendorEnvironment.Url, "https://v.example.com"))));
        Assert.Equal(new Compression("zstd", 7), BlobVendorEnvironment.CompressionFrom(Env((BlobVendorEnvironment.ExternalCompression, "zstd:7"))));
        Assert.Null(BlobVendorEnvironment.CompressionFrom(Env()));
    }

    [Fact]
    public void Environment_WithoutAVendor()
    {
        Assert.Null(ExternalizationOptions.FromEnvironment(Env()));
        var options = ExternalizationOptions.FromEnvironment(Env((BlobVendorEnvironment.MaxRequestBytes, "32768")));
        Assert.NotNull(options);
        Assert.Equal(32768, options.MaxRequestBytes);
        Assert.Null(options.UploadUrlProvider);
    }

    [Fact]
    public void Environment_WithAVendor()
    {
        var options = ExternalizationOptions.FromEnvironment(Env(
            (BlobVendorEnvironment.Url, "http://127.0.0.1:9"),
            (BlobVendorEnvironment.Token, Token),
            (BlobVendorEnvironment.ExternalizeThreshold, "4096"),
            (BlobVendorEnvironment.MaxRequestBytes, "32768"),
            (BlobVendorEnvironment.MaxUploadBytes, "268435456")));
        Assert.NotNull(options);
        Assert.IsType<VendorStorage>(options.External!.Storage);
        Assert.Same(options.External.Storage, options.UploadUrlProvider);
        Assert.Same(options.External.Storage, options.EffectiveObjectStore);
        Assert.Equal(4096, options.External.ExternalizeThresholdBytes);
        Assert.Equal(268435456, options.MaxUploadBytes);
        Assert.Equal(268435456, options.External.FetchConfig.MaxFetchBytes);
    }

    [Fact]
    public async Task Vendor_ObjectStoreRoundTripAndMissing()
    {
        await using var vendor = await FakeVendor.StartAsync();
        using var storage = new VendorStorage(vendor.Url, Token);
        var data = RandomNumberGenerator.GetBytes(5000);

        var before = await vendor.StatsAsync();
        var id = await storage.PutObjectAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(data, await storage.GetAsync(id, null, TestContext.Current.CancellationToken));
        var after = await vendor.StatsAsync();
        Assert.Equal(1, after["puts_state_ref"] - before["puts_state_ref"]);

        Assert.True(await vendor.DeleteAsync("state_ref") >= 1);
        await Assert.ThrowsAsync<ObjectMissingException>(() => storage.GetAsync(id, null, TestContext.Current.CancellationToken));

        var big = await storage.PutObjectAsync(data, TestContext.Current.CancellationToken);
        var capped = await Assert.ThrowsAsync<BlobVendorException>(() => storage.GetAsync(big, 10, TestContext.Current.CancellationToken));
        Assert.Contains("max_fetch_bytes", capped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vendor_ClientUploadGrantIsSizedAndChecksummed()
    {
        await using var vendor = await FakeVendor.StartAsync();
        using var storage = new VendorStorage(vendor.Url, Token);
        var data = RandomNumberGenerator.GetBytes(3000);
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));

        var before = await vendor.StatsAsync();
        var grant = await storage.GenerateUploadUrlAsync(data.Length, sha, TestContext.Current.CancellationToken);
        Assert.NotEmpty(grant.ObjectId);
        Assert.DoesNotContain(grant.Headers.Keys, k => k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));

        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(http, grant, [.. data[..^1], (byte)(data[^1] ^ 0xFF)])).StatusCode);
        Assert.True((await Put(http, grant, data)).IsSuccessStatusCode);
        Assert.Equal(data, await storage.GetAsync(grant.ObjectId, null, TestContext.Current.CancellationToken));
        var after = await vendor.StatsAsync();
        Assert.Equal(1, after["puts_upload"] - before["puts_upload"]);
        Assert.Equal(1, after["client_uploads"] - before["client_uploads"]);
        Assert.Equal(0, after["upload_urls"] - before["upload_urls"]);
    }

    [Fact]
    public async Task Vendor_ExternalizedResultIsDownloadableAndPinned()
    {
        await using var vendor = await FakeVendor.StartAsync();
        using var storage = new VendorStorage(vendor.Url, Token);
        var data = RandomNumberGenerator.GetBytes(10000);

        var url = await storage.UploadAsync(data, new Schema([], null), null, TestContext.Current.CancellationToken);
        storage.ValidateUrl(url);
        using var http = new HttpClient();
        Assert.Equal(data, await http.GetByteArrayAsync(url, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => storage.ValidateUrl("https://elsewhere.example.com/x"));
    }

    [Fact]
    public async Task Vendor_BlobClientVerifiesAgainstTheReference()
    {
        await using var vendor = await FakeVendor.StartAsync();
        using var blobs = new VendorBlobClient(vendor.Url, Token);
        var data = Encoding.UTF8.GetBytes(new string('v', 6000));

        var reference = await blobs.PutBytesAsync(data, new BlobScope("loc-anon", "s.test"), "key.0", TestContext.Current.CancellationToken);
        Assert.Equal(data, await blobs.GetBytesAsync(reference, TestContext.Current.CancellationToken));
        var wrong = await Assert.ThrowsAsync<BlobVendorException>(() => blobs.GetBytesAsync(reference with { Sha256 = new string('0', 64) }, TestContext.Current.CancellationToken));
        Assert.Equal(BlobVendorException.Sha256MismatchCode, wrong.Code);
        Assert.Equal(1, await blobs.DeleteScopeAsync(new BlobScope("loc-anon", "s.test"), TestContext.Current.CancellationToken));
    }

    /// <summary>End to end over HTTP with the environment wiring: the capability headers (an
    /// application's one included), a grant whose <c>ref</c> resolves through the vendor, an
    /// over-cap request uploaded through a vendor URL, and an over-threshold result externalized.</summary>
    [Fact]
    public async Task Server_FromEnvironment_UploadsResolvesAndExternalizesThroughTheVendor()
    {
        await using var vendor = await FakeVendor.StartAsync();
        var options = ExternalizationOptions.FromEnvironment(Env(
            (BlobVendorEnvironment.Url, vendor.Url),
            (BlobVendorEnvironment.Token, Token),
            (BlobVendorEnvironment.ExternalizeThreshold, "4096"),
            (BlobVendorEnvironment.MaxRequestBytes, "32768"),
            (BlobVendorEnvironment.MaxUploadBytes, "268435456")))!
            .WithCapabilityHeaders(new Dictionary<string, string> { ["VGI-Arguments-Ref-Bytes"] = "4194304" });
        var tokenKey = RandomNumberGenerator.GetBytes(32);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapVgiRpc(new RpcServer(typeof(IEchoService), new EchoService()), tokenKey: tokenKey, externalization: options);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        using (var http = new HttpClient { BaseAddress = address })
        {
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/health"), TestContext.Current.CancellationToken);
            Assert.Equal("2", response.Headers.GetValues("VGI-Upload-URL-Support").Single());
            Assert.Equal("268435456", response.Headers.GetValues("VGI-Max-Upload-Bytes").Single());
            Assert.Equal("32768", response.Headers.GetValues("VGI-Max-Request-Bytes").Single());
            Assert.Equal("4194304", response.Headers.GetValues("VGI-Arguments-Ref-Bytes").Single());
        }

        await using var client = new HttpRpcClient(address, new HttpRpcClientOptions
        {
            Protocol = "EchoService",
            ExternalLocation = new ClientExternalConfig { UrlValidator = null },
        });

        // A grant: its ref opens for this principal and resolves through the vendor.
        var argument = RandomNumberGenerator.GetBytes(2048);
        var sha = Convert.ToHexStringLower(SHA256.HashData(argument));
        var grant = Assert.Single(await client.RequestUploadUrlsAsync([new UploadRequest(argument.Length, sha)], TestContext.Current.CancellationToken));
        using (var put = new HttpClient())
        {
            Assert.True((await Put(put, grant, argument)).IsSuccessStatusCode);
        }

        var resolved = await ArgumentRefs.ResolveAsync(
            grant.Ref, argument.Length, sha, tokenKey, null, options.EffectiveObjectStore, 1 << 20, new ObjectCache(1 << 20),
            TestContext.Current.CancellationToken);
        Assert.Equal(argument, resolved);

        // A 100 KB request (over the 32 KiB cap) goes up through a vendor URL; the 100 KB result
        // comes back as a vendor pointer.
        var before = await vendor.StatsAsync();
        var value = new string('x', 100_000);
        var schema = new Schema([new Field("value", StringType.Default, false)], null);
        using var parameters = new RecordBatch(schema, [new StringArray.Builder().Append(value).Build()], 1);
        var result = await client.CallUnaryAsync("echo", parameters, cancellationToken: TestContext.Current.CancellationToken);
        using (result.Batch)
        {
            Assert.Equal(value, ((StringArray)result.Batch.Column(0)).GetString(0));
        }

        var after = await vendor.StatsAsync();
        Assert.Equal(1, after["client_uploads"] - before["client_uploads"]);
        Assert.True(after["puts_rpc"] - before["puts_rpc"] >= 1);
        Assert.Equal(0, after["upload_urls"] - before["upload_urls"]);
    }

    private static Task<HttpResponseMessage> Put(HttpClient http, External.UploadUrl grant, byte[] body) =>
        Put(http, grant.UploadUrlValue, grant.Headers, body);

    private static Task<HttpResponseMessage> Put(HttpClient http, Client.Http.UploadUrl grant, byte[] body) =>
        Put(http, grant.Upload, grant.Headers, body);

    private static async Task<HttpResponseMessage> Put(HttpClient http, string url, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public interface IEchoService
    {
        Task<string> EchoAsync(string value);
    }

    public sealed class EchoService : IEchoService
    {
        public Task<string> EchoAsync(string value) => Task.FromResult(value);
    }

    /// <summary>vgi-rpc-python's <c>vgi-rpc-fake-vendor --test-routes</c>, one per test.</summary>
    private sealed class FakeVendor : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly HttpClient _http = new();

        private FakeVendor(Process process, string url)
        {
            _process = process;
            Url = url;
        }

        public string Url { get; }

        public static async Task<FakeVendor> StartAsync()
        {
            var bin = Environment.GetEnvironmentVariable("VGI_RPC_FAKE_VENDOR_BIN");
            if (string.IsNullOrEmpty(bin))
            {
                Assert.Skip("Set VGI_RPC_FAKE_VENDOR_BIN to vgi-rpc-python's vgi-rpc-fake-vendor to run the blob-vendor tests.");
            }

            var start = new ProcessStartInfo(bin)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "--test-routes", "--token", $"{Token}=tenant-a", "--port", "0" })
            {
                start.ArgumentList.Add(arg);
            }

            var process = Process.Start(start)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("URL:", StringComparison.Ordinal))
                {
                    return new FakeVendor(process, line[4..].Trim());
                }
            }

            process.Kill();
            throw new InvalidOperationException("the fake vendor exited before printing URL:");
        }

        public async Task<Dictionary<string, long>> StatsAsync()
        {
            var json = await _http.GetStringAsync($"{Url}/__stats__", TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Number)
                .ToDictionary(p => p.Name, p => p.Value.GetInt64());
        }

        public async Task<int> DeleteAsync(string origin)
        {
            var json = await _http.GetStringAsync($"{Url}/__admin__/delete?origin={origin}", TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("deleted").GetInt32();
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }
}
