using System.Diagnostics;
using System.IO.Compression;
using Apache.Arrow;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using QueryFarm.VgiRpc.AccessLog;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
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

    private static readonly IReadOnlySet<ContentEncoding> s_producibleEncodings = new HashSet<ContentEncoding> { ContentEncoding.Zstd, ContentEncoding.Gzip };
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
    public static IEndpointRouteBuilder MapVgiRpc(this IEndpointRouteBuilder endpoints, RpcServer server, string prefix = "", int? compressionLevel = 1)
    {
        endpoints.MapMethods($"{prefix}/health", ["GET", "HEAD"], (HttpContext context) => HandleHealthAsync(server, context));
        endpoints.MapPost($"{prefix}/{{method}}", (string method, HttpContext context) => HandleUnaryAsync(server, method, context, compressionLevel));
        endpoints.MapPost($"{prefix}/{{method}}/init", () => NotYetImplementedStream());
        endpoints.MapPost($"{prefix}/{{method}}/exchange", () => NotYetImplementedStream());
        return endpoints;
    }

    private static IResult NotYetImplementedStream() =>
        Results.Text(
            "Streaming methods are not yet supported over the HTTP transport — see docs/roadmap.md M6.",
            statusCode: StatusCodes.Status501NotImplemented);

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

    private static async Task HandleUnaryAsync(RpcServer server, string method, HttpContext context, int? compressionLevel)
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
                var actual = exc is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : exc;
                status = "error";
                errorType = actual.GetType().Name;
                errorMessage = actual.Message;
                var metadata = LogMessage.FromException(actual).AddToMetadata();
                await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(info.ResultSchema), metadata), cancellationToken).ConfigureAwait(false);
            }
        }

        // status=error still answers HTTP 200 — the body carries a real in-band error batch, and
        // RpcErrorHeader is the signal a client checks instead of the status code (mirrors
        // Python's _set_http_status 500→200 translation).
        EmitAccessLog(server, info.WireName, status, errorType, errorMessage, start, StatusCodes.Status200OK);

        if (status == "error")
        {
            context.Response.Headers[RpcErrorHeader] = "true";
        }

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
        int? compressionLevel)
    {
        var start = Stopwatch.GetTimestamp();
        using var buffer = new MemoryStream();
        await using (var writer = new WireWriter(buffer, schema))
        {
            var metadata = LogMessage.FromException(exception).AddToMetadata();
            await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(schema), metadata)).ConfigureAwait(false);
        }

        EmitAccessLog(server, method, "error", exception.GetType().Name, exception.Message, start, httpStatusForLog);

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

    private static void EmitAccessLog(RpcServer server, string method, string status, string errorType, string errorMessage, long startTimestamp, int httpStatus)
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
            MethodType: "unary",
            Status: status,
            DurationMs: durationMs,
            ErrorType: errorType,
            ErrorMessage: string.IsNullOrEmpty(errorMessage) ? null : errorMessage,
            ServerVersion: server.ServerVersion));
    }

    /// <summary>
    /// Buffers <see cref="Server.ICallContext.EmitLog"/> calls for one HTTP unary dispatch,
    /// flushed as zero-row log batches ahead of the result batch — the HTTP-transport analog of
    /// <see cref="RpcServer"/>'s own private nested type of the same shape (duplicated rather
    /// than shared since that one isn't part of the core assembly's public/internal surface).
    /// </summary>
    private sealed class BufferedHttpCallContext : Server.ICallContext
    {
        public List<LogMessage> Buffered { get; } = [];

        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            Buffered.Add(new LogMessage(level, message, extra));
    }
}
