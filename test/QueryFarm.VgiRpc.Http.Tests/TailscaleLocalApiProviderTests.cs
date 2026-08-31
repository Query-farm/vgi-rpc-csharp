using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Identity;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class TailscaleLocalApiProviderTests
{
    [Fact]
    public async Task PerformsUncachedLookupsAndUsesStableUserIdentity()
    {
        var client = new FakeClient(200, """
            {"Node":{"StableID":"node-1","Name":"workstation"},
             "UserProfile":{"ID":42,"LoginName":"alice@example.com","DisplayName":"Alice"},
             "CapMap":{"query.farm/cap":[{"role":"reader"}]}}
            """);
        var provider = new TailscaleLocalApiProvider("tailnet:example", client);
        var first = await provider.ResolveAsync(Context());
        var second = await provider.ResolveAsync(Context());
        Assert.Equal(2, client.Calls);
        Assert.Equal(PeerIdentityStatus.Available, first.Status);
        var identity = Assert.Single(first.Identities);
        Assert.Equal("user:42", identity.SubjectKey);
        Assert.Equal(PeerSubjectKind.User, identity.SubjectKind);
        Assert.Equal(SubjectStability.Stable, Assert.Single(second.Identities).SubjectStability);
        Assert.Equal("destination_ip", identity.Attributes["capability_target"].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TaggedNodeWinsAndFailuresRemainDistinct()
    {
        var tagged = await new TailscaleLocalApiProvider("tailnet:example", new FakeClient(200, """
            {"Node":{"StableID":"n123","Tags":["tag:worker"]},
             "UserProfile":{"ID":99,"LoginName":"owner@example.com"}}
            """)).ResolveAsync(Context(service: "svc:vgi"));
        Assert.Equal("node:n123", Assert.Single(tagged.Identities).SubjectKey);
        Assert.Equal(PeerSubjectKind.TaggedNode, Assert.Single(tagged.Identities).SubjectKind);
        Assert.Equal(PeerIdentityStatus.NoMatch, await Status(404));
        Assert.Equal(PeerIdentityStatus.PermissionDenied, await Status(403));
        Assert.Equal(PeerIdentityStatus.Invalid, await Status(302));
        Assert.Equal(PeerIdentityStatus.Invalid, await Status(400));
        Assert.Equal(PeerIdentityStatus.Unavailable, await Status(500));
        foreach (var contentTypes in new IReadOnlyList<string>[]
        {
            Array.Empty<string>(), ["text/plain"], ["application/json", "application/json"],
        })
        {
            var result = await new TailscaleLocalApiProvider("tailnet:example",
                new FakeClient(200, "{\"Node\":{},\"UserProfile\":{\"ID\":1}}", contentTypes))
                .ResolveAsync(Context());
            Assert.Equal(PeerIdentityStatus.Invalid, result.Status);
        }
        foreach (var malformed in new[]
        {
            "[]", "{\"Node\":{},\"UserProfile\":{}}",
            "{\"Node\":{},\"UserProfile\":{\"ID\":1},\"CapMap\":{\"x\":[],\"x\":[]}}",
        })
        {
            var result = await new TailscaleLocalApiProvider("tailnet:example", new FakeClient(200, malformed))
                .ResolveAsync(Context());
            Assert.Equal(PeerIdentityStatus.Invalid, result.Status);
        }
    }

    [Fact]
    public async Task RejectsUnstableNodeAndNonNumericUserPrincipals()
    {
        foreach (var body in new[]
        {
            "{\"Node\":{\"ID\":\"ephemeral\",\"Tags\":[\"tag:worker\"]}}",
            "{\"Node\":{},\"UserProfile\":{\"ID\":\"alice\"}}",
            "{\"Node\":{},\"UserProfile\":{\"ID\":0}}",
            "{\"Node\":{\"StableID\":\"n1\",\"Tags\":[\"worker\"]}}",
            "{\"Node\":{\"StableID\":\"n1\",\"Tags\":[\"tag:\"]}}",
            "{\"Node\":{\"StableID\":\"n1\",\"Tags\":[\"tag:worker\\u000a\"]}}",
        })
        {
            var result = await new TailscaleLocalApiProvider("tailnet:example", new FakeClient(200, body))
                .ResolveAsync(Context());
            Assert.Equal(PeerIdentityStatus.Invalid, result.Status);
        }
    }

    [Fact]
    public async Task ExplicitHttpTransportUsesLocalApiHostTokenAndDestinationQuery()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var bytes = new List<byte>();
            var state = 0;
            while (state < 4)
            {
                var value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException();
                bytes.Add((byte)value);
                state = state switch
                {
                    0 when value == '\r' => 1,
                    1 when value == '\n' => 2,
                    2 when value == '\r' => 3,
                    3 when value == '\n' => 4,
                    _ => 0,
                };
            }
            request.SetResult(Encoding.Latin1.GetString(bytes.ToArray()));
            var body = Encoding.UTF8.GetBytes("{\"Node\":{},\"UserProfile\":{\"ID\":7}}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        });
        try
        {
            var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            using var client = new TailscaleLocalApiHttpClient(endpoint, "secret");
            var result = await new TailscaleLocalApiProvider("tailnet:example", client)
                .ResolveAsync(Context());
            Assert.Equal(PeerIdentityStatus.Available, result.Status);
            var headers = await request.Task;
            Assert.StartsWith("GET /localapi/v0/whois?addr=100.64.0.1%3A1234&proto=tcp&dst_ip=100.100.100.100 HTTP/1.1\r\n", headers);
            Assert.Contains("\r\nHost: local-tailscaled.sock\r\n", headers, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\r\nAuthorization: Basic OnNlY3JldA==\r\n", headers, StringComparison.OrdinalIgnoreCase);
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ExplicitHttpTransportClassifiesOversizedBodyAsInvalid()
    {
        const int oversizedLength = 65_537;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var state = 0;
            while (state < 4)
            {
                var value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException();
                state = state switch
                {
                    0 when value == '\r' => 1,
                    1 when value == '\n' => 2,
                    2 when value == '\r' => 3,
                    3 when value == '\n' => 4,
                    _ => 0,
                };
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {oversizedLength}\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(new byte[oversizedLength]);
        });
        try
        {
            var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            using var client = new TailscaleLocalApiHttpClient(endpoint);
            var result = await new TailscaleLocalApiProvider("tailnet:example", client)
                .ResolveAsync(Context());
            Assert.Equal(PeerIdentityStatus.Invalid, result.Status);
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task UnixSocketTransportPerformsWhoIs()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"vgi-localapi-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptAsync(TestContext.Current.CancellationToken);
            await ServeOne(new NetworkStream(socket, ownsSocket: false));
        });
        try
        {
            using var client = TailscaleLocalApiHttpClient.ForUnixSocket(path);
            var result = await new TailscaleLocalApiProvider("tailnet:example", client).ResolveAsync(Context());
            Assert.Equal("user:8", Assert.Single(result.Identities).SubjectKey);
            await server;
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public async Task NamedPipeTransportPerformsWhoIs()
    {
        var pipeName = $"vgi-{Guid.NewGuid():N}"[..12];
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        var server = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            await ServeOne(pipe);
        });
        using var client = TailscaleLocalApiHttpClient.ForWindowsNamedPipe(pipeName);
        var result = await new TailscaleLocalApiProvider("tailnet:example", client).ResolveAsync(Context());
        Assert.Equal("user:8", Assert.Single(result.Identities).SubjectKey);
        await server;
    }

    private static async Task<PeerIdentityStatus> Status(int status) =>
        (await new TailscaleLocalApiProvider("tailnet:example", new FakeClient(status, "{}"))
            .ResolveAsync(Context())).Status;

    private static PeerResolutionContext Context(string? service = null) => new("tcp",
        immediatePeer: "127.0.0.1:5000", assertedPeer: "100.64.0.1:1234",
        destinationAddress: "100.100.100.100:9400", serviceName: service);

    private static async Task ServeOne(Stream stream)
    {
        using (stream)
        {
            var state = 0;
            while (state < 4)
            {
                var one = new byte[1];
                if (await stream.ReadAsync(one) == 0) throw new EndOfStreamException();
                state = state switch
                {
                    0 when one[0] == '\r' => 1,
                    1 when one[0] == '\n' => 2,
                    2 when one[0] == '\r' => 3,
                    3 when one[0] == '\n' => 4,
                    _ => 0,
                };
            }
            var body = Encoding.UTF8.GetBytes("{\"Node\":{},\"UserProfile\":{\"ID\":8}}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        }
    }

    private sealed class FakeClient(
        int status, string body, IReadOnlyList<string>? contentTypes = null) : ITailscaleLocalApiClient
    {
        public int Calls { get; private set; }
        public ValueTask<TailscaleLocalApiResponse> WhoIsAsync(PeerResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new TailscaleLocalApiResponse(
                status, Encoding.UTF8.GetBytes(body), contentTypes ?? ["application/json"]));
        }
    }
}
