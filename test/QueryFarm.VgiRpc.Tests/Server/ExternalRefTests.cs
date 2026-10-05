using System.Security.Cryptography;
using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

/// <summary>
/// <see cref="ExternalRef"/>, <see cref="ExternalLocation.PublishExternalAsync"/>, and the
/// byte-stream dispatcher answering a unary call through
/// <see cref="ICallContext.RespondWithExternalRef"/> -- the port of the reference's
/// <c>tests/test_external_ref.py</c>. The client here resolves nothing (no external-location
/// config), so each pointer reaches the test exactly as the server wrote it.
/// </summary>
public sealed class ExternalRefTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly Schema s_params = new([new Field("value", StringType.Default, nullable: false)], null);

    public interface IRefService
    {
        Task<string> FetchAsync(string value, ICallContext? ctx = null);

        Task<long> PlainAsync(long value);

        Task<RpcStream<NoopProducer>> StreamAsync(ICallContext? ctx = null);
    }

    public sealed class NoopProducer : ProducerState
    {
        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            output.Finish();
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers <c>fetch</c> with <see cref="Reference"/> after one log line, returning
    /// <see langword="null"/> as the ignored placeholder -- which an ordinary return of a
    /// non-nullable string would refuse.</summary>
    private sealed class RefService : IRefService
    {
        public ExternalRef Reference { get; set; } = new("https://storage.invalid/published/1", Digest);

        public Task<string> FetchAsync(string value, ICallContext? ctx = null)
        {
            ctx!.EmitLog(Logging.VgiLogLevel.Info, "answering with a ref");
            ctx.RespondWithExternalRef(Reference);
            return Task.FromResult<string>(null!);
        }

        public Task<long> PlainAsync(long value) => Task.FromResult(value);

        public Task<RpcStream<NoopProducer>> StreamAsync(ICallContext? ctx = null)
        {
            ctx!.RespondWithExternalRef(Reference);
            return Task.FromResult(new RpcStream<NoopProducer>(new Schema([new Field("v", Int64Type.Default, false)], null), new NoopProducer()));
        }
    }

    // ---------------------------------------------------------------- ExternalRef validation

    [Fact]
    public void ExternalRef_KeepsUrlAndDigest()
    {
        var reference = new ExternalRef("https://example.com/x", Digest);
        Assert.Equal("https://example.com/x", reference.Url);
        Assert.Equal(Digest, reference.Sha256);
        Assert.Equal(reference, new ExternalRef("https://example.com/x", Digest));
    }

    [Fact]
    public void ExternalRef_DigestIsOptional() =>
        Assert.Null(new ExternalRef("https://example.com/x").Sha256);

    [Fact]
    public void ExternalRef_RejectsEmptyUrl() =>
        Assert.Throws<ArgumentException>(() => new ExternalRef(""));

    [Theory]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void ExternalRef_RejectsMalformedDigest(string sha256) =>
        Assert.Throws<ArgumentException>(() => new ExternalRef("https://example.com/x", sha256));

    [Fact]
    public void ExternalRef_PointerBatch_OmitsDigestKeyWhenAbsent()
    {
        var schema = new Schema([new Field("result", StringType.Default, false)], null);
        var (withBatch, withMetadata) = new ExternalRef("https://example.com/x", Digest).PointerBatch(schema);
        var (withoutBatch, withoutMetadata) = new ExternalRef("https://example.com/x").PointerBatch(schema);
        using (withBatch)
        using (withoutBatch)
        {
            Assert.Equal(0, withBatch.Length);
            Assert.Equal(Digest, withMetadata[MetadataKeys.LocationSha256]);
            Assert.Equal("https://example.com/x", withoutMetadata[MetadataKeys.Location]);
            Assert.False(withoutMetadata.ContainsKey(MetadataKeys.LocationSha256));
        }
    }

    // ---------------------------------------------------------------- PublishExternalAsync

    [Fact]
    public async Task Publish_UploadsOnce_AndTheDigestCoversTheRawStream()
    {
        var storage = new CapturingStorage();
        using var batch = ResultBatch("hello");
        var reference = await ExternalLocation.PublishExternalAsync(batch, storage, cancellationToken: TestContext.Current.CancellationToken);

        var upload = Assert.Single(storage.Uploads);
        Assert.Null(upload.ContentEncoding);
        Assert.Equal(upload.Url, reference.Url);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(upload.Data)), reference.Sha256);
        Assert.Equal("hello", await ReadSingleStringAsync(upload.Data));

        // Byte-for-byte what the per-call externalizer uploads for the same result.
        var perCall = await ExternalLocation.SerializeBatchAsync(batch, metadata: null, TestContext.Current.CancellationToken);
        Assert.Equal(perCall, upload.Data);
    }

    [Fact]
    public async Task Publish_WithoutDigest_LeavesSha256Null()
    {
        var storage = new CapturingStorage();
        using var batch = ResultBatch("hello");
        var reference = await ExternalLocation.PublishExternalAsync(batch, storage, includeSha256: false, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(reference.Sha256);
        Assert.Single(storage.Uploads);
    }

    [Fact]
    public async Task Publish_Compresses_AndHashesTheUncompressedBytes()
    {
        var storage = new CapturingStorage();
        using var batch = ResultBatch("compressed");
        var reference = await ExternalLocation.PublishExternalAsync(batch, storage, new Compression(), cancellationToken: TestContext.Current.CancellationToken);

        var upload = Assert.Single(storage.Uploads);
        Assert.Equal("zstd", upload.ContentEncoding);
        using var decompressor = new ZstdSharp.Decompressor();
        var raw = decompressor.Unwrap(upload.Data).ToArray();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), reference.Sha256);
        Assert.Equal("compressed", await ReadSingleStringAsync(raw));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Publish_RequiresExactlyOneRow(int rows)
    {
        var builder = new StringArray.Builder();
        for (var i = 0; i < rows; i++)
        {
            builder.Append("x");
        }

        using var batch = new RecordBatch(ResultSchema, [builder.Build()], rows);
        var storage = new CapturingStorage();
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalLocation.PublishExternalAsync(batch, storage, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(storage.Uploads);
    }

    // ---------------------------------------------------------------- dispatch

    [Fact]
    public async Task Unary_RespondsWithThePointer_WithoutStorageConfigured()
    {
        var service = new RefService();
        var logs = new List<string>();
        var (client, server, serverTransport) = Setup(service, externalConfig: null, logs);
        await using (client)
        {
            var pointer = await CallFetchAsync(client, server, serverTransport);
            using (pointer.Batch)
            {
                Assert.True(ExternalLocation.IsExternalLocationBatch(pointer.Batch, pointer.Metadata));
                Assert.Equal("result", pointer.Batch.Schema.GetFieldByIndex(0).Name);
                Assert.Equal(service.Reference.Url, pointer.Metadata![MetadataKeys.Location]);
                Assert.Equal(Digest, pointer.Metadata[MetadataKeys.LocationSha256]);
            }

            Assert.Equal(["answering with a ref"], logs);
        }
    }

    [Fact]
    public async Task Unary_RefIsNeverReUploaded_EvenBelowAnyThreshold()
    {
        var storage = new CapturingStorage();
        var service = new RefService { Reference = new ExternalRef("https://storage.invalid/published/no-digest") };
        var (client, server, serverTransport) = Setup(service, new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 1 });
        await using (client)
        {
            var pointer = await CallFetchAsync(client, server, serverTransport);
            using (pointer.Batch)
            {
                Assert.Equal(service.Reference.Url, pointer.Metadata![MetadataKeys.Location]);
                Assert.False(pointer.Metadata.ContainsKey(MetadataKeys.LocationSha256), "a ref without a digest must not carry one");
            }

            Assert.Empty(storage.Uploads);

            // An ordinary method on the same server still externalizes as configured.
            var serve = server.ServeOneAsync(serverTransport, TestContext.Current.CancellationToken);
            using var request = new RecordBatch(new Schema([new Field("value", Int64Type.Default, false)], null), [new Int64Array.Builder().Append(7).Build()], 1);
            var plain = await client.CallUnaryAsync("plain", request, cancellationToken: TestContext.Current.CancellationToken);
            plain.Batch.Dispose();
            Assert.True(await serve.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Single(storage.Uploads);
        }
    }

    [Fact]
    public async Task Stream_RefusesARef()
    {
        var (client, server, serverTransport) = Setup(new RefService(), externalConfig: null);
        await using (client)
        {
            var serve = server.ServeOneAsync(serverTransport, TestContext.Current.CancellationToken);
            using var parameters = new RecordBatch(new Schema([], null), [], 1);
            var error = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                // The open's error surfaces on the first read, whichever side reports it.
                await using var producer = await client.OpenProducerAsync("stream", parameters, cancellationToken: TestContext.Current.CancellationToken);
                _ = await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken);
            });
            Assert.Contains("unary", error.ErrorMessage, StringComparison.Ordinal);
            Assert.True(await serve.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void BuildResultBatch_IsOneRowOfTheResultSchema()
    {
        var method = ServiceRegistry.GetMethods(typeof(IRefService))["fetch"];
        using var batch = method.BuildResultBatch("v");
        Assert.Equal(1, batch.Length);
        Assert.Equal("v", ((StringArray)batch.Column(0)).GetString(0));
    }

    private static Schema ResultSchema => ServiceRegistry.GetMethods(typeof(IRefService))["fetch"].ResultSchema;

    private static RecordBatch ResultBatch(string value) =>
        ServiceRegistry.GetMethods(typeof(IRefService))["fetch"].BuildResultBatch(value);

    private static async Task<AnnotatedBatch> CallFetchAsync(RpcClient client, RpcServer server, IRpcTransport serverTransport)
    {
        var serve = server.ServeOneAsync(serverTransport, TestContext.Current.CancellationToken);
        using var request = new RecordBatch(s_params, [new StringArray.Builder().Append("ignored").Build()], 1);
        var pointer = await client.CallUnaryAsync(
            "fetch", request, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await serve.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        return pointer;
    }

    private static (RpcClient Client, RpcServer Server, IRpcTransport ServerTransport) Setup(IRefService service, ServerExternalConfig? externalConfig, List<string>? logs = null)
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IRefService), service) { ExternalConfig = externalConfig };
        var client = new RpcClient(clientTransport, new RpcClientOptions
        {
            Protocol = WireNaming.ForProtocol(typeof(IRefService)),
            OnLog = logs is null ? null : message => logs.Add(message.Message),
        });
        return (client, server, serverTransport);
    }

    private static async Task<string?> ReadSingleStringAsync(byte[] ipc)
    {
        using var reader = new WireReader(new MemoryStream(ipc));
        _ = await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
        var item = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(item);
        using (item.Batch)
        {
            Assert.Equal(1, item.Batch.Length);
            Assert.Null(await reader.ReadNextAsync(TestContext.Current.CancellationToken));
            return ((StringArray)item.Batch.Column(0)).GetString(0);
        }
    }

    private sealed class CapturingStorage : IExternalStorage
    {
        private readonly List<(byte[] Data, string? ContentEncoding, string Url)> _uploads = [];

        public List<(byte[] Data, string? ContentEncoding, string Url)> Uploads
        {
            get
            {
                lock (_uploads)
                {
                    return [.. _uploads];
                }
            }
        }

        public Task<string> UploadAsync(byte[] data, Schema schema, string? contentEncoding, CancellationToken cancellationToken)
        {
            lock (_uploads)
            {
                var url = $"https://storage.invalid/object/{_uploads.Count + 1}";
                _uploads.Add((data, contentEncoding, url));
                return Task.FromResult(url);
            }
        }
    }
}
