using System.IO.Pipes;
using QueryFarm.VgiRpc.Transport;

namespace QueryFarm.VgiRpc.Client;

public sealed partial class RpcClient
{
    public static RpcClient StartSubprocess(
        IReadOnlyList<string> command,
        RpcClientOptions? options = null,
        SubprocessStderrMode stderr = SubprocessStderrMode.Inherit,
        Action<string>? onStderr = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null) =>
        new(new SubprocessTransport(command, stderr, onStderr, environment: environment, workingDirectory: workingDirectory), options);

    public static async Task<RpcClient> ConnectUnixAsync(
        string path,
        RpcClientOptions? options = null,
        CancellationToken cancellationToken = default) =>
        new(await SocketTransport.ConnectUnixAsync(path, cancellationToken).ConfigureAwait(false), options);

    public static async Task<RpcClient> ConnectTcpAsync(
        string host,
        int port,
        RpcClientOptions? options = null,
        CancellationToken cancellationToken = default) =>
        new(await SocketTransport.ConnectTcpAsync(host, port, cancellationToken).ConfigureAwait(false), options);

    public static async Task<RpcClient> ConnectNamedPipeAsync(
        string pipeName,
        string serverName = ".",
        RpcClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var stream = new NamedPipeClientStream(
            serverName,
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new RpcClient(new DuplexStreamTransport(stream), options);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class DuplexStreamTransport(Stream stream) : IRpcTransport, IAsyncDisposable
    {
        public Stream Input => stream;

        public Stream Output => stream;

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
