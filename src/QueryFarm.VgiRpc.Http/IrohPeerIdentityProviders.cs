using QueryFarm.VgiRpc.Identity;

namespace QueryFarm.VgiRpc.Http;

/// <summary>Trusted HTTP forwarding of bridge-verified Iroh EndpointIds.</summary>
public static class IrohPeerIdentityProviders
{
    public const string ForwardedEndpointHeader = "VGI-Forwarded-Iroh-Endpoint";
    private const string ProviderName = "iroh";

    /// <summary>
    /// Resolves one sanitized EndpointId only from an exact trusted bridge address. The issuer
    /// is always operator-local and is never accepted from the forwarded request.
    /// </summary>
    public static IPeerIdentityProvider Forwarded(
        string issuer,
        IEnumerable<string> trustedProxyAddresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        if (issuer.Any(character => character <= 0x1f || character == 0x7f))
            throw new ArgumentException("Iroh issuer contains control characters", nameof(issuer));
        var proxies = TrustedProxyAddresses.Parse(
            trustedProxyAddresses, nameof(trustedProxyAddresses));
        return new ForwardedProvider(issuer, proxies);
    }

    private sealed class ForwardedProvider(string issuer, HashSet<string> proxies)
        : IPeerIdentityProvider
    {
        public string Provider => ProviderName;

        public ValueTask<PeerIdentityResult> ResolveAsync(
            PeerResolutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (TrustedProxyAddresses.Normalize(context.ImmediatePeer) is not { } peer
                || !proxies.Contains(peer))
                return ValueTask.FromResult(Result(PeerIdentityStatus.UntrustedProxy));
            try
            {
                var endpointId = context.Header(ForwardedEndpointHeader);
                if (endpointId is null)
                    return ValueTask.FromResult(Result(PeerIdentityStatus.NoMatch));
                if (endpointId.Length != 64
                    || endpointId.Any(character => character is not
                        (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    return ValueTask.FromResult(Result(PeerIdentityStatus.Invalid));

                var identity = new PeerIdentity(
                    ProviderName,
                    "http_proxy",
                    IdentityAssurance.ConfiguredProxy,
                    issuer,
                    "http",
                    PeerSubjectKind.Endpoint,
                    endpointId,
                    SubjectStability.Stable,
                    subjectVerified: true,
                    attributes: new Dictionary<string, object?>
                    {
                        ["original_assurance"] = "cryptographic_peer",
                    },
                    sourceAddress: endpointId,
                    proxyAddress: peer);
                return ValueTask.FromResult(PeerIdentityResult.Available(identity));
            }
            catch (Exception exception) when (exception is ArgumentException
                or PeerIdentityRejectedException)
            {
                return ValueTask.FromResult(Result(PeerIdentityStatus.Invalid));
            }
        }
    }

    private static PeerIdentityResult Result(PeerIdentityStatus status) =>
        new(ProviderName, status);
}
