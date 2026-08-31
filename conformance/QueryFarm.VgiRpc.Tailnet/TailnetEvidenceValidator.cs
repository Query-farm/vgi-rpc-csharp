using System.Text.Json;
using QueryFarm.VgiRpc.Identity;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Tailnet;

public static class TailnetEvidenceValidator
{
    public static void ValidateSnapshot(string snapshot, TailnetSnapshotExpectations expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        ArgumentNullException.ThrowIfNull(expected);
        using var document = JsonDocument.Parse(snapshot, new JsonDocumentOptions { MaxDepth = 16 });
        var root = RequireObject(document.RootElement, "snapshot");
        var statuses = RequireObject(Required(root, "provider_status"), "provider_status");
        Equal("available", RequiredString(statuses, "tailscale"), "provider_status.tailscale");

        var identities = Required(root, "identities");
        if (identities.ValueKind != JsonValueKind.Array || identities.GetArrayLength() != 1)
            throw new InvalidDataException("snapshot must contain exactly one peer identity");
        var identity = RequireObject(identities[0], "identity");
        Equal("tailscale", RequiredString(identity, "provider"), "identity.provider");
        Equal(expected.Issuer, RequiredString(identity, "issuer"), "identity.issuer");
        Equal(expected.EvidenceSource, RequiredString(identity, "evidence_source"), "identity.evidence_source");
        Equal(expected.Assurance, RequiredString(identity, "assurance"), "identity.assurance");
        Equal(expected.SubjectKind, RequiredString(identity, "subject_kind"), "identity.subject_kind");
        Equal(expected.SubjectStability, RequiredString(identity, "subject_stability"), "identity.subject_stability");
        if (RequiredBoolean(identity, "subject_verified") != expected.Authenticated)
            throw new InvalidDataException("identity.subject_verified does not match expectation");
        if (!RequiredBoolean(identity, "capabilities_verified"))
            throw new InvalidDataException("identity capabilities are not verified");
        if (identity.TryGetProperty("subject_key", out _) || identity.TryGetProperty("principal", out _))
            throw new InvalidDataException("snapshot exposes a raw peer principal");
        if (expected.Authenticated)
            RequireSha256Fingerprint(identity, "subject_fingerprint");
        else if (identity.TryGetProperty("subject_fingerprint", out var subjectFingerprint)
            && subjectFingerprint.ValueKind is not JsonValueKind.Null)
            throw new InvalidDataException("anonymous evidence unexpectedly exposes a subject fingerprint");

        var capabilityNames = Required(identity, "capability_names");
        RequireStringArrayContains(capabilityNames, expected.Capability, "identity.capability_names");
        if (expected.Tag is not null)
            RequireStringArrayContains(Required(identity, "tags"), expected.Tag, "identity.tags");
        if (expected.TargetKind is not null)
        {
            var target = RequireObject(Required(identity, "capability_target"), "identity.capability_target");
            Equal(expected.TargetKind, RequiredString(target, "kind"), "identity.capability_target.kind");
        }
        if (expected.ExpectProxy != RequiredBoolean(identity, "proxy_present"))
            throw new InvalidDataException("identity.proxy_present does not match expectation");

        var auth = RequireObject(Required(root, "auth"), "auth");
        if (auth.TryGetProperty("principal", out _))
            throw new InvalidDataException("snapshot exposes a raw authentication principal");
        if (RequiredBoolean(auth, "authenticated") != expected.Authenticated)
            throw new InvalidDataException("auth.authenticated does not match expectation");
        var bindingPresent = RequiredBoolean(auth, "peer_evidence_binding_present");
        if (!bindingPresent) throw new InvalidDataException("auth peer evidence binding is absent");
        if (expected.Authenticated)
        {
            Equal("tailscale", RequiredString(auth, "domain"), "auth.domain");
            if (!RequiredBoolean(auth, "principal_matches_identity"))
                throw new InvalidDataException("auth principal does not match the sole peer identity");
            RequireSha256Fingerprint(auth, "principal_fingerprint");
        }
        else
        {
            if (RequiredBoolean(auth, "principal_matches_identity"))
                throw new InvalidDataException("anonymous auth unexpectedly matches a peer principal");
            if (auth.TryGetProperty("principal_fingerprint", out var fingerprint)
                && fingerprint.ValueKind is not JsonValueKind.Null)
                throw new InvalidDataException("anonymous auth unexpectedly exposes a principal fingerprint");
            if (auth.TryGetProperty("domain", out var domain) && domain.ValueKind is not JsonValueKind.Null)
                throw new InvalidDataException("anonymous auth unexpectedly declares a domain");
        }
    }

