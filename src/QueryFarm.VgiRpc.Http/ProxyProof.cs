using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Proxy proof: HMAC evidence that a request arrived through a trusted proxy — a full port of
/// the canonical Python repo's <c>vgi_rpc.http._proof</c>. See <c>docs/proxy-proof-spec.md</c>
/// for the normative cross-language contract and <c>docs/roadmap.md</c> M11 for what this port
/// implements.
///
/// <para>A proxy mints a per-request HMAC-SHA256 proof over a timestamp, a nonce, and the
/// worker's own identifier, keyed by a secret shared only with that worker. The worker
/// recomputes and compares in constant time. The proof establishes the <b>hop</b>, never the
/// caller — it is ANDed with whatever authenticates the end user (<see cref="RequireAll"/>), not
/// an alternative credential.</para>
///
/// <para><b>Composition is simpler than Python's here.</b> Python needs a distinct
/// <c>PreconditionGate</c>/<c>require_all</c> combinator system because its
/// <c>chain_authenticate</c> is an OR-combinator that swallows <c>ValueError</c> to try the next
/// credential, so a precondition gate must raise a distinguished exception type
/// (<c>PermissionError</c>) to avoid being silently skipped. This port's
/// <see cref="RpcHttpEndpoints.AuthenticateDelegate"/> has no OR-combinator at all — there is
/// exactly one authenticate delegate per <c>MapVgiRpc</c> call — so sequential
/// <c>async</c>/<c>await</c> composition already gives the exact "gate first, only call inner on
/// success" behavior the spec requires (§8), with nothing to swallow the gate's exception.
/// <see cref="RequireAll"/> exists for naming parity with Python (and because
/// spelling out the composition inline is one line either way), not because C# needs a special
/// type to make it safe.</para>
/// </summary>
public static class ProxyProof
{
    /// <summary>Request-only: the proof token. Exactly one instance permitted.</summary>
    public const string ProofHeader = "VGI-Proxy-Proof";

    /// <summary>Response-only, <c>require</c> mode only: advertises that this worker enforces
    /// proofs, so a misconfigured proxy can detect it is minting for a worker that ignores them.</summary>
    public const string ProofRequiredHeader = "VGI-Proxy-Proof-Required";

    private const string Version = "v1";
    private const int MaxHeaderBytes = 512;
    private const int SecretLen = 32;
    private const int NonceBytes = 16;

    private static readonly byte[] s_domainPrefix = "vgi.proxy.proof.v1"u8.ToArray();
    private static readonly byte[] s_deriveLabel = "vgi.proxy.proof.v1/"u8.ToArray();

