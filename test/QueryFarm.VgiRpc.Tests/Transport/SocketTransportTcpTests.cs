using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tests.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Transport;

public sealed class SocketTransportTcpTests
{
    [Fact]
    public async Task UnaryCallRoundTripsOverTcp()
    {
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        using var cancellation = new CancellationTokenSource();
        var boundPort = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serveTask = SocketTransport.ServeTcpAsync(
            "127.0.0.1",
            0,
            (transport, token) => server.ServeAsync(transport, token),
            cancellation.Token,
            port => boundPort.TrySetResult(port));

        using var clientTransport = (SocketTransport)await SocketTransport.ConnectTcpAsync(
            "127.0.0.1",
            await boundPort.Task);
        var client = new RpcConnection<IGreeter>(clientTransport).CreateProxy();

        Assert.Equal("hello-over-tcp", await client.EchoStringAsync("hello-over-tcp"));

        cancellation.Cancel();
        await serveTask;
    }

    [Fact]
    public async Task PeerIdentityIsResolvedOnceAndSnapshottedForTheConnection()
    {
        var provider = new TestPeerProvider();
        var server = new RpcServer(typeof(IIdentityService), new IdentityService());
        using var cancellation = new CancellationTokenSource();
        var boundPort = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TcpServerOptions
        {
            PeerIdentityProviders = [provider],
            PeerAuthenticationPolicy = PeerAuthenticationPolicies.Primary("test-peer"),
            IdentityResolutionTimeout = TimeSpan.FromSeconds(2),
        };
        var serveTask = SocketTransport.ServeTcpAsync(
            "127.0.0.1",
            0,
            (transport, token) => server.ServeAsync(transport, token),
            options,
            cancellation.Token,
            port => boundPort.TrySetResult(port));

        using var clientTransport = (SocketTransport)await SocketTransport.ConnectTcpAsync(
            "127.0.0.1",
            await boundPort.Task);
        var client = new RpcConnection<IIdentityService>(clientTransport).CreateProxy();

        Assert.Equal("first:test-peer:Available", await client.WhoAmIAsync("first"));
        Assert.Equal("second:test-peer:Available", await client.WhoAmIAsync("second"));
        Assert.Equal(1, provider.Resolutions);

        cancellation.Cancel();
        await serveTask;
    }

    [Fact]
    public async Task CompletedInvalidEvidenceSurvivesSiblingTimeout()
    {
        PeerIdentityStatus[]? observed = null;
        var server = new RpcServer(typeof(IIdentityService), new IdentityService());
        using var cancellation = new CancellationTokenSource();
        var boundPort = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TcpServerOptions
        {
            PeerIdentityProviders = [new HungPeerProvider(), new InvalidPeerProvider()],
            PeerAuthenticationPolicy = (evidence, auth) =>
            {
                observed = [evidence.Status("hung"), evidence.Status("invalid")];
                return ValueTask.FromResult(auth);
            },
            IdentityResolutionTimeout = TimeSpan.FromMilliseconds(40),
        };
        var serveTask = SocketTransport.ServeTcpAsync(
            "127.0.0.1", 0,
            (transport, token) => server.ServeAsync(transport, token),
            options, cancellation.Token, port => boundPort.TrySetResult(port));
        using var clientTransport = (SocketTransport)await SocketTransport.ConnectTcpAsync(
            "127.0.0.1", await boundPort.Task);
        var client = new RpcConnection<IIdentityService>(clientTransport).CreateProxy();
        Assert.Equal("value::Off", await client.WhoAmIAsync("value"));
        Assert.Equal(
            [PeerIdentityStatus.Unavailable, PeerIdentityStatus.Invalid], observed);
        cancellation.Cancel();
        await serveTask;
    }

    [Fact]
    public async Task ProxyV2UsesAssertedPeerAndPreservesTheFirstVgiFrame()
    {
        var provider = new TestPeerProvider();
        var server = new RpcServer(typeof(IIdentityService), new IdentityService());
        using var cancellation = new CancellationTokenSource();
        var boundPort = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TcpServerOptions
        {
            PeerIdentityProviders = [provider],
            PeerAuthenticationPolicy = PeerAuthenticationPolicies.Primary("test-peer"),
            ProxyProtocolV2Required = true,
            TrustedProxyAddresses = ["127.0.0.1"],
            ProxyPreambleTimeout = TimeSpan.FromSeconds(1),
        };
        var serveTask = SocketTransport.ServeTcpAsync(
            "127.0.0.1", 0,
            (transport, token) => server.ServeAsync(transport, token),
            options, cancellation.Token, port => boundPort.TrySetResult(port));

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, await boundPort.Task);
        var source = new IPEndPoint(IPAddress.Parse("192.0.2.7"), 42000);
        var destination = new IPEndPoint(IPAddress.Parse("198.51.100.9"), 19400);
        await socket.SendAsync(ProxyHeader(source, destination), SocketFlags.None);
        using var clientTransport = new SocketTransport(socket);
        var client = new RpcConnection<IIdentityService>(clientTransport).CreateProxy();

