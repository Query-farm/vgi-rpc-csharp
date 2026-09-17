using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// Transport PARITY for <see cref="OutputCollector.Emit(RecordBatch, IReadOnlyDictionary{string, string}?)"/>'s
/// second argument — the per-batch application custom_metadata a stream turn attaches to its
/// output batch.
///
/// <para>This is not a hypothetical channel. VGI rides its entire result-cache protocol
/// (<c>vgi.cache.*</c>), its partition-pruning contract (<c>vgi_partition_values#b64</c>), its
/// ordered-scan contract (<c>vgi_batch_index</c>) and its blended-LATERAL row provenance
/// (<c>vgi_rpc.parent_row#b64</c>) on exactly this key — and the DuckDB extension REJECTS a batch
/// that declares one of those features and then arrives without the metadata. HTTP dispatch read
/// <see cref="OutputCollector.EmittedBatch"/> but never <see cref="OutputCollector.EmittedMetadata"/>,
/// so every one of those keys was silently dropped on that transport alone — 57 integration files
/// red on http, all green on the pipe/launcher lane, because <c>Emit(batch, metadata)</c> had no
/// test anywhere in this repo: every existing fixture called the one-argument overload.
///
/// <para>Each case asserts the SAME service over BOTH transports rather than hard-coding an
/// expectation per transport, so a future divergence fails here no matter which side drifts. The
/// producer cases deliberately read two batches: over HTTP the first is produced by the tick
/// folded into <c>/init</c> and the second by a <c>/exchange</c> continuation — two distinct code
/// paths that each had their own dropped-metadata bug.</para>
/// </summary>
public sealed class StreamBatchMetadataTests
{
    private static readonly Schema s_valueSchema = new([new Field("value", Int64Type.Default, false)], null);

    /// <summary>An application key with no framework meaning whatsoever — the point is that
    /// dispatch carries through keys it does not itself understand.</summary>
    private const string AppKey = "app.batch_tag";

    /// <summary>Stands in for VGI's <c>vgi.cache.if_none_match</c> — a validator the CLIENT puts on
    /// the call that opens the stream, which the producer must see on its very first turn.</summary>
    private const string ValidatorKey = "app.validator";

    private static readonly Schema s_seenSchema = new(
        [
            new Field("seen", StringType.Default, true),
            new Field("leaked_framing_keys", BooleanType.Default, false),
        ],
        null);

    private static RpcClientOptions PipeClientOptions =>
        new() { Protocol = WireNaming.ForProtocol(typeof(ITaggingService)) };

    private static string HttpProtocol => WireNaming.ForProtocol(typeof(ITaggingService));

    public interface ITaggingService
    {
        Task<RpcStream<TaggingProducer>> TaggedCountAsync(long count);

        Task<RpcStream<TaggingExchange>> TaggedDoubleAsync();

        Task<RpcStream<EchoTickMetadataProducer>> EchoTickMetadataAsync();

        Task<RpcStream<FramingKeyForgingExchange>> ForgeFramingKeyAsync();
    }

    private sealed class TaggingService : ITaggingService
    {
        public Task<RpcStream<TaggingProducer>> TaggedCountAsync(long count) =>
            Task.FromResult(new RpcStream<TaggingProducer>(s_valueSchema, new TaggingProducer(count)));

        public Task<RpcStream<TaggingExchange>> TaggedDoubleAsync() =>
            Task.FromResult(new RpcStream<TaggingExchange>(s_valueSchema, new TaggingExchange(), s_valueSchema));

        public Task<RpcStream<EchoTickMetadataProducer>> EchoTickMetadataAsync() =>
            Task.FromResult(new RpcStream<EchoTickMetadataProducer>(s_seenSchema, new EchoTickMetadataProducer()));

        public Task<RpcStream<FramingKeyForgingExchange>> ForgeFramingKeyAsync() =>
            Task.FromResult(new RpcStream<FramingKeyForgingExchange>(s_valueSchema, new FramingKeyForgingExchange(), s_valueSchema));
    }

