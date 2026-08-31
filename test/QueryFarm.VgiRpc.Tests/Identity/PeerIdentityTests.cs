using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Server;
using System.Text.Json;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Identity;

public sealed class PeerIdentityTests
{
    private static PeerIdentity Identity(string provider = "spiffe", string subject = "spiffe://example.org/workload") =>
        new(provider, "test", IdentityAssurance.CryptographicPeer, "spiffe://example.org", "tcp",
            PeerSubjectKind.Workload, subject, SubjectStability.Stable, subjectVerified: true);

    [Fact]
    public void MatchesSharedPrincipalAndBindingVector()
    {
        var identity = Identity();
        var evidence = new PeerEvidenceSet([PeerIdentityResult.Available(identity)]);
        Assert.Equal(
            "peer/spiffe/spiffe%3A%2F%2Fexample.org/spiffe%3A%2F%2Fexample.org%2Fworkload",
            identity.CanonicalPrincipal);
        Assert.Equal(
            "948ce118ddd5f212e7bfd62e13ffdba0675397c56a43060e98656965389e5367",
            evidence.BindingDigest(["spiffe"]));
    }

    [Fact]
    public void BindingIgnoresRoutingTopologyButNotCapabilities()
    {
        static PeerIdentity WithTopology(string source, string proxy,
            IReadOnlyDictionary<string, object?>? capabilities = null) =>
            new("spiffe", "test", IdentityAssurance.CryptographicPeer, "spiffe://example.org", "tcp",
                PeerSubjectKind.Workload, "spiffe://example.org/workload", SubjectStability.Stable,
                subjectVerified: true, capabilities: capabilities, sourceAddress: source, proxyAddress: proxy);
        var first = new PeerEvidenceSet([PeerIdentityResult.Available(WithTopology("100.64.0.1:40001", "10.0.0.10"))]);
        var second = new PeerEvidenceSet([PeerIdentityResult.Available(WithTopology("100.64.0.1:49999", "10.0.0.11"))]);
        Assert.Equal(first.BindingDigest(["spiffe"]), second.BindingDigest(["spiffe"]));

        var changed = new PeerEvidenceSet([PeerIdentityResult.Available(WithTopology(
            "100.64.0.1:49999", "10.0.0.11",
            new Dictionary<string, object?> { ["query.farm/run"] = Array.Empty<object>() }))]);
        Assert.NotEqual(first.BindingDigest(["spiffe"]), changed.BindingDigest(["spiffe"]));
    }

    [Fact]
    public void DeeplySnapshotsStructuredEvidence()
    {
        var roles = new List<string> { "reader" };
        var identity = new PeerIdentity("test", "test", IdentityAssurance.LocalDaemon,
            "test://issuer", "tcp", attributes: new Dictionary<string, object?> { ["roles"] = roles });
        roles[0] = "writer";
        Assert.Equal("reader", identity.Attributes["roles"][0].GetString());
    }

    [Fact]
    public void RejectsMalformedUnicodeAndOverLimitJsonBeforeItBecomesEvidence()
    {
        Assert.Throws<ArgumentException>(() => Identity(subject: "bad\ud800subject"));
        Assert.Throws<ArgumentException>(() => new PeerIdentity(
            "test", "test", IdentityAssurance.LocalDaemon, "test://issuer", "tcp",
            attributes: new Dictionary<string, object?> { ["bad\udfffkey"] = true }));
        Assert.Throws<ArgumentException>(() => new PeerIdentity(
            "test", "test", IdentityAssurance.LocalDaemon, "test://issuer", "tcp",
            attributes: new Dictionary<string, object?> { ["oversized"] = new string('x', 65_537) }));
        Assert.Throws<ArgumentException>(() => new PeerIdentity(
            "test", "test", IdentityAssurance.LocalDaemon, "test://issuer", "tcp",
            attributes: new Dictionary<string, object?> { ["not_json"] = new Version(1, 2) }));
        Assert.Throws<ArgumentException>(() => new PeerIdentity(
            "test", "test", IdentityAssurance.LocalDaemon, "test://issuer", "tcp",
            attributes: new Dictionary<string, object?> { ["not_finite"] = double.PositiveInfinity }));
        using var duplicateDocument = JsonDocument.Parse("{\"role\":\"reader\",\"role\":\"writer\"}");
        Assert.Throws<ArgumentException>(() => new PeerIdentity(
            "test", "test", IdentityAssurance.LocalDaemon, "test://issuer", "tcp",
            attributes: new Dictionary<string, object?> { ["duplicate"] = duplicateDocument.RootElement }));
    }

