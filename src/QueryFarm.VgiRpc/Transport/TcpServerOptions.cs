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

    internal void Validate()
    {
        if (IdentityResolutionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(IdentityResolutionTimeout), "identity resolution timeout must be positive");
        if (PeerAuthenticationPolicy is not null && PeerIdentityProviders.Count == 0)
            throw new ArgumentException("peer authentication policy requires an identity provider");
        if (PeerProviderConcurrency <= 0 || PeerProviderConcurrency < PeerIdentityProviders.Count)
            throw new ArgumentOutOfRangeException(nameof(PeerProviderConcurrency),
                "provider concurrency must accommodate one complete resolution fanout");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in PeerIdentityProviders)
        {
            if (provider is null || string.IsNullOrWhiteSpace(provider.Provider) || !names.Add(provider.Provider))
                throw new ArgumentException("peer identity providers must have unique non-empty names");
        }
    }
}
