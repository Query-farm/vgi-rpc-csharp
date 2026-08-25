// Conformance worker entry point. Registers ConformanceServiceImpl and serves it over stdio
// (default), --unix PATH, --tcp [HOST:]PORT, or --http [--host HOST] [--port PORT] — the
// mandatory CLI contract from docs/porting-guide.md (canonical Python repo). --http currently
// serves unary calls only — streaming (/init, /exchange) is not yet implemented, see
// docs/roadmap.md M6.

using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QueryFarm.VgiRpc.AccessLog;
using QueryFarm.VgiRpc.Conformance;
using QueryFarm.VgiRpc.ConformanceWorker;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

var options = CliOptions.Parse(args);
if (options is null)
{
    return 1;
}

using var accessLog = options.AccessLogPath is { } accessLogPath ? new JsonlAccessLogSink(accessLogPath, debug: options.AccessLogDebug) : null;
var server = new RpcServer(typeof(IConformanceService), new ConformanceServiceImpl(), accessLog: accessLog);

using var cts = new CancellationTokenSource();
RegisterShutdownHandlers(cts);

if (options.UnixSocketPath is { } unixPath)
{
    Console.WriteLine($"UNIX:{unixPath}");
    Console.Out.Flush();
    await SocketTransport.ServeUnixAsync(
        unixPath,
        (transport, ct) => server.ServeAsync(transport, ct),
        cts.Token);
    return 0;
}

if (options.Tcp is { } tcp)
{
    await SocketTransport.ServeTcpAsync(
        tcp.Host,
        tcp.Port,
        (transport, ct) => server.ServeAsync(transport, ct),
        cts.Token,
        onBound: boundPort =>
        {
            Console.WriteLine($"PORT:{boundPort}");
            Console.Out.Flush();
        });
    return 0;
}

