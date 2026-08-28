using System.Text.Json;
using System.Text.Json.Nodes;
using Apache.Arrow;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;

RpcClient? native = null;
HttpRpcClient? http = null;
RpcProducerSession? nativeProducer = null;
RpcExchangeSession? nativeExchange = null;
HttpProducerSession? httpProducer = null;
HttpExchangeSession? httpExchange = null;
var logs = new List<LogMessage>();

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonObject request;
    try
    {
        request = JsonNode.Parse(line)!.AsObject();
    }
    catch (Exception exception)
    {
        await ReplyAsync(new JsonObject { ["ok"] = false, ["error"] = $"bad json: {exception.Message}" });
        continue;
    }

    try
    {
        var op = request["op"]?.GetValue<string>() ?? "";
        switch (op)
        {
            case "connect":
                (native, http) = await ConnectAsync(request, logs);
                await ReplyAsync(Ok());
                break;
            case "unary":
            case "describe":
                await HandleUnaryAsync(op, request);
                break;
            case "stream_open":
                await OpenStreamAsync(request);
                break;
            case "tick":
            case "next_with_token":
                await TickAsync(op == "next_with_token", request);
                break;
            case "exchange":
                await ExchangeAsync(request);
                break;
            case "cancel":
                await CancelStreamAsync();
                await ReplyAsync(new JsonObject { ["ok"] = true, ["logs"] = DrainLogs(logs) });
                await ClearStreamAsync();
                break;
            case "close":
                await ClearStreamAsync();
                await ReplyAsync(Ok());
                break;
            case "capabilities":
                await CapabilitiesAsync();
                break;
            case "request_upload_urls":
                var urls = await RequireHttp().RequestUploadUrlsAsync(request["count"]?.GetValue<int>() ?? 1);
                await ReplyAsync(new JsonObject
                {
                    ["ok"] = true,
                    ["urls"] = new JsonArray(urls.Select(url => new JsonObject
                    {
                        ["upload_url"] = url.Upload,
                        ["download_url"] = url.Download,
                        ["expires_at"] = url.ExpiresAt.ToUnixTimeSeconds(),
                    }).ToArray()),
                });
                break;
            case "session_begin":
                RequireHttp().BeginSession();
                if (request["token"]?.GetValue<string>() is { Length: > 0 } token) RequireHttp().AttachSession(token);
                await ReplyAsync(Ok());
                break;
            case "session_token":
                await ReplyAsync(new JsonObject { ["ok"] = true, ["token"] = RequireHttp().SessionToken });
                break;
            case "session_echo_headers":
                await ReplyAsync(new JsonObject { ["ok"] = true, ["headers"] = JsonSerializer.SerializeToNode(RequireHttp().LastEchoHeaders) });
                break;
            case "session_detach":
                await ReplyAsync(new JsonObject { ["ok"] = true, ["token"] = RequireHttp().DetachSession() });
                break;
            case "session_end":
                await RequireHttp().EndSessionAsync();
                await ReplyAsync(Ok());
                break;
            case "shutdown":
                await ClearStreamAsync();
                if (native is not null) await native.DisposeAsync();
                if (http is not null) await http.DisposeAsync();
                await ReplyAsync(Ok());
                return;
            default:
                await ReplyAsync(new JsonObject { ["ok"] = false, ["error"] = $"unknown op: {op}" });
                break;
        }
    }
    catch (RpcException exception)
    {
        await ReplyAsync(new JsonObject { ["ok"] = true, ["done"] = true, ["logs"] = DrainLogs(logs), ["error"] = Error(exception) });
    }
    catch (Exception exception)
    {
        await ReplyAsync(new JsonObject { ["ok"] = false, ["error"] = exception.ToString() });
    }
}

async Task HandleUnaryAsync(string op, JsonObject request)
{
    RecordBatch batch;
    IReadOnlyDictionary<string, string>? metadata;
    string method;
    if (op == "describe")
    {
        batch = ValueCodec.EmptyRow(new Schema([], null));
        metadata = null;
        method = "__describe__";
    }
    else
    {
        (batch, metadata) = await ReadOneAsync(request["request_b64"]!.GetValue<string>());
        method = metadata?.GetValueOrDefault(MetadataKeys.Method) ?? "__describe__";
    }

    using (batch)
    {
        var result = native is not null
            ? await native.CallUnaryAsync(method, batch, metadata)
            : await RequireHttp().CallUnaryAsync(method, batch, metadata);
        using (result.Batch)
        {
            await ReplyAsync(new JsonObject
            {
                ["ok"] = true,
                ["result_b64"] = await WriteOneAsync(result),
                ["logs"] = DrainLogs(logs),
                ["error"] = null,
            });
        }
    }
}

