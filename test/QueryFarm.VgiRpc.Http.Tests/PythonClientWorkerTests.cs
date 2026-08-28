using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Reflection;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// Native-client acceptance tests ported from vgi-rpc-python's
/// tests/test_client_conformance_worker.py.  The Python worker reverses the normal conformance
/// direction: these tests make the C# SDK construct every Arrow request itself, which catches
/// schema inference and continuation bugs that a Python client driving a C# server cannot see.
/// </summary>
public sealed class PythonClientWorkerTests
{
    private const string Prefix = "/vgi";
    private static readonly Schema s_emptySchema = new([], null);
    private static readonly Schema s_producerSchema = new(
        [
            new Field("index", Int64Type.Default, false),
            new Field("payload", BinaryType.Default, false),
        ],
        null);
    private static readonly Schema s_typedExchangeSchema = BuildTypedExchangeSchema();

    [Fact]
    public async Task HttpTypedExchange_PreservesDeclaredSchemaForAllNullValues()
    {
        await using var worker = await PythonWorker.StartHttpAsync(Prefix);
        await using var client = new HttpRpcClient(worker.Address, new HttpRpcClientOptions { Prefix = Prefix });
        using var parameters = EmptyParameters();
        await using var exchange = await client.OpenExchangeAsync(
            "typed_exchange",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken);

        using var allNull = ValueCodec.BuildRow(s_typedExchangeSchema, [null, null, null, null, null, null]);
        using var echoedNull = (await exchange.ExchangeAsync(
            allNull,
            cancellationToken: TestContext.Current.CancellationToken))!.Batch;
        AssertExactSchema(s_typedExchangeSchema, echoedNull.Schema);
        Assert.Equal(1, echoedNull.Length);
        Assert.All(echoedNull.Arrays, array => Assert.True(array.IsNull(0)));

    }

    [Fact]
    public async Task HttpTypedExchange_RoundTripsNestedLogicalTypes()
    {
        await using var worker = await PythonWorker.StartHttpAsync(Prefix);
        await using var client = new HttpRpcClient(worker.Address, new HttpRpcClientOptions { Prefix = Prefix });
        using var parameters = EmptyParameters();
        await using var exchange = await client.OpenExchangeAsync(
            "typed_exchange",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken);
        using var input = PopulatedTypedBatch();

        using var output = (await exchange.ExchangeAsync(
            input,
            cancellationToken: TestContext.Current.CancellationToken))!.Batch;

        AssertExactSchema(s_typedExchangeSchema, output.Schema);
        Assert.Equal(1.5, ((DoubleArray)output.Column(0)).GetValue(0));
        var category = (DictionaryArray)output.Column(2);
        Assert.Equal("blue", ((StringArray)category.Dictionary).GetString(((Int16Array)category.Indices).GetValue(0)!.Value));
        Assert.Equal(1234.5000m, ((Decimal128Array)output.Column(4)).GetValue(0));
        var nested = (StructArray)output.Column(5);
        Assert.Equal("sample", ((StringArray)nested.Fields[0]).GetString(0));
    }

    [Fact]
    public async Task HttpTypedExchange_RejectsAnInferredAllNullWireSchema()
    {
        await using var worker = await PythonWorker.StartHttpAsync(Prefix);
        await using var client = new HttpRpcClient(worker.Address, new HttpRpcClientOptions { Prefix = Prefix });
        using var parameters = EmptyParameters();
        await using var exchange = await client.OpenExchangeAsync(
            "typed_exchange",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken);
        var inferredSchema = new Schema(
            s_typedExchangeSchema.FieldsList.Select(field => new Field(field.Name, NullType.Default, true)),
            null);
        using var inferred = new RecordBatch(
            inferredSchema,
            inferredSchema.FieldsList.Select(_ => (IArrowArray)new NullArray(1)),
            1);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => exchange.ExchangeAsync(inferred, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, error.StatusCode);
    }

