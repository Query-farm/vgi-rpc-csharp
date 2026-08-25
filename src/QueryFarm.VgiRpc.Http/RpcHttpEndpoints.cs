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
    /// <param name="authenticate">Run before every unary/init/exchange dispatch (never for
    /// <c>/health</c> — that endpoint is mandatory and auth-exempt, matching the porting guide).
    /// <see langword="null"/> (the default) means no authentication at all. Throw
    /// <see cref="AuthFailure"/> to reject a request with a specific <see cref="AuthReason"/> —
    /// see <see cref="BearerAuth"/> for a ready-made bearer-token implementation. Any other
    /// exception is treated as <see cref="AuthReason.Unauthorized"/> with no detail (never
    /// leaking the exception's own message to the caller).</param>
    /// <param name="proxyHint">Non-null only on a service whose authentication depends on a
    /// reverse proxy (<c>docs/unauthorized-spec.md</c> §5) — added to every 401 this server
    /// produces, both as the body's <c>proxy_hint</c> field and via
    /// <c>VGI-Auth-Proxy-Required: true</c>. There is no automatic discovery from installed
    /// authenticators yet (Python's mTLS/proxy-proof authenticators self-declare which header
    /// they read; this port doesn't have those yet — see docs/roadmap.md M9/M11), so this is
    /// the spec's "direct way for an operator to state header names" fallback, always.</param>
    /// <param name="corsPolicyName">Name of a CORS policy already registered via
    /// <see cref="Cors.AddVgiRpcCors"/> (on <c>builder.Services</c>, before this call) —
    /// <see langword="null"/> (the default) leaves every route CORS-unaware, matching Python's
    /// default of no <c>cors_origins</c>. See <see cref="Cors"/>'s doc comment for the full
    /// three-piece wiring (service registration + <c>app.UseCors()</c>/
    /// <see cref="Cors.UseVgiRpcCorsExtras"/> + this parameter) and why it can't collapse into
    /// one call here.</param>
    /// <param name="sticky">Enables sticky sessions when non-null (see
    /// <see cref="StickySessionRegistry"/> and <c>docs/sticky-sessions-spec.md</c>) — construct
    /// one, keep the reference for <see cref="StickySessionRegistry.Drain"/>/
    /// <see cref="StickySessionRegistry.Shutdown"/>, and pass it here. <see langword="null"/>
    /// (the default) leaves the wire byte-identical to the non-sticky framework, matching
    /// Python's opt-in default. Session tokens are sealed with the same <paramref name="tokenKey"/>
    /// stream call-id tokens use, matching Python's single shared <c>token_key</c>.</param>
    /// <param name="proxyProofRequired">Set when <paramref name="authenticate"/> enforces proxy
    /// proof in <c>require</c> mode (see <see cref="ProxyProof.CreateGate"/>) — advertises
    /// <see cref="ProxyProof.ProofRequiredHeader"/> on every response, per
    /// <c>docs/proxy-proof-spec.md</c> §2.2. Like <paramref name="proxyHint"/>, this is
    /// operator-declared rather than derived: <paramref name="authenticate"/> is an opaque
    /// callback (possibly composed via <see cref="ProxyProof.RequireAll"/>), so `MapVgiRpc` has
    /// no way to introspect whether it enforces proxy proof or in which mode.</param>
    public static IEndpointRouteBuilder MapVgiRpc(this IEndpointRouteBuilder endpoints, RpcServer server, string prefix = "", int? compressionLevel = 1, byte[]? tokenKey = null, long? maxResponseBytes = null, AuthenticateDelegate? authenticate = null, string? proxyHint = null, string? corsPolicyName = null, StickySessionRegistry? sticky = null, bool proxyProofRequired = false)
    {
        tokenKey ??= RandomNumberGenerator.GetBytes(32);
        var registry = new StreamCallRegistry();
        var health = endpoints.MapMethods($"{prefix}/health", ["GET", "HEAD"], (HttpContext context) => HandleHealthAsync(server, context, proxyProofRequired));
        var capabilities = endpoints.MapMethods($"{prefix}/health", ["OPTIONS"], (HttpContext context) => HandleCapabilitiesAsync(context, maxResponseBytes, sticky, proxyProofRequired));
        var unary = endpoints.MapPost($"{prefix}/{{method}}", (string method, HttpContext context) => HandleUnaryAsync(server, method, context, compressionLevel, maxResponseBytes, authenticate, proxyHint, sticky, tokenKey));
        var init = endpoints.MapPost($"{prefix}/{{method}}/init", (string method, HttpContext context) => HandleStreamInitAsync(server, method, context, compressionLevel, tokenKey, registry, authenticate, proxyHint, sticky));
        var exchange = endpoints.MapPost($"{prefix}/{{method}}/exchange", (string method, HttpContext context) => HandleStreamExchangeAsync(server, method, context, compressionLevel, tokenKey, registry, maxResponseBytes, authenticate, proxyHint, sticky));
        if (corsPolicyName is not null)
        {
            health.RequireCors(corsPolicyName);
            capabilities.RequireCors(corsPolicyName);
            unary.RequireCors(corsPolicyName);
            init.RequireCors(corsPolicyName);
            exchange.RequireCors(corsPolicyName);
        }

        if (sticky is not null)
        {
            var session = endpoints.MapDelete($"{prefix}/{StickySessions.SessionEndpoint}", (HttpContext context) => HandleSessionDeleteAsync(server, sticky, tokenKey, context));
            if (corsPolicyName is not null)
            {
                session.RequireCors(corsPolicyName);
            }
        }

        return endpoints;
    }

    /// <summary>
    /// <c>DELETE {prefix}/__session__</c> — idempotent best-effort session teardown (spec §2.5).
    /// A valid token whose entry is found returns 204 after closing it; everything else (missing
    /// header, malformed token, AAD mismatch, server_id mismatch, registry miss) returns 200 —
    /// so a stale or stolen token cannot be used to probe whether a session exists.
    /// </summary>
    private static async Task HandleSessionDeleteAsync(RpcServer server, StickySessionRegistry sticky, byte[] tokenKey, HttpContext context)
    {
        var tokenHeader = context.Request.Headers[StickySessions.SessionHeader].ToString();
        if (string.IsNullOrEmpty(tokenHeader))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        var identity = AuthIdentity.GetFrom(context);
        string tokenServerId;
        byte[] sessionId;
        try
        {
            var aad = StickySessions.ComputeAad(identity);
            (tokenServerId, sessionId, _) = StickySessions.OpenToken(tokenHeader.Trim(), tokenKey, aad);
        }
        catch (SessionLostException)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (tokenServerId != server.ServerId)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        var principalKey = StickySessions.PrincipalKey(identity);
        var entry = sticky.TryGet(sessionId, principalKey);
        if (entry is null)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        // Serialize with any in-flight call on this session — matches the concurrency contract
        // documented for dispatch (spec §5).
        await entry.Lock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            sticky.Close(sessionId);
        }
        finally
        {
            entry.Lock.Release();
        }

        context.Response.Headers[StickySessions.SessionCloseHeader] = "true";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>Rejects a request by throwing <see cref="AuthFailure"/> (any other exception is
    /// treated as <see cref="AuthReason.Unauthorized"/>), or returns normally to let dispatch
    /// proceed. Mirrors Python's <c>authenticate</c> callback contract.</summary>
    public delegate Task AuthenticateDelegate(HttpContext context);

    /// <summary>Runs <paramref name="authenticate"/> if one is configured, writing a
    /// §4-shaped 401 (see <see cref="UnauthorizedResponseWriter"/>) and returning
    /// <see langword="true"/> on rejection — callers should return immediately when this returns
    /// <see langword="true"/>. Returns <see langword="false"/> (nothing written) when
    /// <paramref name="authenticate"/> is <see langword="null"/> or accepts the request.</summary>
    private static async Task<bool> TryRejectUnauthenticatedAsync(HttpContext context, AuthenticateDelegate? authenticate, string? proxyHint)
    {
        if (authenticate is null)
        {
            return false;
        }

        try
        {
            await authenticate(context).ConfigureAwait(false);
            return false;
        }
        catch (AuthFailure failure)
        {
            await UnauthorizedResponseWriter.WriteAsync(context, failure.Reason, failure.Detail, proxyHint, context.RequestAborted).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            await UnauthorizedResponseWriter.WriteAsync(context, AuthReason.Unauthorized, "", proxyHint, context.RequestAborted).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>Outcome of resolving any presented <c>VGI-Session</c> token before dispatch.
    /// <see cref="Entry"/>/<see cref="SessionIdHex"/> are non-null only when a token was
    /// presented and resolved successfully (a "resume"); both are null on a fresh request with no
    /// token, whether or not the method goes on to open one.</summary>
    private readonly record struct StickyResolution(StickySessionEntry? Entry, string? SessionIdHex, bool AcceptOpens, string PrincipalKey, AuthIdentity? Identity);

    /// <summary>
    /// Resolves any presented <see cref="StickySessions.SessionHeader"/> token against
    /// <paramref name="sticky"/>'s registry — the HTTP-dispatch-time half of the sticky wire
    /// contract shared by unary calls and every stream turn (spec §2.1, §3). On resolution
    /// failure, writes the §6-shaped <see cref="SessionLostException"/> response itself and
    /// returns <see langword="null"/> — callers must return immediately in that case, exactly
    /// like every other <c>ErrorResultAsync</c> short-circuit in this class.
    /// </summary>
    private static async Task<StickyResolution?> TryResolveStickyAsync(StickySessionRegistry? sticky, RpcServer server, string method, HttpContext context, Schema errorSchema, ContentEncoding? encoding, bool useCustomHeader, int? compressionLevel, byte[] tokenKey, string methodType)
    {
        var identity = AuthIdentity.GetFrom(context);
        var principalKey = StickySessions.PrincipalKey(identity);
        var acceptOpens = string.Equals(context.Request.Headers[StickySessions.SessionAcceptHeader].ToString().Trim(), "true", StringComparison.OrdinalIgnoreCase);

        if (sticky is null)
        {
            return new StickyResolution(null, null, acceptOpens, principalKey, identity);
        }

        var tokenHeader = context.Request.Headers[StickySessions.SessionHeader].ToString();
        if (string.IsNullOrEmpty(tokenHeader))
        {
            return new StickyResolution(null, null, acceptOpens, principalKey, identity);
        }

        try
        {
            var aad = StickySessions.ComputeAad(identity);
            var (tokenServerId, sessionId, _) = StickySessions.OpenToken(tokenHeader.Trim(), tokenKey, aad);
            if (tokenServerId != server.ServerId)
            {
                // Wrong worker — without a cross-worker replay mechanism (out of scope, spec §3),
                // the only honest answer is "this session is gone", even though the token might
                // be perfectly valid on its owning worker.
                throw new SessionLostException("session token was issued by a different worker (server_id mismatch)");
            }

            var entry = sticky.TryGet(sessionId, principalKey) ?? throw new SessionLostException("session not found, expired, or principal mismatch");
            return new StickyResolution(entry, Convert.ToHexStringLower(sessionId), acceptOpens, principalKey, identity);
        }
        catch (SessionLostException exc)
        {
            await ErrorResultAsync(server, method, exc, StatusCodes.Status500InternalServerError, errorSchema, StatusCodes.Status200OK, context, encoding, useCustomHeader, compressionLevel, methodType: methodType).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Writes the sticky response headers a completed dispatch produced (spec §2.2) and
    /// releases the per-session lock if <paramref name="state"/> held one. Call this exactly once
    /// after dispatch, in a <c>finally</c>-equivalent position, regardless of whether dispatch
    /// threw — <see cref="StickyCallState.ReleaseLockIfHeld"/> is idempotent.</summary>
    private static void FinishSticky(HttpContext context, StickySessionRegistry sticky, StickyCallState state)
    {
        state.ReleaseLockIfHeld();
        if (state.MintedToken is not null)
        {
            context.Response.Headers[StickySessions.SessionHeader] = state.MintedToken;
            foreach (var (name, value) in sticky.EchoHeaders)
            {
                context.Response.Headers[$"{StickySessions.EchoHeaderPrefix}{name}"] = value;
            }
        }

        if (state.Closed)
        {
            context.Response.Headers[StickySessions.SessionCloseHeader] = "true";
        }
    }

    /// <summary>
    /// <c>OPTIONS {prefix}/health</c> — capability discovery, matching
    /// <c>vgi_rpc.http._client.http_capabilities</c>'s contract exactly: <c>VGI-Max-Response-Bytes</c>
    /// when a cap is configured, <c>VGI-Externalization-Enabled: false</c> and
    /// <c>VGI-Upload-URL-Support: false</c> (neither is implemented yet — see docs/roadmap.md M13),
    /// and <c>VGI-Supported-Encodings</c> naming the codecs this server can actually produce for
    /// responses (see <see cref="s_producibleEncodings"/> — gzip only, for now).
    /// </summary>
    private static Task HandleCapabilitiesAsync(HttpContext context, long? maxResponseBytes, StickySessionRegistry? sticky, bool proxyProofRequired)
    {
        var headers = context.Response.Headers;
        if (maxResponseBytes is { } cap)
        {
            headers["VGI-Max-Response-Bytes"] = cap.ToString();
        }

        headers["VGI-Externalization-Enabled"] = "false";
        headers["VGI-Upload-URL-Support"] = "false";
        headers["VGI-Supported-Encodings"] = "gzip";
        if (sticky is not null)
        {
            // Spec §2.3 — advertised on every response when sticky is enabled; OPTIONS /health is
            // the cheapest discovery point (http_capabilities() reads exactly this endpoint).
            headers[StickySessions.StickyEnabledHeader] = "true";
            headers[StickySessions.StickyDefaultTtlHeader] = ((long)sticky.DefaultTtl.TotalSeconds).ToString();
            if (sticky.EchoHeaders.Count > 0)
            {
                headers[StickySessions.StickyEchoHeadersHeader] = string.Join(",", sticky.EchoHeaders.Keys);
            }
        }

        if (proxyProofRequired)
        {
            // docs/proxy-proof-spec.md §2.2 — require mode only, never emitted as "false" in
            // off/allow (writers MUST emit it only in require mode).
            headers[ProxyProof.ProofRequiredHeader] = "true";
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    }

    private static Task HandleHealthAsync(RpcServer server, HttpContext context, bool proxyProofRequired)
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
        if (proxyProofRequired)
        {
            // docs/proxy-proof-spec.md §2.2 — "advertise on every response"; GET/HEAD /health is
            // itself proof-exempt (§2.3) and is what TestProxyProof's own capability check probes
            // directly (a plain GET, not OPTIONS), unlike sticky's OPTIONS-only discovery contract.
            context.Response.Headers[ProxyProof.ProofRequiredHeader] = "true";
        }
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return Task.CompletedTask;
        }

        return context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }

    private static async Task HandleUnaryAsync(RpcServer server, string method, HttpContext context, int? compressionLevel, long? maxResponseBytes, AuthenticateDelegate? authenticate, string? proxyHint, StickySessionRegistry? sticky, byte[] tokenKey)
    {
        if (await TryRejectUnauthenticatedAsync(context, authenticate, proxyHint).ConfigureAwait(false))
        {
            return;
        }

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

        var stickyResolution = await TryResolveStickyAsync(sticky, server, method, context, info.ResultSchema, encoding, useCustomHeader, compressionLevel, tokenKey, "unary").ConfigureAwait(false);
        if (stickyResolution is null)
        {
            return; // error response already written
        }

        var stickyState = sticky is not null
            ? new StickyCallState(sticky, stickyResolution.Value.Entry, stickyResolution.Value.SessionIdHex, stickyResolution.Value.AcceptOpens, stickyResolution.Value.PrincipalKey, stickyResolution.Value.Identity, server.ServerId, tokenKey)
            : null;
        if (stickyResolution.Value.Entry is not null)
        {
            await stickyResolution.Value.Entry.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var start = Stopwatch.GetTimestamp();
        var status = "ok";
        var errorType = "";
        var errorMessage = "";
        var callContext = info.HasContextParameter ? new BufferedHttpCallContext(stickyState) : null;

        var responseBuffer = new MemoryStream();
        try
        {
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
        }
        finally
        {
            // Release regardless of outcome — a method that raises inside a sticky session must
            // not wedge the per-session lock for subsequent calls (spec §5, and the conformance
            // group's own test_session_survives_method_exception).
            stickyState?.ReleaseLockIfHeld();
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

        if (stickyState is not null)
        {
            FinishSticky(context, sticky!, stickyState);
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
    private static async Task HandleStreamInitAsync(RpcServer server, string method, HttpContext context, int? compressionLevel, byte[] tokenKey, StreamCallRegistry registry, AuthenticateDelegate? authenticate, string? proxyHint, StickySessionRegistry? sticky)
    {
        if (await TryRejectUnauthenticatedAsync(context, authenticate, proxyHint).ConfigureAwait(false))
        {
            return;
        }

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

        var stickyResolution = await TryResolveStickyAsync(sticky, server, method, context, s_emptySchema, encoding, useCustomHeader, compressionLevel, tokenKey, "stream").ConfigureAwait(false);
        if (stickyResolution is null)
        {
            return; // error response already written
        }

        var stickyState = sticky is not null
            ? new StickyCallState(sticky, stickyResolution.Value.Entry, stickyResolution.Value.SessionIdHex, stickyResolution.Value.AcceptOpens, stickyResolution.Value.PrincipalKey, stickyResolution.Value.Identity, server.ServerId, tokenKey)
            : null;
        if (stickyResolution.Value.Entry is not null)
        {
            await stickyResolution.Value.Entry.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var start = Stopwatch.GetTimestamp();
        var invokeContext = info.HasContextParameter ? new BufferedHttpCallContext(stickyState) : null;

        IRpcStream stream;
        try
        {
            var raw = await info.InvokeAsync(server.Implementation, args, invokeContext).ConfigureAwait(false);
            stream = (IRpcStream)raw!;
        }
        catch (Exception exc)
        {
            stickyState?.ReleaseLockIfHeld();
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
        if (stickyState is not null)
        {
            FinishSticky(context, sticky!, stickyState);
        }

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
    private static async Task HandleStreamExchangeAsync(RpcServer server, string method, HttpContext context, int? compressionLevel, byte[] tokenKey, StreamCallRegistry registry, long? maxResponseBytes, AuthenticateDelegate? authenticate, string? proxyHint, StickySessionRegistry? sticky)
    {
        if (await TryRejectUnauthenticatedAsync(context, authenticate, proxyHint).ConfigureAwait(false))
        {
            return;
        }

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

        // Sticky is re-resolved fresh on every turn, exactly like Python's middleware — each
        // /exchange call is its own HTTP request, so the token (if any) is re-validated from
        // scratch and the per-session lock is acquired and released within this one turn only,
        // never held across turns (spec §5's "same-session calls serialize" is about concurrent
        // requests, not about a producer/exchange stream's own successive turns).
        var stickyResolution = await TryResolveStickyAsync(sticky, server, method, context, outputSchema, encoding, useCustomHeader, compressionLevel, tokenKey, "stream").ConfigureAwait(false);
        if (stickyResolution is null)
        {
            return; // error response already written
        }

        var stickyState = sticky is not null
            ? new StickyCallState(sticky, stickyResolution.Value.Entry, stickyResolution.Value.SessionIdHex, stickyResolution.Value.AcceptOpens, stickyResolution.Value.PrincipalKey, stickyResolution.Value.Identity, server.ServerId, tokenKey)
            : null;
        if (stickyResolution.Value.Entry is not null)
        {
            await stickyResolution.Value.Entry.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var collector = new OutputCollector(outputSchema);
        // Always construct a per-turn context, not gated on info.HasContextParameter (that flag
        // reflects whether the RPC method that RETURNED the stream declared a ctx parameter —
        // relevant only to that method's own reflection-invoke arg count, in HandleStreamInitAsync
        // above). StreamState.ProcessAsync's own signature always accepts an ICallContext?, so a
        // StreamState reading ctx.Session (sticky sessions) needs a real object here regardless of
        // whether the constructor method itself took a ctx param — mirrors the same fix in
        // RpcServer.ServeStreamAsync's own turnContext construction.
        var turnContext = new StreamHttpCallContext(collector, stickyState);
        Exception? turnException = null;
        try
        {
            await stream.State.ProcessAsync(turnBatch, collector, turnContext, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            turnException = Unwrap(exc);
        }

        // Release regardless of outcome — a turn that raises must not wedge the per-session lock
        // for the next turn or a concurrent call on the same session (spec §5, and the
        // conformance group's test_session_survives_method_exception).
        stickyState?.ReleaseLockIfHeld();

        if (turnException is not null)
        {
            registry.Remove(callKey);
            // A session opened earlier in this same turn stays open even though the turn itself
            // failed afterward — the registry entry is real regardless — so the minted-token /
            // closed headers still apply, matching Python's process_response (which emits them
            // unconditionally on req_succeeded=False too).
            if (stickyState is not null)
            {
                FinishSticky(context, sticky!, stickyState);
            }

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
            if (stickyState is not null)
            {
                FinishSticky(context, sticky!, stickyState);
            }

            await WriteBytesAsync(context, StatusCodes.Status200OK, responseBuffer.ToArray(), encoding, useCustomHeader, compressionLevel, cancellationToken).ConfigureAwait(false);
            return;
        }

        EmitAccessLog(server, info.WireName, "stream", "ok", "", "", start, StatusCodes.Status200OK, callKey);
        if (stickyState is not null)
        {
            FinishSticky(context, sticky!, stickyState);
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
    private sealed class BufferedHttpCallContext(StickyCallState? sticky = null) : Server.ICallContext
    {
        public List<LogMessage> Buffered { get; } = [];

        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            Buffered.Add(new LogMessage(level, message, extra));

        public object? Session => sticky?.CurrentState;

        public string? SessionId => sticky?.SessionIdHex;

        public void OpenSession(object state, TimeSpan? ttl = null) => RequireSticky().Open(state, ttl);

        public void CloseSession() => RequireSticky().Close();

        private StickyCallState RequireSticky() => sticky ?? throw new RpcException("RuntimeError", "sticky sessions not available on this transport");
    }

    /// <summary>Forwards a stream turn's <see cref="Server.ICallContext.EmitLog"/> calls into
    /// that turn's <see cref="OutputCollector"/> — the HTTP-transport analog of
    /// <see cref="RpcServer"/>'s private nested type of the same shape.</summary>
    private sealed class StreamHttpCallContext(OutputCollector collector, StickyCallState? sticky = null) : Server.ICallContext
    {
        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            collector.ClientLog(level, message, extra);

        public object? Session => sticky?.CurrentState;

        public string? SessionId => sticky?.SessionIdHex;

        public void OpenSession(object state, TimeSpan? ttl = null) => RequireSticky().Open(state, ttl);

        public void CloseSession() => RequireSticky().Close();

        private StickyCallState RequireSticky() => sticky ?? throw new RpcException("RuntimeError", "sticky sessions not available on this transport");
    }

    /// <summary>
    /// Bridges <see cref="Server.ICallContext.OpenSession"/>/<see cref="Server.ICallContext.CloseSession"/>
    /// to <see cref="StickySessionRegistry"/> for one HTTP request — constructed once per
    /// dispatch (unary, stream init, or one exchange/producer turn) by
    /// <see cref="TryResolveStickyAsync"/>'s caller, read back afterward by
    /// <see cref="FinishSticky"/>. The C#-explicit-object analog of Python's contextvar-driven
    /// <c>_StickySink</c> — see <see cref="StickySessions"/>'s class doc comment.
    /// </summary>
    private sealed class StickyCallState
    {
        private readonly StickySessionRegistry _registry;
        private readonly StickySessionEntry? _resumedEntry;
        private readonly bool _acceptOpens;
        private readonly string _principalKey;
        private readonly AuthIdentity? _identity;
        private readonly string _serverId;
        private readonly byte[] _tokenKey;
        private bool _lockReleased;
        private bool _sessionActive;

        public StickyCallState(StickySessionRegistry registry, StickySessionEntry? resumedEntry, string? resumedSessionIdHex, bool acceptOpens, string principalKey, AuthIdentity? identity, string serverId, byte[] tokenKey)
        {
            _registry = registry;
            _resumedEntry = resumedEntry;
            _acceptOpens = acceptOpens;
            _principalKey = principalKey;
            _identity = identity;
            _serverId = serverId;
            _tokenKey = tokenKey;
            if (resumedEntry is not null)
            {
                CurrentState = resumedEntry.State;
                SessionIdHex = resumedSessionIdHex;
                _sessionActive = true;
            }
        }

        /// <summary>The session state visible to <see cref="Server.ICallContext.Session"/> —
        /// either the resumed entry's state, the just-opened state, or <see langword="null"/>
        /// after <see cref="Close"/> or when no session is bound.</summary>
        public object? CurrentState { get; private set; }

        public string? SessionIdHex { get; private set; }

        /// <summary>Non-null once <see cref="Open"/> has minted a token this request — the
        /// caller writes it onto the <see cref="StickySessions.SessionHeader"/> response header.</summary>
        public string? MintedToken { get; private set; }

        /// <summary>Set once <see cref="Close"/> has run — the caller emits
        /// <see cref="StickySessions.SessionCloseHeader"/>.</summary>
        public bool Closed { get; private set; }

        public void Open(object state, TimeSpan? ttl)
        {
            if (!_acceptOpens)
            {
                // Wire type "RuntimeError" — matches Python, which raises its own built-in
                // RuntimeError here (see ICallContext.OpenSession's doc comment).
                throw new RpcException(
                    "RuntimeError",
                    $"client did not opt in to sticky sessions (missing {StickySessions.SessionAcceptHeader}: true header)");
            }

            if (_sessionActive)
            {
                throw new RpcException("RuntimeError", "a sticky session is already active for this request");
            }

            var (sessionId, expiresAt) = _registry.Open(state, ttl, _principalKey); // may throw ServerDrainingException
            var aad = StickySessions.ComputeAad(_identity);
            MintedToken = StickySessions.SealToken(_serverId, sessionId, expiresAt.ToUnixTimeSeconds(), _tokenKey, aad);
            SessionIdHex = Convert.ToHexStringLower(sessionId);
            CurrentState = state;
            _sessionActive = true;
        }

        public void Close()
        {
            if (SessionIdHex is null)
            {
                return; // idempotent no-op — matches Python (no bound session ⇒ close is a no-op)
            }

            var idHex = SessionIdHex;
            // Release the per-session lock now (if this call resumed via a presented token) so a
            // caller that immediately re-presents the same session_id — impossible today since
            // the token is stale after this — never double-waits behind FinishSticky's own
            // idempotent release.
            ReleaseLockIfHeld();
            _registry.Close(Convert.FromHexString(idHex));
            Closed = true;
            CurrentState = null;
            _sessionActive = false;
            SessionIdHex = null;
        }

        /// <summary>Releases the resumed entry's per-session lock exactly once, whether called
        /// from <see cref="Close"/> (early release, mid-dispatch) or from
        /// <see cref="FinishSticky"/> (the normal end-of-dispatch release) — idempotent so calling
        /// both is always safe.</summary>
        public void ReleaseLockIfHeld()
        {
            if (_resumedEntry is null || _lockReleased)
            {
                return;
            }

            _lockReleased = true;
            try
            {
                _resumedEntry.Lock.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already released — shouldn't happen given the guard above, but matches Python's
                // own contextlib.suppress(RuntimeError) belt-and-braces.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
