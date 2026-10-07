using System.Diagnostics;
using System.Text.RegularExpressions;
using Apache.Arrow;
using Apache.Arrow.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using QueryFarm.VgiRpc.Attributes;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Client.Http;
using QueryFarm.VgiRpc.Conformance;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// <see cref="RpcReflection"/>: list and describe protocols over a connection the caller holds,
/// on every transport, against this port's own server and the reference Python conformance
/// server (<c>python -m vgi_rpc.conformance._cli</c>, gated on <c>VGI_PYTHON_BIN</c>).
/// </summary>
/// <remarks>
/// Both servers host <c>ConformanceService</c>, then <c>conformance.Secondary.v1</c>, then
/// reflection -- so one set of assertions covers both. This port's server always hosts
/// reflection; the reference hosts it only with <c>--describe</c> (its
/// <c>enable_describe</c>), which is how the no-reflection path is reached.
/// </remarks>
public sealed partial class ReflectionClientTests
{
    private const string Primary = "ConformanceService";
    private const string Reflection = ReflectionProtocol.ProtocolName;

    /// <summary>Pinned in <see cref="ISecondary"/>'s own docs: the hash is cross-port.</summary>
    private const string SecondaryHash = "58557cf1611546ad22d1c379bc3ce1b04166082f78375e9fc959f0086347eab6";

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Hex64();

    /// <summary>A client-side view of the conformance primary: two of its unary methods.</summary>
    [ProtocolName(Primary)]
    public interface IConformanceEcho
    {
        Task<string> EchoStringAsync(string value);

        Task<long> EchoIntAsync(long value);
    }

    /// <summary>A client-side view of <see cref="ISecondary"/>: a typed proxy needs Task returns.</summary>
    [ProtocolName(ISecondary.Name)]
    public interface ISecondaryView
    {
        Task<string> EchoStringAsync(string value);
    }

    private sealed class ConformanceEcho : IConformanceEcho
    {
        public Task<string> EchoStringAsync(string value) => Task.FromResult(value);

        public Task<long> EchoIntAsync(long value) => Task.FromResult(value);
    }

    private static RpcServer OwnServer() =>
        new(typeof(IConformanceEcho), new ConformanceEcho(),
            additionalProtocols: [new HostedProtocol(typeof(ISecondary), new SecondaryImpl())]);

