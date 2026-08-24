// Conformance worker entry point. Registers ConformanceServiceImpl and serves it over stdio
// (default), --unix PATH, --tcp [HOST:]PORT, or --http [--host HOST] [--port PORT] — the
// mandatory CLI contract from docs/porting-guide.md (canonical Python repo). --http currently
// serves unary calls only — streaming (/init, /exchange) is not yet implemented, see
// docs/roadmap.md M6.

using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    var app = builder.Build();
    // Not mandatory CLI flags per the porting guide — fixed values only this worker needs so the
    // corresponding conformance tests can run instead of skip:
    //  - maxResponseBytes: 64 KiB, small and fast (http_response_cap.* multiplies it by 4 to
    //    guarantee overshoot — see docs/roadmap.md M7).
    //  - --conformance-auth-reason: every RPC call 401s, with the reason read from
    //    X-Conformance-Auth-Reason — a conformance-fixture affordance per
    //    docs/unauthorized-spec.md §7.1, never something a production server would honour from a
    //    request. Mirrors the reference repo's tests/serve_conformance_http_auth.py.
    RpcHttpEndpoints.AuthenticateDelegate? authenticate = options.ConformanceAuthReason
        ? context =>
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
        }
    : null;
    app.MapVgiRpc(server, maxResponseBytes: 65536, authenticate: authenticate, proxyHint: options.ConformanceProxyHint);
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
