using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Apache.Arrow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using QueryFarm.VgiRpc.AccessLog;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Maps an <see cref="RpcServer"/> onto ASP.NET Core minimal-API routes, mirroring the canonical
/// Python repo's Falcon resources (<c>vgi_rpc/http/server/_resources.py</c>): <c>POST
/// {prefix}/{method}</c> for unary calls (plus <c>__describe__</c>), <c>GET/HEAD {prefix}/health</c>
/// for the mandatory auth-exempt discovery endpoint. Streaming (<c>/init</c>/<c>/exchange</c>) and
/// everything auth/cap/compression/external-storage-related are later milestones — see
/// docs/roadmap.md M6+; those two routes are registered now (structurally matching the porting
/// guide's endpoint contract) but answer a clear "not yet implemented" error rather than 404.
///
/// Dispatch here is necessarily a separate code path from <see cref="RpcServer.ServeOneAsync"/>,
/// not a reuse of it: HTTP is one request body in, one response body out, with no persistent
/// connection to drive a serve loop over — the same reason Python's own HTTP server has an
/// entirely separate <c>_app_unary.py</c> rather than calling into <c>_server.py</c>'s
/// <c>serve_one</c>.
/// </summary>
public static class RpcHttpEndpoints
{
    /// <summary>The one Content-Type every vgi-rpc HTTP request/response body must declare —
    /// mirrors Python's <c>_ARROW_CONTENT_TYPE</c>.</summary>
    public const string ArrowContentType = "application/vnd.apache.arrow.stream";

    /// <summary>Set (value <c>"true"</c>) on a 200 response whose body is actually an in-band
    /// error batch — mirrors Python's <c>RPC_ERROR_HEADER</c> / <c>_set_http_status</c>'s
    /// 500→200 translation, so clients that discard bodies on 5xx still see the error metadata.</summary>
    public const string RpcErrorHeader = "X-VGI-RPC-Error";

    private static readonly Schema s_emptySchema = new([], metadata: null);

    // zstd is deliberately excluded from *response* compression (gzip only) — a real,
    // version-dependent incompatibility in the reference Python client's dependency stack, found
    // by reproducing a CI-only failure ("Invalid IPC stream: negative continuation token" on
    // every HTTP unary test) in a Linux x86_64 container: the client advertises zstd support
    // via `Accept-Encoding` whenever `vgi_rpc._codec.available_encodings()` sees the third-party
    // `zstandard` package importable — but as of httpx2 2.12, httpx2's own *response*
    // auto-decompression for zstd no longer uses that package at all; it requires Python 3.14's
    // stdlib `compression.zstd` or the separate `backports.zstd` package, neither of which
    // `vgi-rpc[http]` installs. On Python 3.13 (what this repo's CI runs) with only `zstandard`
    // present, the client claims zstd support it cannot actually exercise: request compression
    // still works (vgi_rpc's own code calls `zstandard` directly, never touching httpx2's
    // decoder), but a zstd-compressed *response* comes back to the client still compressed, and
    // pyarrow fails trying to parse it as a plain IPC stream. This is a pre-existing bug in the
    // published `vgi-rpc[http]` package's interaction with recent httpx2 versions, not specific
    // to this port — any server (Python's own reference included) would hit it against this
    // exact client/Python-version combination. gzip has no such gap: httpx2 auto-decompresses it
    // unconditionally via stdlib `zlib`. Revisit once the ecosystem's zstd story stabilizes (or
    // `vgi-rpc[http]` starts installing `backports.zstd` on <3.14). Request decompression
    // (DecompressingRequestBody) is entirely unaffected by any of this — it doesn't depend on
    // this set or on the client's HTTP library at all.
    private static readonly IReadOnlySet<ContentEncoding> s_producibleEncodings = new HashSet<ContentEncoding> { ContentEncoding.Gzip };
    private static readonly IReadOnlySet<ContentEncoding> s_noEncodings = new HashSet<ContentEncoding>();

