using System.Net;
using System.Net.Http.Headers;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// Valid Arrow IPC carrying the wrong RPC contract is refused with a typed 400 before dispatch,
/// on both the unary route and a stream's <c>/init</c> -- the reference suite's
/// <c>TestAdversarialHttpRequestContract</c>.
/// </summary>
/// <remarks>
/// Every case here used to be dispatched: argument decoding is positional, so an extra, renamed,
/// reordered or nullability-flipped column, or a second row, reached the method; and HTTP never
/// read <c>vgi_rpc.request_version</c> at all. The requests are fabricated, as they must be to
/// break the contract; every response asserted on is the real server's.
/// </remarks>
public sealed class RequestContractHttpTests
{
    private const string ArrowContentType = "application/vnd.apache.arrow.stream";
    private static readonly Field s_a = new("a", Int32Type.Default, nullable: false);
    private static readonly Field s_b = new("b", Int32Type.Default, nullable: false);
    private static readonly Field s_count = new("count", Int64Type.Default, nullable: false);
    private static readonly Field s_start = new("start", Int64Type.Default, nullable: false);
    private static readonly Schema s_values = new([new Field("value", Int64Type.Default, false)], null);

    private static string Protocol => WireNaming.ForProtocol(typeof(IContractService));

    public interface IContractService
    {
        Task<int> AddAsync(int a, int b);

        Task<RpcStream<CountProducer>> CountAsync(long count, long start);
    }

    private sealed class ContractService : IContractService
    {
        public Task<int> AddAsync(int a, int b) => Task.FromResult(a + b);

        public Task<RpcStream<CountProducer>> CountAsync(long count, long start) =>
            Task.FromResult(new RpcStream<CountProducer>(s_values, new CountProducer(count, start)));
    }

    public sealed class CountProducer(long count, long start) : ProducerState
    {
        private long _next = start;

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            if (_next >= start + count)
            {
                output.Finish();
                return Task.CompletedTask;
            }

            output.Emit(new RecordBatch(s_values, [new Int64Array.Builder().Append(_next++).Build()], 1));
            return Task.CompletedTask;
        }
    }

    public static TheoryData<string> SchemaMutations => new()
    {
        "extra_field",
        "wrong_name",
        "wrong_order",
        "wrong_type",
        "wrong_nullability",
        "two_rows",
    };

    [Theory]
    [MemberData(nameof(SchemaMutations))]
    public async Task Unary_ParameterContractBreach_Is400(string mutation)
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var breach = Mutate([s_a, s_b], [2, 3], mutation);
        using var refused = await PostAsync(http, "add", "", breach, Dispatch("add"));
        await AssertTypedRefusalAsync(refused);
        await AssertAddStillAnswersAsync(http);
    }

    [Theory]
    [MemberData(nameof(SchemaMutations))]
    public async Task StreamInit_ParameterContractBreach_Is400(string mutation)
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };

        using var breach = Mutate([s_count, s_start], [2, 0], mutation);
        using var refused = await PostAsync(http, "count", "/init", breach, Dispatch("count"));
        await AssertTypedRefusalAsync(refused);
        await AssertAddStillAnswersAsync(http);
    }

    [Theory]
    [InlineData("add", "", null)]
    [InlineData("add", "", "2")]
    [InlineData("count", "/init", null)]
    [InlineData("count", "/init", "2")]
    public async Task RequestVersionAbsentOrWrong_Is400(string method, string suffix, string? requestVersion)
    {
        await using var host = await StartHostAsync();
        using var http = new System.Net.Http.HttpClient { BaseAddress = host.Address };
        var metadata = Dispatch(method);
        metadata.Remove(MetadataKeys.RequestVersion);
        if (requestVersion is not null)
        {
            metadata[MetadataKeys.RequestVersion] = requestVersion;
        }

        using var batch = method == "add" ? Row([s_a, s_b], [2, 3]) : Row([s_count, s_start], [2, 0]);
        using var refused = await PostAsync(http, method, suffix, batch, metadata);
        await AssertTypedRefusalAsync(refused);
        await AssertAddStillAnswersAsync(http);
    }

    private static Dictionary<string, string> Dispatch(string method) => new()
    {
        [MetadataKeys.Method] = method,
        [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
        [MetadataKeys.Protocol] = Protocol,
    };

    private static async Task AssertTypedRefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var reader = new WireReader(new MemoryStream(bytes));
        await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
        var error = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(error);
        using (error.Batch)
        {
            Assert.Equal("EXCEPTION", error.GetMetadata(MetadataKeys.LogLevel));
        }
    }

    private static async Task AssertAddStillAnswersAsync(System.Net.Http.HttpClient http)
    {
        using var valid = Row([s_a, s_b], [2, 3]);
        using var response = await PostAsync(http, "add", "", valid, Dispatch("add"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var reader = new WireReader(new MemoryStream(bytes));
        await reader.ReadSchemaAsync(TestContext.Current.CancellationToken);
        var result = await reader.ReadNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        using (result.Batch)
        {
            Assert.Equal(5, ((Int32Array)result.Batch.Column("result")).GetValue(0));
        }
    }

    /// <summary>The reference's <c>_schema_mutation_body</c> mutations, applied to a declared
    /// all-int parameter row.</summary>
    private static RecordBatch Mutate(Field[] fields, long[] values, string mutation)
    {
        switch (mutation)
        {
            case "extra_field":
                return Row([.. fields, new Field("unexpected", fields[0].DataType, false)], [.. values, 9]);
            case "wrong_name":
                return Row([new Field("wrong_name", fields[0].DataType, fields[0].IsNullable), .. fields[1..]], values);
            case "wrong_order":
                return Row([.. Enumerable.Reverse(fields)], [.. Enumerable.Reverse(values)]);
            case "wrong_type":
                return new RecordBatch(
                    new Schema([new Field(fields[0].Name, StringType.Default, fields[0].IsNullable), .. fields[1..]], null),
                    [new StringArray.Builder().Append("wrong type").Build(), .. fields[1..].Select((f, i) => Column(f, [values[i + 1]]))],
                    1);
            case "wrong_nullability":
                return Row([new Field(fields[0].Name, fields[0].DataType, !fields[0].IsNullable), .. fields[1..]], values);
            case "two_rows":
                return new RecordBatch(
                    new Schema(fields, null),
                    fields.Select((f, i) => Column(f, [values[i], values[i]])).ToArray(),
                    2);
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static RecordBatch Row(Field[] fields, long[] values) =>
        new(new Schema(fields, null), fields.Select((f, i) => Column(f, [values[i]])).ToArray(), 1);

    private static IArrowArray Column(Field field, long[] values) => field.DataType switch
    {
        Int32Type => new Int32Array.Builder().AppendRange(values.Select(v => (int)v)).Build(),
        Int64Type => new Int64Array.Builder().AppendRange(values).Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field.DataType, null),
    };

    private static async Task<HttpResponseMessage> PostAsync(
        System.Net.Http.HttpClient http, string method, string suffix, RecordBatch batch, Dictionary<string, string> metadata)
    {
        using var buffer = new MemoryStream();
        await using (var writer = new WireWriter(buffer, batch.Schema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, metadata));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{Protocol}/{method}{suffix}")
        {
            Content = new ByteArrayContent(buffer.ToArray()),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ArrowContentType);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<TestHost> StartHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapVgiRpc(new RpcServer(typeof(IContractService), new ContractService()));
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