    /// <summary>A worker that emits the transport's OWN continuation-cursor key as if it were
    /// application metadata. On HTTP the two share one dictionary on the exchange data batch, so
    /// whichever is written last wins — and if the worker's value won, it would overwrite the
    /// cursor and wedge the stream from the second turn on.</summary>
    public sealed class FramingKeyForgingExchange : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            var value = ((Int64Array)input.Batch.Column(0)).GetValue(0)!.Value;
            output.Emit(Batch(value + 1), new Dictionary<string, string>
            {
                [MetadataKeys.StreamState] = "forged-not-a-token",
                [AppKey] = "still-delivered",
            });
            return Task.CompletedTask;
        }
    }

    /// <summary>Echoes each tick's INPUT metadata back as data, so the assertions can live entirely
    /// client-side and read identically on both transports.</summary>
    public sealed class EchoTickMetadataProducer : ProducerState
    {
        private int _ticks;

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            if (_ticks++ >= 2)
            {
                output.Finish();
                return Task.CompletedTask;
            }

            var seen = output.InputMetadata is { } metadata && metadata.TryGetValue(ValidatorKey, out var value)
                ? value
                : "<none>";
            var leakedFramingKeys = output.InputMetadata is { } m
                && (m.ContainsKey(MetadataKeys.StreamState) || m.ContainsKey(MetadataKeys.CallState) || m.ContainsKey(MetadataKeys.Cancel));
            output.Emit(new RecordBatch(
                s_seenSchema,
                [
                    new StringArray.Builder().Append(seen).Build(),
                    new BooleanArray.Builder().Append(leakedFramingKeys).Build(),
                ],
                1));
            return Task.CompletedTask;
        }
    }

    public sealed class TaggingProducer(long count) : ProducerState
    {
        private long _next;

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            if (_next >= count)
            {
                output.Finish();
                return Task.CompletedTask;
            }

            var index = _next++;
            output.Emit(Batch(index), new Dictionary<string, string> { [AppKey] = $"produced-{index}" });
            return Task.CompletedTask;
        }
    }

    public sealed class TaggingExchange : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            var value = ((Int64Array)input.Batch.Column(0)).GetValue(0)!.Value;
            output.Emit(Batch(value * 2), new Dictionary<string, string> { [AppKey] = $"exchanged-{value}" });
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Producer_CarriesPerBatchMetadata_OnThePipeTransport()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(ITaggingService), new TaggingService());
        await using var client = new RpcClient(clientTransport, PipeClientOptions);
        using var parameters = CountParameters(2);
        var serveTask = server.ServeOneAsync(serverTransport);

        await using (var stream = await client.OpenProducerAsync("tagged_count", parameters))
        {
            await AssertProducerTagsAsync(stream);
        }

        Assert.True(await serveTask);
    }

    [Fact]
    public async Task Producer_CarriesPerBatchMetadata_OnTheHttpTransport()
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = HttpProtocol });
        using var parameters = CountParameters(2);

        await using var stream = await client.OpenProducerAsync(
            "tagged_count", parameters, cancellationToken: TestContext.Current.CancellationToken);
        await AssertProducerTagsAsync(stream);
    }

    [Fact]
    public async Task Exchange_CarriesPerBatchMetadata_OnThePipeTransport()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(ITaggingService), new TaggingService());
        await using var client = new RpcClient(clientTransport, PipeClientOptions);
        using var parameters = new RecordBatch(new Schema([], null), [], 1);
        var serveTask = server.ServeOneAsync(serverTransport);

        await using (var exchange = await client.OpenExchangeAsync("tagged_double", parameters))
        {
            await AssertExchangeTagAsync(exchange);
        }

        Assert.True(await serveTask);
    }

    [Fact]
    public async Task Exchange_CarriesPerBatchMetadata_OnTheHttpTransport()
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = HttpProtocol });
        using var parameters = new RecordBatch(new Schema([], null), [], 1);

        await using var exchange = await client.OpenExchangeAsync(
            "tagged_double", parameters, cancellationToken: TestContext.Current.CancellationToken);
        await AssertExchangeTagAsync(exchange);
    }

    /// <summary>Reads two producer batches — on HTTP the first arrives from the tick folded into
    /// <c>/init</c> and the second from a <c>/exchange</c> continuation.</summary>
    private static async Task AssertProducerTagsAsync(IRpcProducerSession stream)
    {
        for (var index = 0; index < 2; index++)
        {
            var annotated = await stream.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(annotated);
            using var batch = annotated.Batch;
            Assert.Equal(index, ((Int64Array)batch.Column(0)).GetValue(0));
            Assert.NotNull(annotated.Metadata);
            Assert.Equal($"produced-{index}", annotated.Metadata![AppKey]);
        }

        Assert.Null(await stream.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task AssertExchangeTagAsync(IRpcExchangeSession exchange)
    {
        using var input = Batch(21);
        var annotated = await exchange.ExchangeAsync(input, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(annotated);
        using var output = annotated.Batch;
        Assert.Equal(42, ((Int64Array)output.Column(0)).GetValue(0));
        Assert.NotNull(annotated.Metadata);
        // Not an equality assertion on the whole dictionary: HTTP legitimately rides its own
        // continuation token on this same batch's metadata, so the contract is "the application's
        // keys survive alongside the framework's", not "the dictionary is exactly this".
        Assert.Equal("exchanged-21", annotated.Metadata![AppKey]);
    }

    /// <summary>
    /// The metadata a client attaches to the call that OPENS a producer stream must reach the
    /// producer's first turn. On the pipe transport that turn is a real tick the client sends, so
    /// the metadata rides there; over HTTP the first turn is folded into <c>/init</c> and the
    /// request body's metadata IS that turn's metadata — dispatch passed <see langword="null"/>
    /// instead, so VGI's conditional revalidation
    /// (<c>vgi.cache.if_none_match</c>) never saw its validators on this transport and every
    /// revalidation answered a full re-stream instead of <c>not_modified</c>.
    /// </summary>
    [Fact]
    public async Task Producer_SeesTheOpeningCallsMetadataOnItsFirstTick_OverHttp()
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = HttpProtocol });
        using var parameters = new RecordBatch(new Schema([], null), [], 1);

        await using var stream = await client.OpenProducerAsync(
            "echo_tick_metadata",
            parameters,
            metadata: new Dictionary<string, string> { [ValidatorKey] = "opening-call" },
            cancellationToken: TestContext.Current.CancellationToken);

        var first = await stream.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        using (first.Batch)
        {
            Assert.Equal("opening-call", ((StringArray)first.Batch.Column(0)).GetString(0));
            Assert.False(((BooleanArray)first.Batch.Column(1)).GetValue(0));
        }
    }

    /// <summary>A continuation turn sees its OWN request's metadata, and never the transport's
    /// stream-framing keys — those are HTTP's cursor bookkeeping, absent on every other transport,
    /// so leaking them into user metadata is a transport-visible difference in its own right.</summary>
    [Fact]
    public async Task Producer_SeesEachContinuationTurnsOwnMetadata_WithoutFramingKeys_OverHttp()
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = HttpProtocol });
        using var parameters = new RecordBatch(new Schema([], null), [], 1);

        await using var stream = await client.OpenProducerAsync(
            "echo_tick_metadata",
            parameters,
            metadata: new Dictionary<string, string> { [ValidatorKey] = "opening-call" },
            cancellationToken: TestContext.Current.CancellationToken);

        using ((await stream.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch)
        {
        }

        var second = await stream.ReadNextAsync(
            new Dictionary<string, string> { [ValidatorKey] = "continuation-turn" },
            TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        using (second.Batch)
        {
            Assert.Equal("continuation-turn", ((StringArray)second.Batch.Column(0)).GetString(0));
            Assert.False(((BooleanArray)second.Batch.Column(1)).GetValue(0));
        }
    }

    [Fact]
    public async Task Exchange_KeepsItsOwnContinuationCursor_WhenAWorkerEmitsThatKeyAsMetadata()
    {
        await using var host = await StartHostAsync();
        await using var client = new HttpRpcClient(host.Address, new HttpRpcClientOptions { Protocol = HttpProtocol });
        using var parameters = new RecordBatch(new Schema([], null), [], 1);

        await using var exchange = await client.OpenExchangeAsync(
            "forge_framing_key", parameters, cancellationToken: TestContext.Current.CancellationToken);

        // Three turns: the second and third only work if the cursor survived the turn before.
        for (var turn = 0; turn < 3; turn++)
        {
            using var input = Batch(turn);
            var annotated = await exchange.ExchangeAsync(input, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(annotated);
            using var output = annotated.Batch;
            Assert.Equal(turn + 1, ((Int64Array)output.Column(0)).GetValue(0));
            Assert.Equal("still-delivered", annotated.Metadata![AppKey]);
        }
    }

    private static RecordBatch CountParameters(long count) =>
        new(new Schema([new Field("count", Int64Type.Default, false)], null),
            [new Int64Array.Builder().Append(count).Build()], 1);

    private static RecordBatch Batch(long value) =>
        new(s_valueSchema, [new Int64Array.Builder().Append(value).Build()], 1);

    private static async Task<TestHost> StartHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(new RpcServer(typeof(ITaggingService), new TaggingService()));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new TestHost(app, new Uri(address));
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
