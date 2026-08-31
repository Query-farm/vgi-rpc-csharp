using System.Text;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Identity;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class SpiffePeerIdentityProvidersTests
{
    private const string Pem = """
        -----BEGIN CERTIFICATE-----
        MIIDazCCAlOgAwIBAgIUG0eLA1ht8L3mAXNQqWfPUZc/Ee4wDQYJKoZIhvcNAQEL
        BQAwEzERMA8GA1UEAwwIdmdpLXRlc3QwHhcNMjYwODMwMjM1MDMwWhcNMzYwODI3
        MjM1MDMwWjATMREwDwYDVQQDDAh2Z2ktdGVzdDCCASIwDQYJKoZIhvcNAQEBBQAD
        ggEPADCCAQoCggEBALSDZQA4+r/bFdEMHAPoiap59VUZLjc2SsJ73dg0lwgdbK2j
        hSH73t+5pGGMcDcByMVRvvwW03rYlCMKonD5R3sddR0N9pGDZotJlBpGxHj0FojS
        Jw/PnVu8HuarrSah8QDLGmaSVOzKtpCaPaEg2HqoTt9mG0GLK9UJ/uYiV3vGyRH7
        opRB3vlReaL2hY3et+CqDGzTMDrBbc/M249mRmKgurHZFF5Pdmb9DGGcLuZKa7Uq
        FLHiKvl3eo/iwy1K9W9s2bG1VQOl4fYPiBhfUFgNDcP2/5haIPerr2owMGf4O0kj
        cJ0KwSNui2OEnePmaht/MYi/wl9ZsRtYyXlv1NsCAwEAAaOBtjCBszAdBgNVHQ4E
        FgQU63dspjRyaZNwyAURajyyM0ASIEswHwYDVR0jBBgwFoAU63dspjRyaZNwyAUR
        ajyyM0ASIEswNAYDVR0RBC0wK4Ypc3BpZmZlOi8vZXhhbXBsZS5vcmcvbnMvZGVm
        YXVsdC9zYS9jbGllbnQwDAYDVR0TAQH/BAIwADAOBgNVHQ8BAf8EBAMCB4AwHQYD
        VR0lBBYwFAYIKwYBBQUHAwIGCCsGAQUFBwMBMA0GCSqGSIb3DQEBCwUAA4IBAQAg
        1WAv5NFHDk/oGOYFQYAaArss02gmHecu6qk8BjZlBx5l8X+ZP9XP4RFN/y1q8FQ6
        nTaxoI5EvBCHHD/RwqO6VzqJoaRvS4gbBuFJj3PeVt3GnAYimBFCkU1z9ckIF4Pb
        AMFiL2NemMcrwZ14FJiH2S+PoBXfJnVQTU912O46kH5rnH53TgNoybg+duCtx46w
        IXPTMNrejCQFvrlag1vSyhybTLqaNf20+0eA4u9CNb2n4jUf2JL7ffOyEKoyXuuh
        FubCM2PL2iXOqdnlDBtza/WP8oh6l55p38nnkApuo068QRsbTwrmMWfPRFSpctnX
        HKiLgbaVBM1fvPmoSdLy
        -----END CERTIFICATE-----
        """;

    [Fact]
    public async Task CertificateProfilesProduceConfiguredProxyEvidence()
    {
        var certificate = PercentEncode(Pem);
        await AssertAvailable(SpiffePeerIdentityProviders.Nginx(["example.org"], ["127.0.0.1"]),
            Headers("X-SSL-Client-Cert", certificate, "X-SSL-Client-Verify", "SUCCESS"), "nginx_mtls");
        await AssertAvailable(SpiffePeerIdentityProviders.AzureApplicationGateway(["example.org"], ["127.0.0.1"]),
            Headers("X-Client-Certificate", certificate, "X-Client-Certificate-Verification", "SUCCESS"),
            "azure_application_gateway_mtls_strict");
        await AssertAvailable(SpiffePeerIdentityProviders.AwsAlb(["example.org"], ["127.0.0.1"]),
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["X-Amzn-Mtls-Clientcert-Leaf"] = [certificate],
            }, "aws_alb_mtls_verify");
    }

    [Fact]
    public async Task CertificateProfilesFailClosedAtTrustAndHeaderBoundaries()
    {
        var provider = SpiffePeerIdentityProviders.Nginx(["example.org"], ["127.0.0.1"]);
        var valid = Headers("X-SSL-Client-Cert", PercentEncode(Pem), "X-SSL-Client-Verify", "SUCCESS");
        Assert.Equal(PeerIdentityStatus.UntrustedProxy, (await provider.ResolveAsync(Context("127.0.0.2", valid))).Status);
        Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1",
            Headers("X-SSL-Client-Cert", PercentEncode(Pem), "X-SSL-Client-Verify", "FAILED")))).Status);
        Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["X-SSL-Client-Cert"] = [PercentEncode(Pem), PercentEncode(Pem)],
                ["X-SSL-Client-Verify"] = ["SUCCESS"],
            }))).Status);
        Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1",
            Headers("X-SSL-Client-Cert", PercentEncode(Pem) + ",duplicate",
                "X-SSL-Client-Verify", "SUCCESS")))).Status);
    }

    [Fact]
    public async Task GcpRequiresAllFrontendMtlsSignalsAndCanonicalId()
    {
        var provider = SpiffePeerIdentityProviders.GcpLoadBalancer(["example.org"], ["127.0.0.1"]);
        var valid = new Dictionary<string, IReadOnlyList<string>>
        {
            ["X-Client-Cert-Present"] = ["true"],
            ["X-Client-Cert-Chain-Verified"] = ["true"],
            ["X-Client-Cert-Spiffe-Id"] = ["spiffe://example.org/ns/default/sa/client"],
        };
        await AssertAvailable(provider, valid, "gcp_load_balancer_mtls");
        var invalid = valid.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        invalid["X-Client-Cert-Chain-Verified"] = ["false"];
        Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1", invalid))).Status);
        Assert.Equal(PeerIdentityStatus.NoMatch, (await provider.ResolveAsync(Context("127.0.0.1",
            new Dictionary<string, IReadOnlyList<string>> { ["X-Client-Cert-Present"] = ["false"] }))).Status);
        invalid["X-Client-Cert-Chain-Verified"] = ["true"];
        invalid["X-Client-Cert-Spiffe-Id"] = ["spiffe://example.org/a%2Fb"];
        Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1", invalid))).Status);
    }

    [Fact]
    public async Task EnvoyRequiresOneSanitizeSetElementUriAndHash()
    {
        var provider = SpiffePeerIdentityProviders.EnvoyXfcc(["example.org"], ["127.0.0.1"]);
        var valid = "By=spiffe://mesh.example/proxy;Hash=" + new string('a', 64)
            + ";URI=spiffe://example.org/ns/default/sa/client";
        await AssertAvailable(provider,
            new Dictionary<string, IReadOnlyList<string>> { ["X-Forwarded-Client-Cert"] = [valid] },
            "envoy_xfcc_sanitize_set");
        foreach (var invalid in new[]
        {
            valid + ",Hash=" + new string('b', 64) + ";URI=spiffe://example.org/other",
            "Hash=" + new string('a', 64) + ";Hash=" + new string('b', 64) + ";URI=spiffe://example.org/client",
            "Hash=" + new string('a', 64) + ";URI=spiffe://other.org/client",
            "Unknown=x;Hash=" + new string('a', 64) + ";URI=spiffe://example.org/client",
            "Hash=abc;URI=spiffe://example.org/client",
        })
            Assert.Equal(PeerIdentityStatus.Invalid, (await provider.ResolveAsync(Context("127.0.0.1",
                new Dictionary<string, IReadOnlyList<string>> { ["X-Forwarded-Client-Cert"] = [invalid] }))).Status);
    }

    [Fact]
    public void SpiffeValidationRejectsAliasesAndInvalidConfiguration()
    {
        Assert.Equal("example.org", SpiffePeerIdentityProviders.ValidateSpiffeId(
            "spiffe://example.org/ns/default/sa/client", ["example.org"]));
        foreach (var invalid in new[]
        {
            "spiffe://example.org/a%2Fb", "spiffe://example.org/a//b", "spiffe://example.org/a/../b",
            "spiffe://example.org/a/", "spiffe://Example.org/a", "spiffe://example.org:443/a",
            "spiffe://example.org/a?x=1",
        })
            Assert.Throws<ArgumentException>(() =>
                SpiffePeerIdentityProviders.ValidateSpiffeId(invalid, ["example.org"]));
        Assert.Throws<ArgumentException>(() => SpiffePeerIdentityProviders.Nginx([], ["127.0.0.1"]));
    }

    [Fact]
    public async Task SpiffeTrustedProxyAddressesAreExactAndNormalized()
    {
        var certificate = PercentEncode(Pem);
        var ipv6 = SpiffePeerIdentityProviders.Nginx(
            ["example.org"], ["0:0:0:0:0:0:0:1"]);
        var ipv6Result = await ipv6.ResolveAsync(Context("::1",
            Headers("X-SSL-Client-Cert", certificate, "X-SSL-Client-Verify", "SUCCESS")));
        Assert.Equal(PeerIdentityStatus.Available, ipv6Result.Status);

        var mapped = SpiffePeerIdentityProviders.GcpLoadBalancer(
            ["example.org"], ["::ffff:127.0.0.1"]);
        var mappedResult = await mapped.ResolveAsync(Context("127.0.0.1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["X-Client-Cert-Present"] = ["true"],
                ["X-Client-Cert-Chain-Verified"] = ["true"],
                ["X-Client-Cert-Spiffe-Id"] = ["spiffe://example.org/ns/default/sa/client"],
            }));
        Assert.Equal(PeerIdentityStatus.Available, mappedResult.Status);

        foreach (var invalid in new[]
        {
            "proxy.example", "127.0.0.1/32", "127.0.0.1:443", "[::1]:443", "127.1",
        })
            Assert.Throws<ArgumentException>(() =>
                SpiffePeerIdentityProviders.Nginx(["example.org"], [invalid]));
        Assert.Throws<ArgumentException>(() => SpiffePeerIdentityProviders.Nginx(
            ["example.org"], ["::1", "0:0:0:0:0:0:0:1"]));
        Assert.Throws<ArgumentException>(() => SpiffePeerIdentityProviders.Nginx(
            ["example.org"], ["127.0.0.1", "::ffff:127.0.0.1"]));
    }

    private static async Task AssertAvailable(IPeerIdentityProvider provider,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string evidenceSource)
    {
        var result = await provider.ResolveAsync(Context("127.0.0.1", headers));
        Assert.Equal(PeerIdentityStatus.Available, result.Status);
        var identity = Assert.Single(result.Identities);
        Assert.Equal("spiffe://example.org/ns/default/sa/client", identity.SubjectKey);
        Assert.Equal(evidenceSource, identity.EvidenceSource);
        Assert.Equal(IdentityAssurance.ConfiguredProxy, identity.Assurance);
    }

    private static PeerResolutionContext Context(string immediatePeer,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers) =>
        new("http", immediatePeer, "client", headers: headers);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Headers(
        string firstName, string firstValue, string secondName, string secondValue) =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [firstName] = [firstValue],
            [secondName] = [secondValue],
        };

    private static string PercentEncode(string value)
    {
        var encoded = new StringBuilder();
        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            if (octet is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' || "-._~".Contains((char)octet))
                encoded.Append((char)octet);
            else
                encoded.Append('%').Append(octet.ToString("X2"));
        }
        return encoded.ToString();
    }
}
