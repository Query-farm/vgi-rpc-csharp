using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Identity;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class TailscalePeerIdentityProvidersTests
{
    [Fact]
    public async Task ServeProducesLoginSubjectAndVerifiedCapabilities()
    {
        var provider = TailscalePeerIdentityProviders.Serve("tailnet:example", ["127.0.0.1"]);
        var result = await provider.ResolveAsync(Context("127.0.0.1", new Dictionary<string, IReadOnlyList<string>>
        {
            ["Tailscale-User-Login"] = ["alice@example.com"],
            ["Tailscale-User-Name"] = ["=?UTF-8?Q?Alice_=E2=98=83?="],
            ["Tailscale-App-Capabilities"] = ["{\"query.farm/cap\":[{\"role\":\"reader\"}]}"],
        }));
        Assert.Equal(PeerIdentityStatus.Available, result.Status);
        var identity = Assert.Single(result.Identities);
        Assert.Equal(PeerSubjectKind.User, identity.SubjectKind);
        Assert.Equal(SubjectStability.Login, identity.SubjectStability);
        Assert.Equal("login:alice@example.com", identity.SubjectKey);
        Assert.Equal("Alice ☃", identity.Attributes["user_display_name"].GetString());
        Assert.True(identity.CapabilitiesVerified);
    }

    [Fact]
    public async Task CapabilityOnlyEvidenceIsSubjectless()
    {
        var provider = TailscalePeerIdentityProviders.Serve("tailnet:example", ["127.0.0.1"]);
        var result = await provider.ResolveAsync(Context("127.0.0.1", new Dictionary<string, IReadOnlyList<string>>
        {
            ["Tailscale-App-Capabilities"] = ["{\"query.farm/cap\":[]}"],
        }));
        var identity = Assert.Single(result.Identities);
        Assert.Equal(PeerSubjectKind.Unknown, identity.SubjectKind);
        Assert.Null(identity.SubjectKey);
    }

    [Fact]
    public async Task CapabilityVerificationTracksPresentNonemptyEvidence()
    {
        var provider = TailscalePeerIdentityProviders.Serve("tailnet:example", ["127.0.0.1"]);
        var loginOnly = await provider.ResolveAsync(Context("127.0.0.1", new Dictionary<string, IReadOnlyList<string>>
        {
            ["Tailscale-User-Login"] = ["alice@example.com"],
        }));
        Assert.False(Assert.Single(loginOnly.Identities).CapabilitiesVerified);

        var empty = await provider.ResolveAsync(Context("127.0.0.1", new Dictionary<string, IReadOnlyList<string>>
        {
            ["Tailscale-App-Capabilities"] = ["{}"],
        }));
        Assert.Equal(PeerIdentityStatus.NoMatch, empty.Status);
        Assert.Empty(empty.Identities);
    }

    [Fact]
    public async Task Rfc2047CapabilitiesAreDecodedBeforeJsonParsing()
    {
        var provider = TailscalePeerIdentityProviders.Serve("tailnet:example", ["127.0.0.1"]);
        var result = await provider.ResolveAsync(Context("127.0.0.1", new Dictionary<string, IReadOnlyList<string>>
        {
            ["Tailscale-App-Capabilities"] =
                ["=?utf-8?q?{=22query.farm/run=22:[{=22queue=22:=22blue=22}]}?="],
        }));

        Assert.Equal(PeerIdentityStatus.Available, result.Status);
        Assert.True(Assert.Single(result.Identities).CapabilitiesVerified);
    }

    [Fact]
    public async Task ServeFailsClosedForTrustDuplicatesAndMalformedCapabilities()
    {
        var provider = TailscalePeerIdentityProviders.Serve("tailnet:example", ["127.0.0.1"]);
        Assert.Equal(PeerIdentityStatus.UntrustedProxy, (await provider.ResolveAsync(Context("127.0.0.2",
            new Dictionary<string, IReadOnlyList<string>> { ["Tailscale-User-Login"] = ["alice"] }))).Status);
        Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1",
            new Dictionary<string, IReadOnlyList<string>> { ["Tailscale-User-Login"] = ["alice", "mallory"] }))).Status);
        Assert.Equal(PeerIdentityStatus.NotApplicable, (await provider.ResolveAsync(Context("127.0.0.1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tailscale-Funnel-Request"] = ["?1"],
                ["Tailscale-User-Login"] = ["spoof@example.com"],
            }))).Status);
        Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tailscale-Funnel-Request"] = ["true"],
                ["Tailscale-User-Login"] = ["spoof@example.com"],
            }))).Status);
        Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1",
            new Dictionary<string, IReadOnlyList<string>> { ["Tailscale-User-Name"] = ["Alice"] }))).Status);
        foreach (var json in new[] { "[]", "{\"cap\":{}}", "{\"cap\":[],\"cap\":[]}",
                     "{\"cap\":[\"\ud800\"]}" })
            Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1",
                new Dictionary<string, IReadOnlyList<string>> { ["Tailscale-App-Capabilities"] = [json] }))).Status);
    }

    [Fact]
    public async Task TrustedProxyAddressesAreExactNormalizedIpLiterals()
    {
        var headers = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Tailscale-User-Login"] = ["alice@example.com"],
        };
        var ipv6 = TailscalePeerIdentityProviders.Serve(
            "tailnet:example", ["0:0:0:0:0:0:0:1"]);
        Assert.Equal(PeerIdentityStatus.Available,
            (await ipv6.ResolveAsync(Context("::1", headers))).Status);

        var mapped = TailscalePeerIdentityProviders.Serve(
            "tailnet:example", ["::ffff:127.0.0.1"]);
        Assert.Equal(PeerIdentityStatus.Available,
            (await mapped.ResolveAsync(Context("127.0.0.1", headers))).Status);

        foreach (var invalid in new[]
        {
            "proxy.example", "127.0.0.1/32", "127.0.0.1:443", "[::1]:443", "127.1",
        })
            Assert.Throws<ArgumentException>(() =>
                TailscalePeerIdentityProviders.Serve("tailnet:example", [invalid]));
        Assert.Throws<ArgumentException>(() => TailscalePeerIdentityProviders.Serve(
            "tailnet:example", ["::1", "0:0:0:0:0:0:0:1"]));
        Assert.Throws<ArgumentException>(() => TailscalePeerIdentityProviders.Serve(
            "tailnet:example", ["127.0.0.1", "::ffff:127.0.0.1"]));
    }

    private static PeerResolutionContext Context(string peer,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers) =>
        new("http", peer, "client", headers: headers);
}
