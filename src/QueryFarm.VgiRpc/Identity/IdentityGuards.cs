using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Identity;

/// <summary>
/// The guards <c>vgi_rpc.Identity.v1</c> applies before any worker policy runs.
/// </summary>
/// <remarks>
/// Separate from <see cref="IdentityImpl"/> and public because each one is a rule a deployment
/// may need to apply somewhere else too (a custom authenticate delegate that also resolves
/// opaque credentials wants the same JWS refusal and the same digest-not-credential discipline),
/// and because a guard that can only be exercised through a full dispatch is a guard whose
/// ordering is hard to pin down in a test.
/// </remarks>
public static class IdentityGuards
{
    /// <summary>
    /// Three dot-separated base64url segments -- a JWS. Such a credential is validated locally
    /// against a key set and MUST NOT be routed to a resolver: doing so sends a bearer token the
    /// asker may itself have rejected (expired, wrong audience) to a third party that might
    /// accept it, which turns introspection into a laundering step.
    /// </summary>
    /// <remarks>
    /// Always matched against the <em>trimmed</em> credential -- see
    /// <see cref="RejectJwsShaped"/>. .NET's <c>$</c> means "end of string, or immediately before
    /// a <c>\n</c> at the end of the string", which is a dialect quirk this guard must not depend
    /// on, in either direction.
    /// </remarks>
    private static readonly Regex s_jwsShaped =
        new(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Cap on a credential this framework will even attempt to resolve, <b>in UTF-8 bytes</b>.
    /// Anything longer is not a bearer token; refusing early keeps a resolver from being handed
    /// megabytes.
    /// </summary>
    /// <remarks>
    /// <b>Bytes, not characters.</b> The ports reached for three different units here and bytes
    /// is the one the purpose implies: "do not hand a resolver megabytes" is a statement about
    /// what crosses the wire and what a store is asked to hold, and it is also the most
    /// conservative of the three. In this port the distinction is not academic —
    /// <see cref="string.Length"/> counts UTF-16 code units, so a credential made of multibyte
    /// characters would otherwise get up to three times its intended allowance (four, counting
    /// surrogate pairs by code unit). Measure with
    /// <see cref="Encoding.GetByteCount(string)"/>.
    /// </remarks>
    public const int MaxTokenBytes = 4096;

    /// <summary>Returns a lowercase hex SHA-256 digest of <paramref name="token"/>, for diagnostics.</summary>
    /// <remarks>
    /// The credential itself must never reach a log, a span, or an error message. A digest is
    /// stable enough to correlate one credential's failures across records without being the
    /// credential.
    /// </remarks>
    public static string TokenDigest(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Validates the introspector allowlist.</summary>
    /// <param name="principals">Principals permitted to introspect.</param>
    /// <returns>The allowlist as an immutable set.</returns>
    /// <exception cref="ArgumentException">The allowlist is missing or empty. There is no
    /// permissive default: "any authenticated caller" is precisely the configuration that turns
    /// introspection into an open oracle, so it cannot be reached by omission.</exception>
    public static IReadOnlySet<string> NormalisePrincipals(IEnumerable<string>? principals)
    {
        var allowed = new HashSet<string>((principals ?? []).Where(p => !string.IsNullOrEmpty(p)), StringComparer.Ordinal);
        if (allowed.Count == 0)
        {
            throw new ArgumentException(
                "introspectPrincipals must name at least one principal. Introspection is a distinct " +
                "capability from authentication: allowing any authenticated caller lets any user " +
                "resolve any other user's credential to its owner.",
                nameof(principals));
        }

        return allowed;
    }

    /// <summary>Returns the caller principal, or refuses.</summary>
    /// <param name="auth">The calling connection's authentication result.</param>
    /// <param name="principals">The introspector allowlist.</param>
    /// <returns>The caller's principal.</returns>
    /// <exception cref="IntrospectionRefusedException">The caller is unauthenticated or off the
    /// allowlist.</exception>
    /// <remarks>
    /// Checked before anything touches the subject credential: an unauthorized caller must not
    /// learn anything about it, including how long it took.
    /// </remarks>
    public static string CheckIntrospector(AuthContext auth, IReadOnlySet<string> principals)
    {
        var caller = auth.Principal ?? "";
        if (!auth.Authenticated || !principals.Contains(caller))
        {
            throw new IntrospectionRefusedException("caller is not an introspector");
        }

        return caller;
    }

    /// <summary>Refuses a blank, over-long, or JWS-shaped subject before it reaches a resolver.</summary>
    /// <param name="token">The subject credential, exactly as the caller sent it.</param>
    /// <exception cref="TokenUnresolvedException">Uniform with "unknown" and "expired" -- see
    /// <see cref="TokenUnresolvedException"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>The shape test runs against the whitespace-trimmed credential, and the resolver still
    /// receives what the caller actually sent.</b> Trimming can only add refusals, never remove
    /// one, and it closes a padding bypass that anchor semantics alone cannot: to a strict
    /// matcher <c>"a.b.c\n"</c> is not JWS-shaped, so it gets routed onward -- precisely what
    /// this guard exists to stop.
    /// </para>
    /// <para>
    /// This port happened to refuse a single trailing newline before the change, because .NET's
    /// <c>$</c> matches before one -- the same accident the Python reference had, and just as
    /// arbitrary: two trailing newlines slipped through, and so did <c>"a.b.c\r\n"</c>, which
    /// matters on the Windows targets in this port's own CI matrix. Ports spelling the anchors
    /// strictly (Go's <c>\A..\z</c>, JavaScript's unflagged <c>$</c>) diverged in the unsafe
    /// direction. Trimming first is the rule that survives translation into seven regex dialects,
    /// because it does not depend on any of them.
    /// </para>
    /// <para>
    /// A whitespace-only credential is refused too: it is not a credential.
    /// </para>
    /// <para>
    /// <b>Trimming is for the shape test only.</b> Rewriting a credential before resolving it
    /// would make the worker answer about a string the caller never sent, so the length cap is
    /// measured against the original and the original is what
    /// <see cref="IdentityImpl.IntrospectToken"/> hands the resolver.
    /// </para>
    /// <para>
    /// The cap is counted in UTF-8 bytes — see <see cref="MaxTokenBytes"/> for why that is not
    /// the same as <see cref="string.Length"/> on this runtime.
    /// </para>
    /// </remarks>
    public static void RejectJwsShaped(string token)
    {
        if (string.IsNullOrEmpty(token) || Encoding.UTF8.GetByteCount(token) > MaxTokenBytes)
        {
            throw new TokenUnresolvedException("unresolved");
        }

        var candidate = token.Trim();
        if (candidate.Length == 0 || s_jwsShaped.IsMatch(candidate))
        {
            throw new TokenUnresolvedException("unresolved");
        }
    }

    /// <summary>Returns the caller's <c>auth_time</c>, or refuses if it is missing or stale.</summary>
    /// <param name="auth">The calling connection's authentication result.</param>
    /// <param name="maxAuthAge">How recently the caller must have authenticated, in seconds.</param>
    /// <param name="now">Unix seconds, for tests. Defaults to the wall clock -- <c>auth_time</c>
    /// is an absolute timestamp, so this comparison is necessarily against wall time.</param>
    /// <returns>The caller's <c>auth_time</c>.</returns>
    /// <exception cref="StaleAuthException">The caller is unauthenticated, carries no
    /// <c>auth_time</c>, carries an unusable one, or authenticated too long ago.</exception>
    /// <remarks>
    /// <para>
    /// A credential with no verifiable <c>auth_time</c> cannot mint. That single rule is what
    /// stops a grant being used to mint another grant: a grant is not an IdP-issued token, so it
    /// carries no <c>auth_time</c>, so the lineage cannot escape the identity provider. It also
    /// makes subprocess, pipe and unix transports fail closed for free -- there is no
    /// authenticated principal there at all.
    /// </para>
    /// <para>
    /// A static bearer proves a machine holds a secret, never that a human just authenticated, so
    /// it is refused here too.
    /// </para>
    /// <para>
    /// <b>Warning.</b> <c>auth_time</c> is an OIDC claim meaning <em>when this session began</em>,
    /// which can be arbitrarily old while still present and cryptographically valid. Requiring it
    /// is not the same as requiring a recent login: the deployment must send <c>max_age</c> (or an
    /// appropriate <c>acr</c>) at the authorize endpoint for this guard to mean what it says.
    /// </para>
    /// </remarks>
    public static double CheckFreshness(AuthContext auth, double maxAuthAge, double? now = null)
    {
        if (!auth.Authenticated || string.IsNullOrEmpty(auth.Principal))
        {
            throw new StaleAuthException("caller is not authenticated");
        }

        if (!auth.Claims.TryGetValue("auth_time", out var raw) || raw is null)
        {
            throw new StaleAuthException(
                "credential carries no auth_time; only a recently authenticated user may mint a grant");
        }

        double authTime;
        try
        {
            // A JWT claim reaches this port as whatever the JSON decoder produced -- a number, or
            // a string when an IdP quotes it. Both are accepted; anything else is refused rather
            // than coerced, because a guard that silently reads garbage as zero would treat every
            // caller as having authenticated in 1970.
            authTime = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        }
        catch (Exception exc) when (exc is FormatException or InvalidCastException or OverflowException)
        {
            throw new StaleAuthException("credential carries an unusable auth_time");
        }

        var age = (now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0) - authTime;
        if (age > maxAuthAge)
        {
            throw new StaleAuthException(string.Format(
                CultureInfo.InvariantCulture,
                "last authentication was {0:F0}s ago, which exceeds the {1:F0}s ceiling for minting a grant; re-authenticate",
                age,
                maxAuthAge));
        }

        return authTime;
    }
}
