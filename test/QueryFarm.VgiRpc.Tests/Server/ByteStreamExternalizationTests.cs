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
/// <see cref="RpcServer.ExternalConfig"/>: a byte-stream server externalizes like the HTTP one
/// -- the reference suite's <c>TestExternalByteStream</c> in the server role, which boots a
/// worker with <c>--fake-storage URL --externalize-threshold 1</c> over a pipe and counts the
/// uploads. Before, the byte-stream server had no externalization at all.
/// </summary>
/// <remarks>
/// The client here resolves nothing (it has no external-location config), so each pointer
/// reaches the test as the server wrote it and the uploaded object is read directly. Both are
/// the real server's output.
/// </remarks>
public sealed class ByteStreamExternalizationTests
{
    private static readonly Schema s_declared = new([new Field("value", Int64Type.Default, nullable: false)], null);
    private static readonly Schema s_undeclaredNullability = new([new Field("value", Int64Type.Default, nullable: true)], null);

    public interface IExternalService
    {
        Task<long> EchoAsync(long value);

        Task<RpcStream<EchoExchange>> EchoStreamAsync();
    }

    private sealed class ExternalService : IExternalService
    {
        public Task<long> EchoAsync(long value) => Task.FromResult(value);

        public Task<RpcStream<EchoExchange>> EchoStreamAsync() =>
            Task.FromResult(new RpcStream<EchoExchange>(s_declared, new EchoExchange(), s_declared));
    }

    /// <summary>Answers on the client's schema object, as an exchange that echoes its input
    /// does; the object must still name the declared output schema.</summary>
    public sealed class EchoExchange : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            var value = ((Int64Array)input.Batch.Column(0)).GetValue(0)!.Value;
            output.Emit(Values(s_undeclaredNullability, value), new Dictionary<string, string> { ["app.turn"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task UnaryResult_IsUploadedAndReplacedByAPointer()
    {
        var storage = new CapturingStorage();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, server, serverTransport) = Setup(storage);
        await using (client)
        {
            var serve = server.ServeOneAsync(serverTransport, cancellationToken);
            using var request = new RecordBatch(s_declared, [new Int64Array.Builder().Append(42).Build()], 1);
            var pointer = await client.CallUnaryAsync("echo", request, cancellationToken: cancellationToken);
            Assert.True(await serve.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));

            using (pointer.Batch)
            {
                Assert.True(ExternalLocation.IsExternalLocationBatch(pointer.Batch, pointer.Metadata));
            }

            var (schema, batch, _) = await ReadObjectAsync(storage.Uploads.Single());
            using (batch)
            {
                Assert.Equal(42L, ((Int64Array)batch.Column(0)).GetValue(0));
                Assert.Equal("result", schema.GetFieldByIndex(0).Name);
            }
        }
    }

    [Fact]
    public async Task ExchangeTurn_IsUploadedUnderTheDeclaredSchema_WithItsMetadataInside()
    {
        var storage = new CapturingStorage();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, server, serverTransport) = Setup(storage);
        await using (client)
        {
            var serve = server.ServeOneAsync(serverTransport, cancellationToken);
            using var parameters = new RecordBatch(new Schema([], null), [], 1);
            await using (var exchange = await client.OpenExchangeAsync("echo_stream", parameters, s_undeclaredNullability, cancellationToken: cancellationToken))
            {
                using var input = Values(s_undeclaredNullability, 7);
                var pointer = await exchange.ExchangeAsync(input, cancellationToken: cancellationToken);
                Assert.NotNull(pointer);
                using (pointer.Batch)
                {
                    Assert.True(ExternalLocation.IsExternalLocationBatch(pointer.Batch, pointer.Metadata));
                    Assert.False(pointer.Metadata!.ContainsKey("app.turn"), "per-batch metadata belongs inside the object, not on the pointer");
                }
            }

            Assert.True(await serve.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
            var (schema, batch, metadata) = await ReadObjectAsync(storage.Uploads.Single());
            using (batch)
            {
                Assert.False(schema.GetFieldByIndex(0).IsNullable, "the object must name the stream's declared schema");
                Assert.Equal(7L, ((Int64Array)batch.Column(0)).GetValue(0));
                Assert.Equal("7", metadata?.GetValueOrDefault("app.turn"));
            }
        }
    }

    [Fact]
    public async Task UploadFailure_IsATypedError_AndTheConnectionSurvives()
    {
        var storage = new CapturingStorage { Fail = true };
        var cancellationToken = TestContext.Current.CancellationToken;
        var (client, server, serverTransport) = Setup(storage);
        await using (client)
        {
            var serve = server.ServeOneAsync(serverTransport, cancellationToken);
            using var parameters = new RecordBatch(new Schema([], null), [], 1);
            await using (var exchange = await client.OpenExchangeAsync("echo_stream", parameters, s_undeclaredNullability, cancellationToken: cancellationToken))
            {
                using var input = Values(s_undeclaredNullability, 7);
                var error = await Assert.ThrowsAsync<RpcException>(() => exchange.ExchangeAsync(input, cancellationToken: cancellationToken));
                Assert.Contains("storage unavailable", error.ErrorMessage, StringComparison.Ordinal);
            }

            Assert.True(await serve.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));

            storage.Fail = false;
            var next = server.ServeOneAsync(serverTransport, cancellationToken);
            using var request = new RecordBatch(s_declared, [new Int64Array.Builder().Append(1).Build()], 1);
            var pointer = await client.CallUnaryAsync("echo", request, cancellationToken: cancellationToken);
            pointer.Batch.Dispose();
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
        }
    }

    private static (RpcClient Client, RpcServer Server, IRpcTransport ServerTransport) Setup(IExternalStorage storage)
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IExternalService), new ExternalService())
        {
            ExternalConfig = new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 1 },
        };
        var client = new RpcClient(clientTransport, new RpcClientOptions { Protocol = WireNaming.ForProtocol(typeof(IExternalService)) });
        return (client, server, serverTransport);
    }

    private static async Task<(Schema Schema, RecordBatch Batch, IReadOnlyDictionary<string, string>? Metadata)> ReadObjectAsync(byte[] uploaded)
    {
        using var reader = new WireReader(new MemoryStream(uploaded));
        var schema = await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
        var item = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(item);
        return (schema, item.Batch, item.Metadata);
    }

    private static RecordBatch Values(Schema schema, long value) =>
        new(schema, [new Int64Array.Builder().Append(value).Build()], 1);

    private sealed class CapturingStorage : IExternalStorage
    {
        private readonly List<byte[]> _uploads = [];

        public bool Fail { get; set; }

        public List<byte[]> Uploads
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
            if (Fail)
            {
                throw new InvalidOperationException("storage unavailable");
            }

            lock (_uploads)
            {
                _uploads.Add(data);
                return Task.FromResult($"https://storage.invalid/object/{_uploads.Count}");
            }
        }
    }
}
