// HTTP client that connects to the demo HTTP server.
//
// This repo doesn't ship a typed HTTP client proxy yet (see the root
// README's Transports section) — every call this port's in-process/
// subprocess clients make ultimately builds one request RecordBatch and
// reads one response RecordBatch via WireWriter/WireReader, so that's what
// this example does directly against HttpClient. A typed HTTP
// RpcConnection-equivalent is a natural thing to add on top of this later.
//
// Start the server first:
//
//     dotnet run --project examples/04-http/Server
//
// Then run this:
//
//     dotnet run --project examples/04-http/Client

using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;

const int Port = 8234;

// This interface is duplicated from Server/Program.cs so each side is
// self-contained. In a real project you'd define it once in a shared
// project and reference it from both.
var info = ServiceRegistry.GetMethods(typeof(IDemoService)).Values.Single();

using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };

var result = (string?)await CallUnaryAsync(http, info, ["Hello from HTTP!"]);
Console.WriteLine($"echo: {result}");

// Builds one request batch, POSTs it to "/{wireMethodName}", and decodes the
// one response batch — the same shape RpcConnection<T>.CallUnaryAsync uses
// for the in-process/subprocess transports, just over HTTP instead of a
// persistent stream.
static async Task<object?> CallUnaryAsync(HttpClient http, RpcMethodInfo info, object?[] args)
{
    var requestBatch = ValueCodec.BuildRow(info.ParamsSchema, args);
    var requestMetadata = new Dictionary<string, string>
    {
        [MetadataKeys.Method] = info.WireName,
        [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
    };

    using var requestStream = new MemoryStream();
    await using (var writer = new WireWriter(requestStream, info.ParamsSchema))
    {
        await writer.WriteBatchAsync(new AnnotatedBatch(requestBatch, requestMetadata));
    }

    using var content = new ByteArrayContent(requestStream.ToArray());
    content.Headers.Add("Content-Type", RpcHttpEndpoints.ArrowContentType);

    using var response = await http.PostAsync($"/{info.WireName}", content);
    await using var responseStream = await response.Content.ReadAsStreamAsync();

    using var reader = new WireReader(responseStream);
    await reader.ReadSchemaAsync();

    AnnotatedBatch? terminal = null;
    while (await reader.ReadNextAsync() is { } batch)
    {
        if (batch.GetMetadata(MetadataKeys.LogLevel) == "EXCEPTION")
        {
            throw new RpcException("RpcException", batch.GetMetadata(MetadataKeys.LogMessage) ?? "Remote error");
        }

        terminal = batch;
    }

    if (info.ResultSchema.FieldsList.Count == 0 || terminal is null)
    {
        return null;
    }

    return ValueCodec.ExtractRow(terminal.Batch, [info.ResultClrType])[0];
}

public interface IDemoService
{
    Task<string> EchoAsync(string message);
}
