using QueryFarm.VgiRpc.Transport;

namespace QueryFarm.VgiRpc.Client;

/// <summary>Options passed to a native Iroh transport provider.</summary>
public sealed class IrohConnectOptions
{
    /// <summary>A stable 32-byte endpoint key. Prefer a mutable buffer and clear it after first use.</summary>
    public byte[]? SecretKey { get; init; }
    public IReadOnlyList<string> RelayUrls { get; init; } = [];
    public bool NoRelay { get; init; }
    /// <summary>Optional relay hint for the remote endpoint, independent of local relay selection.</summary>
    public string? RemoteRelayUrl { get; init; }
    /// <summary>Optional direct-address hints for private or direct-only networks.</summary>
    public IReadOnlyList<string> DirectAddresses { get; init; } = [];
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IoTimeout { get; init; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (SecretKey is not null && SecretKey.Length != 32)
            throw new IrohTransportException("Iroh SecretKey must contain exactly 32 bytes.",
                IrohErrorStage.Parse, IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
        if (NoRelay && RelayUrls.Count != 0)
            throw new IrohTransportException("NoRelay and RelayUrls are mutually exclusive.",
                IrohErrorStage.Parse, IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
        if (ConnectTimeout <= TimeSpan.Zero)
            throw new IrohTransportException("ConnectTimeout must be positive.",
                IrohErrorStage.Parse, IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
        if (IoTimeout <= TimeSpan.Zero)
            throw new IrohTransportException("IoTimeout must be positive.",
                IrohErrorStage.Parse, IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
        foreach (var value in RelayUrls.Concat(DirectAddresses).Append(RemoteRelayUrl).OfType<string>())
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(character => character <= 0x1f || character == 0x7f))
                throw new IrohTransportException("Iroh relay and direct-address hints must be non-empty and contain no controls.",
                    IrohErrorStage.Parse, IrohErrorCategory.InvalidInput, IrohDispatchCertainty.NotSent);
        }
    }
}

/// <summary>
/// Optional native provider implemented by the version-matched vgi-iroh C ABI package.
/// Core does not download or execute a connector binary.
/// </summary>
public interface IIrohTransportProvider
{
    ValueTask<IRpcTransport> OpenArrowMuxAsync(
        IrohEndpoint endpoint, IrohConnectOptions options, CancellationToken cancellationToken = default);
}

/// <summary>A bounded, fully materialized HTTP request carried by <c>iroh-http/2</c>.</summary>
public sealed record IrohHttpRequest(
    string Method,
    string Path,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    long MaxResponseBytes,
    long MaxResponseHeaderBytes);

/// <summary>An HTTP response whose body remains backed by the native Iroh stream.</summary>
public sealed record IrohHttpResponse(
    int StatusCode,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    Stream Body,
    string RemoteEndpointId);

/// <summary>Optional HTTP-over-Iroh provider implemented by the version-matched native package.</summary>
public interface IIrohHttpTransportProvider
{
    ValueTask<IrohHttpResponse> SendHttpAsync(
        IrohEndpoint endpoint,
        IrohHttpRequest request,
        IrohConnectOptions options,
        CancellationToken cancellationToken = default);
}
