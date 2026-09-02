using System.IO.Compression;
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
using QueryFarm.VgiRpc.Errors;
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
        Assert.Equal(3, handler.Requests); // OPTIONS capability discovery + compressed attempt + identity retry
    }

    [Fact]
    public async Task ResponseBudget_IsDiscoveredAdvertisedAndStrictlyEnforced()
    {
        await using var host = await StartHostAsync(maxResponseBytes: 64L << 10);
        await using var client = new HttpRpcClient(host.Address);
        var capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.True(capabilities.AcceptMaxResponseBytesSupport);
        Assert.Equal(64L << 10, capabilities.MaxResponseBytes);

        var schema = new Schema([new Field("value", StringType.Default, false)], null);
        using var parameters = new RecordBatch(schema,
            [new StringArray.Builder().Append(new string('x', 100_000)).Build()], 1);
        var error = await Assert.ThrowsAsync<RpcException>(() => client.CallUnaryAsync(
            "echo", parameters, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("ResponseTooLargeError", error.ErrorType);
        Assert.Contains("max_response_bytes", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_SendsBudgetAndAcceptsAnySuccessfulStatus()
    {
        using var handler = new BudgetResponseHandler(BudgetResponseMode.Success, optionsStatus: 204);
        using var http = new System.Net.Http.HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1"),
        };
        await using var client = new HttpRpcClient(http,
            new HttpRpcClientOptions { AcceptedMaxResponseBytes = 64L << 10 });

        var capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.True(capabilities.AcceptMaxResponseBytesSupport);
        Assert.Equal((64L << 10).ToString(), handler.DiscoveryBudget);
    }

    [Theory]
    [InlineData(BudgetResponseMode.MissingSupport)]
    [InlineData(BudgetResponseMode.UppercaseSupport)]
    [InlineData(BudgetResponseMode.DuplicateSupport)]
    public async Task EveryRpcResponse_RequiresOneLiteralSupportValue(BudgetResponseMode mode)
    {
        using var handler = new BudgetResponseHandler(mode);
        using var http = new System.Net.Http.HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1"),
        };
        await using var client = new HttpRpcClient(http);
        using var parameters = new RecordBatch(new Schema([], null), [], 1);

        var error = await Assert.ThrowsAsync<RpcException>(() => client.CallUnaryAsync(
            "echo", parameters, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("ProtocolError", error.ErrorType);
        Assert.Contains(RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader,
            error.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BudgetResponseMode.OversizedIdentity)]
    [InlineData(BudgetResponseMode.OversizedGzipDecoded)]
    public async Task ResponseBody_IsBoundedWhileStreamingAndDecompressing(BudgetResponseMode mode)
    {
        using var handler = new BudgetResponseHandler(mode);
        using var http = new System.Net.Http.HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1"),
        };
        await using var client = new HttpRpcClient(http,
            new HttpRpcClientOptions { AcceptedMaxResponseBytes = 64L << 10 });
        using var parameters = new RecordBatch(new Schema([], null), [], 1);

        var error = await Assert.ThrowsAsync<RpcException>(() => client.CallUnaryAsync(
            "echo", parameters, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("ResponseTooLargeError", error.ErrorType);
        Assert.Contains("max_response_bytes", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("echo", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvertisedResponseBudget_NarrowsDecodedBodyLimitOnly()
    {
        foreach (var (discovery, response) in new[]
        {
            (new[] { "65536" }, (string[]?)null),
            ((string[]?)null, new[] { "65536" }),
        })
        {
            using var handler = new BudgetResponseHandler(
                BudgetResponseMode.OversizedIdentity,
                advertisedResponseBudgets: discovery,
                responseAdvertisedResponseBudgets: response);
            using var http = new System.Net.Http.HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1"),
            };
            await using var client = new HttpRpcClient(http,
                new HttpRpcClientOptions { AcceptedMaxResponseBytes = 128L << 10 });
            using var parameters = new RecordBatch(new Schema([], null), [], 1);

            var error = await Assert.ThrowsAsync<RpcException>(() => client.CallUnaryAsync(
                "echo", parameters, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("ResponseTooLargeError", error.ErrorType);
            Assert.Contains("73728 > 65536", error.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Discovery_RejectsMalformedOrDuplicateAdvertisedResponseBudget()
    {
        foreach (var values in new[]
        {
            new[] { "invalid" },
            new[] { "65535" },
            new[] { "65536", "65537" },
        })
        {
            using var handler = new BudgetResponseHandler(
                BudgetResponseMode.Success, advertisedResponseBudgets: values);
            using var http = new System.Net.Http.HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1"),
            };
            await using var client = new HttpRpcClient(http);
            var error = await Assert.ThrowsAsync<RpcException>(() =>
                client.GetCapabilitiesAsync(TestContext.Current.CancellationToken));
            Assert.Equal("ProtocolError", error.ErrorType);
            Assert.Contains("VGI-Max-Response-Bytes", error.ErrorMessage,
                StringComparison.Ordinal);
        }

        using var responseHandler = new BudgetResponseHandler(
            BudgetResponseMode.Success,
            responseAdvertisedResponseBudgets: ["65536", "65537"]);
        using var responseHttp = new System.Net.Http.HttpClient(responseHandler)
        {
            BaseAddress = new Uri("http://127.0.0.1"),
        };
        await using var responseClient = new HttpRpcClient(responseHttp);
        using var parameters = new RecordBatch(new Schema([], null), [], 1);
        var responseError = await Assert.ThrowsAsync<RpcException>(() =>
            responseClient.CallUnaryAsync("echo", parameters,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("ProtocolError", responseError.ErrorType);
        Assert.Contains("VGI-Max-Response-Bytes", responseError.ErrorMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Options_ValidatesPresentBudgetAfterAuthentication()
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };
        foreach (var values in new[]
        {
            new[] { "invalid" },
            new[] { "65535" },
            new[] { "65536", "65537" },
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/health");
            request.Headers.TryAddWithoutValidation(
                RpcHttpEndpoints.AcceptMaxResponseBytesHeader, values);
            using var response = await http.SendAsync(request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("true", response.Headers.GetValues(
                RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader).Single());
            Assert.Equal("true", response.Headers.GetValues("X-VGI-RPC-Error").Single());
            await using var body = await response.Content.ReadAsStreamAsync(
                TestContext.Current.CancellationToken);
            using var reader = new WireReader(body);
            await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
            var batch = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(batch);
            var error = RpcErrorDecoder.Decode(batch!);
            Assert.Equal("ValueError", error.ErrorType);
            batch!.Batch.Dispose();
        }

        using var missing = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/health"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);

        await using var authenticated = await StartHostAsync(authenticate: _ =>
            throw new AuthFailure(AuthReason.InvalidCredential, "bad token"));
        using var authenticatedHttp = new System.Net.Http.HttpClient
        {
            BaseAddress = authenticated.Address,
        };
        using var malformed = new HttpRequestMessage(HttpMethod.Options, "/health");
        malformed.Headers.TryAddWithoutValidation(
            RpcHttpEndpoints.AcceptMaxResponseBytesHeader, "invalid");
        using var rejected = await authenticatedHttp.SendAsync(
            malformed, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task UploadUrlResponse_ObeysNegotiatedBudget()
    {
        await using var host = await StartHostAsync(externalization: new ExternalizationOptions
        {
            UploadUrlProvider = new LongUploadUrlProvider(),
        });
        await using var client = new HttpRpcClient(host.Address,
            new HttpRpcClientOptions { AcceptedMaxResponseBytes = 64L << 10 });

        var error = await Assert.ThrowsAsync<RpcException>(() => client.RequestUploadUrlsAsync(
            64, TestContext.Current.CancellationToken));
        Assert.Equal("ResponseTooLargeError", error.ErrorType);
        Assert.Contains("__upload_url__", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("max_response_bytes", error.ErrorMessage, StringComparison.Ordinal);
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

    private static async Task<TestHost> StartHostAsync(long? maxResponseBytes = null,
        ExternalizationOptions? externalization = null,
        RpcHttpEndpoints.AuthenticateDelegate? authenticate = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(new RpcServer(typeof(IService), new Service()),
            maxResponseBytes: maxResponseBytes, externalization: externalization,
            authenticate: authenticate);
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
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.UnsupportedMediaType)
                {
                    RequestMessage = request,
                };
                response.Headers.TryAddWithoutValidation(
                    RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader, "true");
                return Task.FromResult(response);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class PlainTextErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("schema mismatch"),
                RequestMessage = request,
            };
            response.Headers.TryAddWithoutValidation(
                RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader, "true");
            return Task.FromResult(response);
        }
    }

    public enum BudgetResponseMode
    {
        Success,
        MissingSupport,
        UppercaseSupport,
        DuplicateSupport,
        OversizedIdentity,
        OversizedGzipDecoded,
    }

    private sealed class BudgetResponseHandler(
        BudgetResponseMode mode, int optionsStatus = 200,
        string[]? advertisedResponseBudgets = null,
        string[]? responseAdvertisedResponseBudgets = null) : HttpMessageHandler
    {
        public string? DiscoveryBudget { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Options)
            {
                DiscoveryBudget = request.Headers.TryGetValues(
                    RpcHttpEndpoints.AcceptMaxResponseBytesHeader, out var values)
                    ? values.SingleOrDefault()
                    : null;
                var optionsResponse = Response((HttpStatusCode)optionsStatus, "true", []);
                if (advertisedResponseBudgets is not null)
                {
                    optionsResponse.Headers.TryAddWithoutValidation(
                        "VGI-Max-Response-Bytes", advertisedResponseBudgets);
                }
                return Task.FromResult(optionsResponse);
            }

            var support = mode switch
            {
                BudgetResponseMode.MissingSupport => null,
                BudgetResponseMode.UppercaseSupport => "TRUE",
                _ => "true",
            };
            byte[] body = mode switch
            {
                BudgetResponseMode.OversizedIdentity => new byte[72 * 1024],
                BudgetResponseMode.OversizedGzipDecoded => Gzip(new byte[128 * 1024]),
                _ => [],
            };
            var response = Response(HttpStatusCode.OK, support, body);
            if (responseAdvertisedResponseBudgets is not null)
            {
                response.Headers.TryAddWithoutValidation(
                    "VGI-Max-Response-Bytes", responseAdvertisedResponseBudgets);
            }
            if (mode == BudgetResponseMode.DuplicateSupport)
            {
                response.Headers.Remove(RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader);
                response.Headers.TryAddWithoutValidation(
                    RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader, ["true", "true"]);
            }
            if (mode == BudgetResponseMode.OversizedGzipDecoded)
            {
                response.Content.Headers.ContentEncoding.Add("gzip");
            }
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Response(
            HttpStatusCode status, string? support, byte[] body)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StreamContent(new MemoryStream(body, writable: false)),
            };
            response.Content.Headers.ContentType =
                MediaTypeHeaderValue.Parse(RpcHttpEndpoints.ArrowContentType);
            if (support is not null)
            {
                response.Headers.TryAddWithoutValidation(
                    RpcHttpEndpoints.AcceptMaxResponseBytesSupportHeader, support);
            }
            return response;
        }

        private static byte[] Gzip(byte[] body)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(body);
            }
            return output.ToArray();
        }
    }

    private sealed class LongUploadUrlProvider : IUploadUrlProvider
    {
        private readonly string _suffix = new('x', 700);

        public Task<UploadUrl> GenerateUploadUrlAsync(
            Schema schema, CancellationToken cancellationToken) => Task.FromResult(new UploadUrl(
                "https://upload.invalid/" + _suffix,
                "https://download.invalid/" + _suffix,
                DateTimeOffset.UtcNow.AddHours(1)));
    }
}
