using QueryFarm.VgiRpc.Identity;

namespace QueryFarm.VgiRpc.Transport;

/// <summary>Optional connection-snapshot peer identity settings for raw TCP.</summary>
public sealed class TcpServerOptions
{
    public IReadOnlyList<IPeerIdentityProvider> PeerIdentityProviders { get; init; } = [];
    public PeerAuthenticationPolicy? PeerAuthenticationPolicy { get; init; }
    public string? PeerServiceName { get; init; }
    public TimeSpan IdentityResolutionTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public int PeerProviderConcurrency { get; init; } = 64;
    public bool ProxyProtocolV2Required { get; init; }
    public IReadOnlyList<string> TrustedProxyAddresses { get; init; } = [];
    public TimeSpan ProxyPreambleTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public int MaximumProxyPreambleBytes { get; init; } = ProxyProtocolV2.DefaultMaximumPreambleBytes;
    /// <summary>
    /// Enables trusted bridge-forwarded Iroh EndpointId evidence under this local namespace.
    /// </summary>
    public string? IrohProxyIssuer { get; init; }

    internal void Validate()
    {
        if (IdentityResolutionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(IdentityResolutionTimeout), "identity resolution timeout must be positive");
        if (PeerAuthenticationPolicy is not null && PeerIdentityProviders.Count == 0
            && IrohProxyIssuer is null)
            throw new ArgumentException("peer authentication policy requires an identity provider");
        if (PeerProviderConcurrency <= 0 || PeerProviderConcurrency < PeerIdentityProviders.Count)
            throw new ArgumentOutOfRangeException(nameof(PeerProviderConcurrency),
                "provider concurrency must accommodate one complete resolution fanout");
        if (ProxyPreambleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ProxyPreambleTimeout),
                "PROXY v2 preamble timeout must be positive");
        if (MaximumProxyPreambleBytes < 16 || MaximumProxyPreambleBytes > 16 + ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaximumProxyPreambleBytes),
                "maximum PROXY v2 preamble bytes must be between 16 and 65551");
        var trustedProxies = ParseTrustedProxyAddresses();
        if (ProxyProtocolV2Required && trustedProxies.Count == 0)
            throw new ArgumentException(
                "PROXY v2 requires at least one exact trusted proxy address");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in PeerIdentityProviders)
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.Provider) || !names.Add(provider.Provider))
                throw new ArgumentException("peer identity providers must have unique non-empty names");
        }
        if (IrohProxyIssuer is not null)
        {
            if (string.IsNullOrWhiteSpace(IrohProxyIssuer)
                || IrohProxyIssuer.Any(character => character <= 0x1f || character == 0x7f))
                throw new ArgumentException(
                    "Iroh proxy issuer must be non-empty text without controls",
                    nameof(IrohProxyIssuer));
            if (!ProxyProtocolV2Required)
                throw new ArgumentException(
                    "Iroh proxy issuer requires PROXY v2",
                    nameof(IrohProxyIssuer));
            if (names.Contains("iroh"))
                throw new ArgumentException(
                    "forwarded Iroh identity conflicts with another iroh provider",
                    nameof(PeerIdentityProviders));
        }
    }

    internal IReadOnlySet<System.Net.IPAddress> ParseTrustedProxyAddresses()
    {
        var parsed = new HashSet<System.Net.IPAddress>();
        foreach (var value in TrustedProxyAddresses)
        {
            if (!TryParseExactIpAddress(value, out var address))
                throw new ArgumentException(
                    "trusted PROXY v2 senders must be exact IP addresses without ports, CIDRs, hostnames, or zones",
                    nameof(TrustedProxyAddresses));
            if (!parsed.Add(ProxyProtocolV2.Normalize(address)))
                throw new ArgumentException("trusted PROXY v2 sender addresses must be unique",
                    nameof(TrustedProxyAddresses));
        }
        return parsed;
    }

    private static bool TryParseExactIpAddress(
        string? value, out System.Net.IPAddress address)
    {
        address = System.Net.IPAddress.None;
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character > 0x7f))
            return false;
        if (!value.Contains(':'))
        {
            var octets = value.Split('.');
            if (octets.Length != 4) return false;
            Span<byte> bytes = stackalloc byte[4];
            for (var index = 0; index < octets.Length; index++)
            {
                var octet = octets[index];
                if (octet.Length is < 1 or > 3
                    || octet.Length > 1 && octet[0] == '0'
                    || octet.Any(character => character is < '0' or > '9')
                    || !byte.TryParse(octet, out bytes[index]))
                    return false;
            }
            address = new System.Net.IPAddress(bytes);
            return true;
        }
        if (value.Contains('%') || value.Contains('[') || value.Contains(']')
            || value.Any(character => character is not
                (':' or '.' or >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')))
            return false;
        var embeddedIpv4 = value[(value.LastIndexOf(':') + 1)..];
        if (embeddedIpv4.Contains('.') && !TryParseExactIpAddress(embeddedIpv4, out _))
            return false;
        if (!System.Net.IPAddress.TryParse(value, out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return false;
        address = parsed;
        return true;
    }
}
