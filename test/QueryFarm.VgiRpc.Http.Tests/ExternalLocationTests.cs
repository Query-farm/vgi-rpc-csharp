using System.Net;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>Direct unit coverage for <see cref="ExternalLocation"/>/<see cref="ExternalFetch"/>/
/// <see cref="RequestCap"/> — the wire end-to-end behavior (externalization thresholds, request
/// pointer resolution across every dispatch path, fetch security, upload-URL routes) is covered by
/// the canonical TestExternalLocation/TestExternalizedResponseCap/TestExternalInputRoutes/
/// TestExternalFetchFailures/TestExternalFetchSecurity/TestExternalStorageUrlPair groups imported
/// into test_csharp_conformance.py (see docs/roadmap.md M13).</summary>
public class ExternalLocationTests
{
    private static readonly Schema s_schema = new([new Field("value", Int64Type.Default, nullable: false)], metadata: null);

    private static RecordBatch MakeBatch(params long[] values)
    {
        var builder = new Int64Array.Builder();
        builder.AppendRange(values);
        return new RecordBatch(s_schema, [builder.Build()], values.Length);
    }

    private sealed class FakeStorage : IExternalStorage
    {
        public List<(byte[] Data, string? ContentEncoding)> Uploads { get; } = [];

        public string? NextUrl { get; set; }

        public Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
        {
            Uploads.Add((data, contentEncoding));
            return Task.FromResult(NextUrl ?? $"https://storage.example/{Uploads.Count}");
        }
    }

    [Fact]
    public void GetTotalBufferSize_SumsColumnBuffers()
    {
        var batch = MakeBatch(1, 2, 3, 4);
        Assert.True(batch.GetTotalBufferSize() > 0);
    }

    [Fact]
    public void GetTotalBufferSize_EmptyBatch_IsZeroOrTiny()
    {
        var batch = MakeBatch();
        // A zero-row batch's validity/data buffers are empty — total size should be minimal.
        Assert.True(batch.GetTotalBufferSize() < 64);
    }

    [Fact]
    public void MakePointerBatch_ProducesZeroRowBatchWithLocationMetadata()
    {
        var (batch, metadata) = ExternalLocation.MakePointerBatch(s_schema, "https://example.com/blob/1", sha256: "abcd");
        Assert.Equal(0, batch.Length);
        Assert.Equal("https://example.com/blob/1", metadata[QueryFarm.VgiRpc.Wire.MetadataKeys.Location]);
        Assert.Equal("abcd", metadata[QueryFarm.VgiRpc.Wire.MetadataKeys.LocationSha256]);
        Assert.True(ExternalLocation.IsExternalLocationBatch(batch, metadata));
    }

    [Fact]
    public void IsExternalLocationBatch_NonZeroRowBatch_IsFalse()
    {
        var batch = MakeBatch(1);
        var metadata = new Dictionary<string, string> { [QueryFarm.VgiRpc.Wire.MetadataKeys.Location] = "https://example.com" };
        Assert.False(ExternalLocation.IsExternalLocationBatch(batch, metadata));
    }

    [Fact]
    public void IsExternalLocationBatch_LogBatch_IsFalse()
    {
        var batch = MakeBatch();
        var metadata = new Dictionary<string, string>
        {
            [QueryFarm.VgiRpc.Wire.MetadataKeys.Location] = "https://example.com",
            [QueryFarm.VgiRpc.Wire.MetadataKeys.LogLevel] = "INFO",
        };
        Assert.False(ExternalLocation.IsExternalLocationBatch(batch, metadata));
    }

    [Fact]
    public void PredictExternalizeBytes_BelowThreshold_IsZero()
    {
        var batch = MakeBatch(1, 2, 3);
        var config = new ServerExternalConfig { Storage = new FakeStorage(), ExternalizeThresholdBytes = 1_000_000 };
        Assert.Equal(0, ExternalLocation.PredictExternalizeBytes(batch, config));
    }

    [Fact]
    public void PredictExternalizeBytes_AboveThreshold_IsPositive()
    {
        var batch = MakeBatch(Enumerable.Range(0, 1000).Select(i => (long)i).ToArray());
        var config = new ServerExternalConfig { Storage = new FakeStorage(), ExternalizeThresholdBytes = 8 };
        Assert.True(ExternalLocation.PredictExternalizeBytes(batch, config) > 0);
    }

