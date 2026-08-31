using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using QueryFarm.VgiRpc.Identity;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Trusted-HTTP-proxy SPIFFE evidence providers for Envoy, nginx, and the major cloud load
/// balancers. Every provider requires exact immediate-peer values and returns
/// <see cref="IdentityAssurance.ConfiguredProxy"/> evidence; the proxy must replace all configured
/// identity headers and the backend must be unreachable around it.
/// </summary>
public static partial class SpiffePeerIdentityProviders
{
    private const string ProviderName = "spiffe";
    private const int DefaultMaxHeaderBytes = 16_384;
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);
    private static readonly HashSet<string> s_xfccFields =
        ["by", "hash", "cert", "chain", "subject", "uri", "dns", "issuer"];
    private static readonly HashSet<string> s_xfccMultiFields = ["by", "uri", "dns"];
    private static readonly HashSet<string> s_xfccPercentFields = ["by", "uri", "cert", "chain"];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]{0,253}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex TrustDomainRegex();

    [GeneratedRegex("^/(?:[A-Za-z0-9._-]+)(?:/[A-Za-z0-9._-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex XfccKeyRegex();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    /// <summary>Strict X.509-SVID header provider with a positive verification header.</summary>
    public static IPeerIdentityProvider X509Header(
        IEnumerable<string> trustDomains,
        IEnumerable<string> trustedProxyAddresses,
        string certificateHeader,
        string verificationHeader,
        string verificationValue = "true",
        int maxHeaderBytes = DefaultMaxHeaderBytes)
    {
        var config = ValidateConfig(trustDomains, trustedProxyAddresses);
        RequireHeader(certificateHeader, nameof(certificateHeader));
        RequireHeader(verificationHeader, nameof(verificationHeader));
        if (certificateHeader.Equals(verificationHeader, StringComparison.OrdinalIgnoreCase)
            || ContainsControl(verificationValue) || maxHeaderBytes <= 0)
            throw new ArgumentException("distinct certificate/verification headers and a positive size limit are required");
        return CertificateProvider(config, certificateHeader, verificationHeader, verificationValue,
            maxHeaderBytes, "verified_certificate_header");
    }

    /// <summary>nginx mTLS evidence using its escaped certificate and verification variables.</summary>
    public static IPeerIdentityProvider Nginx(
        IEnumerable<string> trustDomains, IEnumerable<string> trustedProxyAddresses,
        string certificateHeader = "X-SSL-Client-Cert",
        string verificationHeader = "X-SSL-Client-Verify",
        int maxHeaderBytes = DefaultMaxHeaderBytes) =>
        NamedCertificate(trustDomains, trustedProxyAddresses, certificateHeader,
            verificationHeader, "SUCCESS", "nginx_mtls", maxHeaderBytes);

    /// <summary>Azure Application Gateway strict-mode mTLS server-variable evidence.</summary>
    public static IPeerIdentityProvider AzureApplicationGateway(
        IEnumerable<string> trustDomains, IEnumerable<string> trustedProxyAddresses,
        string certificateHeader = "X-Client-Certificate",
        string verificationHeader = "X-Client-Certificate-Verification",
        int maxHeaderBytes = DefaultMaxHeaderBytes) =>
        NamedCertificate(trustDomains, trustedProxyAddresses, certificateHeader,
            verificationHeader, "SUCCESS", "azure_application_gateway_mtls_strict", maxHeaderBytes);

    /// <summary>
    /// AWS ALB verify-mode evidence. ALB has no per-request verified boolean, so listener verify
    /// mode, header replacement, backend isolation, and the ALB trust store are operator-enforced
    /// parts of this trust boundary. Passthrough mode is not valid for this adapter.
    /// </summary>
    public static IPeerIdentityProvider AwsAlb(
        IEnumerable<string> trustDomains, IEnumerable<string> trustedProxyAddresses,
        string leafHeader = "X-Amzn-Mtls-Clientcert-Leaf",
        int maxHeaderBytes = DefaultMaxHeaderBytes)
    {
        var config = ValidateConfig(trustDomains, trustedProxyAddresses);
        RequireHeader(leafHeader, nameof(leafHeader));
        if (maxHeaderBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxHeaderBytes));
        return CertificateProvider(config, leafHeader, null, "", maxHeaderBytes, "aws_alb_mtls_verify");
    }

    /// <summary>GCP Application Load Balancer frontend-mTLS custom-header evidence.</summary>
    public static IPeerIdentityProvider GcpLoadBalancer(
        IEnumerable<string> trustDomains, IEnumerable<string> trustedProxyAddresses,
        string spiffeIdHeader = "X-Client-Cert-Spiffe-Id",
        string presentHeader = "X-Client-Cert-Present",
        string chainVerifiedHeader = "X-Client-Cert-Chain-Verified",
        string errorHeader = "X-Client-Cert-Error")
    {
        var config = ValidateConfig(trustDomains, trustedProxyAddresses);
        var headers = new[] { spiffeIdHeader, presentHeader, chainVerifiedHeader, errorHeader };
        foreach (var header in headers) RequireHeader(header, "GCP header");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new ArgumentException("GCP mTLS header names must be distinct");
        return new DelegateProvider(context =>
        {
            if (!IsTrustedProxy(config, context.ImmediatePeer)) return Result(PeerIdentityStatus.UntrustedProxy);
            try
            {
                var present = context.Header(presentHeader);
                var verified = context.Header(chainVerifiedHeader);
                var spiffeId = context.Header(spiffeIdHeader);
                var error = context.Header(errorHeader);
                if (present == "false" && (verified is null or "false") && spiffeId is null)
                    return Result(PeerIdentityStatus.NoMatch);
                if (present != "true" || verified != "true" || !string.IsNullOrEmpty(error) || spiffeId is null)
                    return Result(PeerIdentityStatus.Invalid);
                var id = ParseSpiffeId(spiffeId, config.Domains);
                return PeerIdentityResult.Available(Identity(id, "gcp_load_balancer_mtls", context,
                    new Dictionary<string, object?>
                    {
                        ["client_certificate_present"] = true,
                        ["client_certificate_chain_verified"] = true,
                    }));
            }
            catch (Exception exception) when (IsInvalidEvidence(exception))
            {
                return Result(PeerIdentityStatus.Invalid);
            }
        });
    }

    /// <summary>Strict Envoy SANITIZE_SET text-format XFCC evidence.</summary>
    public static IPeerIdentityProvider EnvoyXfcc(
        IEnumerable<string> trustDomains, IEnumerable<string> trustedProxyAddresses,
        string header = "X-Forwarded-Client-Cert",
        int maxHeaderBytes = DefaultMaxHeaderBytes)
    {
        var config = ValidateConfig(trustDomains, trustedProxyAddresses);
        RequireHeader(header, nameof(header));
        if (maxHeaderBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxHeaderBytes));
        return new DelegateProvider(context =>
        {
            if (!IsTrustedProxy(config, context.ImmediatePeer)) return Result(PeerIdentityStatus.UntrustedProxy);
            try
            {
                var raw = context.Header(header);
                if (raw is null) return Result(PeerIdentityStatus.NoMatch);
                var fields = ParseSingleXfcc(raw, maxHeaderBytes);
                var uris = fields.GetValueOrDefault("uri") ?? [];
                var hashes = fields.GetValueOrDefault("hash") ?? [];
                if (uris.Count != 1 || hashes.Count != 1 || !Sha256Regex().IsMatch(hashes[0]))
                    return Result(PeerIdentityStatus.Invalid);
                var id = ParseSpiffeId(uris[0], config.Domains);
                var attributes = new Dictionary<string, object?>
                {
                    ["certificate_sha256"] = hashes[0].ToLowerInvariant(),
                };
                if (fields.TryGetValue("by", out var by)) attributes["proxy_identities"] = by.ToArray();
                return PeerIdentityResult.Available(Identity(id, "envoy_xfcc_sanitize_set", context, attributes));
            }
            catch (Exception exception) when (IsInvalidEvidence(exception))
            {
                return Result(PeerIdentityStatus.Invalid);
            }
        });
    }

    /// <summary>Validate a canonical workload SPIFFE ID and return its trust domain.</summary>
    public static string ValidateSpiffeId(string value, IEnumerable<string> trustDomains) =>
        ParseSpiffeId(value, new HashSet<string>(trustDomains, StringComparer.Ordinal)).TrustDomain;

    private static IPeerIdentityProvider NamedCertificate(
        IEnumerable<string> domains, IEnumerable<string> proxies, string certificateHeader,
        string verificationHeader, string verificationValue, string evidenceSource, int maxHeaderBytes)
    {
        var config = ValidateConfig(domains, proxies);
        RequireHeader(certificateHeader, nameof(certificateHeader));
        RequireHeader(verificationHeader, nameof(verificationHeader));
        if (certificateHeader.Equals(verificationHeader, StringComparison.OrdinalIgnoreCase)
            || maxHeaderBytes <= 0)
            throw new ArgumentException("distinct headers and a positive maxHeaderBytes are required");
        return CertificateProvider(config, certificateHeader, verificationHeader, verificationValue,
            maxHeaderBytes, evidenceSource);
    }

    private static IPeerIdentityProvider CertificateProvider(
        Config config, string certificateHeader, string? verificationHeader,
        string verificationValue, int maxHeaderBytes, string evidenceSource) =>
        new DelegateProvider(context =>
        {
            if (!IsTrustedProxy(config, context.ImmediatePeer)) return Result(PeerIdentityStatus.UntrustedProxy);
            try
            {
                var raw = context.Header(certificateHeader);
                if (raw is null) return Result(PeerIdentityStatus.NoMatch);
                if (!IsAscii(raw) || Encoding.UTF8.GetByteCount(raw) > maxHeaderBytes)
                    return Result(PeerIdentityStatus.Invalid);
                if (verificationHeader is not null && context.Header(verificationHeader) != verificationValue)
                    return Result(PeerIdentityStatus.Invalid);
                var pem = StrictPercentDecode(raw, allowLineBreaks: true);
                if (!IsAscii(pem) || Encoding.UTF8.GetByteCount(pem) > maxHeaderBytes
                    || Count(pem, "-----BEGIN CERTIFICATE-----") != 1
                    || Count(pem, "-----END CERTIFICATE-----") != 1
                    || !pem.Trim().EndsWith("-----END CERTIFICATE-----", StringComparison.Ordinal))
                    return Result(PeerIdentityStatus.Invalid);
                using var certificate = X509Certificate2.CreateFromPem(pem);
                var id = ValidateCertificate(certificate, config.Domains);
                return PeerIdentityResult.Available(Identity(id, evidenceSource, context,
                    new Dictionary<string, object?>()));
            }
            catch (Exception exception) when (IsInvalidEvidence(exception))
            {
                return Result(PeerIdentityStatus.Invalid);
            }
        });

    private static SpiffeId ValidateCertificate(X509Certificate2 certificate, HashSet<string> trustDomains)
    {
        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            throw new CryptographicException("X.509-SVID is outside its validity period");

        var sanRaw = certificate.Extensions["2.5.29.17"]
            ?? throw new CryptographicException("X.509-SVID requires SAN");
        var uriSans = ReadUriSans(sanRaw);
        if (uriSans.Count != 1) throw new CryptographicException("X.509-SVID requires exactly one URI SAN");
        if (string.IsNullOrEmpty(certificate.SubjectName.Name) && !sanRaw.Critical)
            throw new CryptographicException("subjectless X.509-SVID requires critical SAN");

        var basicRaw = certificate.Extensions["2.5.29.19"]
            ?? throw new CryptographicException("X.509-SVID requires basic constraints");
        var basic = new X509BasicConstraintsExtension(basicRaw, basicRaw.Critical);
        if (basic.CertificateAuthority) throw new CryptographicException("X.509-SVID leaf cannot be a CA");

        var usageRaw = certificate.Extensions["2.5.29.15"]
            ?? throw new CryptographicException("X.509-SVID requires key usage");
        var usage = new X509KeyUsageExtension(usageRaw, usageRaw.Critical);
        if (!usage.Critical || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)
            || usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign)
            || usage.KeyUsages.HasFlag(X509KeyUsageFlags.CrlSign))
            throw new CryptographicException("invalid X.509-SVID key usage");

        if (certificate.Extensions["2.5.29.37"] is { } extendedRaw)
        {
            var extended = new X509EnhancedKeyUsageExtension(extendedRaw, extendedRaw.Critical);
            var usages = extended.EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value).ToHashSet(StringComparer.Ordinal);
            if (!usages.Contains("1.3.6.1.5.5.7.3.1") || !usages.Contains("1.3.6.1.5.5.7.3.2"))
                throw new CryptographicException("invalid X.509-SVID extended key usage");
        }
        return ParseSpiffeId(uriSans[0], trustDomains);
    }

    private static List<string> ReadUriSans(X509Extension extension)
    {
        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        var uris = new List<string>();
        var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);
        while (sequence.HasData)
        {
            if (sequence.PeekTag().HasSameClassAndValue(uriTag))
                uris.Add(sequence.ReadCharacterString(UniversalTagNumber.IA5String, uriTag));
            else
                sequence.ReadEncodedValue();
        }
        reader.ThrowIfNotEmpty();
        return uris;
    }

    private static SpiffeId ParseSpiffeId(string value, HashSet<string> trustDomains)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || !IsAscii(value) || Encoding.UTF8.GetByteCount(value) > 2048 || value.Contains('%'))
            throw new ArgumentException("SPIFFE ID is empty, non-ASCII, encoded, or oversized", nameof(value));
        const string prefix = "spiffe://";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("invalid SPIFFE scheme", nameof(value));
        var slash = value.IndexOf('/', prefix.Length);
        if (slash < 0) throw new ArgumentException("SPIFFE ID requires a path", nameof(value));
        var domain = value[prefix.Length..slash];
        var path = value[slash..];
        if (!TrustDomainRegex().IsMatch(domain) || !PathRegex().IsMatch(path)
            || path.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("SPIFFE ID is not canonical", nameof(value));
        if (!trustDomains.Contains(domain)) throw new ArgumentException("SPIFFE trust domain is not allowed", nameof(value));
        return new SpiffeId(value, domain);
    }

    private static Dictionary<string, List<string>> ParseSingleXfcc(string raw, int maxHeaderBytes)
    {
        if (!IsAscii(raw) || Encoding.UTF8.GetByteCount(raw) > maxHeaderBytes || ContainsControl(raw))
            throw new ArgumentException("invalid XFCC bytes", nameof(raw));
        var elements = SplitXfcc(raw, ',');
        if (elements.Count != 1 || string.IsNullOrWhiteSpace(elements[0]))
            throw new ArgumentException("XFCC must contain one SANITIZE_SET element", nameof(raw));
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var rawPair in SplitXfcc(elements[0], ';'))
        {
            var pair = rawPair.Trim();
            var equals = pair.IndexOf('=');
            if (equals <= 0) throw new ArgumentException("malformed XFCC field", nameof(raw));
            var rawKey = pair[..equals].Trim();
            var key = rawKey.ToLowerInvariant();
            if (!XfccKeyRegex().IsMatch(rawKey) || !s_xfccFields.Contains(key))
                throw new ArgumentException("unsupported XFCC field", nameof(raw));
            var value = XfccValue(pair[(equals + 1)..].Trim());
            if (s_xfccPercentFields.Contains(key)) value = StrictPercentDecode(value, allowLineBreaks: false);
            if (!s_xfccMultiFields.Contains(key) && fields.ContainsKey(key))
                throw new ArgumentException("duplicate XFCC singleton", nameof(raw));
            if (!fields.TryGetValue(key, out var values)) fields.Add(key, values = []);
            values.Add(value);
        }
        return fields;
    }

    private static List<string> SplitXfcc(string value, char delimiter)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                if (character is not ('"' or '\\')) throw new ArgumentException("unsupported XFCC escape");
                current.Append(character);
                escaped = false;
            }
            else if (quoted && character == '\\') escaped = true;
            else if (character == '"') { quoted = !quoted; current.Append(character); }
            else if (character == delimiter && !quoted) { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(character);
        }
        if (quoted || escaped) throw new ArgumentException("unterminated XFCC quote");
        parts.Add(current.ToString());
        return parts;
    }

    private static string XfccValue(string value)
    {
        if (value.StartsWith('"') || value.EndsWith('"'))
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
                throw new ArgumentException("malformed XFCC quoted value");
            value = value[1..^1];
        }
        else if (value.IndexOfAny([',', ';', '=']) >= 0) throw new ArgumentException("unquoted XFCC delimiter");
        if (value.Length == 0) throw new ArgumentException("empty XFCC value");
        return value;
    }

    private static string StrictPercentDecode(string value, bool allowLineBreaks)
    {
        var bytes = new byte[value.Length];
        var length = 0;
        for (var index = 0; index < value.Length;)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length) throw new ArgumentException("invalid percent escape");
                var high = Hex(value[index + 1]);
                var low = Hex(value[index + 2]);
                if (high < 0 || low < 0) throw new ArgumentException("invalid percent escape");
                bytes[length++] = (byte)((high << 4) | low);
                index += 3;
            }
            else
            {
                if (character > 0x7f) throw new ArgumentException("non-ASCII encoded header");
                bytes[length++] = (byte)character;
                index++;
            }
        }
        var decoded = s_strictUtf8.GetString(bytes, 0, length);
        if (decoded.Any(character => (character <= 0x1f && !(allowLineBreaks && character is '\r' or '\n'))
            || character == 0x7f)) throw new ArgumentException("decoded header contains controls");
        return decoded;
    }

    private static PeerIdentity Identity(SpiffeId id, string evidenceSource,
        PeerResolutionContext context, IReadOnlyDictionary<string, object?> attributes) =>
        new(ProviderName, evidenceSource, IdentityAssurance.ConfiguredProxy,
            $"spiffe://{id.TrustDomain}", "http", PeerSubjectKind.Workload, id.Value,
            SubjectStability.Stable, true, attributes, sourceAddress: context.AssertedPeer,
            proxyAddress: context.ImmediatePeer);

    private static Config ValidateConfig(IEnumerable<string> trustDomains, IEnumerable<string> proxies)
    {
        ArgumentNullException.ThrowIfNull(trustDomains);
        ArgumentNullException.ThrowIfNull(proxies);
        var domains = new HashSet<string>(trustDomains, StringComparer.Ordinal);
        var copiedProxies = TrustedProxyAddresses.Parse(proxies, nameof(proxies));
        if (domains.Count == 0)
            throw new ArgumentException("trustDomains and trustedProxyAddresses must not be empty");
        if (domains.Any(domain => domain is null || !TrustDomainRegex().IsMatch(domain)))
            throw new ArgumentException("invalid SPIFFE trust domain", nameof(trustDomains));
        return new Config(domains, copiedProxies);
    }

    private static bool IsTrustedProxy(Config config, string? immediatePeer) =>
        TrustedProxyAddresses.Normalize(immediatePeer) is { } normalized
        && config.Proxies.Contains(normalized);

    private static PeerIdentityResult Result(PeerIdentityStatus status) => new(ProviderName, status);
    private static bool IsInvalidEvidence(Exception exception) =>
        exception is ArgumentException or CryptographicException or PeerIdentityRejectedException or AsnContentException;
    private static bool ContainsControl(string? value) => value is null || value.Any(character => character <= 0x1f || character == 0x7f);
    private static bool IsAscii(string value) => value.All(character => character <= 0x7f);
    private static void RequireHeader(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || ContainsControl(value)) throw new ArgumentException("invalid header", name);
    }
    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = 0; (at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0; at += needle.Length) count++;
        return count;
    }
    private static int Hex(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };

    private sealed record Config(HashSet<string> Domains, HashSet<string> Proxies);
    private sealed record SpiffeId(string Value, string TrustDomain);
    private sealed class DelegateProvider(Func<PeerResolutionContext, PeerIdentityResult> resolve) : IPeerIdentityProvider
    {
        public string Provider => ProviderName;
        public ValueTask<PeerIdentityResult> ResolveAsync(PeerResolutionContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(resolve(context));
    }
}
