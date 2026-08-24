using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Mutual TLS (mTLS) authentication factories for the HTTP transport — port of the canonical
/// Python repo's <c>vgi_rpc.http._mtls</c>. Two header conventions are supported, mirroring
/// Python exactly:
/// <list type="bullet">
/// <item><b>PEM-in-header</b>: <see cref="MtlsAuth.FromHeader"/>,
/// <see cref="MtlsAuth.FromFingerprint"/>, and <see cref="MtlsAuth.FromSubject"/> parse
/// URL-encoded PEM certificates from headers like <c>X-SSL-Client-Cert</c> (nginx) or
/// <c>X-Amzn-Mtls-Clientcert</c> (AWS ALB). No extra package is needed — unlike Python (which
/// gates this behind an optional <c>cryptography</c> dependency), .NET's
/// <see cref="System.Security.Cryptography.X509Certificates.X509Certificate2"/> ships in the
/// base class library.</item>
/// <item><b>XFCC</b>: <see cref="MtlsAuth.Xfcc"/> parses the Envoy
/// <c>x-forwarded-client-cert</c> structured header. No cryptography involved at all.</item>
/// </list>
///
/// <para><b>Header spoofing risk.</b> The reverse proxy MUST strip client-supplied
/// <c>X-SSL-Client-Cert</c> / <c>x-forwarded-client-cert</c> headers before forwarding. Failure
/// to do so allows clients to forge certificate identity. These factories trust the header
/// unconditionally — matching Python's own documented posture.</para>
///
/// <para>No certificate chain validation is performed — that is the proxy's responsibility.
/// These factories only extract identity from the forwarded certificate information.</para>
///
/// <para><b>Divergence from Python</b>: Python's authenticate callbacks return an
/// <c>AuthContext</c> that flows into RPC dispatch, so a method implementation can read
/// <c>ctx.auth.principal</c>. This port's <see cref="RpcHttpEndpoints.AuthenticateDelegate"/> is
/// a pure accept/reject gate (see <c>docs/roadmap.md</c> M8/M9) with no context-propagation
/// mechanism yet, so the extracted <see cref="MtlsIdentity"/> is stashed on
/// <c>HttpContext.Items["vgi_rpc.mtls.identity"]</c> instead — usable by application code that
/// reaches the same <see cref="HttpContext"/> (e.g. a custom endpoint filter), but not yet wired
/// into <see cref="QueryFarm.VgiRpc.Server.RpcServer"/> dispatch itself.</para>
/// </summary>
public static class MtlsAuth
{
    private const string ItemsKey = "vgi_rpc.mtls.identity";

    private static readonly Dictionary<string, HashAlgorithmName> s_hashAlgorithms = new()
    {
        ["sha256"] = HashAlgorithmName.SHA256,
        ["sha1"] = HashAlgorithmName.SHA1,
        ["sha384"] = HashAlgorithmName.SHA384,
        ["sha512"] = HashAlgorithmName.SHA512,
    };

    /// <summary>Retrieves the <see cref="MtlsIdentity"/> stashed by a successful mTLS
    /// authenticate delegate, or <see langword="null"/> if none ran (or ran and the request was
    /// authenticated some other way).</summary>
    public static MtlsIdentity? GetIdentity(HttpContext context) => context.Items[ItemsKey] as MtlsIdentity;

    // -------------------------------------------------------------------
    // PEM-based factories
    // -------------------------------------------------------------------