    public static void ValidateServerContext(ICallContext context, TailnetServerExpectations expected)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(expected);
        if (context.PeerEvidence.Status("tailscale") != PeerIdentityStatus.Available)
            throw new UnauthorizedAccessException("Tailscale evidence is not available");
        if (context.PeerEvidence.Identities is not [var identity])
            throw new UnauthorizedAccessException("exactly one Tailscale identity is required");
        if (!StringComparer.Ordinal.Equals(identity.Provider, "tailscale")
            || !StringComparer.Ordinal.Equals(identity.Issuer, expected.Issuer)
            || !StringComparer.Ordinal.Equals(identity.EvidenceSource, expected.EvidenceSource)
            || Wire(identity.Assurance) != expected.Assurance
            || identity.SubjectKind != PeerSubjectKind.Unknown
            || identity.SubjectKey is not null
            || identity.SubjectStability != SubjectStability.None
            || identity.SubjectVerified
            || !identity.CapabilitiesVerified
            || !identity.Capabilities.ContainsKey(expected.Capability)
            || string.IsNullOrWhiteSpace(identity.ProxyAddress))
            throw new UnauthorizedAccessException("Tailnet Serve evidence does not match the qualification contract");
        var derived = PeerAuthenticationPolicies.Require("tailscale")(
            context.PeerEvidence, AuthContext.Anonymous).GetAwaiter().GetResult();
        if (!AuthMatches(context.Auth, derived))
            throw new UnauthorizedAccessException("authentication context does not match the evidence-derived policy result");
    }

    private static JsonElement Required(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result)
            ? result
            : throw new InvalidDataException($"snapshot is missing {property}");

    private static JsonElement RequireObject(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"{name} must be an object");

    private static string RequiredString(JsonElement value, string property)
    {
        var item = Required(value, property);
        return item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException($"{property} must be a non-empty string");
    }

    private static void RequireNonEmptyString(JsonElement value, string property) =>
        _ = RequiredString(value, property);

    private static void RequireSha256Fingerprint(JsonElement value, string property)
    {
        var fingerprint = RequiredString(value, property);
        if (fingerprint.Length != 64 || fingerprint.Any(character => character is not
            (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException($"{property} must be a lowercase SHA-256 hex digest");
    }

    private static bool RequiredBoolean(JsonElement value, string property)
    {
        var item = Required(value, property);
        return item.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{property} must be a boolean"),
        };
    }

    private static void RequireStringArrayContains(JsonElement value, string expected, string name)
    {
        if (value.ValueKind != JsonValueKind.Array
            || !value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                && StringComparer.Ordinal.Equals(item.GetString(), expected)))
            throw new InvalidDataException($"{name} does not contain the expected value");
    }

    private static void Equal(string expected, string actual, string name)
    {
        if (!StringComparer.Ordinal.Equals(expected, actual))
            throw new InvalidDataException($"{name} does not match expectation");
    }

    private static bool AuthMatches(AuthContext actual, AuthContext expected) =>
        StringComparer.Ordinal.Equals(actual.Domain, expected.Domain)
        && actual.Authenticated == expected.Authenticated
        && StringComparer.Ordinal.Equals(actual.Principal, expected.Principal)
        && actual.Claims.Count == expected.Claims.Count
        && expected.Claims.All(entry => actual.Claims.TryGetValue(entry.Key, out var value)
            && Equals(value, entry.Value));

    private static string Wire(IdentityAssurance assurance) => assurance switch
    {
        IdentityAssurance.ConfiguredProxy => "configured_proxy",
        IdentityAssurance.CryptographicPeer => "cryptographic_peer",
        IdentityAssurance.LocalDaemon => "local_daemon",
        _ => throw new ArgumentOutOfRangeException(nameof(assurance)),
    };
}
