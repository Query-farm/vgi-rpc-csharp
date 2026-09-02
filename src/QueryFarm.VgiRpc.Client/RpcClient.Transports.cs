using System.IO.Pipes;
using QueryFarm.VgiRpc.Transport;

namespace QueryFarm.VgiRpc.Client;

public sealed partial class RpcClient
{
    /// <summary>
    /// Connect through the process-shared native provider. Reusing this provider gives ephemeral
    /// configurations one stable local EndpointId for the process lifetime.
    /// </summary>
    public static Task<RpcClient> ConnectIrohAsync(
        string endpoint,
        IrohConnectOptions? irohOptions = null,
        RpcClientOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ConnectIrohAsync(endpoint, NativeIrohTransportProvider.Shared, irohOptions, options, cancellationToken);

    /// <summary>
    /// Connect through a caller-supplied native Iroh provider. The provider is an explicit
    /// dependency so this package never claims support on a platform lacking the native C ABI.
    /// </summary>
    public static async Task<RpcClient> ConnectIrohAsync(
        string endpoint,
        IIrohTransportProvider provider,
        IrohConnectOptions? irohOptions = null,
        RpcClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var parsed = IrohEndpoint.Parse(endpoint);
        if (parsed.Scheme != "iroh")
            throw new IrohTransportException(
                "Raw RpcClient requires iroh://; httpi:// requires an iroh-http/2 client.",
                IrohErrorStage.Bind, IrohErrorCategory.Unsupported, IrohDispatchCertainty.NotSent);
        irohOptions ??= new IrohConnectOptions();
        irohOptions.Validate();
        return new RpcClient(await provider.OpenArrowMuxAsync(parsed, irohOptions, cancellationToken).ConfigureAwait(false), options);
    }

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

    public static async Task<RpcClient> ConnectTcpAsync(
        string host,
        int port,
        string proxy,
        TimeSpan connectTimeout,
        RpcClientOptions? options = null,
        CancellationToken cancellationToken = default) =>
        new(await SocketTransport.ConnectTcpAsync(host, port, proxy, connectTimeout, cancellationToken)
            .ConfigureAwait(false), options);

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
