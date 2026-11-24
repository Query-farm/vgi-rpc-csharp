using System.Security.Cryptography.X509Certificates;
using QueryFarm.VgiRpc.External;
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

    /// <summary>The <c>vgi_rpc.protocol_version</c> every protocol-scoped request declares, or
    /// <see langword="null"/> to declare none -- the HTTP twin of
    /// <c>RpcClientOptions.ProtocolVersion</c>. Required against a server whose protocol declares a
    /// version, which gates every request against it on HTTP as on the byte stream.</summary>
    public string? ProtocolVersion { get; init; }

    /// <summary>The <c>major.minor</c> window this client can speak for its protocol, as
    /// <c>MIN-MAX</c> (PROTOCOL_VERSIONS_V1.md §6); <see langword="null"/> means exactly
    /// <see cref="ProtocolVersion"/>. Before the server is heard from the client declares
    /// <see cref="ProtocolVersion"/>; afterwards the highest version both windows share (with
    /// <see cref="ProtocolVersion"/>'s patch), failing before sending when there is none.</summary>
    public string? ProtocolVersionRange { get; init; }

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

/// <summary>What <c>OPTIONS {prefix}/health</c> advertised. <paramref name="UploadUrlVersion"/>
/// is the upload-URL protocol version from <c>VGI-Upload-URL-Support</c>: 1 for the retired
/// <c>true</c>, the integer for a positive decimal, and 0 when absent or unparseable.</summary>
public sealed record HttpServerCapabilities(
    long? MaxRequestBytes,
    long? MaxResponseBytes,
    long? MaxUploadBytes,
    long? MaxExternalizedResponseBytes,
    bool AcceptMaxResponseBytesSupport,
    bool ExternalizationEnabled,
    int UploadUrlVersion,
    bool StickyEnabled,
    long? StickyDefaultTtl,
    IReadOnlyList<string> StickyEchoHeaders,
    IReadOnlyList<ContentEncoding> SupportedEncodings)
{
    /// <summary>Whether the server advertises upload URLs at any protocol version. This client
    /// uploads only when <see cref="UploadUrlVersion"/> is exactly
    /// <see cref="HttpRpcClient.UploadUrlProtocolVersion"/>.</summary>
    public bool UploadUrlSupport => UploadUrlVersion > 0;
}

/// <summary>One upload the client intends to make: the exact byte length and lowercase-hex SHA-256
/// of the body it will PUT (upload-URL protocol v2).</summary>
public sealed record UploadRequest(long Size, string Sha256);

/// <summary>A server-vended upload URL pair. <paramref name="Headers"/> are the headers the PUT
/// MUST carry, verbatim (never <c>Content-Length</c>).</summary>
public sealed record UploadUrl(string Upload, string Download, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The storage id of the object the PUT creates (protocol 3.4 <c>object_id</c>
    /// column; empty from an older server).</summary>
    public string ObjectId { get; init; } = "";

    /// <summary>The sealed argument reference for that object (protocol 3.4 <c>ref</c> column,
    /// <c>STATE_REF_V1.md</c> §10.2), bound to this client's principal; empty from an older
    /// server. Present it with the object's size and SHA-256 to name the object in a later call.</summary>
    public byte[] Ref { get; init; } = [];
}
