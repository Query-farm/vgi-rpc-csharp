using System.Net.Sockets;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Transport;

/// <summary>
/// Wraps a connected <see cref="Socket"/> (Unix domain or TCP) as an <see cref="IRpcTransport"/>
/// — both directions read/write the same underlying <see cref="NetworkStream"/>, since a socket
/// is naturally bidirectional (unlike the anonymous-pipe pair <see cref="PipeTransport"/> needs).
/// </summary>
public sealed class SocketTransport : IRpcTransport, IDisposable
{
    private const int UnixSocketBufferBytes = 1 << 20;

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
        await ServeTcpAsync(host, port, handleConnection, new TcpServerOptions(), cancellationToken, onBound)
            .ConfigureAwait(false);
    }

    /// <summary>Listens on raw TCP and resolves off-wire identity once per accepted connection.</summary>
    public static async Task ServeTcpAsync(
        string host, int port, Func<IRpcTransport, CancellationToken, Task> handleConnection,
        TcpServerOptions options, CancellationToken cancellationToken, Action<int>? onBound = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var address = host is "localhost" or "" ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Parse(host);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new System.Net.IPEndPoint(address, port));
        listener.Listen();
        onBound?.Invoke(((System.Net.IPEndPoint)listener.LocalEndPoint!).Port);
        var providerSlots = new SemaphoreSlim(
            options.PeerProviderConcurrency, options.PeerProviderConcurrency);
        await AcceptLoopAsync(listener, handleConnection, cancellationToken, options, providerSlots)
            .ConfigureAwait(false);
    }

    private static async Task AcceptLoopAsync(
        Socket listener, Func<IRpcTransport, CancellationToken, Task> handleConnection,
        CancellationToken cancellationToken, TcpServerOptions? tcpOptions = null,
        SemaphoreSlim? providerSlots = null)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                if (accepted.AddressFamily == AddressFamily.Unix)
                {
                    WidenUnixSocketBuffers(accepted);
                }
                else if (accepted.SocketType == SocketType.Stream)
                {
                    accepted.NoDelay = true;
                }
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
                    using var identityScope = tcpOptions is null
                        ? null
                        : PeerIdentityScope.Push(await ResolveIdentityAsync(
                                accepted, tcpOptions, providerSlots!, cancellationToken)
                            .ConfigureAwait(false));
                    await handleConnection(transport, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A single connection's failure must never take down the accept loop.
                }
            }, cancellationToken);
        }
    }

    private static async Task<PeerConnectionIdentity> ResolveIdentityAsync(
        Socket socket, TcpServerOptions options, SemaphoreSlim providerSlots,
        CancellationToken cancellationToken)
    {
        if (options.PeerIdentityProviders.Count == 0)
            return PeerConnectionIdentity.Anonymous;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.IdentityResolutionTimeout);
        var remote = socket.RemoteEndPoint as System.Net.IPEndPoint;
        var local = socket.LocalEndPoint as System.Net.IPEndPoint;
        var sourceEndpoint = remote?.ToString();
        var context = new PeerResolutionContext(
            "tcp",
            immediatePeer: remote?.Address.ToString(),
            destinationAddress: local?.ToString(),
            serviceName: options.PeerServiceName,
            metadata: sourceEndpoint is null
                ? null
                : new Dictionary<string, object?> { ["remote_addr"] = sourceEndpoint },
            deadline: DateTimeOffset.UtcNow + options.IdentityResolutionTimeout,
            sourceEndpoint: sourceEndpoint);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var tasks = options.PeerIdentityProviders.Select(provider =>
        {
            if (!providerSlots.Wait(0))
            {
                return Task.FromResult(
                    new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable));
            }
            return ResolveProviderAsync(provider, context, deadline.Token, providerSlots);
        }).ToArray();
        var results = new PeerIdentityResult[tasks.Length];
        for (var index = 0; index < tasks.Length; index++)
        {
            var task = tasks[index];
            var remaining = options.IdentityResolutionTimeout - started.Elapsed;
            try
            {
                if (task.IsCompleted)
                    results[index] = await task.ConfigureAwait(false);
                else if (remaining <= TimeSpan.Zero)
                    results[index] = new PeerIdentityResult(
                        options.PeerIdentityProviders[index].Provider, PeerIdentityStatus.Unavailable);
                else
                    results[index] = await task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                results[index] = new PeerIdentityResult(
                    options.PeerIdentityProviders[index].Provider, PeerIdentityStatus.Unavailable);
            }
        }
        deadline.Cancel();
        var evidence = new PeerEvidenceSet(results);
        var auth = options.PeerAuthenticationPolicy is null
            ? AuthContext.Anonymous
            : await options.PeerAuthenticationPolicy(evidence, AuthContext.Anonymous).ConfigureAwait(false);
        return new PeerConnectionIdentity(
            auth,
            evidence,
            sourceEndpoint is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["remote_addr"] = sourceEndpoint });
    }

    private static async Task<PeerIdentityResult> ResolveProviderAsync(
        IPeerIdentityProvider provider, PeerResolutionContext context,
        CancellationToken cancellationToken, SemaphoreSlim providerSlots)
    {
        try
        {
            var result = await provider.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
            return result.Provider == provider.Provider
                ? result
                : new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Invalid);
        }
        catch (PeerIdentityUnavailableException)
        {
            return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable);
        }
        catch (PeerIdentityRejectedException)
        {
            return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Invalid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Unavailable);
        }
        catch
        {
            return new PeerIdentityResult(provider.Provider, PeerIdentityStatus.Invalid);
        }
        finally
        {
            providerSlots.Release();
        }
    }

    /// <summary>Dials a Unix domain socket at <paramref name="path"/>.</summary>
    public static async Task<IRpcTransport> ConnectUnixAsync(string path, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
        WidenUnixSocketBuffers(socket);
        return new SocketTransport(socket);
    }

    /// <summary>Dials a TCP endpoint.</summary>
    public static async Task<IRpcTransport> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return new SocketTransport(socket);
    }

    /// <summary>Dials TCP through an explicit credential-free SOCKS5h proxy.</summary>
    public static async Task<IRpcTransport> ConnectTcpAsync(
        string host, int port, string proxy, TimeSpan connectTimeout,
        CancellationToken cancellationToken = default) =>
        new SocketTransport(await Socks5h.ConnectAsync(host, port, proxy, connectTimeout, cancellationToken)
            .ConfigureAwait(false));

    private static void WidenUnixSocketBuffers(Socket socket)
    {
        try
        {
            socket.SendBufferSize = UnixSocketBufferBytes;
        }
        catch (SocketException)
        {
            // Best effort: kernels may clamp or refuse the requested size.
        }

        try
        {
            socket.ReceiveBufferSize = UnixSocketBufferBytes;
        }
        catch (SocketException)
        {
            // Best effort: the transport remains correct with platform defaults.
        }
    }
}
