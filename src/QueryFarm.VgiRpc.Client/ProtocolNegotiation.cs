using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

/// <summary>
/// Client-side protocol version negotiation for one endpoint (<c>PROTOCOL_VERSIONS_V1.md</c> §6).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Before anything is learned the client speaks its newest version.</item>
/// <item>A server window -- learned from <c>VGI-Protocol-Versions</c> (every HTTP response), from
/// <c>vgi_rpc.protocol_versions</c> (reflection results, refusals) or from a refusal's
/// <see cref="ErrorInfo"/> -- picks the highest common <c>major.minor</c>; the client's own patch
/// is kept (<c>3.5.2</c> speaks <c>3.4.2</c>).</item>
/// <item>A pre-negotiation refusal (<c>protocol_version_mismatch</c>, no windows) names one server
/// version (<c>Server: X.Y.Z</c> / <c>server requires X.Y.Z</c>), which is then spoken.</item>
/// <item>With neither, the client keeps its own newest.</item>
/// <item>Once a server window is known to share nothing with the client's, calls fail before
/// sending, naming both windows.</item>
/// </list>
/// Thread-safe: one instance is shared by every call on a client.
/// </remarks>
public sealed class ProtocolNegotiation
{
    private readonly object _gate = new();
    private readonly (int Major, int Minor) _newest;
    private readonly string _patch;
    private Dictionary<string, VersionRange>? _server;
    private (int Major, int Minor)? _legacy;

    private ProtocolNegotiation(string protocol, (int, int) newest, string patch, VersionRange window)
    {
        Protocol = protocol;
        _newest = newest;
        _patch = patch;
        Window = window;
    }

    /// <summary>A client of <paramref name="protocol"/> whose newest version is
    /// <paramref name="version"/> (semver) and which supports <paramref name="range"/>
    /// (<c>MIN-MAX</c>; default exactly <paramref name="version"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="version"/> is not semver, or
    /// <paramref name="range"/> is malformed or excludes <paramref name="version"/>.</exception>
    public static ProtocolNegotiation Create(string protocol, string version, string? range = null)
    {
        var parts = version.Split('.');
        if (parts.Length != 3 || VersionRange.ParseMajorMinor(version) is not { } newest)
        {
            throw new ArgumentException($"protocol version must be MAJOR.MINOR.PATCH: '{version}'", nameof(version));
        }

        var window = range is null
            ? new VersionRange(newest, newest)
            : VersionRange.Parse(range) ?? throw new ArgumentException($"protocol version range must be MIN-MAX of MAJOR.MINOR: '{range}'", nameof(range));
        if (!window.Contains(newest))
        {
            throw new ArgumentException($"protocol version range {window} excludes the client's version {version}", nameof(range));
        }

        return new ProtocolNegotiation(protocol, newest, parts[2], window);
    }

    /// <summary>The protocol this client is bound to.</summary>
    public string Protocol { get; }

    /// <summary>The client's own window.</summary>
    public VersionRange Window { get; }

    /// <summary>The client's windows as sent in <c>vgi_rpc.protocol_versions_client</c> /
    /// <c>VGI-Client-Protocol-Versions</c>: <c>protocol=MIN-MAX</c>.</summary>
    public string ClientVersions => $"{Protocol}={Window}";

    /// <summary>Whether the client's window holds more than one version.</summary>
    public bool IsWide => Window.Min != Window.Max;

    /// <summary>Whether the server's windows have been learned.</summary>
    public bool Learned
    {
        get
        {
            lock (_gate)
            {
                return _server is not null;
            }
        }
    }

    /// <summary>The version to declare on the next request: the chosen <c>major.minor</c> plus
    /// the client's patch.</summary>
    /// <exception cref="ProtocolVersionException">The server's window is known and shares no
    /// version with the client's (kind <c>protocol_version_unsupported</c>).</exception>
    public string VersionToSend()
    {
        lock (_gate)
        {
            (int Major, int Minor) mm;
            if (_server is not null && _server.TryGetValue(Protocol, out var server))
            {
                mm = Window.HighestCommon(server) ?? throw NoCommonVersion(server);
            }
            else
            {
                mm = _legacy ?? _newest;
            }

            return $"{mm.Major}.{mm.Minor}.{_patch}";
        }
    }

    private ProtocolVersionException NoCommonVersion(VersionRange server)
    {
        var direction = VersionRange.Compare(Window.Max, server.Min) < 0
            ? "The client is too old (the server is too new for it). Upgrade the client."
            : "The server is too old (the client is too new for it). Upgrade the worker.";
        return new ProtocolVersionException(
            $"{MetadataKeys.ErrorKinds.ProtocolVersionUnsupported}: no common version of {Protocol}: this client supports "
            + $"{Protocol} {Window}, the server supports {Protocol} {server}. {direction}")
        {
            ErrorCode = ErrorCodes.FailedPrecondition,
            ErrorKind = MetadataKeys.ErrorKinds.ProtocolVersionUnsupported,
        };
    }

    /// <summary>Learn from a <c>VGI-Protocol-Versions</c> / <c>vgi_rpc.protocol_versions</c>
    /// value. A present value replaces what was cached (a redeploy behind one URL); an empty one
    /// says the server gates nothing; <see langword="null"/> (absent) changes nothing.</summary>
    public void LearnAdvertisement(string? value)
    {
        if (value is null)
        {
            return;
        }

        var parsed = ProtocolVersions.Parse(value);
        lock (_gate)
        {
            _server = parsed;
        }
    }

    /// <summary>Learn from a refusal: its <see cref="ErrorInfo"/> windows, else a legacy
    /// refusal's single server version. Returns whether it was a version refusal.</summary>
    public bool LearnRefusal(Exception error)
    {
        if (error is not RpcException rpc
            || (rpc.ErrorKind != MetadataKeys.ErrorKinds.ProtocolVersionUnsupported
                && rpc.ErrorKind != MetadataKeys.ErrorKinds.ProtocolVersionMismatch))
        {
            return false;
        }

        var advertised = (rpc as ProtocolVersionException)?.ServerVersions
            ?? rpc.Details().OfType<ErrorInfo>()
                .Select(i => i.Metadata.TryGetValue(ProtocolVersions.Key, out var v) ? v : null)
                .FirstOrDefault(v => v is not null);
        if (advertised is not null)
        {
            LearnAdvertisement(advertised);
            return true;
        }

        lock (_gate)
        {
            if (_server is null || !_server.ContainsKey(Protocol))
            {
                var legacy = ProtocolVersions.LegacyServerVersion(rpc.ErrorMessage)
                    ?? rpc.Details().OfType<PreconditionFailure>()
                        .SelectMany(p => p.Violations)
                        .Select(v => ProtocolVersions.LegacyServerVersion(v.Description))
                        .FirstOrDefault(v => v is not null);
                if (legacy is not null)
                {
                    _legacy = legacy;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a call may be retried once after a version refusal (§6): reflection
    /// only, until methods can declare idempotency.</summary>
    public static bool IsRetrySafe(string protocol) => protocol == ReflectionProtocol.ProtocolName;
}
