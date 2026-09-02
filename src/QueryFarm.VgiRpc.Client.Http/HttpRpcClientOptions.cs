using System.Security.Cryptography.X509Certificates;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Logging;

namespace QueryFarm.VgiRpc.Client.Http;

public sealed class HttpRpcClientOptions
{
    public const long DefaultAcceptedMaxResponseBytes = 256L << 20;
    public string Prefix { get; init; } = "";

    public int? CompressionLevel { get; init; } = 1;

    public ContentEncoding PreferredEncoding { get; init; } = ContentEncoding.Zstd;

    public ClientExternalConfig? ExternalLocation { get; init; }

    public bool AcceptNewSession { get; init; }

    public IReadOnlyDictionary<string, string>? DefaultHeaders { get; init; }

    public Action<LogMessage>? OnLog { get; init; }

    public X509Certificate2? ClientCertificate { get; init; }

    public bool FollowRedirects { get; init; } = true;

    /// <summary>Explicit credential-free <c>socks5h://host:port</c> proxy.</summary>
    public string? TcpProxy { get; init; }

    /// <summary>One setup deadline for proxy DNS, connect, negotiation, and target connect.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Largest decoded Arrow IPC response accepted by this native client.</summary>
    public long AcceptedMaxResponseBytes { get; init; } = DefaultAcceptedMaxResponseBytes;
}

public sealed record HttpServerCapabilities(
    long? MaxRequestBytes,
    long? MaxResponseBytes,
    long? MaxUploadBytes,
    long? MaxExternalizedResponseBytes,
    bool AcceptMaxResponseBytesSupport,
    bool ExternalizationEnabled,
    bool UploadUrlSupport,
    bool StickyEnabled,
    long? StickyDefaultTtl,
    IReadOnlyList<string> StickyEchoHeaders,
    IReadOnlyList<ContentEncoding> SupportedEncodings);

public sealed record UploadUrl(string Upload, string Download, DateTimeOffset ExpiresAt);