    // Charsets are load-bearing, not cosmetic — see docs/proxy-proof-spec.md §3/§4: the canonical
    // string is NUL-separated, so framing is only unambiguous because no field can contain a NUL
    // (and kid cannot contain the '.' that separates wire fields). Validated before any MAC is
    // computed, and before base64-decoding the MAC field specifically — .NET's Convert.FromBase64String
    // throws on invalid input (unlike Python's urlsafe_b64decode, which silently drops non-alphabet
    // bytes), but the charset check still runs first so every port reports the same reason code
    // (`malformed`, not `bad_mac`) for the same bad input, matching the spec's explicit requirement.
    private static readonly Regex s_kidRegex = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);
    private static readonly Regex s_tsRegex = new(@"\A[0-9]{1,20}\z", RegexOptions.Compiled);
    private static readonly Regex s_nonceRegex = new(@"\A[A-Za-z0-9_-]{22}\z", RegexOptions.Compiled);
    private static readonly Regex s_macRegex = new(@"\A[A-Za-z0-9_-]{43}\z", RegexOptions.Compiled);
    private static readonly Regex s_originRegex = new(@"\A[A-Za-z0-9._:/-]{1,255}\z", RegexOptions.Compiled);

    private static string B64(byte[] raw) => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Unb64(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Derives the secret shared between one proxy and one worker:
    /// <c>HMAC-SHA256(baseKey, "vgi.proxy.proof.v1/" + proxyId + \0 + originId)</c>. A worker is
    /// configured with its derived secret only, never <paramref name="baseKey"/> — otherwise it
    /// could mint proofs accepted by its siblings.
    /// </summary>
    public static byte[] DeriveSecret(byte[] baseKey, string proxyId, string originId)
    {
        if (baseKey.Length != SecretLen)
        {
            throw new ArgumentException($"baseKey must be exactly {SecretLen} bytes, got {baseKey.Length}", nameof(baseKey));
        }

        if (!s_originRegex.IsMatch(proxyId))
        {
            throw new ArgumentException($"proxyId must match {s_originRegex}, got '{proxyId}'", nameof(proxyId));
        }

        if (!s_originRegex.IsMatch(originId))
        {
            throw new ArgumentException($"originId must match {s_originRegex}, got '{originId}'", nameof(originId));
        }

        var message = new byte[s_deriveLabel.Length + proxyId.Length + 1 + originId.Length];
        var pos = 0;
        s_deriveLabel.CopyTo(message, pos);
        pos += s_deriveLabel.Length;
        pos += System.Text.Encoding.UTF8.GetBytes(proxyId, 0, proxyId.Length, message, pos);
        message[pos++] = 0;
        System.Text.Encoding.UTF8.GetBytes(originId, 0, originId.Length, message, pos);
        return HMACSHA256.HashData(baseKey, message);
    }

    /// <summary>Builds the MAC input. <paramref name="originId"/> is folded in but never
    /// transmitted — the worker supplies its own, which is what makes a proof minted for one
    /// worker fail at every other worker even under key misconfiguration.</summary>
    public static byte[] CanonicalString(string kid, string ts, string nonce, string originId)
    {
        using var ms = new MemoryStream();
        ms.Write(s_domainPrefix);
        WriteNulSeparated(ms, kid);
        WriteNulSeparated(ms, ts);
        WriteNulSeparated(ms, nonce);
        ms.WriteByte(0);
        ms.Write(System.Text.Encoding.UTF8.GetBytes(originId));
        return ms.ToArray();

        static void WriteNulSeparated(MemoryStream stream, string field)
        {
            stream.WriteByte(0);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(field));
        }
    }

    /// <summary>Mints a proof token. <paramref name="now"/>/<paramref name="nonce"/> are
    /// injectable for tests; production callers omit both.</summary>
    public static string MintProof(byte[] secret, string kid, string originId, long? now = null, string? nonce = null)
    {
        if (!s_kidRegex.IsMatch(kid))
        {
            throw new ArgumentException($"kid must match {s_kidRegex}, got '{kid}'", nameof(kid));
        }

        if (!s_originRegex.IsMatch(originId))
        {
            throw new ArgumentException($"originId must match {s_originRegex}, got '{originId}'", nameof(originId));
        }

        var ts = (now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        nonce ??= B64(RandomNumberGenerator.GetBytes(NonceBytes));
        var mac = HMACSHA256.HashData(secret, CanonicalString(kid, ts, nonce, originId));
        return $"{Version}.{kid}.{ts}.{nonce}.{B64(mac)}";
    }

    /// <summary>
    /// Verifies a proof token, returning the claims to record. Cheap rejections run before any
    /// MAC is computed (spec §6, steps 1–4), so an unparseable header costs a few regex matches
    /// rather than a hash.
    /// </summary>
    /// <exception cref="ProofException">On any failure, carrying a §6 reason code.</exception>
    public static ProxyProofResult VerifyProof(string token, ProxyProofConfig config, NonceCache? nonceCache, long? now = null)
    {
        if (token.Length > MaxHeaderBytes)
        {
            throw new ProofException("malformed", "proof header too long");
        }

        var parts = token.Split('.');
        if (parts.Length != 5)
        {
            throw new ProofException("malformed", $"expected 5 fields, got {parts.Length}");
        }

        var (version, kid, tsRaw, nonce, macB64) = (parts[0], parts[1], parts[2], parts[3], parts[4]);
        if (version != Version)
        {
            throw new ProofException("malformed", $"unsupported version '{version}'");
        }

        if (!s_kidRegex.IsMatch(kid))
        {
            throw new ProofException("malformed", "kid charset");
        }

        if (!s_tsRegex.IsMatch(tsRaw))
        {
            throw new ProofException("malformed", "ts charset");
        }

        if (!s_nonceRegex.IsMatch(nonce))
        {
            throw new ProofException("malformed", "nonce charset");
        }

        if (!s_macRegex.IsMatch(macB64))
        {
            throw new ProofException("malformed", "mac charset");
        }

        if (!config.Secrets.TryGetValue(kid, out var entry))
        {
            throw new ProofException("unknown_kid", $"no secret for kid '{kid}'");
        }

        // Two-sided — checking only the upper bound would let a far-future timestamp pass forever.
        var current = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ts = long.Parse(tsRaw);
        var age = current - ts;
        if (age > config.SkewSeconds)
        {
            throw new ProofException("expired", $"age={age}s skew={config.SkewSeconds}s");
        }

        if (-age > config.SkewSeconds)
        {
            throw new ProofException("not_yet_valid", $"age={age}s skew={config.SkewSeconds}s");
        }

        var expected = HMACSHA256.HashData(entry.Secret, CanonicalString(kid, tsRaw, nonce, config.OriginId));
        var received = Unb64(macB64); // charset already validated above
        // kid is public, so selecting one candidate secret is a safe branch; only the resulting
        // MAC needs the constant-time compare.
        if (!CryptographicOperations.FixedTimeEquals(received, expected))
        {
            throw new ProofException("bad_mac", "signature mismatch");
        }

        if (nonceCache is not null && !nonceCache.CheckAndAdd(nonce))
        {
            throw new ProofException("replayed", "nonce already seen");
        }

        return new ProxyProofResult(Verified: true, Proxy: entry.Label, Kid: kid, OriginId: config.OriginId, Reason: "ok");
    }

    /// <summary>Parses a <c>kid:hex,kid:hex</c> secret specification — the <c>kid</c> doubles as
    /// the proxy's label, so attribution needs no extra configuration. Never a partial parse
    /// (which would silently drop a proxy's access): any malformed entry throws.</summary>
    public static Dictionary<string, ProxyProofSecret> ParseSecrets(string raw)
    {
        var result = new Dictionary<string, ProxyProofSecret>();
        foreach (var chunk in raw.Split(','))
        {
            var item = chunk.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            var colon = item.IndexOf(':');
            if (colon < 0)
            {
                throw new ArgumentException($"expected 'kid:hex', got '{item}'", nameof(raw));
            }

            var kid = item[..colon];
            var hexSecret = item[(colon + 1)..];
            if (!s_kidRegex.IsMatch(kid))
            {
                throw new ArgumentException($"kid must match {s_kidRegex}, got '{kid}'", nameof(raw));
            }

            byte[] secret;
            try
            {
                secret = Convert.FromHexString(hexSecret);
            }
            catch (FormatException exc)
            {
                throw new ArgumentException($"secret for kid '{kid}' is not valid hex", nameof(raw), exc);
            }

            if (secret.Length != SecretLen)
            {
                throw new ArgumentException($"secret for kid '{kid}' must be {SecretLen} bytes ({SecretLen * 2} hex chars), got {secret.Length}", nameof(raw));
            }

            result[kid] = new ProxyProofSecret(secret, kid);
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("no secrets parsed", nameof(raw));
        }

        return result;
    }

    /// <summary>
    /// Builds the request gate for a configured worker. <paramref name="config"/>'s
    /// <see cref="ProxyProofConfig.Mode"/> must not be <see cref="ProxyProofMode.Off"/> — an off
    /// worker installs no gate at all, so there is zero per-request cost rather than a gate that
    /// always passes.
    /// </summary>
    public static RpcHttpEndpoints.AuthenticateDelegate CreateGate(ProxyProofConfig config)
    {
        if (config.Mode == ProxyProofMode.Off)
        {
            throw new ArgumentException("CreateGate called with Mode=Off; install no gate instead", nameof(config));
        }

        var cache = config.EnableReplayCache ? new NonceCache(config.SkewSeconds, config.ReplayCapacity) : null;
        var required = config.Mode == ProxyProofMode.Require;

        return context =>
        {
            var headerValues = context.Request.Headers[ProofHeader];
            try
            {
                if (headerValues.Count == 0 || string.IsNullOrEmpty(headerValues[0]))
                {
                    throw new ProofException("no_proof", "header absent");
                }

                if (headerValues.Count > 1)
                {
                    throw new ProofException("malformed", "multiple proof headers");
                }

                var result = VerifyProof(headerValues[0]!, config, cache);
                ProxyProofResult.SetOn(context, result);
                return Task.CompletedTask;
            }
            catch (ProofException exc)
            {
                if (required)
                {
                    // Uniform message — the caller controls kid, so echoing any detail would
                    // reflect attacker-supplied text (spec §6).
                    throw new AuthFailure(AuthReason.ProxyRequired, "proxy proof required");
                }

                ProxyProofResult.SetOn(context, new ProxyProofResult(Verified: false, Proxy: "", Kid: "", OriginId: config.OriginId, Reason: exc.Reason));
                return Task.CompletedTask;
            }
        };
    }

    /// <summary>
    /// Composes a precondition <paramref name="gate"/> with an <paramref name="inner"/>
    /// authenticate delegate: runs the gate first and, on failure, never invokes
    /// <paramref name="inner"/> at all; otherwise delegates to <paramref name="inner"/>
    /// unchanged. <paramref name="inner"/> may be <see langword="null"/> — "proof-only" means
    /// "only my proxy may call this worker", with user identity handled entirely upstream (spec
    /// §8). See this class's doc comment for why C# needs no special combinator type here, unlike
    /// Python's <c>require_all</c>/<c>PreconditionGate</c>.
    /// </summary>
    public static RpcHttpEndpoints.AuthenticateDelegate RequireAll(RpcHttpEndpoints.AuthenticateDelegate gate, RpcHttpEndpoints.AuthenticateDelegate? inner) =>
        async context =>
        {
            await gate(context).ConfigureAwait(false);
            if (inner is not null)
            {
                await inner(context).ConfigureAwait(false);
            }
        };
}

