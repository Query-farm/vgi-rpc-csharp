using System.Text.Json;
using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;
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

// Introspection format version, reported for readers who still look for it. Vestigial:
// introspection is a protocol whose major version is part of its own name, so there is no
// separate format number to negotiate and this will not move again.
const string DescribeVersion = "5";

// The prefix the framework reserves for its own co-hosted protocols.
const string ReservedProtocolPrefix = "vgi_rpc.";


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
                await HandleUnaryAsync(op, request);
                break;
            case "describe":
                await DescribeAsync();
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
    var (batch, metadata) = await ReadOneAsync(request["request_b64"]!.GetValue<string>());
    // A unary request always names its method. Defaulting to `__describe__` sent a retired
    // method under a request that had simply lost its routing metadata, so the answer named
    // the wrong problem entirely.
    var method = metadata?.GetValueOrDefault(MetadataKeys.Method)
        ?? throw new InvalidOperationException(
            $"unary op '{op}': the request batch carries no {MetadataKeys.Method} metadata.");

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

/// <summary>
/// The `describe` op: `vgi_rpc.Reflection.v1`, decoded, relayed as JSON.
/// </summary>
/// <remarks>
/// <para>
/// This used to be `__describe__` — one hardcoded method answering one flat batch, which is the
/// shape the driving harness (`rust_client_proxy.describe`, reached through VGI_CLIENT_DRIVER)
/// used to parse. Both ends have moved: every server in the fleet refuses `__describe__` now, and
/// the harness asks the driver for an already-decoded description because reflection's reply is
/// two nested payloads rather than one batch, and relaying raw Arrow would make the Python shim
/// re-implement the reflection schema.
/// </para>
/// <para>
/// Two round trips, the documented way: `list_protocols` for what the server hosts and its
/// identity, then `describe` on the first protocol that is not framework-owned. The two
/// server-identity fields live on the listing and not on the description, because two processes
/// serving the same protocol must describe it identically or the description is not a property of
/// the protocol.
/// </para>
/// <para>
/// The per-method schemas are relayed as the server's own bytes rather than decoded and
/// re-encoded here: `params_schema_ipc` is already an IPC stream, which is exactly what the
/// harness opens. Re-encoding would only add a place for this shim to disagree with the server
/// about a schema neither of them authored.
/// </para>
/// </remarks>
async Task DescribeAsync()
{
    using var listing = await ReflectionCallAsync(
        ReflectionProtocol.ListProtocolsMethod,
        new RecordBatch(new Schema([], null), [], 1));

    var (summaries, summaryStart, summaryEnd) = StructList(listing, "protocols");
    var names = StructColumn<StringArray>(summaries, "protocol");
    string? primary = null;
    var hosted = new List<string>();
    for (var i = summaryStart; i < summaryEnd; i++)
    {
        var name = names.GetString(i);
        hosted.Add(name);
        // The framework's own protocols are co-hosted beside the application surface; the one a
        // client means by "describe this server" is the one that is not framework-owned.
        if (primary is null && !name.StartsWith(ReservedProtocolPrefix, StringComparison.Ordinal))
        {
            primary = name;
        }
    }

    if (primary is null)
    {
        throw new RpcException(
            "ProtocolError",
            $"Server '{Row0<StringArray>(listing, "server_id").GetString(0)}' hosts no application "
                + $"protocol; it lists only [{string.Join(", ", hosted)}].");
    }

    using var described = await ReflectionCallAsync(
        ReflectionProtocol.DescribeMethod,
        new RecordBatch(
            new Schema([new Field("protocol", StringType.Default, nullable: false)], null),
            [new StringArray.Builder().Append(primary).Build()],
            1));

    var (methodRows, methodStart, methodEnd) = StructList(described, "methods");
    var methodName = StructColumn<StringArray>(methodRows, "name");
    var methodType = StructColumn<StringArray>(methodRows, "method_type");
    var hasReturn = StructColumn<BooleanArray>(methodRows, "has_return");
    var hasHeader = StructColumn<BooleanArray>(methodRows, "has_header");
    var streamKind = StructColumn<StringArray>(methodRows, "stream_kind");
    var paramsIpc = StructColumn<BinaryArray>(methodRows, "params_schema_ipc");
    var resultIpc = StructColumn<BinaryArray>(methodRows, "result_schema_ipc");
    var headerIpc = StructColumn<BinaryArray>(methodRows, "header_schema_ipc");

    var methods = new JsonArray();
    for (var i = methodStart; i < methodEnd; i++)
    {
        var header = hasHeader.GetValue(i) == true;
        methods.Add(new JsonObject
        {
            ["name"] = methodName.GetString(i),
            ["method_type"] = methodType.GetString(i),
            ["has_return"] = hasReturn.GetValue(i) == true,
            ["has_header"] = header,
            ["is_exchange"] = IsExchange(streamKind.GetString(i)),
            ["params_schema_b64"] = SchemaBase64(paramsIpc, i),
            ["result_schema_b64"] = SchemaBase64(resultIpc, i),
            ["header_schema_b64"] = header ? SchemaBase64(headerIpc, i) : null,
        });
    }

    await ReplyAsync(new JsonObject
    {
        ["ok"] = true,
        ["describe"] = new JsonObject
        {
            ["protocol_name"] = Row0<StringArray>(described, "protocol").GetString(0),
            // From the listing hop: server identity is a property of the server, so the
            // description deliberately does not carry it.
            ["request_version"] = Row0<StringArray>(listing, "request_version").GetString(0),
            ["server_id"] = Row0<StringArray>(listing, "server_id").GetString(0),
            ["describe_version"] = DescribeVersion,
            ["protocol_hash"] = Row0<StringArray>(described, "protocol_hash").GetString(0),
            ["protocol_version"] = Row0<StringArray>(described, "protocol_version").GetString(0),
            ["methods"] = methods,
        },
        ["logs"] = DrainLogs(logs),
        ["error"] = null,
    });
}

