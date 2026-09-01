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
        public int Resolutions => Volatile.Read(ref _resolutions);
        public string Provider => "test-peer";

        public ValueTask<PeerIdentityResult> ResolveAsync(
            PeerResolutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _resolutions);
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