    [Fact]
    public void PredictExternalizeBytes_NoStorage_IsZero()
    {
        var batch = MakeBatch(Enumerable.Range(0, 1000).Select(i => (long)i).ToArray());
        var config = new ServerExternalConfig { Storage = null, ExternalizeThresholdBytes = 8 };
        Assert.Equal(0, ExternalLocation.PredictExternalizeBytes(batch, config));
    }

    [Fact]
    public async Task MaybeExternalizeAsync_BelowThreshold_ReturnsBatchUnchanged()
    {
        var batch = MakeBatch(1, 2, 3);
        var storage = new FakeStorage();
        var config = new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 1_000_000 };
        var (resultBatch, metadata, externalBytes) = await ExternalLocation.MaybeExternalizeAsync(batch, null, config);
        Assert.Same(batch, resultBatch);
        Assert.Null(metadata);
        Assert.Equal(0, externalBytes);
        Assert.Empty(storage.Uploads);
    }

    [Fact]
    public async Task MaybeExternalizeAsync_AboveThreshold_UploadsAndReturnsPointer()
    {
        var batch = MakeBatch(Enumerable.Range(0, 1000).Select(i => (long)i).ToArray());
        var storage = new FakeStorage { NextUrl = "https://storage.example/blob/xyz" };
        var config = new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 8 };
        var (resultBatch, metadata, externalBytes) = await ExternalLocation.MaybeExternalizeAsync(batch, null, config);
        Assert.Equal(0, resultBatch.Length);
        Assert.NotNull(metadata);
        Assert.Equal("https://storage.example/blob/xyz", metadata![QueryFarm.VgiRpc.Wire.MetadataKeys.Location]);
        Assert.True(metadata.ContainsKey(QueryFarm.VgiRpc.Wire.MetadataKeys.LocationSha256));
        Assert.True(externalBytes > 0);
        Assert.Single(storage.Uploads);
        Assert.Null(storage.Uploads[0].ContentEncoding); // no Compression configured
    }

    [Fact]
    public async Task MaybeExternalizeAsync_ZeroRowBatch_NeverExternalizes()
    {
        var batch = MakeBatch();
        var storage = new FakeStorage();
        var config = new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 0 };
        var (resultBatch, _, externalBytes) = await ExternalLocation.MaybeExternalizeAsync(batch, null, config);
        Assert.Same(batch, resultBatch);
        Assert.Equal(0, externalBytes);
        Assert.Empty(storage.Uploads);
    }

    [Fact]
    public async Task MaybeExternalizeAsync_WithZstdCompression_SetsContentEncoding()
    {
        var batch = MakeBatch(Enumerable.Range(0, 1000).Select(i => (long)i).ToArray());
        var storage = new FakeStorage();
        var config = new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 8, Compression = new Compression() };
        await ExternalLocation.MaybeExternalizeAsync(batch, null, config);
        Assert.Single(storage.Uploads);
        Assert.Equal("zstd", storage.Uploads[0].ContentEncoding);
    }

    [Fact]
    public async Task ResolveAsync_NonPointerBatch_ReturnsUnchanged()
    {
        var batch = MakeBatch(1, 2, 3);
        var config = new ClientExternalConfig { UrlValidator = null };
        var (resolved, metadata) = await ExternalLocation.ResolveAsync(batch, null, config);
        Assert.Same(batch, resolved);
        Assert.Null(metadata);
    }

    [Fact]
    public async Task ResolveAsync_NullConfig_ReturnsUnchanged()
    {
        var batch = MakeBatch(1, 2, 3);
        var (resolved, metadata) = await ExternalLocation.ResolveAsync(batch, null, null);
        Assert.Same(batch, resolved);
        Assert.Null(metadata);
    }

    [Fact]
    public async Task RoundTrip_ExternalizeThenResolve_RecoversOriginalData()
    {
        var original = MakeBatch(Enumerable.Range(0, 1000).Select(i => (long)i).ToArray());
        var storage = new InMemoryHttpStorage();
        using var server = await LoopbackServer.StartAsync(storage.HandleAsync);
        storage.BaseUrl = server.BaseUrl;

        var serverConfig = new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 8 };
        var (pointerBatch, pointerMetadata, _) = await ExternalLocation.MaybeExternalizeAsync(original, null, serverConfig);
        Assert.Equal(0, pointerBatch.Length);

        var clientConfig = new ClientExternalConfig { UrlValidator = null };
        var (resolvedBatch, resolvedMetadata) = await ExternalLocation.ResolveAsync(pointerBatch, pointerMetadata, clientConfig);

        Assert.Equal(original.Length, resolvedBatch.Length);
        var resolvedColumn = (Int64Array)resolvedBatch.Column(0);
        var originalColumn = (Int64Array)original.Column(0);
        Assert.Equal(originalColumn.Values.ToArray(), resolvedColumn.Values.ToArray());
        Assert.Null(resolvedMetadata); // the uploaded stream carried no extra metadata of its own
    }

    [Fact]
    public async Task ResolveAsync_ChecksumMismatch_Throws()
    {
        var original = MakeBatch(1, 2, 3);
        var storage = new InMemoryHttpStorage();
        using var server = await LoopbackServer.StartAsync(storage.HandleAsync);
        storage.BaseUrl = server.BaseUrl;

        var url = await storage.UploadAsync(await ExternalLocation.SerializeBatchAsync(original, null), original.Schema, null, default);
        var (pointerBatch, pointerMetadata) = ExternalLocation.MakePointerBatch(original.Schema, url, sha256: new string('0', 64));

        var clientConfig = new ClientExternalConfig { UrlValidator = null };
        var exc = await Assert.ThrowsAsync<RpcException>(() => ExternalLocation.ResolveAsync(pointerBatch, pointerMetadata, clientConfig));
        Assert.Contains("checksum", exc.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HttpsOnlyValidator_RejectsHttp()
    {
        Assert.Throws<ArgumentException>(() => ExternalFetch.HttpsOnlyValidator("http://example.com/blob"));
    }

    [Fact]
    public void HttpsOnlyValidator_AcceptsHttps()
    {
        ExternalFetch.HttpsOnlyValidator("https://example.com/blob"); // does not throw
    }

    [Fact]
    public void HttpsOnlyValidator_RejectsMalformedUrl()
    {
        Assert.Throws<ArgumentException>(() => ExternalFetch.HttpsOnlyValidator("not a url"));
    }

    [Fact]
    public void RedactUrl_StripsQueryAndFragment()
    {
        var redacted = ExternalFetch.RedactUrl("https://example.com/blob/1?X-Amz-Signature=secret#frag");
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("X-Amz-Signature", redacted);
        Assert.Contains("example.com", redacted);
        Assert.Contains("/blob/1", redacted);
    }

    [Fact]
    public void RedactUrl_InvalidUrl_ReturnsPlaceholder()
    {
        Assert.Equal("<invalid-url>", ExternalFetch.RedactUrl("not a url"));
    }

    [Fact]
    public async Task FetchUrlAsync_EncodedCapExceeded_Throws()
    {
        using var server = await LoopbackServer.StartAsync(async ctx =>
        {
            var body = new byte[10_000];
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
        });

        var config = new FetchConfig { MaxFetchBytes = 100 };
        var exc = await Assert.ThrowsAsync<RpcException>(() => ExternalFetch.FetchUrlAsync($"{server.BaseUrl}/blob", config, null, default));
        Assert.Contains("max_fetch_bytes", exc.Message);
    }

    [Fact]
    public async Task FetchUrlAsync_RedirectLoop_ThrowsAfterMaxRedirects()
    {
        using var server = await LoopbackServer.StartAsync(ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Add("Location", ctx.Request.Url!.AbsoluteUri);
            return Task.CompletedTask;
        });

        var config = new FetchConfig { MaxRedirects = 2 };
        var exc = await Assert.ThrowsAsync<RpcException>(() => ExternalFetch.FetchUrlAsync($"{server.BaseUrl}/loop", config, null, default));
        Assert.Contains("redirect limit", exc.Message);
    }

    [Fact]
    public async Task FetchUrlAsync_ValidatorRejectsUrl_ThrowsBeforeFetching()
    {
        var fetched = false;
        using var server = await LoopbackServer.StartAsync(ctx =>
        {
            fetched = true;
            return Task.CompletedTask;
        });

        var config = new FetchConfig();
        var exc = await Assert.ThrowsAsync<RpcException>(() =>
            ExternalFetch.FetchUrlAsync($"{server.BaseUrl}/blob", config, _ => throw new ArgumentException("rejected"), default));
        Assert.Contains("URL rejected", exc.Message);
        Assert.False(fetched);
    }

    [Fact]
    public async Task FetchUrlAsync_404_ThrowsRpcException()
    {
        using var server = await LoopbackServer.StartAsync(ctx =>
        {
            ctx.Response.StatusCode = 404;
            return Task.CompletedTask;
        });

        var config = new FetchConfig();
        var exc = await Assert.ThrowsAsync<RpcException>(() => ExternalFetch.FetchUrlAsync($"{server.BaseUrl}/missing", config, null, default));
        Assert.Contains("404", exc.Message);
    }

    [Fact]
    public void RequestCap_Enforce_NoLimit_ReturnsRawBody()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream([1, 2, 3]);
        context.Request.Body = body;
        var stream = RequestCap.Enforce(context.Request, null);
        Assert.Same(body, stream);
    }

    [Fact]
    public void RequestCap_Enforce_DeclaredContentLengthExceedsCap_ThrowsImmediately()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[100]);
        context.Request.ContentLength = 100;
        var exc = Assert.Throws<RequestTooLargeException>(() => RequestCap.Enforce(context.Request, 50));
        Assert.Equal(100, exc.ActualOrObservedBytes);
        Assert.Equal(50, exc.MaxRequestBytes);
    }

    [Fact]
    public async Task RequestCap_Enforce_ChunkedBodyExceedsCap_ThrowsWhileReading()
    {
        var context = new DefaultHttpContext();
        // No declared Content-Length — mirrors a chunked-transfer-encoded request.
        context.Request.Body = new MemoryStream(new byte[100]);
        var stream = RequestCap.Enforce(context.Request, 50);
        var buffer = new byte[100];
        await Assert.ThrowsAsync<RequestTooLargeException>(async () =>
        {
            int totalRead;
            do
            {
                totalRead = await stream.ReadAsync(buffer);
            }
            while (totalRead > 0);
        });
    }

    [Fact]
    public async Task RequestCap_Enforce_UnderCap_ReadsFully()
    {
        var context = new DefaultHttpContext();
        var data = new byte[40];
        Random.Shared.NextBytes(data);
        context.Request.Body = new MemoryStream(data);
        var stream = RequestCap.Enforce(context.Request, 50);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        Assert.Equal(data, output.ToArray());
    }

    /// <summary>Minimal in-memory <see cref="IExternalStorage"/> backed by a real loopback HTTP
    /// server — lets <see cref="ExternalFetch.FetchUrlAsync"/> (a real <see cref="HttpClient"/>)
    /// round-trip against uploaded bytes without any external dependency.</summary>
    private sealed class InMemoryHttpStorage : IExternalStorage
    {
        private readonly Dictionary<string, (byte[] Data, string? ContentEncoding)> _blobs = [];
        private int _nextId;

        public string BaseUrl { get; set; } = "";

        public Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
        {
            var id = (++_nextId).ToString();
            _blobs[id] = (data, contentEncoding);
            return Task.FromResult($"{BaseUrl}/blob/{id}");
        }

        public async Task HandleAsync(HttpListenerContext ctx)
        {
            var id = ctx.Request.Url!.Segments[^1];
            if (!_blobs.TryGetValue(id, out var entry))
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            if (entry.ContentEncoding is not null)
            {
                ctx.Response.Headers.Add("Content-Encoding", entry.ContentEncoding);
            }

            ctx.Response.ContentLength64 = entry.Data.Length;
            await ctx.Response.OutputStream.WriteAsync(entry.Data);
        }
    }

    /// <summary>A minimal loopback <see cref="HttpListener"/>-based server for fetch tests — real
    /// sockets/HTTP framing, no mocking of <see cref="HttpClient"/> internals.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        private LoopbackServer(HttpListener listener, string baseUrl, Func<HttpListenerContext, Task> handler)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(handler, _cts.Token));
        }

        public string BaseUrl { get; }

        public static Task<LoopbackServer> StartAsync(Func<HttpListenerContext, Task> handler)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    return Task.FromResult(new LoopbackServer(listener, $"http://127.0.0.1:{port}", handler));
                }
                catch (HttpListenerException)
                {
                    // Port already in use — retry with a different one.
                }
            }

            throw new InvalidOperationException("Could not bind a loopback test server after 10 attempts.");
        }

        private async Task AcceptLoopAsync(Func<HttpListenerContext, Task> handler, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler(ctx).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        ctx.Response.StatusCode = 500;
                    }
                    finally
                    {
                        ctx.Response.OutputStream.Close();
                    }
                }, cancellationToken);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }
    }
}
