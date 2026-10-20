using System.Net;
using System.Net.Http.Headers;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// <c>POST {prefix}/__upload_url__/init</c> reads its request with the standard framing. It used
/// to read only the first batch's columns: the request's <c>vgi_rpc.method</c> was never checked,
/// and any failure to parse the body fell through to <c>count = 1</c>, so a request meant for
/// another method -- or not an Arrow request at all -- was answered with fresh upload URLs. The
/// reference (<c>_UploadUrlResource.on_post</c>), Go, Rust and Java all answer both with a 400.
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

    [Fact]
    public async Task WithCount_VendsThatManyUrls()
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = "PingService" });

        var urls = await client.RequestUploadUrlsAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal(3, urls.Count);
        Assert.All(urls, u => Assert.StartsWith("https://upload.invalid/", u.Upload, StringComparison.Ordinal));
    }

    /// <summary>An absent count is 1, as in the reference's <c>kwargs.get("count", 1)</c>.</summary>
    [Fact]
    public async Task WithoutCount_VendsOneUrl()
    {
        await using var host = await StartHostAsync();
        using var request = new RecordBatch(new Schema([], null), [], 1);

        using var response = await PostAsync(host, await RequestBytesAsync(request, method: "__upload_url__"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var reader = new WireReader(body);
        await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
        var batch = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(batch);
        using (batch.Batch)
        {
            Assert.Null(batch.GetMetadata(MetadataKeys.LogLevel));
            Assert.Equal(1, batch.Batch.Length);
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

    private static async Task<TestHost> StartHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(
            new RpcServer(typeof(IPingService), new PingService()),
            externalization: new ExternalizationOptions { UploadUrlProvider = new FixedUploadUrlProvider() });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHost(app, new Uri(address));
    }

    private sealed class FixedUploadUrlProvider : IUploadUrlProvider
    {
        private int _next;

        public Task<External.UploadUrl> GenerateUploadUrlAsync(Schema schema, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _next);
            return Task.FromResult(new External.UploadUrl(
                $"https://upload.invalid/{n}", $"https://download.invalid/{n}", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
