using System.Net.Sockets;

namespace QueryFarm.VgiRpc.Transport;

/// <summary>
/// Wraps a connected <see cref="Socket"/> (Unix domain or TCP) as an <see cref="IRpcTransport"/>
/// — both directions read/write the same underlying <see cref="NetworkStream"/>, since a socket
/// is naturally bidirectional (unlike the anonymous-pipe pair <see cref="PipeTransport"/> needs).
/// </summary>
public sealed class SocketTransport : IRpcTransport, IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _readStream;
    private readonly NetworkStream _writeStream;

    public SocketTransport(Socket socket)
    {
        _socket = socket;
        // Two separate NetworkStream wrappers around the one full-duplex socket, rather than
        // one instance used for both directions — needed to avoid a hang reading a stream's
        // schema message shortly after writing on a socket-backed transport (not observed on
        // pipe/stdio transports, where each direction was already backed by a distinct Stream).
        // NOT implicated in the M17/M18 producer-stream-over-socket hang (see docs/roadmap.md's
        // M17 entry) — a single shared NetworkStream, a synchronous-write wrapper, and a
        // synchronous-read wrapper were each tested in isolation and none changed the symptom.
        _readStream = new NetworkStream(socket, FileAccess.Read, ownsSocket: false);
        _writeStream = new NetworkStream(socket, FileAccess.Write, ownsSocket: false);
    }

    public Stream Input => _readStream;
    public Stream Output => _writeStream;

    public void Dispose()
    {
        _readStream.Dispose();
        _writeStream.Dispose();
        _socket.Dispose();
    }

    /// <summary>Listens on a Unix domain socket at <paramref name="path"/>, invoking
    /// <paramref name="handleConnection"/> once per accepted connection until
    /// <paramref name="cancellationToken"/> is cancelled. Removes any stale socket file first —
    /// binding to an existing path otherwise fails — and unlinks it (best-effort) once the accept
    /// loop ends, matching the AF_UNIX launcher protocol's worker-shutdown contract (see
    /// docs/launcher-protocol.md in the canonical Python repo: "unlink &lt;hash&gt;.sock
    /// (best-effort; not a correctness requirement)"). <paramref name="onBound"/>, if given, is
    /// invoked once immediately after the socket is bound and listening — the launcher protocol
    /// requires the worker to emit its <c>UNIX:&lt;path&gt;</c> discovery line at exactly this
    /// point, before accepting any connection.</summary>
    public static async Task ServeUnixAsync(string path, Func<IRpcTransport, CancellationToken, Task> handleConnection, CancellationToken cancellationToken, Action? onBound = null)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        onBound?.Invoke();
        try
        {
            await AcceptLoopAsync(listener, handleConnection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort per the launcher protocol — a launcher whose next spawn finds a
                // stale socket file unlinks it itself before binding (connect-probe-then-unlink).
            }
        }
    }

    /// <summary>Listens on TCP <paramref name="host"/>:<paramref name="port"/> (port 0 picks an
    /// ephemeral port — check <see cref="Socket.LocalEndPoint"/> on the returned listener to
    /// discover it), invoking <paramref name="handleConnection"/> once per accepted connection.</summary>
    public static async Task ServeTcpAsync(string host, int port, Func<IRpcTransport, CancellationToken, Task> handleConnection, CancellationToken cancellationToken, Action<int>? onBound = null)
    {
        var address = host is "localhost" or "" ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Parse(host);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new System.Net.IPEndPoint(address, port));
        listener.Listen();
        onBound?.Invoke(((System.Net.IPEndPoint)listener.LocalEndPoint!).Port);
        await AcceptLoopAsync(listener, handleConnection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcceptLoopAsync(Socket listener, Func<IRpcTransport, CancellationToken, Task> handleConnection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Fire-and-forget: each connection gets its own independent serve loop, matching
            // every other transport's "one RpcServer.ServeAsync call per connection" model.
            _ = Task.Run(async () =>
            {
                using var transport = new SocketTransport(accepted);
                try
                {
                    await handleConnection(transport, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A single connection's failure must never take down the accept loop.
                }
            }, cancellationToken);
        }
    }

    /// <summary>Dials a Unix domain socket at <paramref name="path"/>.</summary>
    public static async Task<IRpcTransport> ConnectUnixAsync(string path, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
        return new SocketTransport(socket);
    }

    /// <summary>Dials a TCP endpoint.</summary>
    public static async Task<IRpcTransport> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return new SocketTransport(socket);
    }
}