    [Fact]
    public async Task HttpProducer_HandlesContinuationZeroRowAndTerminalTurns()
    {
        await using var worker = await PythonWorker.StartHttpAsync(Prefix, "--producer-turn-bytes", "16384");
        await using var client = new HttpRpcClient(worker.Address, new HttpRpcClientOptions { Prefix = Prefix });

        using (var parameters = LongParameters(("count", 2), ("payload_bytes", 4)))
        await using (var producer = await client.OpenProducerAsync(
            "producer_sequence",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            using var first = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            using var second = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            AssertProducer(first, 0, [0, 0, 0, 0]);
            AssertProducer(second, 1, [1, 1, 1, 1]);
            Assert.Null(await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        using (var parameters = EmptyParameters())
        await using (var producer = await client.OpenProducerAsync(
            "producer_zero_row_then_value",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            using var zero = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            AssertExactSchema(s_producerSchema, zero.Schema);
            Assert.Equal(0, zero.Length);
            using var value = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            AssertProducer(value, 7, "after-zero"u8.ToArray());
            Assert.Null(await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        using (var parameters = EmptyParameters())
        await using (var producer = await client.OpenProducerAsync(
            "producer_emit_and_finish",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            using var terminal = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            AssertProducer(terminal, 99, "terminal"u8.ToArray());
            Assert.Null(await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        using (var parameters = EmptyParameters())
        await using (var producer = await client.OpenProducerAsync(
            "producer_empty",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Null(await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task HttpStickySession_OpensResumesClosesAndRejectsStaleToken()
    {
        await using var worker = await PythonWorker.StartHttpAsync(Prefix, "--sticky");
        await using var client = new HttpRpcClient(worker.Address, new HttpRpcClientOptions { Prefix = Prefix });
        var capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.True(capabilities.StickyEnabled);
        Assert.Equal(60, capabilities.StickyDefaultTtl);
        Assert.Contains("X-VGI-Worker-Affinity", capabilities.StickyEchoHeaders);

        string token;
        await using (var session = client.WithSession())
        {
            Assert.Equal(10, await CallLongAsync(client, "open_client_session", ("initial", 10)));
            token = Assert.IsType<string>(session.CurrentToken);
            Assert.Equal(15, await CallLongAsync(client, "increment_client_session", ("by", 5)));
            Assert.Equal(13, await CallLongAsync(client, "increment_client_session", ("by", -2)));
            Assert.Equal(13, await CallLongAsync(client, "close_client_session"));
        }

        await using var stale = client.WithSession(token);
        var error = await Assert.ThrowsAsync<SessionLostException>(
            () => CallLongAsync(client, "increment_client_session", ("by", 1)));
        Assert.Contains("session", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HttpExternalization_RoundTripsResponsePointersAndOversizedRequests()
    {
        await using var worker = await PythonWorker.StartHttpAsync(
            Prefix,
            "--external",
            "--external-threshold",
            "4096");
        await using var client = new HttpRpcClient(
            worker.Address,
            new HttpRpcClientOptions
            {
                Prefix = Prefix,
                ExternalLocation = new ClientExternalConfig { UrlValidator = null },
            });
        var capabilities = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
        Assert.True(capabilities.ExternalizationEnabled);
        Assert.True(capabilities.UploadUrlSupport);
        Assert.Equal(4096, capabilities.MaxRequestBytes);

        var expected = Enumerable.Range(0, 32 * 1024).Select(index => (byte)(index % 251)).ToArray();
        using var size = LongParameters(("size", expected.Length));
        using var response = (await client.CallUnaryAsync(
            "large_response",
            size,
            cancellationToken: TestContext.Current.CancellationToken)).Batch;
        Assert.Equal(expected, ((BinaryArray)response.Column(0)).GetBytes(0).ToArray());

        using var echo = BinaryParameters("value", expected);
        using var echoed = (await client.CallUnaryAsync(
            "echo_bytes",
            echo,
            cancellationToken: TestContext.Current.CancellationToken)).Batch;
        Assert.Equal(expected, ((BinaryArray)echoed.Column(0)).GetBytes(0).ToArray());
    }

    [Fact]
    public async Task HttpsWorker_RequiresAndAcceptsItsPublishedTrustRoot()
    {
        await using var worker = await PythonWorker.StartHttpsAsync(Prefix);
        await using (var untrusted = new HttpRpcClient(worker.Address, new HttpRpcClientOptions { Prefix = Prefix }))
        {
            await Assert.ThrowsAsync<HttpRequestException>(
                () => untrusted.GetCapabilitiesAsync(TestContext.Current.CancellationToken));
        }

        using var root = X509CertificateLoader.LoadCertificateFromFile(worker.CaPath!);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            },
        };
        using var http = new System.Net.Http.HttpClient(handler) { BaseAddress = worker.Address };
        await using var trusted = new HttpRpcClient(http, new HttpRpcClientOptions { Prefix = Prefix });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                _ = await trusted.GetCapabilitiesAsync(timeout.Token);
                break;
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(25, timeout.Token);
            }
        }
    }

    [Fact]
    public async Task RawWorkers_RoundTripOverStdioSharedMemoryUnixAndTcp()
    {
        var python = PythonExecutable();
        var payload = Enumerable.Range(0, 512 * 1024).Select(index => (byte)(index % 251)).ToArray();
        await using (var client = RpcClient.StartSubprocess(
            [python, "-m", "vgi_rpc.conformance.client_worker", "--stdio"],
            new RpcClientOptions { SharedMemorySize = 2 * 1024 * 1024 },
            SubprocessStderrMode.Discard))
        {
            await AssertBinaryEchoAsync(client, payload);
            await AssertRawProducerAsync(client);
            await AssertRawTypedExchangeAsync(client);
        }

        if (!OperatingSystem.IsWindows())
        {
            var socketPath = Path.Combine(Path.GetTempPath(), $"vgi-csharp-{Guid.NewGuid():n}.sock");
            await using var unixWorker = await PythonWorker.StartSocketAsync("--unix", socketPath);
            await using var unix = await RpcClient.ConnectUnixAsync(
                socketPath,
                cancellationToken: TestContext.Current.CancellationToken);
            await AssertBinaryEchoAsync(unix, "unix"u8.ToArray());
        }

        await using var tcpWorker = await PythonWorker.StartSocketAsync("--tcp", "127.0.0.1:0");
        var address = tcpWorker.Discovery["TCP:".Length..];
        var separator = address.LastIndexOf(':');
        await using var tcp = await RpcClient.ConnectTcpAsync(
            address[..separator],
            int.Parse(address[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken: TestContext.Current.CancellationToken);
        await AssertBinaryEchoAsync(tcp, "tcp"u8.ToArray());
    }

    private static async Task AssertRawProducerAsync(RpcClient client)
    {
        using var parameters = LongParameters(("count", 3), ("payload_bytes", 4));
        await using var producer = await client.OpenProducerAsync(
            "producer_sequence",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken);
        for (var index = 0; index < 3; index++)
        {
            using var item = (await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            AssertProducer(item, index, Enumerable.Repeat((byte)index, 4).ToArray());
        }

        Assert.Null(await producer.ReadNextAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task AssertRawTypedExchangeAsync(RpcClient client)
    {
        using (var parameters = EmptyParameters())
        await using (var exchange = await client.OpenExchangeAsync(
            "typed_exchange",
            parameters,
            s_typedExchangeSchema,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            using var input = ValueCodec.BuildRow(s_typedExchangeSchema, [null, null, null, null, null, null]);
            using var output = (await exchange.ExchangeAsync(
                input,
                cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            AssertExactSchema(s_typedExchangeSchema, output.Schema);
        }

        using (var parameters = EmptyParameters())
        await using (var exchange = await client.OpenExchangeAsync(
            "typed_exchange",
            parameters,
            s_typedExchangeSchema,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            using var empty = ValueCodec.EmptyRow(s_typedExchangeSchema);
            await AssertPyArrowCanReadAsync(empty);
            using var echoedEmpty = (await exchange.ExchangeAsync(
                empty,
                cancellationToken: TestContext.Current.CancellationToken))!.Batch;
            AssertExactSchema(s_typedExchangeSchema, echoedEmpty.Schema);
            Assert.Equal(0, echoedEmpty.Length);
        }
    }

    private static async Task AssertPyArrowCanReadAsync(RecordBatch batch)
    {
        var error = await PyArrowReadErrorAsync(batch);
        if (error is null)
        {
            return;
        }

        var failures = new List<string>();
        foreach (var field in batch.Schema.FieldsList)
        {
            using var fieldBatch = ValueCodec.EmptyRow(new Schema([field], null));
            if (await PyArrowReadErrorAsync(fieldBatch) is { } fieldError)
            {
                failures.Add($"{field.Name}: {fieldError}");
            }
        }

        Assert.Fail($"Full schema: {error}\nPer-field failures:\n{string.Join("\n", failures)}");
    }

    private static async Task<string?> PyArrowReadErrorAsync(RecordBatch batch)
    {
        var bytes = await ExternalLocation.SerializeBatchAsync(batch, null, TestContext.Current.CancellationToken);
        var info = new ProcessStartInfo(PythonExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import sys, pyarrow as pa; b=pa.ipc.open_stream(sys.stdin.buffer).read_next_batch(); assert b.num_rows == 0");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start pyarrow IPC probe.");
        await process.StandardInput.BaseStream.WriteAsync(bytes, TestContext.Current.CancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process.ExitCode == 0
            ? null
            : await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertBinaryEchoAsync(RpcClient client, byte[] value)
    {
        using var parameters = BinaryParameters("value", value);
        using var response = (await client.CallUnaryAsync(
            "echo_bytes",
            parameters,
            cancellationToken: TestContext.Current.CancellationToken)).Batch;
        Assert.Equal(value, ((BinaryArray)response.Column(0)).GetBytes(0).ToArray());
    }

    private static async Task<long> CallLongAsync(
        HttpRpcClient client,
        string method,
        params (string Name, long Value)[] values)
    {
        using var parameters = LongParameters(values);
        using var response = (await client.CallUnaryAsync(
            method,
            parameters,
            cancellationToken: TestContext.Current.CancellationToken)).Batch;
        return ((Int64Array)response.Column(0)).GetValue(0)!.Value;
    }

    private static void AssertProducer(RecordBatch batch, long index, byte[] payload)
    {
        AssertExactSchema(s_producerSchema, batch.Schema);
        Assert.Equal(index, ((Int64Array)batch.Column(0)).GetValue(0));
        Assert.Equal(payload, ((BinaryArray)batch.Column(1)).GetBytes(0).ToArray());
    }

    private static RecordBatch EmptyParameters() => new(s_emptySchema, [], 1);

    private static RecordBatch LongParameters(params (string Name, long Value)[] values)
    {
        var schema = new Schema(values.Select(value => new Field(value.Name, Int64Type.Default, false)), null);
        return new RecordBatch(schema, values.Select(value => (IArrowArray)new Int64Array.Builder().Append(value.Value).Build()), 1);
    }

    private static RecordBatch BinaryParameters(string name, byte[] value)
    {
        var schema = new Schema([new Field(name, BinaryType.Default, false)], null);
        return new RecordBatch(schema, [new BinaryArray.Builder().Append(value).Build()], 1);
    }

    private static RecordBatch PopulatedTypedBatch()
    {
        var tags = new ListArray.Builder(((ListType)s_typedExchangeSchema.GetFieldByIndex(1).DataType).ValueField);
        tags.Append();
        ((StringArray.Builder)tags.ValueBuilder).Append("alpha").AppendNull().Append("omega");

        var dictionaryType = (DictionaryType)s_typedExchangeSchema.GetFieldByIndex(2).DataType;
        var category = new DictionaryArray(
            dictionaryType,
            new Int16Array.Builder().Append(0).Build(),
            new StringArray.Builder().Append("blue").Build());

        var nestedType = (StructType)s_typedExchangeSchema.GetFieldByIndex(5).DataType;
        var scores = new ListArray.Builder(((ListType)nestedType.Fields[1].DataType).ValueField);
        scores.Append();
        ((Int32Array.Builder)scores.ValueBuilder).Append(1).AppendNull().Append(3);
        var nested = new StructArray(
            nestedType,
            1,
            [new StringArray.Builder().Append("sample").Build(), scores.Build()],
            default);

        return new RecordBatch(
            s_typedExchangeSchema,
            [
                new DoubleArray.Builder().Append(1.5).Build(),
                tags.Build(),
                category,
                new TimestampArray.Builder((TimestampType)s_typedExchangeSchema.GetFieldByIndex(3).DataType)
                    .Append(new DateTimeOffset(2026, 8, 18, 12, 34, 56, TimeSpan.Zero)).Build(),
                new Decimal128Array.Builder((Decimal128Type)s_typedExchangeSchema.GetFieldByIndex(4).DataType)
                    .Append(1234.5000m).Build(),
                nested,
            ],
            1);
    }

    private static Schema BuildTypedExchangeSchema()
    {
        var tags = new ListType(new Field("item", StringType.Default, true));
        var category = new DictionaryType(Int16Type.Default, StringType.Default, false);
        var scores = new ListType(new Field("item", Int32Type.Default, true));
        var nested = new StructType(
            [
                new Field("name", StringType.Default, true),
                new Field("scores", scores, true),
            ]);
        return new Schema(
            [
                new Field("nullable_float", DoubleType.Default, true),
                new Field("tags", tags, true),
                new Field("category", category, true),
                new Field("event_time", new TimestampType(TimeUnit.Microsecond, "UTC"), true),
                new Field("amount", new Decimal128Type(18, 4), true),
                new Field("nested", nested, true),
            ],
            null);
    }

    private static void AssertExactSchema(Schema expected, Schema actual)
    {
        Assert.Equal(expected.FieldsList.Count, actual.FieldsList.Count);
        for (var index = 0; index < expected.FieldsList.Count; index++)
        {
            var expectedField = expected.GetFieldByIndex(index);
            var actualField = actual.GetFieldByIndex(index);
            Assert.Equal(expectedField.Name, actualField.Name);
            Assert.Equal(expectedField.IsNullable, actualField.IsNullable);
            Assert.Equal(expectedField.DataType.ToString(), actualField.DataType.ToString());
        }
    }

    private static string PythonExecutable()
    {
        var executable = Environment.GetEnvironmentVariable("VGI_PYTHON_BIN");
        if (string.IsNullOrWhiteSpace(executable))
        {
            Assert.Skip("Set VGI_PYTHON_BIN to run the Python native-client worker acceptance tests.");
        }

        return executable!;
    }

    private sealed class PythonWorker : IAsyncDisposable
    {
        private readonly Process _process;

        private PythonWorker(Process process, string discovery, bool tls, string? caPath)
        {
            _process = process;
            Discovery = discovery;
            CaPath = caPath;
            if (discovery.StartsWith("PORT:", StringComparison.Ordinal))
            {
                Address = new Uri($"{(tls ? "https" : "http")}://127.0.0.1:{discovery["PORT:".Length..]}");
            }
        }

        public Uri Address { get; } = null!;

        public string Discovery { get; }

        public string? CaPath { get; }

        public static async Task<PythonWorker> StartHttpAsync(string prefix, params string[] extra)
        {
            var worker = await StartAsync(["--http", "0", "--prefix", prefix, .. extra]);
            try
            {
                using var http = new System.Net.Http.HttpClient { BaseAddress = worker.Address };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    try
                    {
                        using var response = await http.GetAsync($"{prefix}/health", timeout.Token);
                        response.EnsureSuccessStatusCode();
                        return worker;
                    }
                    catch (HttpRequestException) when (!timeout.IsCancellationRequested)
                    {
                        await Task.Delay(25, timeout.Token);
                    }
                }
            }
            catch
            {
                await worker.DisposeAsync();
                throw;
            }
        }

        public static Task<PythonWorker> StartSocketAsync(params string[] args) => StartAsync(args);

        public static Task<PythonWorker> StartHttpsAsync(string prefix) =>
            StartAsync(["--http", "0", "--prefix", prefix, "--tls"], tls: true);

        private static async Task<PythonWorker> StartAsync(IReadOnlyList<string> args, bool tls = false)
        {
            var info = new ProcessStartInfo(PythonExecutable())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-m");
            info.ArgumentList.Add("vgi_rpc.conformance.client_worker");
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start Python client worker.");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var discovery = await process.StandardOutput.ReadLineAsync(timeout.Token)
                    ?? throw new InvalidOperationException($"Python worker exited before discovery: {await process.StandardError.ReadToEndAsync(timeout.Token)}");
                if (!discovery.StartsWith("PORT:", StringComparison.Ordinal)
                    && !discovery.StartsWith("UNIX:", StringComparison.Ordinal)
                    && !discovery.StartsWith("TCP:", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unexpected Python worker discovery line: '{discovery}'.");
                }

                string? caPath = null;
                if (tls)
                {
                    var caLine = await process.StandardOutput.ReadLineAsync(timeout.Token)
                        ?? throw new InvalidOperationException("TLS worker exited before publishing its CA path.");
                    if (!caLine.StartsWith("TLS-CA:", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Unexpected TLS CA discovery line: '{caLine}'.");
                    }

                    caPath = caLine["TLS-CA:".Length..];
                }

                return new PythonWorker(process, discovery, tls, caPath);
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
        }
    }
}
