using System.Net;
using System.Net.Sockets;
using System.Text;
using QueryFarm.VgiRpc.Client.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class HttpRpcClientSocks5hTests
{
    [Fact]
    public async Task HttpDialerUsesExplicitProxySideNameResolution()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var target = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 5, 1, 0 }, await ReadExact(socket, 3));
            await socket.SendAsync(new byte[] { 5 }, SocketFlags.None);
            await socket.SendAsync(new byte[] { 0 }, SocketFlags.None);
            var header = await ReadExact(socket, 4);
            Assert.Equal(3, header[3]);
            var name = Encoding.ASCII.GetString(await ReadExact(socket, (await ReadExact(socket, 1))[0]));
            _ = await ReadExact(socket, 2);
            target.SetResult(name);
            foreach (var value in new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 1 })
                await socket.SendAsync(new byte[] { value }, SocketFlags.None);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = await ReadHeaders(stream);
            Assert.StartsWith("OPTIONS /health HTTP/1.1\r\n", request);
            Assert.Contains("VGI-Accept-Max-Response-Bytes: 268435456\r\n", request,
                StringComparison.OrdinalIgnoreCase);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 204 No Content\r\nConnection: close\r\n"
                + "Content-Length: 0\r\nVGI-Accept-Max-Response-Bytes-Support: true\r\n\r\n"));
        });
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var client = new HttpRpcClient(new Uri("http://worker.invalid"),
                new HttpRpcClientOptions
                {
                    TcpProxy = $"socks5h://127.0.0.1:{port}",
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                });
            _ = await client.GetCapabilitiesAsync(TestContext.Current.CancellationToken);
            Assert.Equal("worker.invalid", await target.Task);
            await server;
        }
        finally { listener.Stop(); }
    }

    private static async Task<byte[]> ReadExact(Socket socket, int length)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await socket.ReceiveAsync(result.AsMemory(offset), SocketFlags.None);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return result;
    }

    private static async Task<string> ReadHeaders(Stream stream)
    {
        var bytes = new List<byte>();
        var state = 0;
        while (state < 4)
        {
            var one = new byte[1];
            if (await stream.ReadAsync(one) == 0) throw new EndOfStreamException();
            bytes.Add(one[0]);
            state = state switch
            {
                0 when one[0] == '\r' => 1,
                1 when one[0] == '\n' => 2,
                2 when one[0] == '\r' => 3,
                3 when one[0] == '\n' => 4,
                _ => 0,
            };
        }
        return Encoding.Latin1.GetString(bytes.ToArray());
    }
}
