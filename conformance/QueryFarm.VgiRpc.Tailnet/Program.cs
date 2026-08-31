using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tailnet;

try
{
    var arguments = CliArguments.Parse(args);
    switch (arguments.Mode)
    {
        case "client-tcp":
            await RunTcpClientAsync(arguments);
            break;
        case "client-http":
            await RunHttpClientAsync(arguments);
            break;
        case "server-http":
            await RunHttpServerAsync(arguments);
            break;
        default:
            throw new ArgumentException("mode must be client-tcp, client-http, or server-http");
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = exception.Message }));
    Environment.ExitCode = 1;
}

static async Task RunTcpClientAsync(CliArguments arguments)
{
    var host = arguments.Required("host");
    var port = arguments.RequiredInt("port");
    await using var client = arguments.Optional("proxy") is { } proxy
        ? await RpcClient.ConnectTcpAsync(host, port, proxy, arguments.Timeout)
        : await RpcClient.ConnectTcpAsync(host, port);
    await ValidateTwiceAsync(client.CreateProxy<ITailnetEvidenceService>(), arguments);
}

static async Task RunHttpClientAsync(CliArguments arguments)
{
    var headers = arguments.Optional("spoof-login") is { } spoofLogin
        ? new Dictionary<string, string> { ["Tailscale-User-Login"] = spoofLogin }
        : null;
    await using var client = new HttpRpcClient(new Uri(arguments.Required("url")), new HttpRpcClientOptions
    {
        TcpProxy = arguments.Optional("proxy"),
        ConnectTimeout = arguments.Timeout,
        DefaultHeaders = headers,
    });
    await ValidateTwiceAsync(client.CreateProxy<ITailnetEvidenceService>(), arguments);
}

static async Task ValidateTwiceAsync(ITailnetEvidenceService service, CliArguments arguments)
{
    var expected = new TailnetSnapshotExpectations(
        arguments.Required("expected-issuer"),
        arguments.Required("expected-evidence-source"),
        arguments.Required("expected-assurance"),
        arguments.Required("expected-subject-kind"),
        arguments.Required("expected-subject-stability"),
        arguments.Required("expected-capability"),
        arguments.Optional("expected-tag"),
        arguments.Optional("expected-target-kind"),
        arguments.Flag("expect-authenticated"),
        arguments.Flag("expect-proxy"));
    var first = await service.SnapshotAsync();
    var second = await service.SnapshotAsync();
    TailnetEvidenceValidator.ValidateSnapshot(first, expected);
    TailnetEvidenceValidator.ValidateSnapshot(second, expected);
    if (!StringComparer.Ordinal.Equals(first, second))
        throw new InvalidDataException("Tailnet evidence changed between qualification calls");
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, mode = arguments.Mode }));
}

static async Task RunHttpServerAsync(CliArguments arguments)
{
    var issuer = arguments.Required("issuer");
    var capability = arguments.Required("expected-capability");
    var trusted = new[]
    {
        arguments.Optional("trusted-proxy-ipv4") ?? "127.0.0.1",
        arguments.Optional("trusted-proxy-ipv6") ?? "::1",
    };
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls($"http://{arguments.Required("host")}:{arguments.RequiredInt("port")}");
    var app = builder.Build();
    app.UseVgiRpcPhysicalPeerSnapshot();
    var provider = TailscalePeerIdentityProviders.Serve(issuer, trusted);
    var authenticate = PeerIdentityAuthentication.Compose(
        null,
        [provider],
        PeerAuthenticationPolicies.Require("tailscale"),
        timeout: arguments.Timeout);
    var implementation = new TailnetConformanceService(new TailnetServerExpectations(issuer, capability));
    app.MapVgiRpc(new RpcServer(typeof(IConformanceService), implementation), authenticate: authenticate);
    await app.RunAsync();
}

internal sealed class CliArguments
{
    private readonly Dictionary<string, List<string>> _values;

    private CliArguments(string mode, Dictionary<string, List<string>> values)
    {
        Mode = mode;
        _values = values;
    }

    public string Mode { get; }
    public TimeSpan Timeout => TimeSpan.FromSeconds(OptionalInt("timeout-seconds") ?? 10);

    public static CliArguments Parse(string[] arguments)
    {
        if (arguments.Length == 0) throw new ArgumentException("a mode is required");
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Length;)
        {
            var option = arguments[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("options must start with --");
            var name = option[2..];
            var isFlag = name is "expect-authenticated" or "expect-proxy";
            if (!isFlag && index + 1 >= arguments.Length)
                throw new ArgumentException($"--{name} requires a value");
            var value = isFlag ? "true" : arguments[index + 1];
            if (!values.TryGetValue(name, out var entries)) values[name] = entries = [];
            entries.Add(value);
            index += isFlag ? 1 : 2;
        }
        return new CliArguments(arguments[0], values);
    }

    public string Required(string name) => Optional(name)
        ?? throw new ArgumentException($"--{name} is required");

    public string? Optional(string name) => _values.TryGetValue(name, out var values) switch
    {
        true when values.Count == 1 => values[0],
        true => throw new ArgumentException($"--{name} may be supplied only once"),
        false => null,
    };

    public int RequiredInt(string name) => ParseInt(Required(name), name);
    public int? OptionalInt(string name) => Optional(name) is { } value ? ParseInt(value, name) : null;
    public bool Flag(string name) => _values.TryGetValue(name, out var values) && values switch
    {
        ["true"] => true,
        _ => throw new ArgumentException($"--{name} may be supplied only once"),
    };

    private static int ParseInt(string value, string name) =>
        int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"--{name} must be a positive integer");
}
