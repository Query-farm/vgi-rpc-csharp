using System.Buffers.Binary;
using System.Collections;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Identity;

public enum PeerIdentityStatus { Off, NotApplicable, Available, Unavailable, PermissionDenied, NoMatch, Invalid, UntrustedProxy }
public enum IdentityAssurance { CryptographicPeer, LocalDaemon, ConfiguredProxy }
public enum PeerSubjectKind { User, TaggedNode, Workload, Endpoint, Unknown }
public enum SubjectStability { Stable, Login, None }

/// <summary>Immutable transport and request facts supplied to a peer identity provider.</summary>
public sealed class PeerResolutionContext
{
    public PeerResolutionContext(
        string transport,
        string? immediatePeer = null,
        string? assertedPeer = null,
        string? destinationAddress = null,
        string? authority = null,
        string? serviceName = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        DateTimeOffset? deadline = null,
        string? sourceEndpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        Transport = transport;
        ImmediatePeer = immediatePeer;
        SourceEndpoint = sourceEndpoint;
        AssertedPeer = assertedPeer;
        DestinationAddress = destinationAddress;
        Authority = authority;
        ServiceName = serviceName;
        Deadline = deadline;
        var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (string.IsNullOrWhiteSpace(name) || ContainsControl(name)) throw new ArgumentException("invalid peer-resolution header name");
            if (normalized.ContainsKey(name)) throw new PeerIdentityRejectedException("case-varied duplicate peer identity header");
            var copied = values.ToArray();
            if (copied.Any(value => value is null || ContainsControl(value))) throw new ArgumentException("invalid peer-resolution header value");
            normalized.Add(name, Array.AsReadOnly(copied));
        }
        Headers = new ReadOnlyDictionary<string, IReadOnlyList<string>>(normalized);
        Metadata = JsonSnapshot.Map(metadata);
    }

    public string Transport { get; }
    public string? ImmediatePeer { get; }
    public string? SourceEndpoint { get; }
    public string? AssertedPeer { get; }
    public string? DestinationAddress { get; }
    public string? Authority { get; }
    public string? ServiceName { get; }
    public DateTimeOffset? Deadline { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; }

    public string? Header(string name)
    {
        if (!Headers.TryGetValue(name, out var values)) return null;
        if (values.Count > 1) throw new PeerIdentityRejectedException($"duplicate peer identity header: {name}");
        return values.Count == 0 ? null : values[0];
    }

    private static bool ContainsControl(string value) => value.Any(character => character <= 0x1f || character == 0x7f);
}

/// <summary>Immutable verified or observed evidence about one transport peer.</summary>
public sealed class PeerIdentity
{
    public PeerIdentity(
        string provider,
        string evidenceSource,
        IdentityAssurance assurance,
        string issuer,
        string transport,
        PeerSubjectKind subjectKind = PeerSubjectKind.Unknown,
        string? subjectKey = null,
        SubjectStability subjectStability = SubjectStability.None,
        bool subjectVerified = false,
        IReadOnlyDictionary<string, object?>? attributes = null,
        IReadOnlyDictionary<string, object?>? capabilities = null,
        bool capabilitiesVerified = false,
        string? sourceAddress = null,
        string? proxyAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        foreach (var (name, value) in new[]
        {
            (nameof(provider), provider), (nameof(evidenceSource), evidenceSource),
            (nameof(issuer), issuer), (nameof(transport), transport),
            (nameof(subjectKey), subjectKey), (nameof(sourceAddress), sourceAddress),
            (nameof(proxyAddress), proxyAddress),
        })
            if (value is not null) JsonSnapshot.RequireWellFormed(value, name);
        if (subjectVerified && string.IsNullOrEmpty(subjectKey)) throw new ArgumentException("verified peer identity requires subjectKey");
        if (subjectKey is null && subjectStability != SubjectStability.None) throw new ArgumentException("subjectless peer identity must use None stability");
        Provider = provider;
        EvidenceSource = evidenceSource;
        Assurance = assurance;
        Issuer = issuer;
        Transport = transport;
        SubjectKind = subjectKind;
        SubjectKey = subjectKey;
        SubjectStability = subjectStability;
        SubjectVerified = subjectVerified;
        Attributes = JsonSnapshot.Map(attributes);
        Capabilities = JsonSnapshot.Map(capabilities);
        CapabilitiesVerified = capabilitiesVerified;
        SourceAddress = sourceAddress;
        ProxyAddress = proxyAddress;
    }