    /// <summary>Registers <paramref name="server"/>'s routes under <paramref name="prefix"/>
    /// (default the root — matches Python's default <c>prefix=""</c>).</summary>
    /// <param name="endpoints">The route builder to register onto.</param>
    /// <param name="server">The dispatch target.</param>
    /// <param name="prefix">URL prefix for every route (default the root).</param>
    /// <param name="compressionLevel">zstd/gzip level applied to compressible response bodies —
    /// matches Python's <c>make_wsgi_app(compression_level=1)</c> default. <see langword="null"/>
    /// disables response compression outright (request decompression is unaffected either way —
    /// see <see cref="DecompressingRequestBody"/>'s doc comment for why that one isn't optional).</param>
    /// <param name="tokenKey">AEAD master key sealing stream call-id tokens (see
    /// <see cref="StreamCallRegistry"/>) — <see langword="null"/> (the default) generates a
    /// random 32-byte key per call to this method, matching Python's <c>make_wsgi_app</c>
    /// default. A shared key is only needed for multi-process deployments, which this port
    /// doesn't support yet (see <see cref="StreamCallRegistry"/>'s doc comment) — provided now
    /// so the seam exists.</param>
    /// <param name="maxResponseBytes">HTTP body cap enforced on unary results and exchange turns
    /// (hard — no escape valve) — <see langword="null"/> (the default) means unbounded. Producer
    /// turns don't enforce this yet (Python's own wire cap is *soft* there — a continuation token
    /// carries the overshoot to the next turn — which this port doesn't implement; see
    /// docs/roadmap.md M7). Advertised via <c>VGI-Max-Response-Bytes</c> on
    /// <c>OPTIONS {prefix}/health</c>, matching <c>vgi_rpc.http._client.http_capabilities</c>'s
    /// discovery contract.</param>
    public static IEndpointRouteBuilder MapVgiRpc(this IEndpointRouteBuilder endpoints, RpcServer server, string prefix = "", int? compressionLevel = 1, byte[]? tokenKey = null, long? maxResponseBytes = null)
    {
        tokenKey ??= RandomNumberGenerator.GetBytes(32);
        var registry = new StreamCallRegistry();
        endpoints.MapMethods($"{prefix}/health", ["GET", "HEAD"], (HttpContext context) => HandleHealthAsync(server, context));
        endpoints.MapMethods($"{prefix}/health", ["OPTIONS"], (HttpContext context) => HandleCapabilitiesAsync(context, maxResponseBytes));
        endpoints.MapPost($"{prefix}/{{method}}", (string method, HttpContext context) => HandleUnaryAsync(server, method, context, compressionLevel, maxResponseBytes));
        endpoints.MapPost($"{prefix}/{{method}}/init", (string method, HttpContext context) => HandleStreamInitAsync(server, method, context, compressionLevel, tokenKey, registry));
        endpoints.MapPost($"{prefix}/{{method}}/exchange", (string method, HttpContext context) => HandleStreamExchangeAsync(server, method, context, compressionLevel, tokenKey, registry, maxResponseBytes));
        return endpoints;
    }

    /// <summary>
    /// <c>OPTIONS {prefix}/health</c> — capability discovery, matching
    /// <c>vgi_rpc.http._client.http_capabilities</c>'s contract exactly: <c>VGI-Max-Response-Bytes</c>
    /// when a cap is configured, <c>VGI-Externalization-Enabled: false</c> and
    /// <c>VGI-Upload-URL-Support: false</c> (neither is implemented yet — see docs/roadmap.md M13),
    /// and <c>VGI-Supported-Encodings</c> naming the codecs this server can actually produce for
    /// responses (see <see cref="s_producibleEncodings"/> — gzip only, for now).
    /// </summary>
    private static Task HandleCapabilitiesAsync(HttpContext context, long? maxResponseBytes)
    {
        var headers = context.Response.Headers;
        if (maxResponseBytes is { } cap)
        {
            headers["VGI-Max-Response-Bytes"] = cap.ToString();
        }

        headers["VGI-Externalization-Enabled"] = "false";
        headers["VGI-Upload-URL-Support"] = "false";
        headers["VGI-Supported-Encodings"] = "gzip";
        context.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    }

