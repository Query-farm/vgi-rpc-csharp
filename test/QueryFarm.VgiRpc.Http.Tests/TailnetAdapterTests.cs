using System.Text.Json;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Tailnet;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class TailnetAdapterTests
{
    [Fact]
    public void TcpSnapshotRequiresIssuerTargetAndAuthBinding()
    {
        var snapshot = Snapshot(authenticated: true, issuer: "tailnet:test", targetKind: "destination_ip");
        TailnetEvidenceValidator.ValidateSnapshot(snapshot, new TailnetSnapshotExpectations(
            "tailnet:test", "localapi", "local_daemon", "tagged_node", "stable",
            "queryfarm.dev/cap/vgi", "tag:worker", "destination_ip", true, false));

        Assert.Throws<InvalidDataException>(() => TailnetEvidenceValidator.ValidateSnapshot(
            snapshot,
            new TailnetSnapshotExpectations("tailnet:wrong", "localapi", "local_daemon",
                "tagged_node", "stable", "queryfarm.dev/cap/vgi", "tag:worker",
                "destination_ip", true, false)));
        Assert.Throws<InvalidDataException>(() => TailnetEvidenceValidator.ValidateSnapshot(
            snapshot.Replace("\"peer_evidence_binding_present\":true", "\"peer_evidence_binding_present\":false"),
            new TailnetSnapshotExpectations("tailnet:test", "localapi", "local_daemon",
                "tagged_node", "stable", "queryfarm.dev/cap/vgi", "tag:worker",
                "destination_ip", true, false)));
        Assert.Throws<InvalidDataException>(() => TailnetEvidenceValidator.ValidateSnapshot(
            snapshot,
            new TailnetSnapshotExpectations("tailnet:test", "localapi", "local_daemon",
                "tagged_node", "stable", "queryfarm.dev/cap/vgi", "tag:worker",
                "service", true, false)));
        Assert.Throws<InvalidDataException>(() => TailnetEvidenceValidator.ValidateSnapshot(
            snapshot.Replace("\"principal_matches_identity\":true", "\"principal_matches_identity\":false"),
            new TailnetSnapshotExpectations("tailnet:test", "localapi", "local_daemon",
                "tagged_node", "stable", "queryfarm.dev/cap/vgi", "tag:worker",
                "destination_ip", true, false)));
    }

    [Fact]
    public void HttpSnapshotRequiresAnonymousBoundServeEvidence()
    {
        TailnetEvidenceValidator.ValidateSnapshot(
            Snapshot(authenticated: false, issuer: "tailnet:test", targetKind: null),
            new TailnetSnapshotExpectations("tailnet:test", "serve_proxy", "configured_proxy",
                "unknown", "none", "queryfarm.dev/cap/vgi", null, null, false, true));
    }

    [Fact]
    public async Task HttpServiceAcceptsCapabilityOnlyEvidence()
    {
        var identity = ServeIdentity();
        var evidence = new PeerEvidenceSet([PeerIdentityResult.Available(identity)]);
        var auth = await PeerAuthenticationPolicies.Require("tailscale")(evidence, AuthContext.Anonymous);
        var service = new TailnetConformanceService(new TailnetServerExpectations(
            "tailnet:test", "queryfarm.dev/cap/vgi"));

        Assert.Equal("value", await service.EchoStringAsync("value", new StubContext(auth, evidence)));
    }

    [Fact]
    public async Task HttpServiceRejectsSpoofedServeLoginSubject()
    {
        var identity = new PeerIdentity(
            "tailscale", "serve_proxy", IdentityAssurance.ConfiguredProxy, "tailnet:test", "http",
            PeerSubjectKind.User, "login:attacker@example.com", SubjectStability.Login, true,
            capabilities: new Dictionary<string, object?> { ["queryfarm.dev/cap/vgi"] = Array.Empty<object>() },
            capabilitiesVerified: true, proxyAddress: "127.0.0.1");
        var evidence = new PeerEvidenceSet([PeerIdentityResult.Available(identity)]);
        var auth = await PeerAuthenticationPolicies.Require("tailscale")(evidence, AuthContext.Anonymous);
        var service = new TailnetConformanceService(new TailnetServerExpectations(
            "tailnet:test", "queryfarm.dev/cap/vgi"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.EchoStringAsync("value", new StubContext(auth, evidence)));
    }

    private static PeerIdentity ServeIdentity() => new(
        "tailscale", "serve_proxy", IdentityAssurance.ConfiguredProxy, "tailnet:test", "http",
        capabilities: new Dictionary<string, object?> { ["queryfarm.dev/cap/vgi"] = Array.Empty<object>() },
        capabilitiesVerified: true, proxyAddress: "127.0.0.1");

    private static string Snapshot(bool authenticated, string issuer, string? targetKind)
    {
        var evidenceSource = authenticated ? "localapi" : "serve_proxy";
        var assurance = authenticated ? "local_daemon" : "configured_proxy";
        var subjectKind = authenticated ? "tagged_node" : "unknown";
        var stability = authenticated ? "stable" : "none";
        return JsonSerializer.Serialize(new
        {
            provider_status = new { tailscale = "available" },
            identities = new object[]
            {
                new
                {
                    provider = "tailscale",
                    issuer,
                    evidence_source = evidenceSource,
                    assurance,
                    subject_kind = subjectKind,
                    subject_stability = stability,
                    subject_verified = authenticated,
                    subject_fingerprint = authenticated ? new string('a', 64) : null,
                    tags = authenticated ? new[] { "tag:worker" } : Array.Empty<string>(),
                    capability_names = new[] { "queryfarm.dev/cap/vgi" },
                    capabilities_verified = true,
                    capability_target = targetKind is null ? null : new { kind = targetKind },
                    proxy_present = !authenticated,
                },
            },
            auth = new
            {
                authenticated,
                domain = authenticated ? "tailscale" : null,
                principal_fingerprint = authenticated ? new string('b', 64) : null,
                principal_matches_identity = authenticated,
                peer_evidence_binding_present = true,
            },
        });
    }

    private sealed class StubContext(AuthContext auth, PeerEvidenceSet evidence) : ICallContext
    {
        public AuthContext Auth { get; } = auth;
        public PeerEvidenceSet PeerEvidence { get; } = evidence;
        public void EmitLog(QueryFarm.VgiRpc.Logging.VgiLogLevel level, string message,
            IReadOnlyDictionary<string, object?>? extra = null)
        {
        }
    }
}