async Task OpenStreamAsync(JsonObject request)
{
    var (batch, metadata) = await ReadOneAsync(request["request_b64"]!.GetValue<string>());
    using (batch)
    {
        var method = metadata?.GetValueOrDefault(MetadataKeys.Method) ?? throw new InvalidDataException("stream request has no method");
        var exchange = request["is_exchange"]?.GetValue<bool>() == true
            || method.StartsWith("exchange_", StringComparison.Ordinal)
            || method == "cancellable_exchange";
        var hasHeader = request["has_header"]?.GetValue<bool>() ?? false;
        if (native is not null)
        {
            if (exchange)
            {
                nativeExchange = await native.OpenExchangeAsync(method, batch, hasHeader, metadata);
            }
            else
            {
                nativeProducer = await native.OpenProducerAsync(method, batch, hasHeader, metadata);
            }
        }
        else if (exchange)
        {
            httpExchange = await RequireHttp().OpenExchangeAsync(method, batch, hasHeader, metadata);
        }
        else
        {
            httpProducer = await RequireHttp().OpenProducerAsync(method, batch, hasHeader, metadata);
        }

        var header = nativeProducer?.Header ?? nativeExchange?.Header ?? httpProducer?.Header ?? httpExchange?.Header;
        await ReplyAsync(new JsonObject
        {
            ["ok"] = true,
            ["header_b64"] = header is null ? null : await WriteOneAsync(header),
            ["logs"] = DrainLogs(logs),
        });
    }
}

async Task TickAsync(bool withToken, JsonObject request)
{
    IReadOnlyDictionary<string, string>? metadata = null;
    if (request["input_b64"]?.GetValue<string>() is { } input)
    {
        var parsed = await ReadOneAsync(input);
        parsed.Batch.Dispose();
        metadata = parsed.Metadata;
    }

    AnnotatedBatch? item;
    string? token = null;
    if (nativeProducer is not null)
    {
        item = await nativeProducer.ReadNextAsync(metadata);
    }
    else
    {
        item = await RequireHttpProducer().ReadNextAsync(metadata);
        token = withToken ? RequireHttpProducer().ContinuationToken : null;
    }

    await StreamItemReplyAsync(item, token);
    if (item is null) await ClearStreamAsync();
}

async Task ExchangeAsync(JsonObject request)
{
    var (batch, metadata) = await ReadOneAsync(request["input_b64"]!.GetValue<string>());
    using (batch)
    {
        var item = nativeExchange is not null
            ? await nativeExchange.ExchangeAsync(batch, metadata)
            : await RequireHttpExchange().ExchangeAsync(batch, metadata);
        await StreamItemReplyAsync(item, null);
        if (item is null) await ClearStreamAsync();
    }
}

async Task StreamItemReplyAsync(AnnotatedBatch? item, string? token)
{
    if (item is null)
    {
        await ReplyAsync(new JsonObject { ["ok"] = true, ["done"] = true, ["batch_b64"] = null, ["token"] = null, ["logs"] = DrainLogs(logs), ["error"] = null });
        return;
    }

    using (item.Batch)
    {
        await ReplyAsync(new JsonObject { ["ok"] = true, ["done"] = false, ["batch_b64"] = await WriteOneAsync(item), ["token"] = token, ["logs"] = DrainLogs(logs), ["error"] = null });
    }
}

async Task CapabilitiesAsync()
{
    var caps = await RequireHttp().GetCapabilitiesAsync();
    await ReplyAsync(new JsonObject
    {
        ["ok"] = true,
        ["caps"] = new JsonObject
        {
            ["sticky_enabled"] = caps.StickyEnabled,
            ["sticky_default_ttl"] = caps.StickyDefaultTtl,
            ["sticky_echo_headers"] = new JsonArray(caps.StickyEchoHeaders.Select(value => JsonValue.Create(value)).ToArray()),
            ["upload_url_support"] = caps.UploadUrlSupport,
            ["max_request_bytes"] = caps.MaxRequestBytes,
            ["max_response_bytes"] = caps.MaxResponseBytes,
            ["max_externalized_response_bytes"] = caps.MaxExternalizedResponseBytes,
            ["externalization_enabled"] = caps.ExternalizationEnabled,
            ["max_upload_bytes"] = caps.MaxUploadBytes,
            ["supported_encodings"] = new JsonArray(caps.SupportedEncodings.Select(value => JsonValue.Create(value.ToString().ToLowerInvariant())).ToArray()),
        },
    });
}