if (options.Http)
{
    var builder = WebApplication.CreateBuilder([]);
    // The discovery contract is "print exactly PORT:<port>\n on stdout, then flush" — Kestrel's
    // own startup/request logging defaults to stdout too and would interleave with that line, so
    // silence everything except what the app itself explicitly writes.
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls($"http://{options.HttpHost}:{options.HttpPort}");
    const string CorsPolicyName = "vgi-rpc-conformance";
    var corsEnabled = options.ConformanceCorsOrigins.Count > 0;
    if (corsEnabled)
    {
        builder.Services.AddVgiRpcCors(CorsPolicyName, options.ConformanceCorsOrigins, maxResponseBytes: 65536, proxyHint: options.ConformanceProxyHint);
    }

    var app = builder.Build();
    if (corsEnabled)
    {
        app.UseCors();
        app.UseVgiRpcCorsExtras();
    }
    // Not mandatory CLI flags per the porting guide — fixed values only this worker needs so the
    // corresponding conformance tests can run instead of skip:
    //  - maxResponseBytes: 64 KiB, small and fast (http_response_cap.* multiplies it by 4 to
    //    guarantee overshoot — see docs/roadmap.md M7).
    //  - --conformance-auth-reason: every RPC call 401s, with the reason read from
    //    X-Conformance-Auth-Reason — a conformance-fixture affordance per
    //    docs/unauthorized-spec.md §7.1, never something a production server would honour from a
    //    request. Mirrors the reference repo's tests/serve_conformance_http_auth.py.
    RpcHttpEndpoints.AuthenticateDelegate? authenticate;
    if (options.ConformanceAuthReason)
    {
        authenticate = context =>
        {
            var requested = context.Request.Headers["X-Conformance-Auth-Reason"].ToString();
            var reason = requested switch
            {
                "missing_credential" => AuthReason.MissingCredential,
                "invalid_credential" => AuthReason.InvalidCredential,
                "expired_credential" => AuthReason.ExpiredCredential,
                "insufficient_scope" => AuthReason.InsufficientScope,
                _ => (AuthReason?)null,
            };
            // proxy_required and unauthorized are deliberately NOT requestable — see
            // docs/unauthorized-spec.md §7.1: proxy_required must come from server configuration,
            // never the request, and unauthorized is what the *absence* of a requested reason
            // must produce (so making it requestable would hide whether that fallback works).
            if (reason is { } r)
            {
                throw new AuthFailure(r, $"conformance fixture: requested {requested}");
            }

            throw new InvalidOperationException("conformance fixture: no matching X-Conformance-Auth-Reason");
        };
    }
    else if (options.ConformanceMtlsSubject)
    {
        // MtlsAuth.FromSubject() reads X-SSL-Client-Cert (URL-encoded PEM) and accepts any
        // certificate, using its Subject CN as principal — see docs/roadmap.md M9 mTLS.
        authenticate = MtlsAuth.FromSubject();
    }
    else if (options.StickyAuth || options.Introspect)
    {
        // Maps X-Conformance-Principal to an AuthIdentity — absent header stays anonymous (never
        // rejected, so unauthenticated probes like GET /health keep working), matching the
        // canonical Python repo's tests/serve_conformance_http.py::_principal_from_header
        // exactly. Backs TestSticky::test_cross_principal_replay_rejected (spec §9.1) and (shared
        // with --introspect, since token introspection's caller identity is resolved the exact
        // same way) TestTokenIntrospection's caller-authorization checks (docs/roadmap.md M12).
        authenticate = context =>
        {
            var principal = context.Request.Headers["X-Conformance-Principal"].ToString();
            if (!string.IsNullOrEmpty(principal))
            {
                AuthIdentity.SetOn(context, "conformance", principal);
            }

            return Task.CompletedTask;
        };
    }
    else
    {
        authenticate = null;
    }

    // Proxy proof composes with whatever authenticate delegate was selected above (spec §8: a
    // precondition, ANDed with user authentication) via ProxyProof.RequireAll — see that method's
    // doc comment for why this port needs no special combinator type to get "gate first, inner
    // only on success" for free. None of the other conformance flags are exercised together with
    // --proof-mode by the shared TestProxyProof group's own fixtures today, but composing here
    // rather than replacing keeps that combination correct if a future test needs it.
    var proxyProofRequired = false;
    if (options.ProofMode is { } modeArg && modeArg != "off")
    {
        var mode = modeArg switch
        {
            "allow" => ProxyProofMode.Allow,
            "require" => ProxyProofMode.Require,
            _ => throw new ArgumentException($"--proof-mode must be off|allow|require, got '{modeArg}'"),
        };
        var config = new ProxyProofConfig(
            mode,
            originId: options.ProofOriginId ?? "",
            secrets: options.ProofSecrets is { } s ? ProxyProof.ParseSecrets(s) : null,
            skewSeconds: options.ProofSkewSeconds,
            enableReplayCache: !options.ProofNoReplayCache);
        var gate = ProxyProof.CreateGate(config);
        authenticate = ProxyProof.RequireAll(gate, authenticate);
        proxyProofRequired = mode == ProxyProofMode.Require;
    }

    var tokenKey = options.TokenKeyHex is { } hex ? Convert.FromHexString(hex) : null;
    StickySessionRegistry? sticky = null;
    if (options.ConformanceSticky)
    {
        // Fixed marker the canonical TestSticky::test_echo_header_round_trip conformance test
        // asserts on — real deployments substitute their own (e.g. Fly.io's fly-force-instance-id
        // via vgi_rpc.http.fly.fly_sticky_echo_headers() or their own mapping).
        var echoHeaders = new Dictionary<string, string> { ["x-vgi-conformance-echo"] = "conformance-fixed-marker" };
        sticky = new StickySessionRegistry(
            defaultTtl: options.StickyTtlSeconds is { } ttl ? TimeSpan.FromSeconds(ttl) : null,
            echoHeaders: echoHeaders);
    }

    // Fixed constants docs/porting-guide.md's "HTTP token introspection" section and the
    // canonical TestTokenIntrospection conformance group require exactly (see
    // vgi_rpc.conformance._pytest_suite's _INTROSPECTOR/_SUBJECT_TOKEN/_SUBJECT_PRINCIPAL/
    // _UNAVAILABLE_TOKEN — the JWS trap token needs no entry here at all: the shape guard in
    // TokenIntrospection.HandleAsync rejects it before ever reaching this resolver, and the
    // conformance suite's own trap token is deliberately *not* pre-registered — resolving it
    // would be the bug the test exists to catch).
    TokenIntrospection.TokenResolver? introspectResolver = null;
    IReadOnlySet<string>? introspectPrincipals = null;
    if (options.Introspect)
    {
        introspectResolver = token => Task.FromResult(token switch
        {
            "conformance-opaque-subject-token" => new TokenIdentity("subject@conformance.example"),
            "conformance-unavailable-token" => throw new AuthUnavailableException(),
            _ => (TokenIdentity?)null,
        });
        introspectPrincipals = new HashSet<string> { "conformance-introspector" };
    }

    // External storage (M13) — a --fake-storage URL wires both directions: server-response
    // externalization (ServerExternalConfig.Storage) and client-vended upload URLs
    // (ExternalizationOptions.UploadUrlProvider), so the OPTIONS capabilities response advertises
    // the full protocol. Mirrors the reference repo's tests/serve_conformance_http.py exactly,
    // including "max_request_bytes defaults to externalize_threshold when unset" and the
    // 127.0.0.1-only redirect-hop validator --reject-localhost-redirects installs.
    ExternalizationOptions? externalization = null;
    if (options.FakeStorageUrl is { } fakeStorageUrl)
    {
        var backend = new FakeStorageBackend(fakeStorageUrl);
        Action<string>? urlValidator = options.RejectLocalhostRedirects
            ? url => { if (new Uri(url).Host != "127.0.0.1") throw new ArgumentException("external-security fixture permits only 127.0.0.1"); }
        : null;
        var externalConfig = new ServerExternalConfig
        {
            Storage = backend,
            ExternalizeThresholdBytes = options.ExternalizeThresholdBytes,
            Compression = options.CompressionAlgorithm == "zstd" ? new Compression() : null,
            FetchConfig = new FetchConfig
            {
                MaxFetchBytes = options.MaxFetchBytes ?? new FetchConfig().MaxFetchBytes,
                MaxDecompressedBytes = options.MaxDecompressedFetchBytes,
            },
            UrlValidator = urlValidator,
        };
        externalization = new ExternalizationOptions
        {
            External = externalConfig,
            UploadUrlProvider = backend,
            MaxRequestBytes = options.MaxRequestBytes ?? options.ExternalizeThresholdBytes,
            MaxUploadBytes = 64 * 1024 * 1024,
            MaxExternalizedResponseBytes = options.MaxExternalizedResponseBytes,
        };
    }
    else if (options.MaxRequestBytes is { } maxRequestBytesOnly)
    {
        // --max-request-bytes without --fake-storage — backs the small_request_cap fixture
        // (TestHttpResponseCap's 413 enforcement doesn't need externalization wired at all).
        externalization = new ExternalizationOptions { MaxRequestBytes = maxRequestBytesOnly };
    }

    app.MapVgiRpc(server, maxResponseBytes: options.MaxResponseBytes ?? 65536, authenticate: authenticate, proxyHint: options.ConformanceProxyHint, corsPolicyName: corsEnabled ? CorsPolicyName : null, tokenKey: tokenKey, sticky: sticky, proxyProofRequired: proxyProofRequired, introspectResolver: introspectResolver, introspectPrincipals: introspectPrincipals, externalization: externalization);

    // Test-only admin endpoint — NOT part of MapVgiRpc's real surface. Lets
    // TestSticky::test_drain_rejects_new_opens flip the drain flag over the wire instead of
    // sending SIGTERM (which would kill this subprocess fixture mid-test). Mirrors the canonical
    // Python repo's tests/serve_conformance_http.py::_TestDrainResource. 404s (the route simply
    // isn't registered) when sticky isn't enabled, which the conformance test treats as "skip".
    if (sticky is { } stickyRegistry)
    {
        app.MapPost("/__test_drain__", () =>
        {
            stickyRegistry.Drain();
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
        app.MapDelete("/__test_drain__", () =>
        {
            stickyRegistry.ClearDrain();
            return Results.StatusCode(StatusCodes.Status204NoContent);
        });
    }

    await app.StartAsync(cts.Token);
    var boundPort = new Uri(app.Urls.First()).Port;
    Console.WriteLine($"PORT:{boundPort}");
    Console.Out.Flush();
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Normal shutdown path — see RegisterShutdownHandlers.
    }

    await app.StopAsync();
    return 0;
}

// Default: stdio — a single connection over the process's own stdin/stdout.
var stdioTransport = new StdioTransport();
await server.ServeAsync(stdioTransport, cts.Token);
return 0;

static void RegisterShutdownHandlers(CancellationTokenSource cts)
{
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        TryCancel(cts);
    };
    // ProcessExit fires on normal exit too (not just signals) — by then `using var cts` in
    // Main may already have disposed it on the success path, so Cancel() here is best-effort.
    AppDomain.CurrentDomain.ProcessExit += (_, _) => TryCancel(cts);
    if (!OperatingSystem.IsWindows())
    {
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            TryCancel(cts);
        });
    }
}