    public static TheoryData<string, string> Transports()
    {
        var data = new TheoryData<string, string>();
        foreach (var t in new[] { "pipe", "shm", "tcp", "http" }) data.Add("own", t);
        foreach (var t in new[] { "subprocess", "shm", "pool", "tcp", "http" }) data.Add("reference", t);
        if (!OperatingSystem.IsWindows())
        {
            data.Add("own", "unix");
            data.Add("reference", "unix");
        }

        return data;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ every transport

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ListProtocols_ServerOrderWithCanonicalHashes(string server, string transport)
    {
        await using var conn = await Conn.OpenAsync(server, transport);

        var hosted = await RpcReflection.ListProtocolsAsync(conn.Target, Ct);

        Assert.Equal([Primary, ISecondary.Name, Reflection], hosted.Select(p => p.Name));
        Assert.All(hosted, p => Assert.Matches(Hex64(), p.Hash));
        Assert.Equal(SecondaryHash, hosted[1].Hash);
        Assert.False(hosted[0].Deprecated);
        Assert.Equal("", hosted[0].DeprecationMessage);
        Assert.Empty(hosted[0].Features);
        if (conn.Server is { } own)
        {
            Assert.Equal(own.ProtocolHashFor(Primary), hosted[0].Hash);
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task DescribeProtocol_NamesEveryMethodWithTheListedHash(string server, string transport)
    {
        await using var conn = await Conn.OpenAsync(server, transport);

        var hosted = (await conn.Client.ListProtocolsAsync(Ct)).ToDictionary(p => p.Name);
        var description = await RpcReflection.DescribeProtocolAsync(conn.Target, Primary, Ct);
        var reflection = await conn.Client.DescribeProtocolAsync(Reflection, Ct);
        var secondary = await RpcReflection.DescribeProtocolAsync(conn.Target, ISecondary.Name, Ct);

        Assert.Equal(Primary, description.ProtocolName);
        Assert.Equal(hosted[Primary].Hash, description.ProtocolHash);
        Assert.NotEmpty(description.ServerId);
        Assert.NotEmpty(description.RequestVersion);
        var echo = description.Methods["echo_string"];
        Assert.Equal(RpcMethodKind.Unary, echo.MethodType);
        Assert.True(echo.HasReturn);
        Assert.Null(echo.IsExchange);
        Assert.Equal("value", echo.ParamsSchema.FieldsList.Single().Name);
        Assert.IsType<StringType>(echo.ResultSchema.FieldsList.Single().DataType);
        Assert.Equal(["describe", "list_protocols"], reflection.Methods.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(SecondaryHash, secondary.ProtocolHash);
        Assert.Contains("echo_string", secondary.Methods.Keys);
        if (conn.Server is null)
        {
            // The full reference surface, streams included.
            var produce = description.Methods["produce_n"];
            Assert.Equal(RpcMethodKind.Stream, produce.MethodType);
            Assert.False(produce.IsExchange);
            Assert.False(produce.HasReturn);
            Assert.True(description.Methods["exchange_scale"].IsExchange);
        }
        else
        {
            Assert.Equal(["echo_int", "echo_string"], description.Methods.Keys.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>Reflection neither closes, replaces nor desynchronises the held connection.</summary>
    [Theory]
    [MemberData(nameof(Transports))]
    public async Task HeldConnection_IsReusedAndStaysUsable(string server, string transport)
    {
        await using var conn = await Conn.OpenAsync(server, transport);

        Assert.Equal("a", await conn.Proxy.EchoStringAsync("a"));
        var requestsBefore = conn.HttpRequests;
        await RpcReflection.ListProtocolsAsync(conn.Target, Ct);
        await RpcReflection.DescribeProtocolAsync(conn.Target, Primary, Ct);
        var reflectionRequests = conn.HttpRequests - requestsBefore;
        Assert.Equal("b", await conn.Proxy.EchoStringAsync("b"));
        if (conn.Server is null)
        {
            Assert.Equal(3, await ProduceAsync(conn.Client, 3));
        }

        Assert.Equal(Primary, (await RpcReflection.ListProtocolsAsync(conn.Target, Ct))[0].Name);
        Assert.Equal(7L, await conn.Proxy.EchoIntAsync(7));

        if (transport == "http")
        {
            // list, then describe's list + describe: all through the caller's own HttpClient.
            Assert.True(reflectionRequests >= 3, $"only {reflectionRequests} reflection requests went through the held client");
        }

        if (conn.AcceptedConnections is { } accepted)
        {
            Assert.Equal(1, accepted());
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task DescribeUnknownProtocol_IsProtocolNotSupportedNotNoReflection(string server, string transport)
    {
        await using var conn = await Conn.OpenAsync(server, transport);

        var error = await Assert.ThrowsAnyAsync<RpcException>(
            () => RpcReflection.DescribeProtocolAsync(conn.Target, "nope.v1", Ct));

        Assert.IsNotType<ReflectionNotSupportedException>(error);
        Assert.Equal("protocol_not_supported", error.ErrorKind);
        Assert.Equal("c", await conn.Proxy.EchoStringAsync("c"));
    }

    // ------------------------------------------------------------------ targets

    [Fact]
    public async Task Target_TypedProxyBoundToAnotherProtocolReusesItsConnection()
    {
        await using var conn = await Conn.OpenAsync("own", "pipe");
        var rpc = (RpcClient)conn.Client;
        var secondary = rpc.CreateProxy<ISecondaryView>();

        Assert.Equal([Primary, ISecondary.Name, Reflection], (await RpcReflection.ListProtocolsAsync(secondary, Ct)).Select(p => p.Name));
        Assert.Equal([Primary, ISecondary.Name, Reflection], (await RpcReflection.ListProtocolsAsync(conn.Proxy, Ct)).Select(p => p.Name));
        Assert.Equal("d", await conn.Proxy.EchoStringAsync("d"));
    }

    [Fact]
    public async Task Target_RpcConnectionAndRawTransport()
    {
        var server = OwnServer();
        var (client, serverSide) = PipeTransport.CreatePair();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var serve = server.ServeAsync(serverSide, cts.Token);
        await using (var connection = new RpcConnection<ISecondaryView>(client))
        {
            Assert.Equal(ISecondary.Name, (await RpcReflection.ListProtocolsAsync(connection, Ct))[1].Name);
            Assert.Equal(ISecondary.Name, (await RpcReflection.ListProtocolsAsync(connection.CreateProxy(), Ct))[1].Name);
            // A raw transport is borrowed: listing through it leaves it open for the connection.
            Assert.Contains("echo_string", (await RpcReflection.DescribeProtocolAsync(client, Primary, Ct)).Methods.Keys);
            Assert.Equal(Primary, (await RpcReflection.ListProtocolsAsync(connection, Ct))[0].Name);
            Assert.Equal(SecondaryImpl.EchoPrefix + "f", await connection.CreateProxy().EchoStringAsync("f"));
        }

        await cts.CancelAsync();
        await Quietly(serve);
    }

    [Fact]
    public async Task Target_HttpSessionScope()
    {
        await using var conn = await Conn.OpenAsync("own", "http");
        await using var scope = ((HttpRpcClient)conn.Client).WithSession();

        Assert.Equal(Primary, (await RpcReflection.ListProtocolsAsync(scope, Ct))[0].Name);
        Assert.Equal(Primary, (await RpcReflection.ListProtocolsAsync(scope.CreateProxy<IConformanceEcho>(), Ct))[0].Name);
    }

    [Fact]
    public async Task Target_WorkerPoolLease()
    {
        await using var conn = await Conn.OpenAsync("reference", "pool");

        Assert.Equal(Primary, (await RpcReflection.ListProtocolsAsync(conn.Target, Ct))[0].Name);
        Assert.IsType<WorkerPool.WorkerLease>(conn.Target);
    }

    [Fact]
    public async Task Target_AnythingElseIsAnArgumentExceptionNamingTheAcceptedKinds()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => RpcReflection.ListProtocolsAsync(new object(), Ct));
        Assert.Contains("HttpRpcClient", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ no reflection

    public static TheoryData<string> NoReflectionTransports() =>
        OperatingSystem.IsWindows() ? ["subprocess", "tcp", "http"] : ["subprocess", "tcp", "http", "unix"];

    /// <summary>The reference without <c>--describe</c>: a specific error carrying the server's
    /// fields, never an inferred listing, and the connection survives it.</summary>
    [Theory]
    [MemberData(nameof(NoReflectionTransports))]
    public async Task NoReflection_ListRaisesAndTheConnectionSurvives(string transport)
    {
        await using var conn = await Conn.OpenAsync("reference", transport, describe: false);

        var error = await Assert.ThrowsAsync<ReflectionNotSupportedException>(
            () => RpcReflection.ListProtocolsAsync(conn.Target, Ct));

        Assert.IsAssignableFrom<RpcException>(error);
        Assert.Equal("protocol_not_supported", error.ErrorKind);
        Assert.Equal(ErrorCodes.Unimplemented, error.ErrorCode);
        Assert.NotEmpty(error.ErrorMessage);
        Assert.Equal("e", await conn.Proxy.EchoStringAsync("e"));
        Assert.Equal(3, await ProduceAsync(conn.Client, 3));
    }

    /// <summary><c>describe_protocol</c> lists first, so it reports "no reflection", not "no such
    /// protocol".</summary>
    [Theory]
    [MemberData(nameof(NoReflectionTransports))]
    public async Task NoReflection_DescribeRaisesNoReflection(string transport)
    {
        await using var conn = await Conn.OpenAsync("reference", transport, describe: false);

        await Assert.ThrowsAsync<ReflectionNotSupportedException>(
            () => RpcReflection.DescribeProtocolAsync(conn.Target, Primary, Ct));
        Assert.Equal(9L, await conn.Proxy.EchoIntAsync(9));
    }

    /// <summary>An HTTP server older than protocol-scoped routes answers a bare 404, with no
    /// Arrow body: still "no reflection", never an empty listing.</summary>
    [Fact]
    public async Task NoReflection_BareHttp404()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var client = new HttpRpcClient(new Uri(address), new HttpRpcClientOptions { Protocol = Primary });

        var error = await Assert.ThrowsAsync<ReflectionNotSupportedException>(
            () => RpcReflection.ListProtocolsAsync(client, Ct));
        Assert.Equal("HttpError", error.ErrorType);
        Assert.StartsWith("HTTP 404", error.ErrorMessage, StringComparison.Ordinal);
        await app.StopAsync(Ct);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<int> ProduceAsync(IRpcClient client, long count)
    {
        using var parameters = new RecordBatch(
            new Schema([new Field("count", Int64Type.Default, nullable: false)], null),
            [new Int64Array.Builder().Append(count).Build()],
            1);
        await using var producer = await client.OpenProducerAsync("produce_n", parameters, cancellationToken: Ct);
        var batches = 0;
        while (await producer.ReadNextAsync(cancellationToken: Ct) is { } batch)
        {
            batch.Batch.Dispose();
            batches++;
        }

        return batches;
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Teardown: the server loop ends however the connection ends.
        }
    }

    private static string PythonExecutable()
    {
        var executable = Environment.GetEnvironmentVariable("VGI_PYTHON_BIN");
        if (string.IsNullOrWhiteSpace(executable))
        {
            Assert.Skip("Set VGI_PYTHON_BIN to run the reflection client against the reference conformance server.");
        }

        return executable!;
    }

    /// <summary>Counts every request the held <see cref="System.Net.Http.HttpClient"/> sends.</summary>
    private sealed class CountingHandler() : DelegatingHandler(new HttpClientHandler())
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>One held connection to a conformance server, plus what it takes to tear it down.</summary>
    private sealed class Conn : IAsyncDisposable
    {
        private readonly List<Func<ValueTask>> _cleanup = [];
        private CountingHandler? _handler;

        public required IRpcClient Client { get; init; }

        /// <summary>What the tests pass to <see cref="RpcReflection"/>: the client, or the lease.</summary>
        public object Target { get; private init; } = null!;

        public IConformanceEcho Proxy { get; private set; } = null!;

        /// <summary>This port's server, or null for the reference.</summary>
        public RpcServer? Server { get; private init; }

        /// <summary>Connections the in-process socket server accepted, where it can count them.</summary>
        public Func<int>? AcceptedConnections { get; private init; }

        public int HttpRequests => _handler?.Count ?? 0;

        public static async Task<Conn> OpenAsync(string server, string transport, bool describe = true)
        {
            var conn = server == "own" ? await OpenOwnAsync(transport) : await OpenReferenceAsync(transport, describe);
            conn.Proxy = conn.Client switch
            {
                RpcClient rpc => rpc.CreateProxy<IConformanceEcho>(),
                HttpRpcClient http => http.CreateProxy<IConformanceEcho>(),
                _ => throw new UnreachableException(),
            };
            return conn;
        }

        /// <summary>The reference's primary declares <c>2.0.0</c> and gates every call on it.</summary>
        private const string ReferenceVersion = "2.0.0";

        private static RpcClientOptions Options(bool shm = false, string? version = null) =>
            new() { Protocol = Primary, ProtocolVersion = version, SharedMemorySize = shm ? 4 * 1024 * 1024 : null };

        private static async Task<Conn> OpenOwnAsync(string transport)
        {
            var server = OwnServer();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var accepted = 0;
            Task Handle(IRpcTransport t, CancellationToken ct)
            {
                Interlocked.Increment(ref accepted);
                return server.ServeAsync(t, ct);
            }

            Conn conn;
            Task serve;
            switch (transport)
            {
                case "pipe":
                case "shm":
                    {
                        var (client, serverSide) = PipeTransport.CreatePair();
                        serve = server.ServeAsync(serverSide, cts.Token);
                        var rpc = new RpcClient(client, Options(shm: transport == "shm"));
                        conn = new Conn { Client = rpc, Target = rpc, Server = server };
                        break;
                    }

                case "unix":
                    {
                        var path = Path.Combine(Path.GetTempPath(), $"vgi-refl-{Guid.NewGuid():n}.sock");
                        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        serve = SocketTransport.ServeUnixAsync(path, Handle, cts.Token, () => bound.TrySetResult());
                        await bound.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                        var rpc = await RpcClient.ConnectUnixAsync(path, Options(), Ct);
                        conn = new Conn { Client = rpc, Target = rpc, Server = server, AcceptedConnections = () => Volatile.Read(ref accepted) };
                        break;
                    }

                case "tcp":
                    {
                        var bound = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        serve = SocketTransport.ServeTcpAsync("127.0.0.1", 0, Handle, cts.Token, port => bound.TrySetResult(port));
                        var port = await bound.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                        var rpc = await RpcClient.ConnectTcpAsync("127.0.0.1", port, Options(), Ct);
                        conn = new Conn { Client = rpc, Target = rpc, Server = server, AcceptedConnections = () => Volatile.Read(ref accepted) };
                        break;
                    }

                case "http":
                    {
                        var builder = WebApplication.CreateSlimBuilder();
                        builder.WebHost.UseUrls("http://127.0.0.1:0");
                        var app = builder.Build();
                        app.MapVgiRpc(server);
                        await app.StartAsync(Ct);
                        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                        serve = Task.CompletedTask;
                        var handler = new CountingHandler();
                        var http = new HttpRpcClient(
                            new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri(address) },
                            new HttpRpcClientOptions { Protocol = Primary },
                            ownsHttpClient: true);
                        conn = new Conn { Client = http, Target = http, Server = server };
                        conn._handler = handler;
                        conn._cleanup.Add(async () =>
                        {
                            await app.StopAsync();
                            await app.DisposeAsync();
                        });
                        break;
                    }

                default:
                    throw new ArgumentOutOfRangeException(nameof(transport), transport, null);
            }

            conn._cleanup.Insert(0, () => conn.Client.DisposeAsync());
            conn._cleanup.Add(async () =>
            {
                await cts.CancelAsync();
                await Quietly(serve);
                cts.Dispose();
            });
            return conn;
        }

        private static async Task<Conn> OpenReferenceAsync(string transport, bool describe)
        {
            var python = PythonExecutable();
            List<string> command = [python, "-m", "vgi_rpc.conformance._cli"];
            if (describe) command.Add("--describe");

            switch (transport)
            {
                case "subprocess":
                case "shm":
                    {
                        var rpc = RpcClient.StartSubprocess(command, Options(shm: transport == "shm", ReferenceVersion), SubprocessStderrMode.Discard);
                        var conn = new Conn { Client = rpc, Target = rpc };
                        conn._cleanup.Add(() => rpc.DisposeAsync());
                        return conn;
                    }

                case "pool":
                    {
                        var pool = new WorkerPool(new WorkerPoolOptions { MaxIdle = 1, Stderr = SubprocessStderrMode.Discard });
                        var lease = await pool.BorrowAsync(command, Options(version: ReferenceVersion));
                        var conn = new Conn { Client = lease.Client, Target = lease };
                        conn._cleanup.Add(() => lease.DisposeAsync());
                        conn._cleanup.Add(() => pool.DisposeAsync());
                        return conn;
                    }

                case "unix":
                    {
                        var path = Path.Combine(Path.GetTempPath(), $"vgi-refl-{Guid.NewGuid():n}.sock");
                        var process = await StartAsync([.. command, "--unix", path], "UNIX:");
                        var rpc = await RpcClient.ConnectUnixAsync(path, Options(version: ReferenceVersion), Ct);
                        var conn = new Conn { Client = rpc, Target = rpc };
                        conn._cleanup.Add(() => rpc.DisposeAsync());
                        conn._cleanup.Add(() => Kill(process));
                        return conn;
                    }

                case "tcp":
                    {
                        var process = await StartAsync([.. command, "--tcp", "127.0.0.1:0"], "TCP:");
                        var discovery = process.Discovery["TCP:".Length..];
                        var separator = discovery.LastIndexOf(':');
                        var rpc = await RpcClient.ConnectTcpAsync(
                            discovery[..separator], int.Parse(discovery[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture), Options(version: ReferenceVersion), Ct);
                        var conn = new Conn { Client = rpc, Target = rpc };
                        conn._cleanup.Add(() => rpc.DisposeAsync());
                        conn._cleanup.Add(() => Kill(process));
                        return conn;
                    }

                case "http":
                    {
                        var process = await StartAsync([.. command, "--http", "0"], "PORT:");
                        var address = new Uri($"http://127.0.0.1:{process.Discovery["PORT:".Length..]}");
                        await WaitHealthyAsync(address);
                        var handler = new CountingHandler();
                        var http = new HttpRpcClient(
                            new System.Net.Http.HttpClient(handler) { BaseAddress = address },
                            new HttpRpcClientOptions { Protocol = Primary, ProtocolVersion = ReferenceVersion },
                            ownsHttpClient: true);
                        var conn = new Conn { Client = http, Target = http, _handler = handler };
                        conn._cleanup.Add(() => http.DisposeAsync());
                        conn._cleanup.Add(() => Kill(process));
                        return conn;
                    }

                default:
                    throw new ArgumentOutOfRangeException(nameof(transport), transport, null);
            }
        }

        private static async Task WaitHealthyAsync(Uri address)
        {
            using var http = new System.Net.Http.HttpClient { BaseAddress = address };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                try
                {
                    using var response = await http.GetAsync("/health", timeout.Token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) when (!timeout.IsCancellationRequested)
                {
                }

                await Task.Delay(25, timeout.Token);
            }
        }

        private sealed record Started(Process Process, string Discovery);

        private static async Task<Started> StartAsync(IReadOnlyList<string> command, string discoveryPrefix)
        {
            var info = new ProcessStartInfo(command[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
            };
            foreach (var arg in command.Skip(1)) info.ArgumentList.Add(arg);
            var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start the reference server.");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                        ?? throw new InvalidOperationException("The reference server exited before its discovery line.");
                    if (line.StartsWith(discoveryPrefix, StringComparison.Ordinal))
                    {
                        return new Started(process, line.Trim());
                    }
                }
            }
            catch
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
        }

        private static async ValueTask Kill(Started started)
        {
            if (!started.Process.HasExited)
            {
                started.Process.Kill(entireProcessTree: true);
                await started.Process.WaitForExitAsync();
            }

            started.Process.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var step in _cleanup)
            {
                try
                {
                    await step();
                }
                catch (Exception)
                {
                    // Best-effort teardown; the assertion already ran.
                }
            }
        }
    }
}
