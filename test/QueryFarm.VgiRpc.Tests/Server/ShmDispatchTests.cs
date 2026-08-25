using Apache.Arrow;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Shm;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

/// <summary>
/// Direct <see cref="RpcServer"/>-level coverage for M14's SHM dispatch integration:
/// <c>__transport_options__</c> negotiation, dynamic per-request segment attach, request-pointer
/// resolution, and response-side offload for both unary and streaming turns. The real
/// cross-language interop path (a genuine Python client driving this port's worker over a real
/// segment) is verified separately — see docs/roadmap.md M14 — since this port's own client has
/// no SHM segment creation of its own (out of scope: conformance only ever needs the server side).
/// These tests instead hand-craft raw wire requests, bypassing <see cref="RpcConnection{T}"/>'s
/// proxy for the SHM-specific cases while still using it for the plain baseline calls.
/// </summary>
public sealed class ShmDispatchTests
{
    private static (QueryFarm.VgiRpc.Server.RpcServer Server, IGreeter Client, RpcConnection<IGreeter> Connection, IRpcTransport ServerTransport) Setup()
    {
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new QueryFarm.VgiRpc.Server.RpcServer(typeof(IGreeter), new Greeter());
        var connection = new RpcConnection<IGreeter>(clientTransport);
        return (server, connection.CreateProxy(), connection, serverTransport);
    }

    private static async Task WriteRawRequestAsync(Stream output, string methodName, Schema paramsSchema, RecordBatch batch, IReadOnlyDictionary<string, string>? extraMetadata = null)
    {
        var metadata = new Dictionary<string, string>
        {
            [MetadataKeys.Method] = methodName,
            [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
        };
        if (extraMetadata is not null)
        {
            foreach (var (key, value) in extraMetadata)
            {
                metadata[key] = value;
            }
        }

        var writer = new WireWriter(output, paramsSchema);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(batch, metadata)).ConfigureAwait(false);
        }
    }

    private static async Task<AnnotatedBatch?> ReadRawResponseAsync(Stream input)
    {
        using var reader = new WireReader(input);
        _ = await reader.ReadSchemaAsync().ConfigureAwait(false);
        return await reader.ReadNextAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task TransportOptions_AdvertisesShmTrue()
    {
        var (server, _, connection, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        await WriteRawRequestAsync(connection.Transport.Output, "__transport_options__", new Schema([], metadata: null), new RecordBatch(new Schema([], metadata: null), [], 1));
        var response = await ReadRawResponseAsync(connection.Transport.Input);

        Assert.True(await serveTask);
        Assert.NotNull(response);
        Assert.Equal("true", response!.GetMetadata(MetadataKeys.TransportShm));
        Assert.False(string.IsNullOrEmpty(response.GetMetadata(MetadataKeys.ServerId)));
    }

    [Fact]
    public async Task UnaryCall_WithShmPointerParameter_ResolvesCorrectly()
    {
        var (server, _, connection, serverTransport) = Setup();
        using var segment = ShmSegment.Create(1024 * 1024);
        try
        {
            var paramsSchema = new Schema([new Field("value", Apache.Arrow.Types.StringType.Default, nullable: false)], metadata: null);
            var realBatch = ValueCodec.BuildRow(paramsSchema, ["hello via shm"]);
            var written = await segment.AllocateAndWriteAsync(realBatch);
            Assert.NotNull(written);
            var (pointerBatch, pointerMetadata) = ShmPointerBatch.Make(paramsSchema, written!.Value.Offset, written.Value.Length);

            var extraMetadata = new Dictionary<string, string>(pointerMetadata)
            {
                [MetadataKeys.ShmSegmentName] = segment.Name,
                [MetadataKeys.ShmSegmentSize] = segment.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            var serveTask = server.ServeOneAsync(serverTransport);
            await WriteRawRequestAsync(connection.Transport.Output, "echo_string", paramsSchema, pointerBatch, extraMetadata);
            var response = await ReadRawResponseAsync(connection.Transport.Input);

            Assert.True(await serveTask);
            Assert.NotNull(response);
            var resultArray = (StringArray)response!.Batch.Column(0);
            Assert.Equal("hello via shm", resultArray.GetString(0));
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task UnaryCall_LargeResult_OffloadsToShm()
    {
        var (server, client, connection, serverTransport) = Setup();
        using var segment = ShmSegment.Create(8 * 1024 * 1024);
        try
        {
            var big = new string('y', 2 * 1024 * 1024); // comfortably above every platform default
            var paramsSchema = new Schema([new Field("value", Apache.Arrow.Types.StringType.Default, nullable: false)], metadata: null);
            var requestBatch = ValueCodec.BuildRow(paramsSchema, [big]);
            var extraMetadata = new Dictionary<string, string>
            {
                [MetadataKeys.ShmSegmentName] = segment.Name,
                [MetadataKeys.ShmSegmentSize] = segment.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            var serveTask = server.ServeOneAsync(serverTransport);
            await WriteRawRequestAsync(connection.Transport.Output, "echo_string", paramsSchema, requestBatch, extraMetadata);
            var response = await ReadRawResponseAsync(connection.Transport.Input);

            Assert.True(await serveTask);
            Assert.NotNull(response);
            Assert.True(ShmPointerBatch.IsShmPointerBatch(response!.Batch, response.Metadata));

            var (resolved, _, release) = await ShmPointerBatch.ResolveAsync(response.Batch, response.Metadata, segment);
            var resultArray = (StringArray)resolved.Column(0);
            Assert.Equal(big, resultArray.GetString(0));
            release?.Invoke();
        }
        finally
        {
            segment.Unlink();
        }
    }

    [Fact]
    public async Task UnaryCall_MalformedShmMetadata_FallsBackGracefully()
    {
        var (server, _, connection, serverTransport) = Setup();
        var paramsSchema = new Schema([new Field("value", Apache.Arrow.Types.StringType.Default, nullable: false)], metadata: null);
        var requestBatch = ValueCodec.BuildRow(paramsSchema, ["plain call"]);
        var extraMetadata = new Dictionary<string, string>
        {
            [MetadataKeys.ShmSegmentName] = "this-segment-does-not-exist",
            [MetadataKeys.ShmSegmentSize] = "1048576",
        };

        var serveTask = server.ServeOneAsync(serverTransport);
        await WriteRawRequestAsync(connection.Transport.Output, "echo_string", paramsSchema, requestBatch, extraMetadata);
        var response = await ReadRawResponseAsync(connection.Transport.Input);

        Assert.True(await serveTask);
        Assert.NotNull(response);
        var resultArray = (StringArray)response!.Batch.Column(0);
        Assert.Equal("plain call", resultArray.GetString(0));
    }

    [Fact]
    public async Task PlainCall_WithoutShmMetadata_Unaffected()
    {
        var (server, client, connection, serverTransport) = Setup();
        var serveTask = server.ServeOneAsync(serverTransport);

        var result = await client.EchoStringAsync("no shm here");

        Assert.Equal("no shm here", result);
        Assert.True(await serveTask);
    }
}