/// <summary>Worker-side proxy-proof posture. See <c>docs/proxy-proof-spec.md</c> §7.</summary>
public enum ProxyProofMode
{
    /// <summary>The gate is not installed. Zero per-request cost.</summary>
    Off,

    /// <summary>The proof is verified and recorded but never denies — a rollout/rollback lever.</summary>
    Allow,

    /// <summary>Verification failure returns 401.</summary>
    Require,
}

/// <summary>One configured proxy secret: the raw 32-byte key plus the operator-facing label
/// attributed to whichever secret verifies a given proof (spec §5.2 — attribution derives from
/// which secret verified, never from the request's claimed <c>kid</c>).</summary>
public sealed record ProxyProofSecret(byte[] Secret, string Label);

/// <summary>
/// Worker-side proxy-proof configuration — validated eagerly at construction so a misconfigured
/// worker never starts (spec §5.3: "a shared secret spans two independently deployed processes;
/// a lax parse means a typo silently produces different keys on each side, and require mode
/// becomes a 100% rejection outage with no diagnostic").
/// </summary>
public sealed class ProxyProofConfig
{
    public ProxyProofMode Mode { get; }
    public string OriginId { get; }
    public IReadOnlyDictionary<string, ProxyProofSecret> Secrets { get; }
    public int SkewSeconds { get; }
    public int ReplayCapacity { get; }
    public bool EnableReplayCache { get; }

