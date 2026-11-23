using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// <c>POST {prefix}/__upload_url__/init</c> (upload-URL protocol v2) and the client's upload path.
/// The request carries <c>uploads: list&lt;struct&lt;size, sha256&gt;&gt;</c>; every refusal is a 400
/// before any URL is minted; the client uploads only to a server advertising
/// <c>VGI-Upload-URL-Support: 2</c>, PUTs with the vended <c>upload_headers</c>, and stamps
/// <c>vgi_rpc.location.sha256</c> on the pointer, which the server requires and verifies.
///
/// <para>The request is read with the standard framing. It used
/// to read only the first batch's columns: the request's <c>vgi_rpc.method</c> was never checked,
/// and any failure to parse the body fell through to <c>count = 1</c>, so a request meant for
/// another method -- or not an Arrow request at all -- was answered with fresh upload URLs. The
/// reference (<c>_UploadUrlResource.on_post</c>), Go, Rust and Java all answer both with a 400.</para>
/// </summary>
public sealed class UploadUrlRequestTests
{
    public interface IPingService
    {
        Task<string> PingAsync(string value);
    }

    private sealed class PingService : IPingService
    {
        public Task<string> PingAsync(string value) => Task.FromResult(value);
    }

    private static readonly Schema s_countSchema = new([new Field("count", Int64Type.Default, true)], null);

    [Fact]
    public async Task WrongMethod_IsA400()
    {
        await using var host = await StartHostAsync();
        using var request = new RecordBatch(s_countSchema, [new Int64Array.Builder().Append(2).Build()], 1);

        using var response = await PostAsync(host, await RequestBytesAsync(request, method: "ping"));

        var error = await AssertRpcErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("TypeError", error.ErrorType);
        Assert.Contains("Method mismatch", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("'ping'", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMethod_IsA400()
    {
        await using var host = await StartHostAsync();
        using var request = new RecordBatch(s_countSchema, [new Int64Array.Builder().Append(2).Build()], 1);

        using var response = await PostAsync(host, await RequestBytesAsync(request, method: null));

        var error = await AssertRpcErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("Method mismatch", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedBody_IsA400()
    {
        await using var host = await StartHostAsync();

        using var response = await PostAsync(host, "this is not an Arrow IPC stream"u8.ToArray());

        await AssertRpcErrorAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EmptyBody_IsA400()
    {
        await using var host = await StartHostAsync();

        using var response = await PostAsync(host, []);

        await AssertRpcErrorAsync(response, HttpStatusCode.BadRequest);
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static readonly string s_shaA = new('a', 64);

    [Fact]
    public async Task Capabilities_AdvertiseVersion2()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };
        using var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("2", response.Headers.GetValues("VGI-Upload-URL-Support").Single());
        using var health = await http.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal("2", health.Headers.GetValues("VGI-Upload-URL-Support").Single());

        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = "PingService" });
        var caps = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, caps.UploadUrlVersion);
        Assert.True(caps.UploadUrlSupport);
    }

    [Fact]
    public async Task V2Request_VendsOneUrlPerEntryWithHeaders()
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = "PingService" });
        var shaB = new string('b', 64);

        var urls = await client.RequestUploadUrlsAsync(
            [new UploadRequest(10, s_shaA), new UploadRequest(20, shaB), new UploadRequest(30, s_shaA)],
            TestContext.Current.CancellationToken);

