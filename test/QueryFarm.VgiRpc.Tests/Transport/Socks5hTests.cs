using System.Net;
using System.Net.Sockets;
using System.Text;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Transport;

public sealed class Socks5hTests
{
    [Fact]
    public async Task SendsUnicodeTargetAsIdnaAndHandlesFragmentedReply()
    {
        await using var proxy = new FakeProxy([5, 0, 0, 4, .. new byte[16], 0, 1]);
        using var socket = await Socks5h.ConnectAsync("café.invalid", 9400, proxy.Uri,
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var request = await proxy.Request;
        Assert.Equal(3, request.Atyp);
        Assert.Equal("xn--caf-dma.invalid", Encoding.ASCII.GetString(request.Address));
        Assert.Equal(9400, request.Port);
        Assert.True(socket.NoDelay);
    }

    [Theory]
    [InlineData("192.0.2.1", 1)]
    [InlineData("2001:db8::1", 4)]
    public async Task SendsLiteralAddressTypesWithoutDns(string host, byte expectedAtyp)
    {
        await using var proxy = new FakeProxy([5, 0, 0, 1, 127, 0, 0, 1, 0, 1]);
        using var ignored = await Socks5h.ConnectAsync(host, 443, proxy.Uri, TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedAtyp, (await proxy.Request).Atyp);
    }

    [Fact]
    public async Task RejectsCredentialsAndHonorsNegotiationDeadline()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Socks5h.ConnectAsync("example.invalid", 80,
            "socks5h://user:pass@127.0.0.1:9", TimeSpan.FromSeconds(1)));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stalled = Task.Run(async () =>
        {
            using var peer = await listener.AcceptSocketAsync();
            await Task.Delay(500);
        });
        await Assert.ThrowsAnyAsync<Exception>(() => Socks5h.ConnectAsync("example.invalid", 80,
            $"socks5h://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", TimeSpan.FromMilliseconds(50)));
        await stalled;
    }

    [Theory]
    [MemberData(nameof(FailingReplies))]
    public async Task RejectsProxyFailureAndTruncatedRepliesWithoutFallback(byte[] reply)
    {
        await using var proxy = new FakeProxy(reply);
        await Assert.ThrowsAsync<IOException>(() => Socks5h.ConnectAsync("127.0.0.1", 9,
            proxy.Uri, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal(1, (await proxy.Request).Atyp);
    }

    public static TheoryData<byte[]> FailingReplies => new()
    {
        new byte[] { 5, 5, 0, 1, 127, 0, 0, 1, 0, 1 },
        new byte[] { 5, 0, 0, 4, 0, 0, 0 },
    };

    private sealed record Request(byte Atyp, byte[] Address, int Port);

    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serve;
        private readonly TaskCompletionSource<Request> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeProxy(byte[] reply)
        {
            _listener.Start();
            Uri = $"socks5h://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _serve = Task.Run(async () =>
            {
                try
                {
                    using var peer = await _listener.AcceptSocketAsync();
                    Assert.Equal(new byte[] { 5, 1, 0 }, await Receive(peer, 3));
                    await peer.SendAsync(new byte[] { 5 }, SocketFlags.None);
                    await peer.SendAsync(new byte[] { 0 }, SocketFlags.None);
                    var header = await Receive(peer, 4);
                    var size = header[3] switch
                    {
                        1 => 4,
                        4 => 16,
                        3 => (await Receive(peer, 1))[0],
                        _ => throw new InvalidDataException(),
                    };
                    var address = await Receive(peer, size);
                    var port = await Receive(peer, 2);
                    _request.SetResult(new Request(header[3], address, (port[0] << 8) | port[1]));
                    foreach (var value in reply) await peer.SendAsync(new byte[] { value }, SocketFlags.None);
                }
                catch (Exception exception) { _request.TrySetException(exception); }
            });
        }

        public string Uri { get; }
        public Task<Request> Request => _request.Task;
        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serve;
        }
        private static async Task<byte[]> Receive(Socket socket, int length)
        {
            var value = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var count = await socket.ReceiveAsync(value.AsMemory(offset), SocketFlags.None);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
            return value;
        }
    }
}