    private static readonly Regex s_originRegex = new(@"\A[A-Za-z0-9._:/-]{1,255}\z", RegexOptions.Compiled);
    private static readonly Regex s_kidRegex = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled);

    public ProxyProofConfig(
        ProxyProofMode mode,
        string originId = "",
        IReadOnlyDictionary<string, ProxyProofSecret>? secrets = null,
        int skewSeconds = 30,
        int replayCapacity = NonceCache.DefaultCapacity,
        bool enableReplayCache = true)
    {
        Mode = mode;
        OriginId = originId;
        Secrets = secrets ?? new Dictionary<string, ProxyProofSecret>();
        SkewSeconds = skewSeconds;
        ReplayCapacity = replayCapacity;
        EnableReplayCache = enableReplayCache;

        if (mode == ProxyProofMode.Off)
        {
            return;
        }

        if (!s_originRegex.IsMatch(originId))
        {
            throw new ArgumentException($"originId is required in {mode} mode and must match {s_originRegex}, got '{originId}'", nameof(originId));
        }

        if (Secrets.Count == 0)
        {
            throw new ArgumentException($"at least one secret is required in {mode} mode", nameof(secrets));
        }

        foreach (var (kid, entry) in Secrets)
        {
            if (!s_kidRegex.IsMatch(kid))
            {
                throw new ArgumentException($"kid must match {s_kidRegex}, got '{kid}'", nameof(secrets));
            }

            if (entry.Secret.Length != 32)
            {
                throw new ArgumentException($"secret for kid '{kid}' must be exactly 32 bytes, got {entry.Secret.Length}", nameof(secrets));
            }
        }

        if (skewSeconds <= 0)
        {
            throw new ArgumentException($"skewSeconds must be positive, got {skewSeconds}", nameof(skewSeconds));
        }
    }
}