        Assert.Equal(3, urls.Count);
        Assert.All(urls, u => Assert.StartsWith("https://upload.invalid/", u.Upload, StringComparison.Ordinal));
        Assert.Equal([(10L, s_shaA), (20L, shaB), (30L, s_shaA)], host.Provider.Requests);
        Assert.Equal(UploadDigest.HexToBase64(shaB), urls[1].Headers["x-amz-checksum-sha256"]);
        Assert.Equal("20", urls[1].Headers["x-test-size"]);
        Assert.All(urls, u => Assert.False(u.Headers.ContainsKey("Content-Length")));
    }

    private static readonly byte[] s_tokenKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Protocol 3.4 grant columns (STATE_REF_V1.md §10.2): each entry carries the
    /// provider's <c>object_id</c> and a <c>ref</c> sealed to the requesting identity (anonymous
    /// here) over the claimed size and SHA-256.</summary>
    [Fact]
    public async Task V2Request_CarriesObjectIdAndASealedRef()
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = "PingService" });
        var urls = await client.RequestUploadUrlsAsync(
            [new UploadRequest(10, s_shaA), new UploadRequest(20, s_shaA)], TestContext.Current.CancellationToken);

        for (var i = 0; i < urls.Count; i++)
        {
            Assert.StartsWith("object-", urls[i].ObjectId, StringComparison.Ordinal);
            var opened = ArgumentRefs.Open(s_tokenKey, null, urls[i].Ref);
            Assert.Equal(new ArgumentRef(i == 0 ? 10 : 20, s_shaA, urls[i].ObjectId), opened);
            Assert.Throws<Errors.RpcException>(() => ArgumentRefs.Open(s_tokenKey, new AuthIdentity("d", "someone"), urls[i].Ref));
        }
    }

    [Fact]
    public async Task Capabilities_AdvertiseTheUploadCap()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };
        using var health = await http.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(MaxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), health.Headers.GetValues("VGI-Max-Upload-Bytes").Single());
    }

    [Fact]
    public async Task RetiredCountShape_IsRefused()
    {
        await using var host = await StartHostAsync();
        using var request = new RecordBatch(s_countSchema, [new Int64Array.Builder().Append(1).Build()], 1);

        using var response = await PostAsync(host, await RequestBytesAsync(request, method: "__upload_url__"));

        var error = await AssertRpcErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("retired count shape", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("VGI-Upload-URL-Support: 2", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(host.Provider.Requests);
    }

    [Fact]
    public async Task NoUploadsParameter_IsRefusedAsRetiredShape()
    {
        await using var host = await StartHostAsync();
        using var request = new RecordBatch(new Schema([], null), [], 1);

        using var response = await PostAsync(host, await RequestBytesAsync(request, method: "__upload_url__"));

        var error = await AssertRpcErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("retired count shape", error.ErrorMessage, StringComparison.Ordinal);
    }

    public static TheoryData<string, (long Size, string Sha)[]> BadEntries() => new()
    {
        { "'uploads' must have 1-100 entries, got 0", [] },
        { "'uploads' must have 1-100 entries, got 101", Enumerable.Repeat((1L, new string('a', 64)), 101).ToArray() },
        { "uploads[0].size must be an integer >= 1, got 0", [(0L, new string('a', 64))] },
        { "uploads[0].size must be an integer >= 1, got -5", [(-5L, new string('a', 64))] },
        { "uploads[0].sha256 must be 64 lowercase hex characters", [(1L, new string('A', 64))] },
        { "uploads[0].sha256 must be 64 lowercase hex characters", [(1L, new string('a', 63))] },
        { "uploads[1].sha256 must be 64 lowercase hex characters", [(1L, new string('a', 64)), (1L, new string('g', 64))] },
        { "uploads[1].size 1048577 exceeds VGI-Max-Upload-Bytes (1048576)", [(1L, new string('a', 64)), (MaxUploadBytes + 1, new string('a', 64))] },
        // Order per entry: size, then sha256, then the max.
        { "uploads[0].size must be an integer >= 1", [(0L, "bad")] },
    };

    [Theory]
    [MemberData(nameof(BadEntries))]
    public async Task InvalidEntries_AreRefusedBeforeAnyUrlIsMinted(string expected, (long Size, string Sha)[] entries)
    {
        await using var host = await StartHostAsync();

        using var response = await PostAsync(host, await RequestBytesAsync(UploadsBatch(entries), method: "__upload_url__"));

        var error = await AssertRpcErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("ValueError", error.ErrorType);
        Assert.Contains(expected, error.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(host.Provider.Requests);
    }

    [Fact]
    public async Task OversizedRequest_RoundTripsThroughAVerifiedUpload()
    {
        await using var host = await StartHostAsync(storageBacked: true);
        await using var client = NewUploadingClient(host);
        var value = new string('z', 32 * 1024);

        var echoed = await PingAsync(client, value);

        Assert.Equal(value, echoed);
        var put = Assert.Single(host.Storage.Puts);
        Assert.Equal(put.Body.Length, put.ContentLength);
        Assert.Equal("application/octet-stream", put.ContentType);
        Assert.Equal(UploadDigest.HexToBase64(Sha(put.Body)), put.Checksum);
        Assert.Equal([(put.Body.LongLength, Sha(put.Body))], host.Provider.Requests);
    }

    /// <summary>The presigned PUT goes through a bare client: a caller-supplied RPC
    /// <see cref="System.Net.Http.HttpClient"/>'s default <c>Authorization</c> (and any other
    /// default header) reaches the RPC server but never the storage host.</summary>
    [Fact]
    public async Task StoragePut_NeverCarriesTheRpcClientsDefaultHeaders()
    {
        await using var host = await StartHostAsync(storageBacked: true);
        using var rpcHttp = new System.Net.Http.HttpClient { BaseAddress = host.Address };
        rpcHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "rpc-secret-token");
        rpcHttp.DefaultRequestHeaders.TryAddWithoutValidation("X-Caller-Secret", "do-not-forward");
        await using var client = new HttpRpcClient(rpcHttp, new HttpRpcClientOptions
        {
            Protocol = "PingService",
            ExternalLocation = new ClientExternalConfig { UrlValidator = null },
        });
        var value = new string('z', 32 * 1024);

        Assert.Equal(value, await PingAsync(client, value));

        var put = Assert.Single(host.Storage.Puts);
        Assert.Null(put.Authorization);
        Assert.DoesNotContain(put.HeaderNames, h => h.Equals("X-Caller-Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(put.HeaderNames, h => h.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.True(host.RpcSawAuthorization, "the RPC requests themselves should still carry the caller's Authorization");
    }

    [Fact]
    public async Task Capabilities_OmitTheUploadHeaderWithoutAProvider()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapVgiRpc(new RpcServer(typeof(IPingService), new PingService()));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("VGI-Upload-URL-Support"));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UploadWhoseBytesDoNotMatchTheSignedDigest_IsRefusedByStorage()
    {
        await using var host = await StartHostAsync(storageBacked: true);
        host.Storage.CorruptNextPut = true;
        await using var client = NewUploadingClient(host);

        var error = await Assert.ThrowsAsync<Errors.RpcException>(() => PingAsync(client, new string('z', 32 * 1024)));

        Assert.Equal("ExternalUploadFailed", error.ErrorType);
        Assert.Contains("HTTP 400", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredObjectThatNoLongerMatchesThePointer_IsRefusedByTheServer()
    {
        await using var host = await StartHostAsync(storageBacked: true);
        host.Storage.CorruptStoredObjects = true;
        await using var client = NewUploadingClient(host);

        var error = await Assert.ThrowsAsync<Errors.RpcException>(() => PingAsync(client, new string('z', 32 * 1024)));

        Assert.Contains("sha256 mismatch", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestPointerWithoutSha256_IsRefused()
    {
        await using var host = await StartHostAsync(storageBacked: true);
        var schema = new Schema([new Field("value", StringType.Default, false)], null);
        using var inner = new RecordBatch(schema, [new StringArray.Builder().Append("hi").Build()], 1);
        var bytes = await ExternalLocation.SerializeBatchAsync(inner, null, TestContext.Current.CancellationToken);
        var location = host.Storage.Put(bytes);
        var (pointer, metadata) = ExternalLocation.MakePointerBatch(schema, location);
        using (pointer)
        {
            metadata[MetadataKeys.Method] = "ping";
            metadata[MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion;
            using var stream = new MemoryStream();
            await using (var writer = new WireWriter(stream, schema))
            {
                await writer.WriteBatchAsync(new AnnotatedBatch(pointer, metadata), TestContext.Current.CancellationToken);
            }

            using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };
            using var content = new ByteArrayContent(stream.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue(RpcHttpEndpoints.ArrowContentType);
            using var response = await http.PostAsync("/PingService/ping", content, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            using var reader = new WireReader(new MemoryStream(body));
            await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
            var batch = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(batch);
            using (batch.Batch)
            {
                var error = RpcErrorDecoder.Decode(batch);
                Assert.Contains("missing vgi_rpc.location.sha256", error.ErrorMessage, StringComparison.Ordinal);
            }
        }

        Assert.Equal(0, host.Storage.Gets);
    }

    [Theory]
    [InlineData("true", "Server upload protocol is too old: it advertises VGI-Upload-URL-Support v1 and this client needs 2")]
    [InlineData("3", "Server upload protocol is newer than this client")]
    [InlineData("false", "does not advertise upload_url_support")]
    public async Task Client_RefusesToUploadWithoutAVersion2Server(string advertised, string expected)
    {
        using var http = new System.Net.Http.HttpClient(new OversizeRejectingHandler(advertised))
        {
            BaseAddress = new Uri("http://127.0.0.1"),
        };
        await using var client = new HttpRpcClient(http, new HttpRpcClientOptions
        {
            Protocol = "PingService",
            ExternalLocation = new ClientExternalConfig { UrlValidator = null },
        });

        var error = await Assert.ThrowsAsync<Errors.RpcException>(() => PingAsync(client, "x"));

        Assert.Contains(expected, error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionHeader_Parses()
    {
        Assert.Equal(0, HttpRpcClient.UploadUrlVersionHeader(null));
        Assert.Equal(0, HttpRpcClient.UploadUrlVersionHeader("false"));
        Assert.Equal(1, HttpRpcClient.UploadUrlVersionHeader("true"));
        Assert.Equal(2, HttpRpcClient.UploadUrlVersionHeader("2"));
        Assert.Equal(0, HttpRpcClient.UploadUrlVersionHeader("banana"));
        Assert.Equal(0, HttpRpcClient.UploadUrlVersionHeader("02"));
        Assert.Equal(0, HttpRpcClient.UploadUrlVersionHeader("-2"));
    }

    private const long MaxUploadBytes = 1 << 20;

    private static RecordBatch UploadsBatch((long Size, string Sha)[] entries)
    {
        var entryType = new StructType([
            new Field("size", Int64Type.Default, false),
            new Field("sha256", StringType.Default, false),
        ]);
        var listType = new ListType(new Field("item", entryType, true));
        var sizes = new Int64Array.Builder();
        var hashes = new StringArray.Builder();
        foreach (var (size, sha) in entries)
        {
            sizes.Append(size);
            hashes.Append(sha);
        }

        var structs = new StructArray(entryType, entries.Length, [sizes.Build(), hashes.Build()], ArrowBuffer.Empty, 0);
        var offsets = new ArrowBuffer.Builder<int>().Append(0).Append(entries.Length).Build();
        var list = new ListArray(listType, 1, offsets, structs, ArrowBuffer.Empty, 0);
        return new RecordBatch(new Schema([new Field("uploads", listType, false)], null), [list], 1);
    }

    private static HttpRpcClient NewUploadingClient(TestHost host) =>
        new(host.Address, new HttpRpcClientOptions
        {
            Protocol = "PingService",
            ExternalLocation = new ClientExternalConfig { UrlValidator = null },
        });

    private static async Task<string?> PingAsync(HttpRpcClient client, string value)
    {
        var schema = new Schema([new Field("value", StringType.Default, false)], null);
        using var parameters = new RecordBatch(schema, [new StringArray.Builder().Append(value).Build()], 1);
        var result = await client.CallUnaryAsync("ping", parameters, cancellationToken: TestContext.Current.CancellationToken);
        using (result.Batch)
        {
            return ((StringArray)result.Batch.Column(0)).GetString(0);
        }
    }

    /// <summary>Answers every POST with 413 and OPTIONS with the given upload-URL capability.</summary>
    private sealed class OversizeRejectingHandler(string uploadSupport) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(request.Method == HttpMethod.Options
                ? HttpStatusCode.OK
                : HttpStatusCode.RequestEntityTooLarge)
            {
                Content = new ByteArrayContent([]),
            };
            response.Headers.TryAddWithoutValidation(RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader, "true");
            response.Headers.TryAddWithoutValidation("VGI-Upload-URL-Support", uploadSupport);
            response.Headers.TryAddWithoutValidation("VGI-Max-Request-Bytes", "1");
            Assert.NotEqual("/__upload_url__/init", request.RequestUri!.AbsolutePath);
            return Task.FromResult(response);
        }
    }

    private static async Task<byte[]> RequestBytesAsync(RecordBatch batch, string? method)
    {
        var metadata = new Dictionary<string, string> { [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion };
        if (method is not null)
        {
            metadata[MetadataKeys.Method] = method;
        }

        using var stream = new MemoryStream();
        await using (var writer = new WireWriter(stream, batch.Schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, metadata), TestContext.Current.CancellationToken);
        }

        return stream.ToArray();
    }

    private static async Task<HttpResponseMessage> PostAsync(TestHost host, byte[] body)
    {
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(RpcHttpEndpoints.ArrowContentType);
        return await http.PostAsync("/__upload_url__/init", content, TestContext.Current.CancellationToken);
    }

    private static async Task<Errors.RpcException> AssertRpcErrorAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var reader = new WireReader(body);
        await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
        var batch = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(batch);
        using (batch.Batch)
        {
            return RpcErrorDecoder.Decode(batch);
        }
    }

    private static async Task<TestHost> StartHostAsync(bool storageBacked = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var storage = new VerifyingStorage();
        var provider = new RecordingUploadUrlProvider(storageBacked ? storage : null);
        var rpcSawAuthorization = new StrongBox<bool>();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/store") && context.Request.Headers.Authorization.Count > 0)
            {
                rpcSawAuthorization.Value = true;
            }

            await next(context);
        });
        if (storageBacked)
        {
            storage.Map(app);
        }

        app.MapVgiRpc(
            new RpcServer(typeof(IPingService), new PingService()),
            tokenKey: s_tokenKey,
            externalization: new ExternalizationOptions
            {
                UploadUrlProvider = provider,
                MaxUploadBytes = MaxUploadBytes,
                MaxRequestBytes = storageBacked ? 4096 : null,
                External = storageBacked ? new ServerExternalConfig { UrlValidator = null } : null,
            });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        storage.BaseAddress = address.TrimEnd('/');
        return new TestHost(app, new Uri(address), provider, storage, rpcSawAuthorization);
    }

    /// <summary>Records each (size, sha256) it is asked for. Without storage it vends
    /// <c>.invalid</c> URLs; with storage, URLs into <see cref="VerifyingStorage"/>.</summary>
    private sealed class RecordingUploadUrlProvider(VerifyingStorage? storage) : IUploadUrlProvider
    {
        private int _next;
        private readonly List<(long, string)> _requests = [];

        public IReadOnlyList<(long, string)> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public Task<External.UploadUrl> GenerateUploadUrlAsync(long size, string sha256Hex, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add((size, sha256Hex));
            }

            var n = Interlocked.Increment(ref _next);
            var headers = new Dictionary<string, string>
            {
                ["x-amz-checksum-sha256"] = UploadDigest.HexToBase64(sha256Hex),
                ["x-test-size"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // Like the blob vendor's required_headers: the server must drop it.
                ["Content-Length"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            return Task.FromResult(storage is null
                ? new External.UploadUrl($"https://upload.invalid/{n}", $"https://download.invalid/{n}", DateTimeOffset.UtcNow.AddHours(1), headers) { ObjectId = $"object-{n}" }
                : new External.UploadUrl($"{storage.BaseAddress}/store/{n}?size={size}", $"{storage.BaseAddress}/store/{n}", DateTimeOffset.UtcNow.AddHours(1), headers) { ObjectId = $"{n}" });
        }
    }

    /// <summary>In-process storage that verifies a PUT the way R2 does: the body's length must
    /// equal the signed size and its SHA-256 the <c>x-amz-checksum-sha256</c> header, else 400.</summary>
    private sealed class VerifyingStorage
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _objects = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<StoredPut> _puts = new();
        private int _gets;

        public string BaseAddress { get; set; } = "";

        public bool CorruptNextPut { get; set; }

        public bool CorruptStoredObjects { get; set; }

        public IReadOnlyList<StoredPut> Puts => [.. _puts];

        public int Gets => Volatile.Read(ref _gets);

        public string Put(byte[] data)
        {
            var id = "direct-" + Guid.NewGuid().ToString("N");
            _objects[id] = data;
            return $"{BaseAddress}/store/{id}";
        }

        public void Map(WebApplication app)
        {
            app.MapPut("/store/{id}", async (string id, long size, HttpContext context) =>
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer);
                var body = buffer.ToArray();
                if (CorruptNextPut)
                {
                    CorruptNextPut = false;
                    body[0] ^= 0xFF;
                }

                var checksum = context.Request.Headers["x-amz-checksum-sha256"].ToString();
                if (body.Length != size || Convert.ToBase64String(SHA256.HashData(body)) != checksum)
                {
                    return Results.BadRequest("BadDigest");
                }

                _puts.Enqueue(new StoredPut(body, context.Request.ContentLength, context.Request.ContentType, checksum,
                    context.Request.Headers.Authorization.Count > 0 ? context.Request.Headers.Authorization.ToString() : null,
                    [.. context.Request.Headers.Keys]));
                if (CorruptStoredObjects)
                {
                    body = [.. body];
                    body[^1] ^= 0xFF;
                }

                _objects[id] = body;
                return Results.Ok();
            });
            app.MapGet("/store/{id}", (string id) =>
            {
                Interlocked.Increment(ref _gets);
                return _objects.TryGetValue(id, out var data)
                    ? Results.Bytes(data, "application/octet-stream")
                    : Results.NotFound();
            });
        }
    }

    private sealed record StoredPut(byte[] Body, long? ContentLength, string? ContentType, string Checksum,
        string? Authorization, IReadOnlyList<string> HeaderNames);

    private sealed class TestHost(WebApplication app, Uri address, RecordingUploadUrlProvider provider, VerifyingStorage storage, StrongBox<bool> rpcSawAuthorization) : IAsyncDisposable
    {
        public bool RpcSawAuthorization => rpcSawAuthorization.Value;

        public Uri Address { get; } = address;

        public RecordingUploadUrlProvider Provider { get; } = provider;

        public VerifyingStorage Storage { get; } = storage;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
