using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class HttpRpcClientTests
{
    private static readonly Schema s_valueSchema = new([new Field("value", Int64Type.Default, false)], null);

    public interface IService
    {
        Task<string> EchoAsync(string value);

        Task<RpcStream<Counter>> CountAsync(long count);

        Task<RpcStream<Doubler>> DoubleAsync();

        Task<RpcStream<EmptyExchange>> EmptyAsync();
    }

    public interface IClient
    {
        Task<string> EchoAsync(string value, CancellationToken cancellationToken = default);

        Task<IRpcProducerSession> CountAsync(long count, CancellationToken cancellationToken = default);

        Task<RpcExchangeSession<ValueInput>> DoubleAsync(CancellationToken cancellationToken = default);
    }

    public sealed class ValueInput
    {
        public long Value { get; set; }
    }

    private sealed class Service : IService
    {
        public Task<string> EchoAsync(string value) => Task.FromResult(value);

        public Task<RpcStream<Counter>> CountAsync(long count) =>
            Task.FromResult(new RpcStream<Counter>(s_valueSchema, new Counter(count)));

        public Task<RpcStream<Doubler>> DoubleAsync() =>
            Task.FromResult(new RpcStream<Doubler>(s_valueSchema, new Doubler(), s_valueSchema));

        public Task<RpcStream<EmptyExchange>> EmptyAsync() =>
            Task.FromResult(new RpcStream<EmptyExchange>(new Schema([], null), new EmptyExchange(), new Schema([], null)));
    }

    public sealed class Counter(long count) : ProducerState
    {
        private long _next;

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            if (_next == count)
            {
                output.Finish();
            }
            else
            {
                output.Emit(Batch(_next++));
            }

            return Task.CompletedTask;
        }
    }

    public sealed class Doubler : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            var value = ((Int64Array)input.Batch.Column(0)).GetValue(0)!.Value;
            output.Emit(Batch(value * 2));
            return Task.CompletedTask;
        }
    }

    public sealed class EmptyExchange : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            output.Emit(new RecordBatch(new Schema([], null), [], 0));
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(ContentEncoding.Zstd)]
    [InlineData(ContentEncoding.Gzip)]
    public async Task UnaryAndStreams_RoundTripWithCompression(ContentEncoding encoding)
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(
            host.Address,
            new HttpRpcClientOptions { PreferredEncoding = encoding });

        var parametersSchema = new Schema([new Field("value", StringType.Default, false)], null);
        using var parameters = new RecordBatch(parametersSchema, [new StringArray.Builder().Append("hello").Build()], 1);
        var response = await client.CallUnaryAsync("echo", parameters, cancellationToken: TestContext.Current.CancellationToken);
        using (response.Batch)
        {
            Assert.Equal("hello", ((StringArray)response.Batch.Column(0)).GetString(0));
        }

        using var countParameters = new RecordBatch(
            new Schema([new Field("count", Int64Type.Default, false)], null),
            [new Int64Array.Builder().Append(2).Build()],
            1);
        await using (var producer = await client.OpenProducerAsync("count", countParameters, cancellationToken: TestContext.Current.CancellationToken))
        {
            using var first = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            using var second = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            Assert.Equal(0, ((Int64Array)first.Column(0)).GetValue(0));
            Assert.Equal(1, ((Int64Array)second.Column(0)).GetValue(0));
            Assert.Null(await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        using var empty = new RecordBatch(new Schema([], null), [], 1);
        await using (var exchange = await client.OpenExchangeAsync("double", empty, cancellationToken: TestContext.Current.CancellationToken))
        {
            using var input = Batch(7);
            using var doubled = (await exchange.ExchangeAsync(input, cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            Assert.Equal(14, ((Int64Array)doubled.Column(0)).GetValue(0));
        }

        await using (var exchange = await client.OpenExchangeAsync("empty", empty, cancellationToken: TestContext.Current.CancellationToken))
        {
            for (var index = 0; index < 3; index++)
            {
                using var input = new RecordBatch(new Schema([], null), [], 0);
                using var output = (await exchange.ExchangeAsync(input, cancellationToken: TestContext.Current.CancellationToken))!.Batch;
                Assert.Equal(0, output.ColumnCount);
                Assert.Equal(0, output.Length);
            }
        }

        var typed = client.CreateProxy<IClient>();
        Assert.Equal("typed", await typed.EchoAsync("typed", TestContext.Current.CancellationToken));
        await using (var producer = await typed.CountAsync(1, TestContext.Current.CancellationToken))
        {
            using var first = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            Assert.Equal(0, ((Int64Array)first.Column(0)).GetValue(0));
        }

        await using (var exchange = await typed.DoubleAsync(TestContext.Current.CancellationToken))
        {
            using var doubled = (await exchange.ExchangeAsync(
                new ValueInput { Value = 9 },
                cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            Assert.Equal(18, ((Int64Array)doubled.Column(0)).GetValue(0));
        }
    }

    [Fact]
    public async Task Unary_RetriesIdentityOnceWhenServerRejectsCompression()
    {
        await using var host = await StartHostAsync();
        using var handler = new RejectFirstCompressedRequestHandler();
        using var http = new System.Net.Http.HttpClient(handler) { BaseAddress = host.Address };
        await using var client = new HttpRpcClient(http);
        var schema = new Schema([new Field("value", StringType.Default, false)], null);
        using var parameters = new RecordBatch(schema, [new StringArray.Builder().Append("fallback").Build()], 1);

        using var response = (await client.CallUnaryAsync(
            "echo",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken)).Batch;

        Assert.Equal("fallback", ((StringArray)response.Column(0)).GetString(0));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Unary_NonArrowHttpErrorPreservesStatusAndDoesNotEnterArrowDecoder()
    {
        using var http = new System.Net.Http.HttpClient(new PlainTextErrorHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1"),
        };
        await using var client = new HttpRpcClient(http);
        using var parameters = new RecordBatch(new Schema([], null), [], 1);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CallUnaryAsync(
                "echo",
                parameters,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("schema mismatch", error.Message, StringComparison.Ordinal);
    }

    private static async Task<TestHost> StartHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(new RpcServer(typeof(IService), new Service()));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHost(app, new Uri(address));
    }

    private static RecordBatch Batch(long value) =>
        new(s_valueSchema, [new Int64Array.Builder().Append(value).Build()], 1);

    private sealed class TestHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RejectFirstCompressedRequestHandler : DelegatingHandler
    {
        private bool _rejected;

        public RejectFirstCompressedRequestHandler() : base(new HttpClientHandler())
        {
        }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (!_rejected && request.Content?.Headers.ContentEncoding.Count > 0)
            {
                _rejected = true;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.UnsupportedMediaType)
                {
                    RequestMessage = request,
                });
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class PlainTextErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("schema mismatch"),
                RequestMessage = request,
            });
    }
}