    public string Provider { get; }
    public string EvidenceSource { get; }
    public IdentityAssurance Assurance { get; }
    public string Issuer { get; }
    public string Transport { get; }
    public PeerSubjectKind SubjectKind { get; }
    public string? SubjectKey { get; }
    public SubjectStability SubjectStability { get; }
    public bool SubjectVerified { get; }
    public IReadOnlyDictionary<string, JsonElement> Attributes { get; }
    public IReadOnlyDictionary<string, JsonElement> Capabilities { get; }
    public bool CapabilitiesVerified { get; }
    public string? SourceAddress { get; }
    public string? ProxyAddress { get; }

    public string CanonicalPrincipal => SubjectKey is null
        ? throw new InvalidOperationException("subjectless peer evidence has no canonical principal")
        : $"peer/{Uri.EscapeDataString(Provider)}/{Uri.EscapeDataString(Issuer)}/{Uri.EscapeDataString(SubjectKey)}";
}

public sealed record PeerIdentityResult
{
    public PeerIdentityResult(string provider, PeerIdentityStatus status, IReadOnlyList<PeerIdentity>? identities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Provider = provider;
        Status = status;
        Identities = Array.AsReadOnly((identities ?? []).ToArray());
        if ((status == PeerIdentityStatus.Available) != (Identities.Count > 0)) throw new ArgumentException("only an available result may carry identities");
        if (Identities.Any(identity => identity.Provider != provider)) throw new ArgumentException("peer result provider mismatch");
    }
    public string Provider { get; }
    public PeerIdentityStatus Status { get; }
    public IReadOnlyList<PeerIdentity> Identities { get; }
    public static PeerIdentityResult Available(PeerIdentity identity) => new(identity.Provider, PeerIdentityStatus.Available, [identity]);
}

public sealed class PeerEvidenceSet
{
    public static PeerEvidenceSet Empty { get; } = new([]);
    private readonly IReadOnlyDictionary<string, PeerIdentityStatus> _statuses;

    public PeerEvidenceSet(IReadOnlyList<PeerIdentityResult> results)
    {
        var statuses = new Dictionary<string, PeerIdentityStatus>(StringComparer.Ordinal);
        var identities = new List<PeerIdentity>();
        foreach (var result in results)
        {
            if (!statuses.TryAdd(result.Provider, result.Status)) throw new ArgumentException($"duplicate peer identity provider: {result.Provider}");
            identities.AddRange(result.Identities);
        }
        _statuses = new ReadOnlyDictionary<string, PeerIdentityStatus>(statuses);
        Identities = identities.AsReadOnly();
    }

    public IReadOnlyList<PeerIdentity> Identities { get; }
    public PeerIdentityStatus Status(string provider) => _statuses.GetValueOrDefault(provider, PeerIdentityStatus.Off);
    public IReadOnlyList<PeerIdentity> ForProvider(string provider) => Identities.Where(identity => identity.Provider == provider).ToArray();
    public IReadOnlyList<PeerIdentity> EligibleSubjects(string provider) => ForProvider(provider)
        .Where(identity => identity.SubjectVerified && identity.SubjectKey is not null && identity.SubjectStability == SubjectStability.Stable).ToArray();
    public PeerIdentity UniqueVerifiedSubject(string provider) => EligibleSubjects(provider) switch
    {
        [var identity] => identity,
        _ => throw new PeerIdentityRejectedException($"provider {JsonSerializer.Serialize(provider)} did not produce one verified stable subject"),
    };

    public PeerIdentity RequireUsableProvider(string provider) => Status(provider) switch
    {
        PeerIdentityStatus.Unavailable or PeerIdentityStatus.PermissionDenied => throw new PeerIdentityUnavailableException($"peer identity provider {JsonSerializer.Serialize(provider)} is unavailable"),
        PeerIdentityStatus.Invalid or PeerIdentityStatus.UntrustedProxy => throw new PeerIdentityRejectedException($"peer identity provider {JsonSerializer.Serialize(provider)} rejected evidence"),
        _ => UniqueVerifiedSubject(provider),
    };