static void TryCancel(CancellationTokenSource cts)
{
    try
    {
        cts.Cancel();
    }
    catch (ObjectDisposedException)
    {
        // Main already finished and disposed it — nothing left to cancel.
    }
}

internal sealed record TcpOptions(string Host, int Port);

internal sealed class CliOptions
{
    public string? UnixSocketPath { get; private init; }
    public TcpOptions? Tcp { get; private init; }
    public bool Http { get; private init; }
    public string HttpHost { get; private init; } = "127.0.0.1";
    public int HttpPort { get; private init; }
    public string? AccessLogPath { get; private init; }
    public bool AccessLogDebug { get; private init; }
    public bool ConformanceAuthReason { get; private init; }
    public string? ConformanceProxyHint { get; private init; }
    public IReadOnlyList<string> ConformanceCorsOrigins { get; private init; } = [];
    public bool ConformanceMtlsSubject { get; private init; }
    public bool ConformanceSticky { get; private init; }
    public double? StickyTtlSeconds { get; private init; }
    public bool StickyAuth { get; private init; }
    public string? TokenKeyHex { get; private init; }
    public string? ProofMode { get; private init; }
    public string? ProofOriginId { get; private init; }
    public string? ProofSecrets { get; private init; }
    public int ProofSkewSeconds { get; private init; } = 30;
    public bool ProofNoReplayCache { get; private init; }
    public bool Introspect { get; private init; }
    public string? FakeStorageUrl { get; private init; }
    public long ExternalizeThresholdBytes { get; private init; } = 4096;
    public long? MaxRequestBytes { get; private init; }
    public string CompressionAlgorithm { get; private init; } = "none";
    public long? MaxFetchBytes { get; private init; }
    public long? MaxDecompressedFetchBytes { get; private init; }
    public bool RejectLocalhostRedirects { get; private init; }
    public long? MaxResponseBytes { get; private init; }
    public long? MaxExternalizedResponseBytes { get; private init; }