async Task CancelStreamAsync()
{
    if (nativeProducer is not null) await nativeProducer.CancelAsync();
    if (nativeExchange is not null) await nativeExchange.CancelAsync();
    if (httpProducer is not null) await httpProducer.CancelAsync();
    if (httpExchange is not null) await httpExchange.CancelAsync();
}

async Task ClearStreamAsync()
{
    if (nativeProducer is not null) await nativeProducer.DisposeAsync();
    if (nativeExchange is not null) await nativeExchange.DisposeAsync();
    if (httpProducer is not null) await httpProducer.DisposeAsync();
    if (httpExchange is not null) await httpExchange.DisposeAsync();
    nativeProducer = null;
    nativeExchange = null;
    httpProducer = null;
    httpExchange = null;
}

static async Task<(RpcClient? Native, HttpRpcClient? Http)> ConnectAsync(JsonObject request, List<LogMessage> logs)
{
    var transport = request["transport"]?.GetValue<string>() ?? "";
    var options = new RpcClientOptions { OnLog = logs.Add };
    switch (transport)
    {
        case "stdio":
            return (RpcClient.StartSubprocess(Arguments(request), options), null);
        case "shm":
            options = new RpcClientOptions { OnLog = logs.Add, SharedMemorySize = request["shm_size"]?.GetValue<long>() ?? 4 * 1024 * 1024 };
            return (RpcClient.StartSubprocess(Arguments(request), options), null);
        case "unix":
            return (await RpcClient.ConnectUnixAsync(request["target"]!.GetValue<string>(), options), null);
        case "tcp":
            var address = request["target"]!.GetValue<string>();
            var separator = address.LastIndexOf(':');
            var host = separator < 0 ? "127.0.0.1" : address[..separator];
            var port = int.Parse(separator < 0 ? address : address[(separator + 1)..]);
            return (await RpcClient.ConnectTcpAsync(string.IsNullOrEmpty(host) ? "127.0.0.1" : host, port, options), null);
        case "http":
            var headers = request["headers"]?.Deserialize<Dictionary<string, string>>();
            var compressionLevel = request.ContainsKey("compression_level") ? request["compression_level"]?.GetValue<int?>() : 3;
            return (null, new HttpRpcClient(new Uri(request["target"]!.GetValue<string>()), new HttpRpcClientOptions
            {
                CompressionLevel = compressionLevel,
                DefaultHeaders = headers,
                OnLog = logs.Add,
                ExternalLocation = request["external"]?.GetValue<bool>() == true
                    ? new QueryFarm.VgiRpc.Http.ClientExternalConfig { UrlValidator = null }
                    : null,
            }));
        default:
            throw new InvalidOperationException($"unknown transport: {transport}");
    }
}

static string[] Arguments(JsonObject request) => request["target"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();

static async Task<(RecordBatch Batch, IReadOnlyDictionary<string, string>? Metadata)> ReadOneAsync(string base64)
{
    using var stream = new MemoryStream(Convert.FromBase64String(base64));
    using var reader = new WireReader(stream);
    await reader.ReadSchemaAsync();
    var item = await reader.ReadNextAsync() ?? throw new InvalidDataException("IPC stream has no batch");
    return (item.Batch, item.Metadata);
}

static async Task<string> WriteOneAsync(AnnotatedBatch item)
{
    using var buffer = new MemoryStream();
    await using (var writer = new WireWriter(buffer, item.Batch.Schema)) await writer.WriteBatchAsync(item);
    return Convert.ToBase64String(buffer.ToArray());
}

static JsonObject Ok() => new() { ["ok"] = true };
static JsonObject Error(RpcException exception) => new() { ["error_type"] = exception.ErrorType, ["error_message"] = exception.ErrorMessage, ["traceback"] = exception.RemoteTraceback };

static JsonArray DrainLogs(List<LogMessage> logs)
{
    var result = new JsonArray(logs.Select(log => new JsonObject
    {
        ["level"] = log.Level.ToString().ToUpperInvariant(),
        ["message"] = log.Message,
        ["extra"] = JsonSerializer.SerializeToNode(log.Extra),
    }).ToArray());
    logs.Clear();
    return result;
}

static async Task ReplyAsync(JsonObject response)
{
    await Console.Out.WriteLineAsync(response.ToJsonString());
    await Console.Out.FlushAsync();
}

HttpRpcClient RequireHttp() => http ?? throw new InvalidOperationException("op requires http transport");
HttpProducerSession RequireHttpProducer() => httpProducer ?? throw new InvalidOperationException("no producer stream is open");
HttpExchangeSession RequireHttpExchange() => httpExchange ?? throw new InvalidOperationException("no exchange stream is open");
