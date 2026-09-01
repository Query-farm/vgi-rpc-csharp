using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Identity;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class IrohPeerIdentityProvidersTests
{
    private const string EndpointId = "000102030405060708090a0b0c0d0e0f"
        + "101112131415161718191a1b1c1d1e1f";

    [Fact]
    public async Task TrustedSanitizedHeaderProducesStableNamespacedEndpoint()
    {
        var provider = IrohPeerIdentityProviders.Forwarded(
            "production-mesh", ["127.0.0.1"]);
        var result = await provider.ResolveAsync(Context("127.0.0.1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [IrohPeerIdentityProviders.ForwardedEndpointHeader] = [EndpointId],
            }));

        Assert.Equal(PeerIdentityStatus.Available, result.Status);
        var identity = Assert.Single(result.Identities);
        Assert.Equal("production-mesh", identity.Issuer);
        Assert.Equal(EndpointId, identity.SubjectKey);
        Assert.Equal(PeerSubjectKind.Endpoint, identity.SubjectKind);
        Assert.Equal(SubjectStability.Stable, identity.SubjectStability);
        Assert.Equal(IdentityAssurance.ConfiguredProxy, identity.Assurance);
        Assert.Equal("cryptographic_peer",
            identity.Attributes["original_assurance"].GetString());
        Assert.Equal(EndpointId, identity.SourceAddress);
        Assert.Equal("127.0.0.1", identity.ProxyAddress);
    }

    [Fact]
    public async Task HeaderFailsClosedForUntrustedDuplicateOrNoncanonicalValues()
    {
        var provider = IrohPeerIdentityProviders.Forwarded(
            "production-mesh", ["127.0.0.1"]);
        var header = new Dictionary<string, IReadOnlyList<string>>
        {
            [IrohPeerIdentityProviders.ForwardedEndpointHeader] = [EndpointId],
        };
        Assert.Equal(PeerIdentityStatus.UntrustedProxy,
            (await provider.ResolveAsync(Context("192.0.2.1", header))).Status);
        Assert.Equal(PeerIdentityStatus.NoMatch,
            (await provider.ResolveAsync(Context("127.0.0.1",
                new Dictionary<string, IReadOnlyList<string>>()))).Status);

        foreach (var invalid in new[]
        {
            EndpointId.ToUpperInvariant(), EndpointId + " ", EndpointId[1..],
        })
            Assert.Equal(PeerIdentityStatus.Invalid,
                (await provider.ResolveAsync(Context("127.0.0.1",
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        [IrohPeerIdentityProviders.ForwardedEndpointHeader] = [invalid],
                    }))).Status);
        Assert.Equal(PeerIdentityStatus.Invalid,
            (await provider.ResolveAsync(Context("127.0.0.1",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    [IrohPeerIdentityProviders.ForwardedEndpointHeader] =
                        [EndpointId, EndpointId],
                }))).Status);
    }

    [Fact]
    public async Task ConfigurationUsesExactNormalizedProxyTrustAndRejectsCaseDuplicates()
    {
        var provider = IrohPeerIdentityProviders.Forwarded(
            "production-mesh", ["::ffff:192.0.2.10"]);
        Assert.Equal(PeerIdentityStatus.Available,
            (await provider.ResolveAsync(Context("192.0.2.10",
                new Dictionary<string, IReadOnlyList<string>>
                {
                    [IrohPeerIdentityProviders.ForwardedEndpointHeader] = [EndpointId],
                }))).Status);
        Assert.Throws<ArgumentException>(() =>
            IrohPeerIdentityProviders.Forwarded("mesh", ["proxy.internal"]));
        Assert.Throws<ArgumentException>(() =>
            IrohPeerIdentityProviders.Forwarded("bad\nissuer", ["127.0.0.1"]));

        var duplicates = new Dictionary<string, IReadOnlyList<string>>
        {
            [IrohPeerIdentityProviders.ForwardedEndpointHeader] = [EndpointId],
            ["vgi-forwarded-iroh-endpoint"] = [EndpointId],
        };
        Assert.Throws<PeerIdentityRejectedException>(() =>
            Context("127.0.0.1", duplicates));
    }

    private static PeerResolutionContext Context(
        string peer,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers) =>
        new("http", peer, "client", headers: headers);
}
