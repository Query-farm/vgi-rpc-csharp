using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.AccessLog;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// A request argument and a stream's state never reach a log, on the pipe or HTTP transport.
/// </summary>
/// <remarks>
/// The access log used to carry <c>request_data</c> -- the whole request as base64 Arrow IPC --
/// behind <c>--access-log-debug</c>. The framework cannot know which parameters are secret, so a
/// VGI <c>catalog_attach</c>'s API keys and passwords landed in the log whenever that was on. A
/// sentinel rides in a unary argument, in a stream's init argument, and in that stream's state
/// across HTTP continuation turns; the formatted JSONL and everything written to stderr while the
/// calls run must contain neither it nor any base64 alignment of it. This port has no log levels
/// to raise: the JSONL sink is the most verbose output it can write.
/// </remarks>
public sealed class NoPayloadInLogsTests
{
    private const string Sentinel = "sk-live-SENTINEL-7f3a9c41d2e8b605";
    private const string Protocol = "SecretService";
    private static readonly Schema s_valueSchema = new([new Field("value", Int64Type.Default, false)], null);

    public interface ISecretService
    {
        Task<string> AttachAsync(string apiKey);

        Task<RpcStream<SecretProducer>> TickAsync(string apiKey);
    }

    private sealed class SecretService : ISecretService
    {
        public Task<string> AttachAsync(string apiKey) => Task.FromResult(apiKey.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public Task<RpcStream<SecretProducer>> TickAsync(string apiKey) =>
            Task.FromResult(new RpcStream<SecretProducer>(s_valueSchema, new SecretProducer(apiKey)));
    }

    /// <summary>Holds the secret in its state for the life of the stream; emits three rows.</summary>
    public sealed class SecretProducer(string apiKey) : ProducerState
    {
        public string ApiKey { get; } = apiKey;

        private long _next;

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            if (_next == 3)
            {
                output.Finish();
            }
            else
            {
                output.Emit(new RecordBatch(s_valueSchema, [new Int64Array.Builder().Append(_next++ + ApiKey.Length).Build()], 1));
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PipeTransportLogsNoArgumentOrState()
    {
        var log = Path.GetTempFileName();
        try
        {
            var stderr = await CaptureStderrAsync(async () =>
            {
                using var sink = new JsonlAccessLogSink(log);
                var server = new RpcServer(typeof(ISecretService), new SecretService(), accessLog: sink);
                var (clientTransport, serverTransport) = PipeTransport.CreatePair();
                await using var client = new RpcClient(clientTransport, new RpcClientOptions { Protocol = Protocol });

                var serveUnary = server.ServeOneAsync(serverTransport, TestContext.Current.CancellationToken);
                (await client.CallUnaryAsync("attach", Params(), cancellationToken: TestContext.Current.CancellationToken)).Batch.Dispose();

                Assert.True(await serveUnary);

                var serveStream = server.ServeOneAsync(serverTransport, TestContext.Current.CancellationToken);
                await using (var producer = await client.OpenProducerAsync("tick", Params(), cancellationToken: TestContext.Current.CancellationToken))
                {
                    while (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken) is { } batch)
                    {
                        batch.Batch.Dispose();
                    }
                }

                Assert.True(await serveStream);
            });

            AssertNoSentinel(await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken), stderr, expectContinuations: false);
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Fact]
    public async Task HttpTransportLogsNoArgumentOrState()
    {
        var log = Path.GetTempFileName();
        try
        {
            var stderr = await CaptureStderrAsync(async () =>
            {
                using var sink = new JsonlAccessLogSink(log);
                var server = new RpcServer(typeof(ISecretService), new SecretService(), accessLog: sink);
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                await using var app = builder.Build();
                app.MapVgiRpc(server);
                await app.StartAsync(TestContext.Current.CancellationToken);
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

                await using (var client = new HttpRpcClient(new Uri(address), new HttpRpcClientOptions { Protocol = Protocol }))
                {
                    (await client.CallUnaryAsync("attach", Params(), cancellationToken: TestContext.Current.CancellationToken)).Batch.Dispose();

                    await using var producer = await client.OpenProducerAsync("tick", Params(), cancellationToken: TestContext.Current.CancellationToken);
                    while (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken) is { } batch)
                    {
                        batch.Batch.Dispose();
                    }
                }

                await app.StopAsync(TestContext.Current.CancellationToken);
            });

            AssertNoSentinel(await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken), stderr, expectContinuations: true);
        }
        finally
        {
            File.Delete(log);
        }
    }

    private static RecordBatch Params() =>
        new(new Schema([new Field("api_key", StringType.Default, false)], null), [new StringArray.Builder().Append(Sentinel).Build()], 1);

    private static void AssertNoSentinel(string accessLog, string stderr, bool expectContinuations)
    {
        foreach (var needle in Needles())
        {
            Assert.DoesNotContain(needle, accessLog, StringComparison.Ordinal);
            Assert.DoesNotContain(needle, stderr, StringComparison.Ordinal);
        }

        // The records exist and describe the request by shape -- an empty log passes the above.
        var records = accessLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        foreach (var record in records)
        {
            Assert.False(record.TryGetProperty("request_data", out _));
            Assert.False(record.TryGetProperty("request_state", out _));
            Assert.False(record.TryGetProperty("response_state", out _));
            Assert.False(record.TryGetProperty("original_request_bytes", out _));
            // Nothing is omitted, so no record is marked truncated: the reference stopped
            // emitting "payload_omitted" in vgi-rpc 0.50.1.
            Assert.False(record.TryGetProperty("truncated", out _));
        }

        foreach (var method in new[] { "attach", "tick" })
        {
            var entry = records.First(r => r.GetProperty("method").GetString() == method && r.TryGetProperty("request_fields", out _));
            var field = Assert.Single(entry.GetProperty("request_fields").EnumerateArray());
            Assert.Equal("api_key", field.GetProperty("name").GetString());
            Assert.Equal("utf8", field.GetProperty("type").GetString());
            Assert.Equal(1, entry.GetProperty("request_rows").GetInt64());
        }

        if (expectContinuations)
        {
            // HTTP: the stream crossed sealed turns, and each continuation reports the token's size.
            Assert.Contains(records, r => r.GetProperty("method").GetString() == "tick" && r.TryGetProperty("request_state_bytes", out _));
        }
    }

    /// <summary>The sentinel, and every base64 alignment of it (a payload logged as base64 IPC
    /// shows the secret only at some offset modulo 3).</summary>
    private static IEnumerable<string> Needles()
    {
        yield return Sentinel;
        var bytes = Encoding.UTF8.GetBytes(Sentinel);
        for (var shift = 0; shift < 3; shift++)
        {
            var padded = new byte[shift + bytes.Length];
            bytes.CopyTo(padded, shift);
            var encoded = Convert.ToBase64String(padded);
            // Drop the characters that depend on the unknown neighbours on either side.
            var start = shift == 0 ? 0 : 4;
            var end = encoded.TrimEnd('=').Length - 4;
            if (end > start)
            {
                yield return encoded[start..end];
            }
        }
    }

    private static async Task<string> CaptureStderrAsync(Func<Task> body)
    {
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            await body();
        }
        finally
        {
            Console.SetError(original);
        }

        return captured.ToString();
    }
}