    public static CliOptions? Parse(string[] args)
    {
        string? unixPath = null;
        string? tcpArg = null;
        var http = false;
        string? host = null;
        int? port = null;
        string? accessLog = null;
        var accessLogDebug = false;
        var conformanceAuthReason = false;
        string? conformanceProxyHint = null;
        var conformanceCorsOrigins = new List<string>();
        var conformanceMtlsSubject = false;
        var conformanceSticky = false;
        double? stickyTtlSeconds = null;
        var stickyAuth = false;
        string? tokenKeyHex = null;
        string? proofMode = null;
        string? proofOriginId = null;
        string? proofSecrets = null;
        var proofSkewSeconds = 30;
        var proofNoReplayCache = false;
        var introspect = false;
        string? fakeStorageUrl = null;
        var externalizeThresholdBytes = 4096L;
        long? maxRequestBytes = null;
        var compressionAlgorithm = "none";
        long? maxFetchBytes = null;
        long? maxDecompressedFetchBytes = null;
        var rejectLocalhostRedirects = false;
        long? maxResponseBytesOverride = null;
        long? maxExternalizedResponseBytes = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--unix":
                    unixPath = RequireValue(args, ref i, "--unix");
                    break;
                case "--tcp":
                    tcpArg = RequireValue(args, ref i, "--tcp");
                    break;
                case "--http":
                    http = true;
                    break;
                case "--host":
                    host = RequireValue(args, ref i, "--host");
                    break;
                case "--port":
                    port = int.Parse(RequireValue(args, ref i, "--port"));
                    break;
                case "--access-log":
                    accessLog = RequireValue(args, ref i, "--access-log");
                    break;
                case "--access-log-debug":
                    accessLogDebug = true;
                    break;
                case "--conformance-auth-reason":
                    conformanceAuthReason = true;
                    break;
                case "--conformance-proxy-hint":
                    conformanceProxyHint = RequireValue(args, ref i, "--conformance-proxy-hint");
                    break;
                case "--conformance-cors-origin":
                    // Repeatable — mirrors the reference repo's tests/serve_conformance_http_cors.py,
                    // which accepts a list of allowed origins. A conformance-fixture affordance only
                    // (see docs/roadmap.md M9 CORS): a production server picks its own origin list.
                    conformanceCorsOrigins.Add(RequireValue(args, ref i, "--conformance-cors-origin"));
                    break;
                case "--conformance-mtls-subject":
                    // Installs MtlsAuth.FromSubject() (PEM-in-header, X-SSL-Client-Cert) as the
                    // authenticate delegate — a conformance-fixture affordance for verifying mTLS
                    // end-to-end (see docs/roadmap.md M9 mTLS), not something a production server
                    // hardcodes this way.
                    conformanceMtlsSubject = true;
                    break;
                case "--conformance-sticky":
                    // Enables sticky sessions with the registry's default TTL (300s) — a
                    // conformance-fixture affordance (see docs/roadmap.md M10 sticky sessions).
                    conformanceSticky = true;
                    break;
                case "--sticky-ttl":
                    // Overrides the default TTL and implies sticky is enabled — backs
                    // TestSticky::test_expired_session_surfaces_session_lost's short-TTL fixture
                    // (spec §9.1: conformance_http_sticky_short_ttl_port).
                    stickyTtlSeconds = double.Parse(RequireValue(args, ref i, "--sticky-ttl"));
                    conformanceSticky = true;
                    break;
                case "--sticky-auth":
                    // Installs an authenticate delegate that maps X-Conformance-Principal to an
                    // AuthIdentity (absent header ⇒ anonymous, never rejected) and implies sticky
                    // is enabled — backs TestSticky::test_cross_principal_replay_rejected (spec
                    // §9.1: conformance_http_sticky_auth_port).
                    stickyAuth = true;
                    conformanceSticky = true;
                    break;
                case "--token-key":
                    // Hex-encoded AEAD token key shared by stream call-id tokens and sticky
                    // session tokens (see RpcHttpEndpoints.MapVgiRpc's tokenKey parameter). Two
                    // workers booted with the same key back
                    // TestSticky::test_token_from_other_worker_rejected (spec §9.1:
                    // conformance_http_sticky_peer_ports) — the two workers still mint distinct
                    // server_id values (RpcServer.ServerId defaults to a random GUID per process),
                    // which is what makes the rejection meaningful rather than a decrypt failure.
                    tokenKeyHex = RequireValue(args, ref i, "--token-key");
                    break;
                case "--proof-mode":
                    // "off"|"allow"|"require" — mirrors the reference repo's
                    // tests/conformance/proof_harness.py::ProofWorkerConfig.mode, driven by the
                    // shared TestProxyProof group's proof_worker_factory fixture (see
                    // docs/roadmap.md M11).
                    proofMode = RequireValue(args, ref i, "--proof-mode");
                    break;
                case "--proof-origin-id":
                    proofOriginId = RequireValue(args, ref i, "--proof-origin-id");
                    break;
                case "--proof-secrets":
                    // "kid:hex,kid:hex" — see ProxyProof.ParseSecrets.
                    proofSecrets = RequireValue(args, ref i, "--proof-secrets");
                    break;
                case "--proof-skew":
                    proofSkewSeconds = int.Parse(RequireValue(args, ref i, "--proof-skew"));
                    break;
                case "--proof-no-replay-cache":
                    proofNoReplayCache = true;
                    break;
                case "--introspect":
                    // Enables POST {prefix}/__introspect_token__ with the fixed constants
                    // docs/porting-guide.md's "HTTP token introspection" section and
                    // vgi_rpc.conformance._pytest_suite's TestTokenIntrospection group require —
                    // see docs/roadmap.md M12. Caller identity comes from the same
                    // X-Conformance-Principal convention --sticky-auth already uses.
                    introspect = true;
                    break;
                case "--fake-storage":
                    // Base URL of a vgi_rpc.conformance.fake_storage-compatible HTTP service —
                    // enables external-location uploads (see docs/roadmap.md M13). An empty-string
                    // value (as some fixtures pass when composing args conditionally) is treated
                    // the same as omitting the flag entirely.
                    fakeStorageUrl = RequireValue(args, ref i, "--fake-storage");
                    if (fakeStorageUrl.Length == 0)
                    {
                        fakeStorageUrl = null;
                    }

                    break;
                case "--externalize-threshold":
                    externalizeThresholdBytes = long.Parse(RequireValue(args, ref i, "--externalize-threshold"));
                    break;
                case "--max-request-bytes":
                    maxRequestBytes = long.Parse(RequireValue(args, ref i, "--max-request-bytes"));
                    break;
                case "--compression":
                    compressionAlgorithm = RequireValue(args, ref i, "--compression");
                    break;
                case "--max-fetch-bytes":
                    maxFetchBytes = long.Parse(RequireValue(args, ref i, "--max-fetch-bytes"));
                    break;
                case "--max-decompressed-fetch-bytes":
                    maxDecompressedFetchBytes = long.Parse(RequireValue(args, ref i, "--max-decompressed-fetch-bytes"));
                    break;
                case "--reject-localhost-redirects":
                    rejectLocalhostRedirects = true;
                    break;
                case "--max-response-bytes":
                    // Overrides this worker's hardcoded 65536-byte default for --http mode — the
                    // externalized-cap fixture needs this deliberately *generous* (8 MiB) so the
                    // wire-body cap never fires, only the external-channel cap under test.
                    maxResponseBytesOverride = long.Parse(RequireValue(args, ref i, "--max-response-bytes"));
                    break;
                case "--max-externalized-response-bytes":
                    maxExternalizedResponseBytes = long.Parse(RequireValue(args, ref i, "--max-externalized-response-bytes"));
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return null;
            }
        }

        TcpOptions? tcp = null;
        if (tcpArg is not null)
        {
            var parts = tcpArg.Split(':', 2);
            tcp = parts.Length == 2
                ? new TcpOptions(parts[0], int.Parse(parts[1]))
                : new TcpOptions("127.0.0.1", int.Parse(parts[0]));
        }

        return new CliOptions
        {
            UnixSocketPath = unixPath,
            Tcp = tcp,
            Http = http,
            HttpHost = host ?? "127.0.0.1",
            HttpPort = port ?? 0,
            AccessLogPath = accessLog,
            AccessLogDebug = accessLogDebug,
            ConformanceAuthReason = conformanceAuthReason,
            ConformanceProxyHint = conformanceProxyHint,
            ConformanceCorsOrigins = conformanceCorsOrigins,
            ConformanceMtlsSubject = conformanceMtlsSubject,
            ConformanceSticky = conformanceSticky,
            StickyTtlSeconds = stickyTtlSeconds,
            StickyAuth = stickyAuth,
            TokenKeyHex = tokenKeyHex,
            ProofMode = proofMode,
            ProofOriginId = proofOriginId,
            ProofSecrets = proofSecrets,
            ProofSkewSeconds = proofSkewSeconds,
            ProofNoReplayCache = proofNoReplayCache,
            Introspect = introspect,
            FakeStorageUrl = fakeStorageUrl,
            ExternalizeThresholdBytes = externalizeThresholdBytes,
            MaxRequestBytes = maxRequestBytes,
            CompressionAlgorithm = compressionAlgorithm,
            MaxFetchBytes = maxFetchBytes,
            MaxDecompressedFetchBytes = maxDecompressedFetchBytes,
            RejectLocalhostRedirects = rejectLocalhostRedirects,
            MaxResponseBytes = maxResponseBytesOverride,
            MaxExternalizedResponseBytes = maxExternalizedResponseBytes,
        };
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{flag} requires a value.");
        }

        return args[++i];
    }
}