/// <summary>One unary call on <c>vgi_rpc.Reflection.v1</c>, unwrapped to its nested payload.</summary>
/// <remarks>
/// Reflection is an ordinary co-hosted protocol, so its reply obeys the ordinary unary convention
/// for a structured return: the payload serialized into a single non-null <c>result</c> binary
/// column. The connection stays a client of the application protocol — only this call is
/// addressed elsewhere.
/// </remarks>
async Task<RecordBatch> ReflectionCallAsync(string method, RecordBatch parameters)
{
    using (parameters)
    {
        var reply = native is not null
            ? await native.CallUnaryOnAsync(ReflectionProtocol.ProtocolName, method, parameters)
            : await RequireHttp().CallUnaryOnAsync(ReflectionProtocol.ProtocolName, method, parameters);
        using (reply.Batch)
        {
            if (reply.Batch.Column("result") is not BinaryArray result || result.Length == 0 || result.IsNull(0))
            {
                throw new RpcException(
                    "ProtocolError", $"reflection '{method}' reply carries no 'result' payload.");
            }

            using var stream = new MemoryStream(result.GetBytes(0).ToArray());
            using var reader = new WireReader(stream);
            await reader.ReadSchemaAsync();
            var item = await reader.ReadNextAsync()
                ?? throw new RpcException(
                    "ProtocolError", $"reflection '{method}' payload carried no batch.");
            return item.Batch;
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
    // This driver is schema-first -- it builds every batch from the harness's JSON, so there is
    // no contract type to read the protocol from. The harness may name one (to drive a co-hosted
    // framework protocol); absent that, a conformance worker hosts exactly one application
    // protocol and this is its name.
    var protocol = request["protocol"]?.GetValue<string>() is { Length: > 0 } named
        ? named
        : "ConformanceService";
    // `external` is not an HTTP flag: WIRE_PROTOCOL.md §12 governs pointer batches on every
    // transport, and the harness sets it on the byte-stream connections too. The validator is
    // left open because a conformance run's storage is a plain-HTTP loopback fake, which the
    // shipped HTTPS-only default correctly refuses; nothing outside this driver inherits it.
    var external = request["external"]?.GetValue<bool>() == true
        ? new QueryFarm.VgiRpc.External.ClientExternalConfig { UrlValidator = null }
        : null;
    var options = new RpcClientOptions { OnLog = logs.Add, Protocol = protocol, ExternalLocation = external };
    switch (transport)
    {
        case "stdio":
            return (RpcClient.StartSubprocess(Arguments(request), options), null);
        case "shm":
            options = new RpcClientOptions
            {
                OnLog = logs.Add,
                Protocol = protocol,
                ExternalLocation = external,
                SharedMemorySize = request["shm_size"]?.GetValue<long>() ?? 4 * 1024 * 1024,
            };
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
                Protocol = protocol,
                CompressionLevel = compressionLevel,
                DefaultHeaders = headers,
                OnLog = logs.Add,
                ExternalLocation = external,
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

/// <summary>A single-row payload's top-level column.</summary>
static T Row0<T>(RecordBatch payload, string field) where T : IArrowArray =>
    payload.Column(field) is T column
        ? column
        : throw new RpcException("ProtocolError", $"reflection payload missing '{field}'.");

/// <summary>The struct elements of a single-row payload's list column, as [start, end).</summary>
static (StructArray Values, int Start, int End) StructList(RecordBatch payload, string field)
{
    var list = Row0<ListArray>(payload, field);
    if (list.Length == 0 || list.IsNull(0))
    {
        throw new RpcException("ProtocolError", $"reflection payload missing '{field}'.");
    }

    return ((StructArray)list.Values, list.ValueOffsets[0], list.ValueOffsets[1]);
}

/// <summary>One named child of a struct array, by declared field order.</summary>
static T StructColumn<T>(StructArray rows, string field) where T : IArrowArray
{
    var index = ((StructType)rows.Data.DataType).Fields.ToList().FindIndex(f => f.Name == field);
    return index >= 0 && rows.Fields[index] is T column
        ? column
        : throw new RpcException("ProtocolError", $"reflection method table missing '{field}'.");
}

/// <summary>
/// A method's schema as the server serialized it, or null for an absent one.
/// </summary>
/// <remarks>
/// Absent is spelled as empty bytes rather than null on the wire, so that a port need not
/// null-check a value it will only ever treat as absent.
/// </remarks>
static JsonNode? SchemaBase64(BinaryArray column, int row)
{
    if (column.IsNull(row))
    {
        return null;
    }

    var bytes = column.GetBytes(row);
    return bytes.Length == 0 ? null : JsonValue.Create(Convert.ToBase64String(bytes.ToArray()));
}

/// <summary>
/// A stream kind as the tri-state "does this stream accept input" the client-side view uses.
/// </summary>
/// <remarks>
/// Unary (<c>""</c>) and <c>"unknown"</c> both become null: a server describing its own surface
/// often genuinely cannot say, and "unknown" is the honest answer rather than a missing one.
/// </remarks>
static JsonNode? IsExchange(string streamKind) => streamKind switch
{
    "exchange" => JsonValue.Create(true),
    "producer" => JsonValue.Create(false),
    _ => null,
};

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