    /// <summary>
    /// Generic factory: parses the client certificate from a proxy header and delegates identity
    /// extraction to <paramref name="validate"/>.
    /// </summary>
    /// <param name="validate">Receives the parsed certificate and returns an
    /// <see cref="MtlsIdentity"/>, or throws <see cref="AuthFailure"/> (or any exception, treated
    /// as <see cref="AuthReason.Unauthorized"/>) to reject.</param>
    /// <param name="header">HTTP header carrying the URL-encoded PEM certificate.</param>
    /// <param name="checkExpiry">When <see langword="true"/>, verify the certificate is within
    /// its validity period before calling <paramref name="validate"/>.</param>
    public static RpcHttpEndpoints.AuthenticateDelegate FromHeader(
        Func<X509Certificate2, MtlsIdentity> validate,
        string header = "X-SSL-Client-Cert",
        bool checkExpiry = false)
    {
        return context =>
        {
            using var cert = ParseCertFromHeader(context, header);
            if (checkExpiry)
            {
                CheckExpiry(cert);
            }

            context.Items[ItemsKey] = validate(cert);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Certificate-fingerprint lookup — computes the certificate's fingerprint and looks it up in
    /// <paramref name="fingerprints"/>. Keys must be lowercase hex without colons (matching
    /// <c>X509Certificate2.GetCertHash(HashAlgorithmName)</c>'s own output shape).
    /// </summary>
    /// <param name="fingerprints">Lowercase-hex fingerprint → identity mapping.</param>
    /// <param name="header">HTTP header carrying the URL-encoded PEM certificate.</param>
    /// <param name="algorithm">One of <c>"sha256"</c> (default), <c>"sha1"</c>, <c>"sha384"</c>,
    /// <c>"sha512"</c>.</param>
    /// <param name="checkExpiry">When <see langword="true"/>, verify the certificate is within
    /// its validity period first.</param>
    public static RpcHttpEndpoints.AuthenticateDelegate FromFingerprint(
        IReadOnlyDictionary<string, MtlsIdentity> fingerprints,
        string header = "X-SSL-Client-Cert",
        string algorithm = "sha256",
        bool checkExpiry = false)
    {
        if (!s_hashAlgorithms.TryGetValue(algorithm, out var hashAlgorithm))
        {
            throw new ArgumentException($"Unsupported hash algorithm: {algorithm}", nameof(algorithm));
        }

        MtlsIdentity Validate(X509Certificate2 cert)
        {
            var fp = Convert.ToHexStringLower(cert.GetCertHash(hashAlgorithm));
            if (!fingerprints.TryGetValue(fp, out var identity))
            {
                throw new AuthFailure(AuthReason.InvalidCredential, "Unknown certificate fingerprint");
            }

            return identity;
        }

        return FromHeader(Validate, header, checkExpiry);
    }

    /// <summary>
    /// Extracts the Subject Common Name as the principal, with claims populated from the subject
    /// DN, serial number (hex), and expiry timestamp.
    /// </summary>
    /// <param name="header">HTTP header carrying the URL-encoded PEM certificate.</param>
    /// <param name="domain">Domain string for the returned <see cref="MtlsIdentity"/>.</param>
    /// <param name="allowedSubjects">When set, only certificates whose Subject CN is in this set
    /// are accepted — <see langword="null"/> accepts any valid certificate (a deliberate security
    /// decision the caller must make explicitly, matching Python).</param>
    /// <param name="checkExpiry">When <see langword="true"/>, verify the certificate is within
    /// its validity period first.</param>
    public static RpcHttpEndpoints.AuthenticateDelegate FromSubject(
        string header = "X-SSL-Client-Cert",
        string domain = "mtls",
        IReadOnlySet<string>? allowedSubjects = null,
        bool checkExpiry = false)
    {
        MtlsIdentity Validate(X509Certificate2 cert)
        {
            var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (allowedSubjects is not null && !allowedSubjects.Contains(cn))
            {
                throw new AuthFailure(AuthReason.InsufficientScope, "Subject CN not in allowed subjects");
            }

            return new MtlsIdentity(cn, domain, new Dictionary<string, object>
            {
                ["subject_dn"] = cert.SubjectName.Name,
                ["serial"] = Convert.ToHexStringLower(cert.SerialNumberBytes.Span),
                ["not_valid_after"] = cert.NotAfter.ToUniversalTime().ToString("O"),
            });
        }

        return FromHeader(Validate, header, checkExpiry);
    }

    private static X509Certificate2 ParseCertFromHeader(HttpContext context, string header)
    {
        var raw = context.Request.Headers[header].ToString();
        if (string.IsNullOrEmpty(raw))
        {
            // An absent header is reported as proxy_required rather than a missing credential —
            // the header is the proxy's to inject, so its absence points at the deployment, not
            // at the caller (matches Python's exact rationale).
            throw new AuthFailure(AuthReason.ProxyRequired, $"Missing {header} header");
        }

        var pem = Uri.UnescapeDataString(raw);
        if (!pem.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            throw new AuthFailure(AuthReason.InvalidCredential, "Header value is not a PEM certificate");
        }

        try
        {
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (Exception)
        {
            // Deliberately not the framework's raw exception text — docs/unauthorized-spec.md §2's
            // anti-oracle rule, same posture already established for JWT (see JwtAuth.Create).
            // Python's own example interpolates the exception text here; this port doesn't.
            throw new AuthFailure(AuthReason.InvalidCredential, "Failed to parse PEM certificate");
        }
    }

    private static void CheckExpiry(X509Certificate2 cert)
    {
        var now = DateTime.UtcNow;
        if (now < cert.NotBefore.ToUniversalTime())
        {
            throw new AuthFailure(AuthReason.ExpiredCredential, "Certificate is not yet valid");
        }

        if (now > cert.NotAfter.ToUniversalTime())
        {
            throw new AuthFailure(AuthReason.ExpiredCredential, "Certificate has expired");
        }
    }

    // -------------------------------------------------------------------
    // XFCC (no cryptography needed)
    // -------------------------------------------------------------------

    private const string XfccHeader = "x-forwarded-client-cert";

    /// <summary>Which element to use when an <c>x-forwarded-client-cert</c> header carries
    /// multiple (comma-separated) elements — one per hop.</summary>
    public enum XfccSelectElement
    {
        /// <summary>The first element — the original client (default).</summary>
        First,

        /// <summary>The last element — the nearest proxy.</summary>
        Last,
    }

    /// <summary>
    /// Builds an authenticate delegate from Envoy's <c>x-forwarded-client-cert</c> header. Does
    /// not require certificate parsing at all — the proxy has already done that and handed back a
    /// structured summary.
    /// </summary>
    /// <param name="validate">Optional callback receiving the selected
    /// <see cref="XfccElement"/>. When <see langword="null"/>, the Subject field's CN is used as
    /// the principal and every present field is copied into claims.</param>
    /// <param name="domain">Domain string for the returned <see cref="MtlsIdentity"/>, used only
    /// when <paramref name="validate"/> is <see langword="null"/>.</param>
    /// <param name="selectElement">Which element to use when the header carries more than one.</param>
    public static RpcHttpEndpoints.AuthenticateDelegate Xfcc(
        Func<XfccElement, MtlsIdentity>? validate = null,
        string domain = "mtls",
        XfccSelectElement selectElement = XfccSelectElement.First)
    {
        return context =>
        {
            var headerValue = context.Request.Headers[XfccHeader].ToString();
            if (string.IsNullOrEmpty(headerValue))
            {
                throw new AuthFailure(AuthReason.ProxyRequired, $"Missing {XfccHeader} header");
            }

            var elements = ParseXfcc(headerValue);
            if (elements.Count == 0)
            {
                throw new AuthFailure(AuthReason.InvalidCredential, $"Empty {XfccHeader} header");
            }

            var element = selectElement == XfccSelectElement.First ? elements[0] : elements[^1];
            MtlsIdentity identity;
            if (validate is not null)
            {
                identity = validate(element);
            }
            else
            {
                var principal = element.Subject is { Length: > 0 } subject ? ExtractCn(subject) : "";
                var claims = new Dictionary<string, object>();
                if (element.Hash is { Length: > 0 } hash)
                {
                    claims["hash"] = hash;
                }

                if (element.Subject is { Length: > 0 } subj)
                {
                    claims["subject"] = subj;
                }

                if (element.Uri is { Length: > 0 } uri)
                {
                    claims["uri"] = uri;
                }

                if (element.Dns.Count > 0)
                {
                    claims["dns"] = element.Dns;
                }

                if (element.By is { Length: > 0 } by)
                {
                    claims["by"] = by;
                }

                identity = new MtlsIdentity(principal, domain, claims);
            }

            context.Items[ItemsKey] = identity;
            return Task.CompletedTask;
        };
    }

    /// <summary>Extracts the CN attribute from an RFC 4514-ish DN string (e.g.
    /// <c>"CN=client1,O=Acme"</c> → <c>"client1"</c>) — mirrors Python's <c>_extract_cn</c>,
    /// including respecting a backslash-escaped comma inside a value.</summary>
    private static string ExtractCn(string subject)
    {
        foreach (var part in SplitRespectingEscapes(subject, ','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[3..];
            }
        }

        return "";
    }

    private static IEnumerable<string> SplitRespectingEscapes(string text, char delimiter)
    {
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\' && i + 1 < text.Length)
            {
                current.Append(ch).Append(text[i + 1]);
                i++;
            }
            else if (ch == delimiter)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        yield return current.ToString();
    }

    /// <summary>
    /// Parses an <c>x-forwarded-client-cert</c> header value: comma-separated elements
    /// (respecting quoted values), semicolon-separated key=value pairs within each element, and
    /// URL-decoded <c>Cert</c>/<c>URI</c>/<c>By</c> fields — a direct port of Python's
    /// <c>_parse_xfcc</c>.
    /// </summary>
    public static IReadOnlyList<XfccElement> ParseXfcc(string headerValue)
    {
        var elements = new List<XfccElement>();
        foreach (var rawElement in SplitRespectingQuotes(headerValue, ','))
        {
            var trimmedElement = rawElement.Trim();
            if (trimmedElement.Length == 0)
            {
                continue;
            }

            string? hash = null, cert = null, subject = null, uri = null, by = null;
            var dns = new List<string>();
            foreach (var rawPair in SplitRespectingQuotes(trimmedElement, ';'))
            {
                var pair = rawPair.Trim();
                if (pair.Length == 0)
                {
                    continue;
                }

                var eqIdx = pair.IndexOf('=');
                if (eqIdx < 0)
                {
                    continue;
                }

                var key = pair[..eqIdx].Trim().ToLowerInvariant();
                var value = pair[(eqIdx + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                {
                    value = UnescapeQuoted(value[1..^1]);
                }

                if (key is "cert" or "uri" or "by")
                {
                    value = Uri.UnescapeDataString(value);
                }

                switch (key)
                {
                    case "dns":
                        dns.Add(value);
                        break;
                    case "hash":
                        hash = value;
                        break;
                    case "cert":
                        cert = value;
                        break;
                    case "subject":
                        subject = value;
                        break;
                    case "uri":
                        uri = value;
                        break;
                    case "by":
                        by = value;
                        break;
                }
            }

            elements.Add(new XfccElement(hash, cert, subject, uri, dns, by));
        }

        return elements;
    }

    private static IEnumerable<string> SplitRespectingQuotes(string text, char delimiter)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
            }
            else if (ch == '\\' && inQuotes && i + 1 < text.Length)
            {
                current.Append(ch).Append(text[i + 1]);
                i++;
            }
            else if (ch == delimiter && !inQuotes)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }

            i++;
        }

        parts.Add(current.ToString());
        return parts;
    }

    private static string UnescapeQuoted(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\\(.)", "$1");
}

/// <summary>
/// Identity extracted from a client certificate or XFCC element — the mTLS analog of Python's
/// <c>AuthContext</c>, scoped to what this port's gate-only authenticate model can carry (see
/// <see cref="MtlsAuth"/>'s class doc comment on why this doesn't flow into RPC dispatch yet).
/// </summary>
public sealed record MtlsIdentity(string Principal, string Domain = "mtls", IReadOnlyDictionary<string, object>? Claims = null)
{
    public IReadOnlyDictionary<string, object> Claims { get; init; } = Claims ?? new Dictionary<string, object>();
}

/// <summary>A single element from an <c>x-forwarded-client-cert</c> header. <see cref="Cert"/> is
/// URL-decoded PEM when present. Mirrors Python's <c>XfccElement</c> field-for-field.</summary>
public sealed record XfccElement(
    string? Hash = null,
    string? Cert = null,
    string? Subject = null,
    string? Uri = null,
    IReadOnlyList<string>? Dns = null,
    string? By = null)
{
    public IReadOnlyList<string> Dns { get; init; } = Dns ?? [];
}