    private static Task HandleHealthAsync(RpcServer server, HttpContext context)
    {
        // Matches Python's _HealthResource: a small pre-shaped JSON body, and (per the porting
        // guide's mandatory-flags contract) a bodyless HEAD variant with the same headers — the
        // C++ reference client probes readiness with HEAD specifically.
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "ok",
            server_id = server.ServerId,
            protocol = server.ProtocolName,
        });
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return Task.CompletedTask;
        }

        return context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }

    private static async Task HandleUnaryAsync(RpcServer server, string method, HttpContext context, int? compressionLevel, long? maxResponseBytes)
    {
        var request = context.Request;
        var cancellationToken = context.RequestAborted;
        var (encoding, useCustomHeader) = ContentEncodingNegotiation.PickResponseEncoding(
            request, compressionLevel is null ? s_noEncodings : s_producibleEncodings);

        if (request.ContentType != ArrowContentType)
        {
            await ErrorResultAsync(
                server,
                method,
                new RpcException("TypeError", $"Expected Content-Type: '{ArrowContentType}', got '{request.ContentType}'. All vgi-rpc HTTP requests must use Content-Type: {ArrowContentType}"),
                StatusCodes.Status415UnsupportedMediaType,
                s_emptySchema,
                httpStatusForLog: StatusCodes.Status415UnsupportedMediaType, context, encoding, useCustomHeader, compressionLevel).ConfigureAwait(false);
            return;
        }

        if (!server.Methods.TryGetValue(method, out var info))
        {
            var available = string.Join(", ", server.Methods.Keys.OrderBy(k => k, StringComparer.Ordinal));
            await ErrorResultAsync(
                server,
                method,
                new MethodNotImplementedException($"Unknown method: '{method}'. Available methods: [{available}]"),
                StatusCodes.Status404NotFound,
                s_emptySchema,
                httpStatusForLog: StatusCodes.Status404NotFound, context, encoding, useCustomHeader, compressionLevel).ConfigureAwait(false);
            return;
        }

        if (info.Kind == RpcMethodKind.Stream)
        {
            await ErrorResultAsync(
                server,
                method,
                new RpcException("TypeError", $"Stream method '{method}' requires /init and /exchange endpoints"),
                StatusCodes.Status400BadRequest,
                s_emptySchema,
                httpStatusForLog: StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel).ConfigureAwait(false);
            return;
        }

        Stream requestBody;
        try
        {
            requestBody = DecompressingRequestBody(request);
        }
        catch (NotSupportedException exc)
        {
            await ErrorResultAsync(server, method, new RpcException("TypeError", exc.Message), StatusCodes.Status415UnsupportedMediaType, s_emptySchema, httpStatusForLog: StatusCodes.Status415UnsupportedMediaType, context, encoding, useCustomHeader, compressionLevel).ConfigureAwait(false);
            return;
        }

        AnnotatedBatch? requestBatch;
        try
        {
            using var reader = new WireReader(requestBody);
            _ = await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            requestBatch = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            await ErrorResultAsync(server, method, exc, StatusCodes.Status400BadRequest, info.ResultSchema, httpStatusForLog: StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel).ConfigureAwait(false);
            return;
        }
        finally
        {
            if (!ReferenceEquals(requestBody, request.Body))
            {
                await requestBody.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (requestBatch is null)
        {
            await ErrorResultAsync(server, method, new RpcException("RpcException", "Request body carried no batch."), StatusCodes.Status400BadRequest, info.ResultSchema, httpStatusForLog: StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel).ConfigureAwait(false);
            return;
        }

        var ipcMethod = requestBatch.GetMetadata(MetadataKeys.Method);
        if (ipcMethod != method)
        {
            await ErrorResultAsync(
                server,
                method,
                new RpcException("TypeError", $"Method name mismatch: URL path has '{method}' but Arrow IPC custom_metadata 'vgi_rpc.method' has '{ipcMethod}'. These must match."),
                StatusCodes.Status400BadRequest,
                info.ResultSchema,
                httpStatusForLog: StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel).ConfigureAwait(false);
            return;
        }

        object?[] args;
        try
        {
            args = ValueCodec.ExtractRow(requestBatch.Batch, info.Parameters.Select(p => p.ParameterType).ToArray());
        }
        catch (Exception exc)
        {
            await ErrorResultAsync(server, method, exc, StatusCodes.Status400BadRequest, info.ResultSchema, httpStatusForLog: StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel).ConfigureAwait(false);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var status = "ok";
        var errorType = "";
        var errorMessage = "";
        var callContext = info.HasContextParameter ? new BufferedHttpCallContext() : null;

        var responseBuffer = new MemoryStream();
        await using (var writer = new WireWriter(responseBuffer, info.ResultSchema))
        {
            try
            {
                var result = await info.InvokeAsync(server.Implementation, args, callContext).ConfigureAwait(false);
                if (callContext is not null)
                {
                    foreach (var logMessage in callContext.Buffered)
                    {
                        await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(info.ResultSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
                    }
                }

                var resultBatch = info.ResultSchema.FieldsList.Count == 0
                    ? ValueCodec.EmptyRow(info.ResultSchema)
                    : ValueCodec.BuildRow(info.ResultSchema, [result]);
                await writer.WriteBatchAsync(new AnnotatedBatch(resultBatch, null), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                var actual = Unwrap(exc);
                status = "error";
                errorType = actual.GetType().Name;
                errorMessage = actual.Message;
                var metadata = LogMessage.FromException(actual).AddToMetadata();
                await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(info.ResultSchema), metadata), cancellationToken).ConfigureAwait(false);
            }
        }

        // Hard wire-body cap — checked post-flush since building the buffer is free. On overshoot,
        // discard the oversize body and answer with only the error batch instead (mirrors
        // Python's _enforce_response_budgets + its post-overshoot re-write of resp_buf).
        if (status == "ok" && maxResponseBytes is { } cap && responseBuffer.Length > cap)
        {
            var overshoot = new RpcException("RuntimeError", $"HTTP body exceeds max_response_bytes ({responseBuffer.Length} > {cap}) for method '{method}'");
            status = "error";
            errorType = "RuntimeError";
            errorMessage = overshoot.Message;
            responseBuffer = new MemoryStream();
            await using var errWriter = new WireWriter(responseBuffer, info.ResultSchema);
            var errMetadata = LogMessage.FromException(overshoot).AddToMetadata();
            await errWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(info.ResultSchema), errMetadata), cancellationToken).ConfigureAwait(false);
        }

        // status=error still answers HTTP 200 — the body carries a real in-band error batch, and
        // RpcErrorHeader is the signal a client checks instead of the status code (mirrors
        // Python's _set_http_status 500→200 translation).
        EmitAccessLog(server, info.WireName, "unary", status, errorType, errorMessage, start, StatusCodes.Status200OK);

        if (status == "error")
        {
            context.Response.Headers[RpcErrorHeader] = "true";
        }

        await WriteBytesAsync(context, StatusCodes.Status200OK, responseBuffer.ToArray(), encoding, useCustomHeader, compressionLevel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST {prefix}/{method}/init</c> — dispatches a stream method and registers it under a
    /// fresh call id (see <see cref="StreamCallRegistry"/>), returning (optional header stream +)
    /// a zero-row sentinel batch carrying the sealed call-id token on both
    /// <see cref="MetadataKeys.StreamState"/> and <see cref="MetadataKeys.CallState"/> — the real
    /// Python client reads both from exactly this shape (see
    /// <c>vgi_rpc.http._client._init_http_stream_session</c>). Unlike the canonical Python
    /// server, this never folds a producer's first turn into the init response (see
    /// <see cref="StreamCallRegistry"/>'s doc comment on why): every turn, producer or exchange,
    /// happens via <see cref="HandleStreamExchangeAsync"/> — which the client's generic init-response
    /// reader handles correctly regardless (it just sees zero data batches this turn).
    /// </summary>
    private static async Task HandleStreamInitAsync(RpcServer server, string method, HttpContext context, int? compressionLevel, byte[] tokenKey, StreamCallRegistry registry)
    {
        var request = context.Request;
        var cancellationToken = context.RequestAborted;
        var (encoding, useCustomHeader) = ContentEncodingNegotiation.PickResponseEncoding(
            request, compressionLevel is null ? s_noEncodings : s_producibleEncodings);

        if (request.ContentType != ArrowContentType)
        {
            await ErrorResultAsync(server, method, new RpcException("TypeError", $"Expected Content-Type: '{ArrowContentType}', got '{request.ContentType}'. All vgi-rpc HTTP requests must use Content-Type: {ArrowContentType}"), StatusCodes.Status415UnsupportedMediaType, s_emptySchema, StatusCodes.Status415UnsupportedMediaType, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        if (!server.Methods.TryGetValue(method, out var info))
        {
            var available = string.Join(", ", server.Methods.Keys.OrderBy(k => k, StringComparer.Ordinal));
            await ErrorResultAsync(server, method, new MethodNotImplementedException($"Unknown method: '{method}'. Available methods: [{available}]"), StatusCodes.Status404NotFound, s_emptySchema, StatusCodes.Status404NotFound, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        if (info.Kind != RpcMethodKind.Stream)
        {
            await ErrorResultAsync(server, method, new RpcException("TypeError", $"Method '{method}' is not a stream — call it as a plain unary POST /{method} instead."), StatusCodes.Status400BadRequest, s_emptySchema, StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        Stream requestBody;
        try
        {
            requestBody = DecompressingRequestBody(request);
        }
        catch (NotSupportedException exc)
        {
            await ErrorResultAsync(server, method, new RpcException("TypeError", exc.Message), StatusCodes.Status415UnsupportedMediaType, s_emptySchema, StatusCodes.Status415UnsupportedMediaType, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        AnnotatedBatch? requestBatch;
        try
        {
            using var reader = new WireReader(requestBody);
            _ = await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            requestBatch = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            await ErrorResultAsync(server, method, exc, StatusCodes.Status400BadRequest, s_emptySchema, StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }
        finally
        {
            if (!ReferenceEquals(requestBody, request.Body))
            {
                await requestBody.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (requestBatch is null)
        {
            await ErrorResultAsync(server, method, new RpcException("RpcException", "Request body carried no batch."), StatusCodes.Status400BadRequest, s_emptySchema, StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        var ipcMethod = requestBatch.GetMetadata(MetadataKeys.Method);
        if (ipcMethod != method)
        {
            await ErrorResultAsync(server, method, new RpcException("TypeError", $"Method name mismatch: URL path has '{method}' but Arrow IPC custom_metadata 'vgi_rpc.method' has '{ipcMethod}'. These must match."), StatusCodes.Status400BadRequest, s_emptySchema, StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        object?[] args;
        try
        {
            args = ValueCodec.ExtractRow(requestBatch.Batch, info.Parameters.Select(p => p.ParameterType).ToArray());
        }
        catch (Exception exc)
        {
            await ErrorResultAsync(server, method, exc, StatusCodes.Status400BadRequest, s_emptySchema, StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var invokeContext = info.HasContextParameter ? new BufferedHttpCallContext() : null;

        IRpcStream stream;
        try
        {
            var raw = await info.InvokeAsync(server.Implementation, args, invokeContext).ConfigureAwait(false);
            stream = (IRpcStream)raw!;
        }
        catch (Exception exc)
        {
            var actual = Unwrap(exc);
            await ErrorResultAsync(server, method, actual, StatusCodes.Status500InternalServerError, s_emptySchema, StatusCodes.Status200OK, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        var callKey = registry.Register(stream);
        var tokenBase64 = Convert.ToBase64String(Crypto.Seal(Convert.FromHexString(callKey), tokenKey, aad: []));

        var responseBuffer = new MemoryStream();

        // A stream header is its own complete IPC stream (schema + one row + EOS), written
        // before the main output stream begins — see IRpcStream.Header's doc comment. Mirrors
        // RpcServer.ServeStreamAsync's header-writing block exactly (duplicated, not shared —
        // see this class's own doc comment on why HTTP dispatch can't reuse that method).
        if (stream.Header is not null)
        {
            var headerType = stream.Header.GetType();
            var headerSchema = SchemaDerivation.InnerSchemaFor(headerType);
            var headerValues = headerSchema.FieldsList
                .Select(f => headerType.GetProperty(ValueCodec.FindClrPropertyName(headerType, f))!.GetValue(stream.Header))
                .ToList();
            var headerBatch = ValueCodec.BuildRow(headerSchema, headerValues);
            await using (var headerWriter = new WireWriter(responseBuffer, headerSchema))
            {
                if (invokeContext is not null)
                {
                    foreach (var logMessage in invokeContext.Buffered)
                    {
                        await headerWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(headerSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
                    }

                    invokeContext.Buffered.Clear();
                }

                await headerWriter.WriteBatchAsync(new AnnotatedBatch(headerBatch, null), cancellationToken).ConfigureAwait(false);
            }
        }

        var outputSchema = stream.OutputSchema;
        await using (var outputWriter = new WireWriter(responseBuffer, outputSchema))
        {
            if (invokeContext is not null)
            {
                foreach (var logMessage in invokeContext.Buffered)
                {
                    await outputWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
                }
            }

            var tokenMetadata = new Dictionary<string, string>
            {
                [MetadataKeys.StreamState] = tokenBase64,
                [MetadataKeys.CallState] = tokenBase64,
            };
            await outputWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), tokenMetadata), cancellationToken).ConfigureAwait(false);
        }

        EmitAccessLog(server, info.WireName, "stream", "ok", "", "", start, StatusCodes.Status200OK, callKey);
        await WriteBytesAsync(context, StatusCodes.Status200OK, responseBuffer.ToArray(), encoding, useCustomHeader, compressionLevel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST {prefix}/{method}/exchange</c> — runs exactly one lockstep turn against the stream
    /// <see cref="HandleStreamInitAsync"/> registered, resolved from the request's echoed
    /// <see cref="MetadataKeys.StreamState"/> token. Handles both producer ticks (an empty-schema
    /// request batch — the HTTP analog of the pipe transport's <c>_TICK_BATCH</c>) and real
    /// exchange data uniformly, since both just become one <see cref="StreamState.ProcessAsync"/>
    /// call. Response shape differs by kind, matching the real Python client's expectations
    /// exactly (see <c>vgi_rpc.http._client.HttpStreamSession</c>):
    /// <list type="bullet">
    /// <item>Exchange: the refreshed continuation token rides on the SAME data batch's own
    /// metadata (<c>HttpStreamSession.exchange()</c> reads exactly one terminal batch and pulls
    /// <see cref="MetadataKeys.StreamState"/> off it directly).</item>
    /// <item>Producer: token rides on a SEPARATE zero-row sentinel batch, appended only when the
    /// stream isn't finished (<c>HttpStreamSession.__iter__</c>/<c>next_with_token</c> explicitly
    /// look for a zero-row batch carrying the token as a distinct "there's more" signal, separate
    /// from real data batches).</item>
    /// </list>
    /// Deliberately simpler than Python's producer turn (which loops <c>process()</c> until
    /// <c>max_response_bytes</c> or finish, batching several turns into one HTTP response): this
    /// always runs exactly one turn per request, matching the pipe transport's lockstep model and
    /// (unlike accumulate-until-cap) trivially supporting mid-stream cancel — see
    /// <see cref="StreamCallRegistry"/>'s doc comment for the same simplification's rationale.
    /// </summary>
    private static async Task HandleStreamExchangeAsync(RpcServer server, string method, HttpContext context, int? compressionLevel, byte[] tokenKey, StreamCallRegistry registry, long? maxResponseBytes = null)
    {
        var request = context.Request;
        var cancellationToken = context.RequestAborted;
        var (encoding, useCustomHeader) = ContentEncodingNegotiation.PickResponseEncoding(
            request, compressionLevel is null ? s_noEncodings : s_producibleEncodings);

        if (request.ContentType != ArrowContentType)
        {
            await ErrorResultAsync(server, method, new RpcException("TypeError", $"Expected Content-Type: '{ArrowContentType}', got '{request.ContentType}'. All vgi-rpc HTTP requests must use Content-Type: {ArrowContentType}"), StatusCodes.Status415UnsupportedMediaType, s_emptySchema, StatusCodes.Status415UnsupportedMediaType, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        if (!server.Methods.TryGetValue(method, out var info) || info.Kind != RpcMethodKind.Stream)
        {
            await ErrorResultAsync(server, method, new MethodNotImplementedException($"Unknown stream method: '{method}'."), StatusCodes.Status404NotFound, s_emptySchema, StatusCodes.Status404NotFound, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        Stream requestBody;
        try
        {
            requestBody = DecompressingRequestBody(request);
        }
        catch (NotSupportedException exc)
        {
            await ErrorResultAsync(server, method, new RpcException("TypeError", exc.Message), StatusCodes.Status415UnsupportedMediaType, s_emptySchema, StatusCodes.Status415UnsupportedMediaType, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        AnnotatedBatch? requestBatch;
        try
        {
            using var reader = new WireReader(requestBody);
            _ = await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            requestBatch = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            await ErrorResultAsync(server, method, exc, StatusCodes.Status400BadRequest, s_emptySchema, StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }
        finally
        {
            if (!ReferenceEquals(requestBody, request.Body))
            {
                await requestBody.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (requestBatch is null)
        {
            await ErrorResultAsync(server, method, new RpcException("RpcException", "Request body carried no batch."), StatusCodes.Status400BadRequest, s_emptySchema, StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        var tokenB64 = requestBatch.GetMetadata(MetadataKeys.StreamState);
        if (tokenB64 is null)
        {
            await ErrorResultAsync(server, method, new RpcException("TypeError", $"Exchange request is missing the {MetadataKeys.StreamState} continuation token."), StatusCodes.Status400BadRequest, s_emptySchema, StatusCodes.Status400BadRequest, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        string callKey;
        try
        {
            callKey = Convert.ToHexStringLower(Crypto.Open(Convert.FromBase64String(tokenB64), tokenKey, aad: []));
        }
        catch (Exception)
        {
            await ErrorResultAsync(server, method, new SessionLostException("Stream continuation token is invalid, tampered, or expired."), StatusCodes.Status500InternalServerError, s_emptySchema, StatusCodes.Status200OK, context, encoding, useCustomHeader, compressionLevel, methodType: "stream").ConfigureAwait(false);
            return;
        }

        if (!registry.TryGet(callKey, out var stream))
        {
            await ErrorResultAsync(server, method, new SessionLostException("No active stream for this token — it may have expired, been cancelled, or this server process restarted."), StatusCodes.Status500InternalServerError, s_emptySchema, StatusCodes.Status200OK, context, encoding, useCustomHeader, compressionLevel, methodType: "stream", streamId: callKey).ConfigureAwait(false);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var outputSchema = stream.OutputSchema;
        var isProducer = stream.InputSchema is not { FieldsList.Count: > 0 };

        if (requestBatch.GetMetadata(MetadataKeys.Cancel) is not null)
        {
            stream.State.OnCancel(null);
            registry.Remove(callKey);
            EmitAccessLog(server, info.WireName, "stream", "ok", "", "", start, StatusCodes.Status200OK, callKey);
            var cancelBuffer = new MemoryStream();
            await using (var cancelWriter = new WireWriter(cancelBuffer, outputSchema))
            {
                // No batches — an empty (schema, EOS) IPC stream the client just drains, matching
                // Python's cancel response. WriteStartAsync forces the schema message even with
                // zero batches (WireWriter otherwise defers it lazily to the first batch write).
                await cancelWriter.WriteStartAsync(cancellationToken).ConfigureAwait(false);
            }

            await WriteBytesAsync(context, StatusCodes.Status200OK, cancelBuffer.ToArray(), encoding, useCustomHeader, compressionLevel, cancellationToken).ConfigureAwait(false);
            return;
        }

        var turnBatch = requestBatch;
        if (!isProducer && stream.InputSchema is { } declaredInputSchema)
        {
            try
            {
                turnBatch = turnBatch with { Batch = ValueCodec.CoerceBatch(turnBatch.Batch, declaredInputSchema) };
            }
            catch (Exception exc)
            {
                registry.Remove(callKey);
                await ErrorResultAsync(server, method, exc, StatusCodes.Status500InternalServerError, outputSchema, StatusCodes.Status200OK, context, encoding, useCustomHeader, compressionLevel, methodType: "stream", streamId: callKey).ConfigureAwait(false);
                return;
            }
        }

        var collector = new OutputCollector(outputSchema);
        var turnContext = info.HasContextParameter ? new StreamHttpCallContext(collector) : null;
        Exception? turnException = null;
        try
        {
            await stream.State.ProcessAsync(turnBatch, collector, turnContext, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            turnException = Unwrap(exc);
        }

        if (turnException is not null)
        {
            registry.Remove(callKey);
            await ErrorResultAsync(server, method, turnException, StatusCodes.Status500InternalServerError, outputSchema, StatusCodes.Status200OK, context, encoding, useCustomHeader, compressionLevel, methodType: "stream", streamId: callKey).ConfigureAwait(false);
            return;
        }

        var finished = collector.Finished;
        string? freshTokenB64 = null;
        if (!finished)
        {
            // The call id hasn't changed — the same sealed token still resolves to this entry,
            // so there's nothing to re-seal (unlike Python's cursor token, ours carries no
            // serialized StreamState to refresh — see StreamCallRegistry's doc comment).
            freshTokenB64 = tokenB64;
        }
        else
        {
            registry.Remove(callKey);
        }

        var responseBuffer = new MemoryStream();
        await using (var writer = new WireWriter(responseBuffer, outputSchema))
        {
            foreach (var logMessage in collector.Logs)
            {
                await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
            }

            if (isProducer)
            {
                if (collector.EmittedBatch is not null)
                {
                    await writer.WriteBatchAsync(new AnnotatedBatch(collector.EmittedBatch, null), cancellationToken).ConfigureAwait(false);
                }

                if (freshTokenB64 is not null)
                {
                    var sentinelMetadata = new Dictionary<string, string> { [MetadataKeys.StreamState] = freshTokenB64 };
                    await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), sentinelMetadata), cancellationToken).ConfigureAwait(false);
                }
                else if (collector.EmittedBatch is null)
                {
                    // Finished with no data this turn — an empty (schema, EOS) response tells the
                    // client's __iter__/next_with_token the producer is done (mirrors WireStartAsync
                    // in the cancel branch above).
                    await writer.WriteStartAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // Exchange: the refreshed token rides on the data batch's own metadata, not a
                // separate sentinel — see this method's doc comment. ExchangeState never finishes
                // server-side (the client ends the exchange by simply stopping calling exchange()),
                // so freshTokenB64 is always set here in practice.
                var dataMetadata = freshTokenB64 is not null
                    ? new Dictionary<string, string> { [MetadataKeys.StreamState] = freshTokenB64 }
                    : null;
                var dataBatch = collector.EmittedBatch ?? ValueCodec.EmptyRow(outputSchema);
                await writer.WriteBatchAsync(new AnnotatedBatch(dataBatch, dataMetadata), cancellationToken).ConfigureAwait(false);
            }
        }

        // Hard wire-body cap, exchange turns only — matches Python's _skip_if_no_wire_cap
        // reasoning: producer turns have a *soft* cap (a continuation token carries the
        // overshoot to the next turn), which this port doesn't implement, so producer turns
        // aren't capped at all yet. Exchange has no such escape valve.
        if (!isProducer && maxResponseBytes is { } cap && responseBuffer.Length > cap)
        {
            var overshoot = new RpcException("RuntimeError", $"HTTP body exceeds max_response_bytes ({responseBuffer.Length} > {cap}) for method '{method}'");
            responseBuffer = new MemoryStream();
            await using (var errWriter = new WireWriter(responseBuffer, outputSchema))
            {
                var errMetadata = LogMessage.FromException(overshoot).AddToMetadata();
                await errWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), errMetadata), cancellationToken).ConfigureAwait(false);
            }

            EmitAccessLog(server, info.WireName, "stream", "error", "RuntimeError", overshoot.Message, start, StatusCodes.Status200OK, callKey);
            await WriteBytesAsync(context, StatusCodes.Status200OK, responseBuffer.ToArray(), encoding, useCustomHeader, compressionLevel, cancellationToken).ConfigureAwait(false);
            return;
        }

        EmitAccessLog(server, info.WireName, "stream", "ok", "", "", start, StatusCodes.Status200OK, callKey);
        await WriteBytesAsync(context, StatusCodes.Status200OK, responseBuffer.ToArray(), encoding, useCustomHeader, compressionLevel, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ErrorResultAsync(
        RpcServer server,
        string method,
        Exception exception,
        int httpStatusCode,
        Schema schema,
        int httpStatusForLog,
        HttpContext context,
        ContentEncoding? encoding,
        bool useCustomHeader,
        int? compressionLevel,
        string methodType = "unary",
        string? streamId = null)
    {
        var start = Stopwatch.GetTimestamp();
        using var buffer = new MemoryStream();
        await using (var writer = new WireWriter(buffer, schema))
        {
            var metadata = LogMessage.FromException(exception).AddToMetadata();
            await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(schema), metadata)).ConfigureAwait(false);
        }

        EmitAccessLog(server, method, methodType, "error", exception.GetType().Name, exception.Message, start, httpStatusForLog, streamId);

        // Matches Python's _set_http_status: only a 500 gets folded into 200+header — 4xx/415
        // protocol-level rejections keep their real status code.
        if (httpStatusCode == StatusCodes.Status500InternalServerError)
        {
            context.Response.Headers[RpcErrorHeader] = "true";
            await WriteBytesAsync(context, StatusCodes.Status200OK, buffer.ToArray(), encoding, useCustomHeader, compressionLevel, context.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            await WriteBytesAsync(context, httpStatusCode, buffer.ToArray(), encoding, useCustomHeader, compressionLevel, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wraps <paramref name="request"/>'s body in the decompressor its <c>Content-Encoding</c>
    /// names, or returns the body unchanged for no/identity encoding. The reference Python HTTP
    /// client compresses every request body with zstd by default (<c>compression_level: 1</c> —
    /// not an opt-in), so this isn't an optional M7 refinement: without it, no unary call over
    /// HTTP can succeed against a real client. Mirrors <c>_CompressionMiddleware</c>'s codec set
    /// (zstd, gzip — no brotli despite it appearing in clients' Accept-Encoding lists).
    /// </summary>
    private static Stream DecompressingRequestBody(HttpRequest request)
    {
        var encoding = request.Headers.ContentEncoding.ToString();
        return encoding.ToLowerInvariant() switch
        {
            "" or "identity" => request.Body,
            "zstd" => new ZstdSharp.DecompressionStream(request.Body),
            "gzip" => new GZipStream(request.Body, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"Content-Encoding '{encoding}' is not supported by this server."),
        };
    }

    /// <summary>
    /// Writes <paramref name="body"/> as the response, compressing it with <paramref name="encoding"/>
    /// first when one was negotiated and the body isn't empty (an empty body carries nothing worth
    /// compressing — matches Python's early-return on <c>size == 0</c>). Mirrors
    /// <c>_CompressionMiddleware.process_response</c>'s codec dispatch and header choice
    /// (<c>X-VGI-Content-Encoding</c> when the client's preference came from the custom
    /// <c>X-VGI-Accept-Encoding</c> header, else the standard <c>Content-Encoding</c>).
    /// </summary>
    private static Task WriteBytesAsync(HttpContext context, int statusCode, byte[] body, ContentEncoding? encoding, bool useCustomHeader, int? compressionLevel, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ArrowContentType;

        if (encoding is { } enc && compressionLevel is { } level && body.Length > 0)
        {
            body = CompressBody(body, enc, level);
            context.Response.Headers[useCustomHeader ? "X-VGI-Content-Encoding" : "Content-Encoding"] = enc switch
            {
                ContentEncoding.Zstd => "zstd",
                ContentEncoding.Gzip => "gzip",
                _ => throw new InvalidOperationException($"Unexpected response encoding '{enc}'."),
            };
        }

        context.Response.ContentLength = body.Length;
        return context.Response.Body.WriteAsync(body, cancellationToken).AsTask();
    }

    private static byte[] CompressBody(byte[] body, ContentEncoding encoding, int level)
    {
        if (encoding == ContentEncoding.Zstd)
        {
            using var compressor = new ZstdSharp.Compressor(level);
            return compressor.Wrap(body).ToArray();
        }

        // .NET's GZipStream takes System.IO.Compression's four-level CompressionLevel enum, not
        // zstd's/zlib's finer numeric scale — level<=1 (Python's own default) maps to Fastest,
        // matching the "cheap, not maximal" intent; anything higher goes to Optimal.
        var gzipLevel = level <= 1 ? CompressionLevel.Fastest : CompressionLevel.Optimal;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, gzipLevel, leaveOpen: true))
        {
            gzip.Write(body);
        }

        return output.ToArray();
    }

    private static void EmitAccessLog(RpcServer server, string method, string methodType, string status, string errorType, string errorMessage, long startTimestamp, int httpStatus, string? streamId = null)
    {
        if (server.AccessLog is not { } sink)
        {
            return;
        }

        var durationMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        sink.Write(new AccessLogRecord(
            Timestamp: DateTimeOffset.UtcNow,
            ServerId: server.ServerId,
            Protocol: server.ProtocolName,
            ProtocolHash: server.ProtocolHash,
            Method: method,
            MethodType: methodType,
            Status: status,
            DurationMs: durationMs,
            ErrorType: errorType,
            ErrorMessage: string.IsNullOrEmpty(errorMessage) ? null : errorMessage,
            ServerVersion: server.ServerVersion,
            StreamId: streamId));
    }

    /// <summary>Unwraps a reflection-invocation exception to the real one it wraps — matches
    /// <see cref="RpcServer"/>'s private helper of the same name (see that type for why one
    /// exists in each: HTTP dispatch is a genuinely separate code path).</summary>
    private static Exception Unwrap(Exception exc) =>
        exc is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : exc;

    /// <summary>
    /// Buffers <see cref="Server.ICallContext.EmitLog"/> calls for one HTTP unary dispatch or
    /// stream init call, flushed as zero-row log batches ahead of the result/header batch — the
    /// HTTP-transport analog of <see cref="RpcServer"/>'s own private nested type of the same
    /// shape (duplicated rather than shared since that one isn't part of the core assembly's
    /// public/internal surface).
    /// </summary>
    private sealed class BufferedHttpCallContext : Server.ICallContext
    {
        public List<LogMessage> Buffered { get; } = [];

        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            Buffered.Add(new LogMessage(level, message, extra));
    }

    /// <summary>Forwards a stream turn's <see cref="Server.ICallContext.EmitLog"/> calls into
    /// that turn's <see cref="OutputCollector"/> — the HTTP-transport analog of
    /// <see cref="RpcServer"/>'s private nested type of the same shape.</summary>
    private sealed class StreamHttpCallContext(OutputCollector collector) : Server.ICallContext
    {
        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            collector.ClientLog(level, message, extra);
    }
}