/// <summary>
/// A proof failure carrying its §6 reason code. Never exposed to the caller verbatim (see
/// <see cref="ProxyProof.CreateGate"/>, which collapses every reason onto
/// <see cref="AuthReason.ProxyRequired"/> with a fixed detail in <c>require</c> mode) — safe to
/// log, never safe to echo, since <c>kid</c> is caller-controlled.
/// </summary>
public sealed class ProofException(string reason, string detail) : Exception(detail)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// A verified (or attempted) proof's attribution, surfaced in claims never in
/// <c>domain</c>/<c>principal</c> — those belong to the end user (spec §9). Stash via
/// <see cref="SetOn"/> from a proxy-proof gate; read back via <see cref="GetFrom"/> from
/// application code sharing the same <see cref="HttpContext"/>. Same
/// <c>HttpContext.Items</c>-based convention as <see cref="MtlsIdentity"/>/<see cref="AuthIdentity"/>
/// — this port has no full claims-propagation-to-dispatch mechanism yet (see those types' doc
/// comments for the same architecture note).
/// </summary>
public sealed record ProxyProofResult(bool Verified, string Proxy, string Kid, string OriginId, string Reason)
{
    private const string ItemsKey = "vgi_rpc.proxy_proof";

    public static void SetOn(HttpContext context, ProxyProofResult result) => context.Items[ItemsKey] = result;

    public static ProxyProofResult? GetFrom(HttpContext context) => context.Items[ItemsKey] as ProxyProofResult;
}

/// <summary>
/// Thread-safe, bounded set of recently-seen proof nonces (spec §10). Entries expire after
/// <c>ttlSeconds</c> and the total is capped at <c>capacity</c>. Because every entry shares the
/// same TTL, insertion order is also expiry order, so expired entries are always a prefix of the
/// backing <see cref="OrderedDictionary{TKey,TValue}"/> — sweeping from the front until the first
/// live entry is both exact and amortized O(1) per insertion, mirroring Python's
/// <c>collections.OrderedDict</c>-based implementation exactly.
/// </summary>
public sealed class NonceCache
{
    public const int DefaultCapacity = 100_000;

    private readonly double _ttlSeconds;
    private readonly int _capacity;
    private readonly Func<double> _clock;
    private readonly System.Collections.Generic.OrderedDictionary<string, double> _entries = [];
    private readonly Lock _lock = new();
    private long _evicted;
    private long _replays;

    /// <param name="ttlSeconds">Retention window, normally the proof acceptance skew.</param>
    /// <param name="capacity">Hard upper bound on retained entries.</param>
    /// <param name="clock">Monotonic time source (seconds), injectable for tests. Defaults to a
    /// monotonic wall-independent clock — an NTP step must never expire or resurrect entries.</param>
    public NonceCache(double ttlSeconds, int capacity = DefaultCapacity, Func<double>? clock = null)
    {
        if (ttlSeconds <= 0)
        {
            throw new ArgumentException($"ttlSeconds must be positive, got {ttlSeconds}", nameof(ttlSeconds));
        }

        if (capacity <= 0)
        {
            throw new ArgumentException($"capacity must be positive, got {capacity}", nameof(capacity));
        }

        _ttlSeconds = ttlSeconds;
        _capacity = capacity;
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
    }

    /// <summary>Atomically tests whether <paramref name="nonce"/> is fresh, remembering it if so.
    /// Test-and-insert is one locked operation deliberately — a separate contains-then-add would
    /// let two concurrent replays of the same nonce both observe "not seen".</summary>
    /// <returns><see langword="true"/> if the nonce had not been seen (now remembered);
    /// <see langword="false"/> if it is a replay.</returns>
    public bool CheckAndAdd(string nonce)
    {
        var now = _clock();
        lock (_lock)
        {
            Sweep(now);
            if (_entries.ContainsKey(nonce))
            {
                _replays++;
                return false;
            }

            // Evict oldest rather than refuse — a burst past capacity is an availability
            // problem, not an authentication one, and the timestamp window still bounds the
            // evicted nonce's usefulness.
            while (_entries.Count >= _capacity)
            {
                _entries.RemoveAt(0);
                _evicted++;
            }

            _entries[nonce] = now + _ttlSeconds;
            return true;
        }
    }

    /// <summary>Drops expired entries. Caller holds the lock.</summary>
    private void Sweep(double now)
    {
        while (_entries.Count > 0)
        {
            var (_, expiresAt) = _entries.GetAt(0);
            if (expiresAt > now)
            {
                break; // uniform TTL ⇒ insertion order is expiry order; first live entry ends the sweep
            }

            _entries.RemoveAt(0);
        }
    }

    /// <summary>Counters for observability — <c>overflow_evictions</c> rising means
    /// <c>capacity</c> is undersized for the offered rate (or someone is deliberately flooding
    /// distinct nonces); either way it should be alerted on.</summary>
    public (int Size, int Capacity, long ReplaysRejected, long OverflowEvictions) Stats()
    {
        lock (_lock)
        {
            return (_entries.Count, _capacity, _replays, _evicted);
        }
    }
}
