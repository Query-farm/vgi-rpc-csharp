using System.Security.Cryptography.X509Certificates;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Logging;

namespace QueryFarm.VgiRpc.Client.Http;

public sealed class HttpRpcClientOptions
{
    public const long DefaultAcceptedMaxResponseBytes = 256L << 20;
    public string Prefix { get; init; } = "";

    /// <summary>
    /// Routing key of the hosted protocol every RPC call on this client addresses — e.g.
    /// <c>"ConformanceService"</c>, <c>"vgi_rpc.Reflection.v1"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Required: RPC paths are <c>{Prefix}/{Protocol}/{method}</c> and every request batch also
    /// carries the name as <c>vgi_rpc.protocol</c>. There is no default and no single-protocol
    /// exemption — a client that does not say which protocol it is addressing would be relying on
    /// the server to guess, which is precisely the guess the routing rules exist to remove.
    /// </para>
    /// <para>
    /// A client addressing two protocols on one worker (the application's, plus
    /// <c>vgi_rpc.Reflection.v1</c> to discover it) uses one instance per protocol; they are
    /// cheap, and an <see cref="System.Net.Http.HttpClient"/> can be shared between them.
    /// </para>
    /// <para>
    /// The server-level reserved endpoints (<c>__upload_url__</c>, the sticky-session route,
    /// capability discovery) are not protocol-scoped and ignore this.
    /// </para>
    /// </remarks>
    public string Protocol { get; init; } = "";

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
