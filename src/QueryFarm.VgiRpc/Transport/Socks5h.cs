using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QueryFarm.VgiRpc.Transport;

/// <summary>Credential-free SOCKS5h dialing with proxy-side target resolution.</summary>
public static class Socks5h
{
    public static async Task<Socket> ConnectAsync(
        string targetHost,
        int targetPort,
        string proxyUri,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var proxy = ParseProxy(proxyUri);
        var target = Target(targetHost, targetPort);
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setup.CancelAfter(Remaining(started, timeout));

        var addresses = await Dns.GetHostAddressesAsync(proxy.Host, setup.Token).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                setup.CancelAfter(Remaining(started, timeout));
                await socket.ConnectAsync(new IPEndPoint(address, proxy.Port), setup.Token).ConfigureAwait(false);
                await NegotiateAsync(socket, target, started, timeout, setup, setup.Token).ConfigureAwait(false);
                return socket;
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
            {
                socket.Dispose();
                last = exception;
                if (cancellationToken.IsCancellationRequested) throw;
                if (Remaining(started, timeout) <= TimeSpan.Zero) throw new TimeoutException("SOCKS5h setup timed out", exception);
            }
        }
        throw new IOException("SOCKS5h proxy resolved without a usable address", last);
    }

    private static async Task NegotiateAsync(Socket socket, TargetAddress target, long started,
        TimeSpan timeout, CancellationTokenSource setup, CancellationToken cancellationToken)
    {
        await SendAllAsync(socket, new byte[] { 5, 1, 0 }, started, timeout, setup, cancellationToken).ConfigureAwait(false);
        var greeting = await ReceiveExactAsync(socket, 2, started, timeout, setup, cancellationToken).ConfigureAwait(false);
        if (greeting[0] != 5 || greeting[1] != 0) throw new IOException("SOCKS5h proxy did not accept NO AUTH");

        var request = new byte[4 + target.Address.Length + 2];
        request[0] = 5;
        request[1] = 1;
        request[3] = target.Atyp;
        target.Address.CopyTo(request, 4);
        request[^2] = (byte)(target.Port >> 8);
        request[^1] = (byte)target.Port;
        await SendAllAsync(socket, request, started, timeout, setup, cancellationToken).ConfigureAwait(false);

        var reply = await ReceiveExactAsync(socket, 4, started, timeout, setup, cancellationToken).ConfigureAwait(false);
        if (reply[0] != 5 || reply[2] != 0) throw new IOException("malformed SOCKS5h reply");
        if (reply[1] != 0) throw new IOException($"SOCKS5h proxy rejected target (reply {reply[1]})");
        var addressLength = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReceiveExactAsync(socket, 1, started, timeout, setup, cancellationToken).ConfigureAwait(false))[0],
            _ => throw new IOException("SOCKS5h proxy returned an invalid address type"),
        };
        _ = await ReceiveExactAsync(socket, addressLength + 2, started, timeout, setup, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> value, long started,
        TimeSpan timeout, CancellationTokenSource setup, CancellationToken cancellationToken)
    {
        var sent = 0;
        while (sent < value.Length)
        {
            setup.CancelAfter(Remaining(started, timeout));
            var count = await socket.SendAsync(value[sent..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (count <= 0) throw new IOException("SOCKS5h proxy closed while writing");
            sent += count;
        }
    }

    private static async Task<byte[]> ReceiveExactAsync(Socket socket, int length, long started,
        TimeSpan timeout, CancellationTokenSource setup, CancellationToken cancellationToken)
    {
        var value = new byte[length];
        var received = 0;
        while (received < length)
        {
            setup.CancelAfter(Remaining(started, timeout));
            var count = await socket.ReceiveAsync(value.AsMemory(received), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (count <= 0) throw new EndOfStreamException("SOCKS5h proxy returned a truncated reply");
            received += count;
        }
        return value;
    }

    private static TargetAddress Target(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535 || ContainsControl(host)) throw new ArgumentException("invalid SOCKS5h target");
        if (host.Contains('%'))
            throw new ArgumentException("scoped IPv6 targets are unsupported", nameof(host));
        if (IPAddress.TryParse(host, out var address))
            return new TargetAddress(address.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4,
                address.GetAddressBytes(), port);
        var ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(host);
        if (ascii.Length is < 1 or > 253 || ContainsControl(ascii)) throw new ArgumentException("invalid target hostname");
        var name = Encoding.ASCII.GetBytes(ascii);
        var encoded = new byte[name.Length + 1];
        encoded[0] = (byte)name.Length;
        name.CopyTo(encoded, 1);
        return new TargetAddress(3, encoded, port);
    }

    private static ProxyEndpoint ParseProxy(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "socks5h"
            || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host)
            || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("proxy must be credential-free socks5h://host:port", nameof(value));
        return new ProxyEndpoint(uri.Host, uri.Port);
    }

    private static TimeSpan Remaining(long started, TimeSpan timeout)
    {
        var remaining = timeout - TimeProvider.System.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static bool ContainsControl(string value) => value.Any(character => character <= 0x1f || character == 0x7f);
    private sealed record ProxyEndpoint(string Host, int Port);
    private sealed record TargetAddress(byte Atyp, byte[] Address, int Port);
}
