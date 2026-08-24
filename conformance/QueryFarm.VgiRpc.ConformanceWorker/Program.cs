// Conformance worker entry point. Registers ConformanceServiceImpl and serves it over stdio
// (default), --unix PATH, or --tcp [HOST:]PORT — the mandatory CLI contract from
// docs/porting-guide.md (canonical Python repo). --http/--access-log are accepted (so the
// worker doesn't reject a caller who always passes them) but not yet implemented — see
// docs/roadmap.md M5/M6.

using System.Runtime.InteropServices;
using QueryFarm.VgiRpc.Conformance;
using QueryFarm.VgiRpc.ConformanceWorker;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

var options = CliOptions.Parse(args);
if (options is null)
{
    return 1;
}

var server = new RpcServer(typeof(IConformanceService), new ConformanceServiceImpl());

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
    Console.Error.WriteLine("--http is not yet implemented (see docs/roadmap.md M6).");
    return 1;
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
        cts.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
    if (!OperatingSystem.IsWindows())
    {
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            cts.Cancel();
        });
    }
}

internal sealed record TcpOptions(string Host, int Port);

internal sealed class CliOptions
{
    public string? UnixSocketPath { get; private init; }
    public TcpOptions? Tcp { get; private init; }
    public bool Http { get; private init; }
    public string? AccessLogPath { get; private init; }

    public static CliOptions? Parse(string[] args)
    {
        string? unixPath = null;
        string? tcpArg = null;
        var http = false;
        string? host = null;
        int? port = null;
        string? accessLog = null;

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

        _ = host; // reserved for --http (see docs/roadmap.md M6); not used by --unix/--tcp
        _ = port;

        return new CliOptions
        {
            UnixSocketPath = unixPath,
            Tcp = tcp,
            Http = http,
            AccessLogPath = accessLog,
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