        Assert.Equal("first:test-peer:Available", await client.WhoAmIAsync("first"));
        Assert.Equal("second:test-peer:Available", await client.WhoAmIAsync("second"));
        Assert.Equal(1, provider.Resolutions);
        Assert.Equal("127.0.0.1", provider.LastContext!.ImmediatePeer);
        Assert.Equal(source.ToString(), provider.LastContext.AssertedPeer);
        Assert.StartsWith("127.0.0.1:", provider.LastContext.SourceEndpoint);
        Assert.Equal(destination.ToString(), provider.LastContext.DestinationAddress);
        Assert.Equal(source.ToString(), provider.LastContext.Metadata["asserted_peer"].GetString());
        Assert.True(provider.LastContext.Metadata["proxy_protocol_v2"].GetBoolean());

        cancellation.Cancel();
        await serveTask;
    }

    [Fact]
    public async Task ProxyV2RejectsAnUntrustedPeerBeforeReadingAndBoundsSlowPreambles()
    {
        var provider = new TestPeerProvider();
        await AssertRejectedConnectionAsync(new TcpServerOptions
        {
            PeerIdentityProviders = [provider],
            ProxyProtocolV2Required = true,
            TrustedProxyAddresses = ["192.0.2.1"],
            ProxyPreambleTimeout = TimeSpan.FromSeconds(5),
        }, sendOneByte: false);
        Assert.Equal(0, provider.Resolutions);

        await AssertRejectedConnectionAsync(new TcpServerOptions
        {
            PeerIdentityProviders = [provider],
            ProxyProtocolV2Required = true,
            TrustedProxyAddresses = ["127.0.0.1"],
            ProxyPreambleTimeout = TimeSpan.FromMilliseconds(40),
        }, sendOneByte: true);
        Assert.Equal(0, provider.Resolutions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("127.0.0.0/8")]
    [InlineData("127.0.0.1:9400")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("0177.0.0.1")]
    [InlineData("01.2.3.4")]
    public async Task ProxyV2ConfigurationRequiresExactTrustedAddresses(string? trusted)
    {
        var options = new TcpServerOptions
        {
            ProxyProtocolV2Required = true,
            TrustedProxyAddresses = trusted is null ? [] : [trusted],
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => SocketTransport.ServeTcpAsync(
            "127.0.0.1", 0, (_, _) => Task.CompletedTask, options, CancellationToken.None));
    }

    public interface IIdentityService
    {
        Task<string> WhoAmIAsync(string value, ICallContext? context = null);
    }

    private sealed class IdentityService : IIdentityService
    {
        public Task<string> WhoAmIAsync(string value, ICallContext? context = null) =>
            Task.FromResult($"{value}:{context!.Auth.Domain}:{context.PeerEvidence.Status("test-peer")}");
    }

    private sealed class TestPeerProvider : IPeerIdentityProvider
    {
        private int _resolutions;
        private PeerResolutionContext? _lastContext;
        public int Resolutions => Volatile.Read(ref _resolutions);
        public PeerResolutionContext? LastContext => Volatile.Read(ref _lastContext);
        public string Provider => "test-peer";

        public ValueTask<PeerIdentityResult> ResolveAsync(
            PeerResolutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolutions);
            Volatile.Write(ref _lastContext, context);
            Assert.Equal("tcp", context.Transport);
            Assert.NotNull(context.SourceEndpoint);
            return ValueTask.FromResult(PeerIdentityResult.Available(new PeerIdentity(
                Provider,
                "test_socket",
                IdentityAssurance.CryptographicPeer,
                "test-issuer",
                "tcp",
                PeerSubjectKind.Workload,
                "worker-1",
                SubjectStability.Stable,
                subjectVerified: true,
                sourceAddress: context.SourceEndpoint)));
        }
    }

    private static async Task AssertRejectedConnectionAsync(
        TcpServerOptions options, bool sendOneByte)
    {
        using var cancellation = new CancellationTokenSource();
        var boundPort = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serveTask = SocketTransport.ServeTcpAsync(
            "127.0.0.1", 0, (_, _) => Task.CompletedTask,
            options, cancellation.Token, port => boundPort.TrySetResult(port));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, await boundPort.Task);
        if (sendOneByte) await socket.SendAsync(new byte[] { 0x0d }, SocketFlags.None);
        var buffer = new byte[1];
        Assert.Equal(0, await socket.ReceiveAsync(buffer, SocketFlags.None)
            .WaitAsync(TimeSpan.FromSeconds(2)));
        cancellation.Cancel();
        await serveTask;
    }

    private static byte[] ProxyHeader(IPEndPoint source, IPEndPoint destination)
    {
        var value = new byte[28];
        new byte[]
        {
            0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a,
        }.CopyTo(value, 0);
        value[12] = 0x21;
        value[13] = 0x11;
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(14, 2), 12);
        source.Address.GetAddressBytes().CopyTo(value, 16);
        destination.Address.GetAddressBytes().CopyTo(value, 20);
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(24, 2), checked((ushort)source.Port));
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(26, 2), checked((ushort)destination.Port));
        return value;
    }

    private sealed class HungPeerProvider : IPeerIdentityProvider
    {
        public string Provider => "hung";
        public async ValueTask<PeerIdentityResult> ResolveAsync(
            PeerResolutionContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class InvalidPeerProvider : IPeerIdentityProvider
    {
        public string Provider => "invalid";
        public ValueTask<PeerIdentityResult> ResolveAsync(
            PeerResolutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PeerIdentityResult(Provider, PeerIdentityStatus.Invalid));
    }
}
