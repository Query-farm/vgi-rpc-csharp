using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// An externalized stream batch must name the same schema as the pointer that replaces it.
/// </summary>
/// <remarks>
/// <para>
/// The pointer is a batch of the response stream, so it carries the method's declared output
/// schema. The object in storage is a standalone IPC stream and names its own. The reference
/// client compares the two exactly, nullability included, and raises "Schema mismatch in
/// ExternalLocation: expected value: double not null, got value: double" -- which is what every
/// exchange that echoes its input hit on the reference suite's <c>http_externalize_always</c>
/// transport: the emitted batch carries the client's nullable schema, the declared output does
/// not, and the object was serialized with the former.
/// </para>
/// <para>
/// This port's own client compares only names and type ids, so it cannot see the difference;
/// the assertions read the uploaded object directly instead. Both halves compared are the
/// server's real output: the pointer as the response delivered it, and the bytes it uploaded.
/// </para>
/// </remarks>
public sealed class StreamExternalizationSchemaTests
{
    private static readonly Schema s_declared = new([new Field("value", Int64Type.Default, nullable: false)], null);

    /// <summary>The same column as a client that did not declare it non-null builds it.</summary>
    private static readonly Schema s_undeclaredNullability = new([new Field("value", Int64Type.Default, nullable: true)], null);

    private static string Protocol => WireNaming.ForProtocol(typeof(IEchoService));

    public interface IEchoService
    {
        Task<RpcStream<EchoInputExchange>> EchoInputAsync();

        Task<RpcStream<LooselyTypedProducer>> ProduceLooselyAsync(long count);
    }

    private sealed class EchoService : IEchoService
    {
        public Task<RpcStream<EchoInputExchange>> EchoInputAsync() =>
            Task.FromResult(new RpcStream<EchoInputExchange>(s_declared, new EchoInputExchange(), s_declared));

        public Task<RpcStream<LooselyTypedProducer>> ProduceLooselyAsync(long count) =>
            Task.FromResult(new RpcStream<LooselyTypedProducer>(s_declared, new LooselyTypedProducer(count)));
    }

    /// <summary>Answers each turn with a batch built on the client's (undeclared-nullability)
    /// schema rather than the declared one -- the shape of the reference's
    /// <c>LoggingExchangeState</c>, which emits its input batch unchanged.</summary>
    public sealed class EchoInputExchange : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            var value = ((Int64Array)input.Batch.Column(0)).GetValue(0)!.Value;
            output.Emit(Values(s_undeclaredNullability, value));
            return Task.CompletedTask;
        }
    }

    /// <summary>Builds each batch with a schema that differs from the declared one only in
    /// nullability. Its first batch comes from the tick folded into <c>/init</c>, its second from
    /// an <c>/exchange</c> continuation -- two separate externalization sites.</summary>
    public sealed class LooselyTypedProducer(long count) : ProducerState
    {
        private long _next;

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            if (_next >= count)
            {
                output.Finish();
                return Task.CompletedTask;
            }

            output.Emit(Values(s_undeclaredNullability, _next++));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExchangeTurn_ExternalizedObjectNamesThePointersSchema()
    {
        var storage = new CapturingStorage();
        await using var host = await StartHostAsync(storage);
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = Protocol });
        using var parameters = new RecordBatch(new Schema([], null), [], 1);

        await using var exchange = await client.OpenExchangeAsync(
            "echo_input", parameters, cancellationToken: TestContext.Current.CancellationToken);
        using var input = Values(s_undeclaredNullability, 7);
        var pointer = await exchange.ExchangeAsync(input, cancellationToken: TestContext.Current.CancellationToken);

        await AssertObjectMatchesPointerAsync(pointer, storage.Uploads.Single());
    }

    [Fact]
    public async Task ProducerTurns_ExternalizedObjectsNameThePointersSchema()
    {
        var storage = new CapturingStorage();
        await using var host = await StartHostAsync(storage);
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = Protocol });
        using var parameters = new RecordBatch(
            new Schema([new Field("count", Int64Type.Default, false)], null),
            [new Int64Array.Builder().Append(2).Build()],
            1);

        await using var producer = await client.OpenProducerAsync(
            "produce_loosely", parameters, cancellationToken: TestContext.Current.CancellationToken);
        var fromInit = await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fromExchange = await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, storage.Uploads.Count);
        await AssertObjectMatchesPointerAsync(fromInit, storage.Uploads[0]);
        await AssertObjectMatchesPointerAsync(fromExchange, storage.Uploads[1]);
    }

    private static async Task AssertObjectMatchesPointerAsync(AnnotatedBatch? pointer, byte[] uploaded)
    {
        Assert.NotNull(pointer);
        using (pointer.Batch)
        {
            Assert.True(ExternalLocation.IsExternalLocationBatch(pointer.Batch, pointer.Metadata), "the turn was not externalized");
            using var reader = new WireReader(new MemoryStream(uploaded));
            var objectSchema = await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
            var item = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(item);
            using (item.Batch)
            {
                Assert.Equal(Describe(pointer.Batch.Schema), Describe(objectSchema));
                Assert.Equal(Describe(s_declared), Describe(objectSchema));
            }
        }
    }

    /// <summary>Names, types and nullability, in order -- everything Arrow schema equality sees.</summary>
    private static string Describe(Schema schema) =>
        string.Join(", ", schema.FieldsList.Select(f => $"{f.Name}: {f.DataType.Name}{(f.IsNullable ? "" : " not null")}"));

    private static RecordBatch Values(Schema schema, long value) =>
        new(schema, [new Int64Array.Builder().Append(value).Build()], 1);

    private static async Task<TestHost> StartHostAsync(IExternalStorage storage)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(
            new RpcServer(typeof(IEchoService), new EchoService()),
            externalization: new ExternalizationOptions
            {
                External = new ServerExternalConfig { Storage = storage, ExternalizeThresholdBytes = 1 },
            });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHost(app, new Uri(address));
    }

    /// <summary>Keeps what the server uploads. The URLs it hands back are never fetched: the
    /// client here has no external-location config, so pointers reach the test unresolved.</summary>
    private sealed class CapturingStorage : IExternalStorage
    {
        private readonly List<byte[]> _uploads = [];

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
            lock (_uploads)
            {
                _uploads.Add(data);
                return Task.FromResult($"https://storage.invalid/object/{_uploads.Count}");
            }
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