    [Fact]
    public void RejectsJsonDepthAndValueCountLimits()
    {
        object? nested = "leaf";
        for (var index = 0; index < 17; index++) nested = new object?[] { nested };
        Assert.Throws<ArgumentException>(() => new PeerIdentity(
            "test", "test", IdentityAssurance.LocalDaemon, "test://issuer", "tcp",
            attributes: new Dictionary<string, object?> { ["nested"] = nested }));

        Assert.Throws<ArgumentException>(() => new PeerIdentity(
            "test", "test", IdentityAssurance.LocalDaemon, "test://issuer", "tcp",
            attributes: new Dictionary<string, object?> { ["many"] = Enumerable.Repeat<object?>(true, 4_096).ToArray() }));
    }

    [Fact]
    public async Task AnyOfIsOrderedAndRejectsAmbiguityBeforeApplicationFallback()
    {
        var ordered = new PeerEvidenceSet([
            new PeerIdentityResult("first", PeerIdentityStatus.Unavailable),
            PeerIdentityResult.Available(Identity("second")),
        ]);
        var auth = await PeerAuthenticationPolicies.AnyOf("first", "second")(ordered, AuthContext.Anonymous);
        Assert.Equal("second", auth.Domain);

        var ambiguous = new PeerEvidenceSet([
            new PeerIdentityResult("spiffe", PeerIdentityStatus.Available,
                [Identity(subject: "spiffe://example.org/one"), Identity(subject: "spiffe://example.org/two")]),
        ]);
        await Assert.ThrowsAsync<PeerIdentityRejectedException>(async () =>
            await PeerAuthenticationPolicies.AnyOf("spiffe")(
                ambiguous, new AuthContext("bearer", true, "alice")));
    }

    [Fact]
    public async Task AllOfBindsApplicationIdentity()
    {
        var evidence = new PeerEvidenceSet([PeerIdentityResult.Available(Identity())]);
        var policy = PeerAuthenticationPolicies.AllOf(["spiffe"], (_, _) => ValueTask.CompletedTask);
        var alice = await policy(evidence, new AuthContext("bearer", true, "alice"));
        var bob = await policy(evidence, new AuthContext("bearer", true, "bob"));
        Assert.NotEqual(alice.Claims["peer_evidence_binding"], bob.Claims["peer_evidence_binding"]);
    }

    [Fact]
    public async Task RequireAcceptsCapabilityOnlyEvidenceButPrimaryRejectsIt()
    {
        var capabilityOnly = new PeerIdentity("tailscale", "serve", IdentityAssurance.ConfiguredProxy,
            "tailnet:test", "http", capabilities: new Dictionary<string, object?>
            {
                ["query.farm/can-run"] = new[] { new Dictionary<string, object?> { ["worker"] = "analytics" } },
            }, capabilitiesVerified: true);
        var evidence = new PeerEvidenceSet([PeerIdentityResult.Available(capabilityOnly)]);
        var application = new AuthContext("bearer", true, "alice");
        var required = await PeerAuthenticationPolicies.Require("tailscale")(evidence, application);
        Assert.True(required.Authenticated);
        Assert.Equal("alice", required.Principal);
        await Assert.ThrowsAsync<PeerIdentityRejectedException>(async () =>
            await PeerAuthenticationPolicies.Primary("tailscale")(evidence, AuthContext.Anonymous));
    }

    [Fact]
    public void LegacyCallContextsDefaultToAnonymousAndEmptyEvidence()
    {
        ICallContext context = new StubContext();
        Assert.Same(AuthContext.Anonymous, context.Auth);
        Assert.Same(PeerEvidenceSet.Empty, context.PeerEvidence);
    }

    private sealed class StubContext : ICallContext
    {
        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) { }
    }
}