    public IReadOnlyList<PeerIdentity> RequireAvailableProvider(string provider)
    {
        switch (Status(provider))
        {
            case PeerIdentityStatus.Unavailable or PeerIdentityStatus.PermissionDenied:
                throw new PeerIdentityUnavailableException($"peer identity provider {JsonSerializer.Serialize(provider)} is unavailable");
            case PeerIdentityStatus.Invalid or PeerIdentityStatus.UntrustedProxy:
                throw new PeerIdentityRejectedException($"peer identity provider {JsonSerializer.Serialize(provider)} rejected evidence");
            case PeerIdentityStatus.Available when ForProvider(provider) is { Count: > 0 } identities:
                return identities;
            default:
                throw new PeerIdentityRejectedException($"peer identity provider {JsonSerializer.Serialize(provider)} did not produce evidence");
        }
    }

    public string BindingDigest(IEnumerable<string> providers, AuthContext? applicationAuth = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var provider in providers.Distinct(StringComparer.Ordinal).Order(Utf8Comparer.Instance))
        {
            Add(hash, provider);
            Add(hash, Wire(Status(provider)));
            foreach (var row in ForProvider(provider).Select(Fields).Order(new FieldListComparer()))
            {
                foreach (var field in row) Add(hash, field);
            }
        }
        if (applicationAuth is not null)
        {
            Add(hash, "application_auth");
            Add(hash, applicationAuth.Domain);
            Add(hash, applicationAuth.Principal ?? "");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string[] Fields(PeerIdentity identity) =>
    [
        identity.Provider, identity.Issuer, identity.SubjectKey ?? "", Wire(identity.Assurance),
        identity.EvidenceSource, identity.Transport, Wire(identity.SubjectKind), Wire(identity.SubjectStability),
        identity.SubjectVerified.ToString().ToLowerInvariant(), identity.CapabilitiesVerified.ToString().ToLowerInvariant(),
        // Routing topology is audit evidence, not authorization evidence. Keep
        // empty framing fields for compatibility with the original digest
        // vector without binding state to an ephemeral port or proxy replica.
        "", "", JsonSnapshot.Canonical(identity.Attributes),
        JsonSnapshot.Canonical(identity.Capabilities),
    ];

    private static void Add(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private sealed class FieldListComparer : IComparer<string[]>
    {
        public int Compare(string[]? left, string[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            for (var index = 0; index < left.Length; index++)
            {
                var comparison = Utf8Comparer.Instance.Compare(left[index], right[index]);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }

    private static string Wire(PeerIdentityStatus value) => value switch
    {
        PeerIdentityStatus.NotApplicable => "not_applicable", PeerIdentityStatus.PermissionDenied => "permission_denied",
        PeerIdentityStatus.NoMatch => "no_match", PeerIdentityStatus.UntrustedProxy => "untrusted_proxy",
        _ => value.ToString().ToLowerInvariant(),
    };
    private static string Wire(IdentityAssurance value) => value switch
    {
        IdentityAssurance.CryptographicPeer => "cryptographic_peer", IdentityAssurance.LocalDaemon => "local_daemon",
        IdentityAssurance.ConfiguredProxy => "configured_proxy", _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string Wire(PeerSubjectKind value) => value == PeerSubjectKind.TaggedNode ? "tagged_node" : value.ToString().ToLowerInvariant();
    private static string Wire(SubjectStability value) => value.ToString().ToLowerInvariant();
}

public interface IPeerIdentityProvider
{
    string Provider { get; }
    ValueTask<PeerIdentityResult> ResolveAsync(PeerResolutionContext context, CancellationToken cancellationToken = default);
}

public delegate ValueTask<AuthContext> PeerAuthenticationPolicy(PeerEvidenceSet evidence, AuthContext existingAuth);
public delegate ValueTask PeerIdentityLinker(AuthContext applicationAuth, IReadOnlyDictionary<string, PeerIdentity> identities);

/// <summary>Built-in provider-neutral authentication composition policies.</summary>
public static class PeerAuthenticationPolicies
{
    public static ValueTask<AuthContext> Observe(PeerEvidenceSet evidence, AuthContext existingAuth) =>
        ValueTask.FromResult(existingAuth);

    public static PeerAuthenticationPolicy Require(string provider) => (evidence, auth) =>
    {
        evidence.RequireAvailableProvider(provider);
        return ValueTask.FromResult(WithBinding(auth, evidence.BindingDigest([provider])));
    };

    public static PeerAuthenticationPolicy Primary(string provider) => (evidence, _) =>
    {
        var identity = evidence.RequireUsableProvider(provider);
        return ValueTask.FromResult(new AuthContext(provider, true, identity.CanonicalPrincipal,
            new Dictionary<string, object?>
            {
                ["issuer"] = identity.Issuer,
                ["subject_kind"] = Wire(identity.SubjectKind),
                ["assurance"] = Wire(identity.Assurance),
                ["evidence_source"] = identity.EvidenceSource,
                ["subject"] = identity.SubjectKey,
                ["peer_evidence_binding"] = evidence.BindingDigest([provider]),
            }));
    };

    public static PeerAuthenticationPolicy AnyOf(params string[] providers)
    {
        if (providers.Length == 0) throw new ArgumentException("at least one provider is required", nameof(providers));
        return async (evidence, auth) =>
        {
            foreach (var provider in providers)
            {
                if (evidence.Status(provider) is PeerIdentityStatus.Invalid or PeerIdentityStatus.UntrustedProxy)
                    throw new PeerIdentityRejectedException($"peer identity provider {JsonSerializer.Serialize(provider)} rejected evidence");
                if (evidence.EligibleSubjects(provider).Count > 1)
                    throw new PeerIdentityRejectedException($"peer identity provider {JsonSerializer.Serialize(provider)} produced ambiguous subjects");
            }
            if (auth.Authenticated) return auth;
            foreach (var provider in providers)
            {
                if (evidence.Status(provider) == PeerIdentityStatus.Available && evidence.EligibleSubjects(provider).Count == 1)
                    return await Primary(provider)(evidence, auth).ConfigureAwait(false);
            }
            if (providers.Any(provider => evidence.Status(provider) is PeerIdentityStatus.Unavailable or PeerIdentityStatus.PermissionDenied))
                throw new PeerIdentityUnavailableException("no usable authentication factor; a peer provider is unavailable");
            throw new PeerIdentityRejectedException("no configured provider produced a verified subject");
        };
    }

    public static PeerAuthenticationPolicy AllOf(
        IReadOnlyList<string> providers,
        PeerIdentityLinker linker,
        string? principalProvider = null)
    {
        if (providers.Count == 0) throw new ArgumentException("at least one provider is required", nameof(providers));
        principalProvider ??= providers[0];
        if (!providers.Contains(principalProvider, StringComparer.Ordinal))
            throw new ArgumentException("principalProvider must be one of providers", nameof(principalProvider));
        return async (evidence, auth) =>
        {
            if (!auth.Authenticated) throw new PeerIdentityRejectedException("all-of requires application authentication");
            var identities = new ReadOnlyDictionary<string, PeerIdentity>(providers.ToDictionary(
                provider => provider, evidence.RequireUsableProvider, StringComparer.Ordinal));
            await linker(auth, identities).ConfigureAwait(false);
            var primary = identities[principalProvider];
            return new AuthContext(principalProvider, true, primary.CanonicalPrincipal,
                new Dictionary<string, object?>
                {
                    ["issuer"] = primary.Issuer,
                    ["subject_kind"] = Wire(primary.SubjectKind),
                    ["assurance"] = Wire(primary.Assurance),
                    ["evidence_source"] = primary.EvidenceSource,
                    ["subject"] = primary.SubjectKey,
                    ["application_domain"] = auth.Domain,
                    ["application_principal"] = auth.Principal ?? "",
                    ["peer_evidence_binding"] = evidence.BindingDigest(providers, auth),
                });
        };
    }

    private static AuthContext WithBinding(AuthContext auth, string binding)
    {
        var claims = new Dictionary<string, object?>(auth.Claims) { ["peer_evidence_binding"] = binding };
        return new AuthContext(auth.Domain, auth.Authenticated, auth.Principal, claims);
    }

    private static string Wire(IdentityAssurance value) => value switch
    {
        IdentityAssurance.CryptographicPeer => "cryptographic_peer",
        IdentityAssurance.LocalDaemon => "local_daemon",
        IdentityAssurance.ConfiguredProxy => "configured_proxy",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string Wire(PeerSubjectKind value) => value == PeerSubjectKind.TaggedNode
        ? "tagged_node"
        : value.ToString().ToLowerInvariant();
}

public sealed class PeerIdentityRejectedException(string message) : UnauthorizedAccessException(message);
public sealed class PeerIdentityUnavailableException(string message, int retryAfterSeconds = 5) : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

internal static class JsonSnapshot
{
    private const int MaxBytes = 65_536;
    private const int MaxDepth = 16;
    private const int MaxValues = 4_096;

    public static IReadOnlyDictionary<string, JsonElement> Map(IReadOnlyDictionary<string, object?>? value)
    {
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var count = 1;
        var active = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (key, item) in value ?? new Dictionary<string, object?>())
        {
            RequireWellFormed(key, "peer evidence object key");
            ValidateSource(item, 1, ref count, active);
            copy.Add(key, JsonSerializer.SerializeToElement(item).Clone());
        }
        var snapshot = new ReadOnlyDictionary<string, JsonElement>(copy);
        if (Encoding.UTF8.GetByteCount(Canonical(snapshot)) > MaxBytes)
            throw new ArgumentException("peer evidence exceeds maximum JSON byte size");
        return snapshot;
    }

    public static string Canonical(IReadOnlyDictionary<string, JsonElement> value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, item) in value.OrderBy(pair => pair.Key, Utf8Comparer.Instance))
            {
                writer.WritePropertyName(key);
                WriteCanonical(writer, item);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void ValidateElement(JsonElement value, int depth, ref int count)
    {
        if (depth > MaxDepth) throw new ArgumentException("peer evidence exceeds maximum JSON depth");
        if (++count > MaxValues) throw new ArgumentException("peer evidence exceeds maximum JSON value count");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                RequireWellFormed(property.Name, "peer evidence object key");
                if (!names.Add(property.Name)) throw new ArgumentException("peer evidence contains a duplicate JSON object key");
                ValidateElement(property.Value, depth + 1, ref count);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateElement(item, depth + 1, ref count);
        else if (value.ValueKind == JsonValueKind.String)
            RequireWellFormed(value.GetString()!, "peer evidence string");
        else if (value.ValueKind is JsonValueKind.Undefined)
            throw new ArgumentException("peer evidence must contain only JSON-compatible values");
    }

    private static void ValidateSource(object? value, int depth, ref int count, HashSet<object> active)
    {
        if (depth > MaxDepth) throw new ArgumentException("peer evidence exceeds maximum JSON depth");
        if (++count > MaxValues) throw new ArgumentException("peer evidence exceeds maximum JSON value count");
        switch (value)
        {
            case null or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                return;
            case float number when float.IsFinite(number):
                return;
            case double number when double.IsFinite(number):
                return;
            case float or double:
                throw new ArgumentException("JSON numbers must be finite");
            case string text:
                RequireWellFormed(text, "peer evidence string");
                if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
                    throw new ArgumentException("peer evidence exceeds maximum JSON byte size");
                return;
            case JsonElement element:
                // The element has already been parsed as JSON, but it still
                // participates in this map's shared depth/value budget.
                count--;
                ValidateElement(element, depth, ref count);
                return;
        }

        if (!active.Add(value)) throw new ArgumentException("peer evidence must not contain cycles");
        try
        {
            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                foreach (var (key, item) in readOnlyDictionary)
                {
                    RequireWellFormed(key, "peer evidence object key");
                    ValidateSource(item, depth + 1, ref count, active);
                }
                return;
            }
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                        throw new ArgumentException("JSON object keys must be strings");
                    RequireWellFormed(key, "peer evidence object key");
                    ValidateSource(entry.Value, depth + 1, ref count, active);
                }
                return;
            }
            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence) ValidateSource(item, depth + 1, ref count, active);
                return;
            }
            throw new ArgumentException("peer evidence must contain only JSON-compatible values");
        }
        finally
        {
            active.Remove(value);
        }
    }

    public static void RequireWellFormed(string value, string field)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ArgumentException($"{field} contains an unpaired surrogate");
                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
                throw new ArgumentException($"{field} contains an unpaired surrogate");
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, Utf8Comparer.Instance))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

internal sealed class Utf8Comparer : IComparer<string>
{
    public static Utf8Comparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        return Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
    }
}
