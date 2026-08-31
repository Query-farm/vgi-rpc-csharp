using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Sticky-session machinery for the HTTP transport — port of the canonical Python repo's
/// <c>vgi_rpc.http.server._sticky</c>. Lets an RPC method bind a handle-bearing object (a DB
/// cursor, a loaded model, an open file) to the worker process that opened it, keyed by a
/// short-lived AEAD-sealed token the client echoes on subsequent requests. See
/// <c>docs/sticky-sessions-spec.md</c> for the normative wire contract and
/// <c>docs/roadmap.md</c> M10 for what this port implements.
///
/// <para><b>HTTP-only and opt-in</b>, matching Python: construct a <see cref="StickySessionRegistry"/>
/// and pass it to <c>MapVgiRpc(..., sticky: registry)</c>. Outside that, the wire is
/// byte-identical to the non-sticky framework.</para>
///
/// <para><b>Architecture simplification over Python.</b> Python threads sticky state through
/// <c>contextvars</c> because its WSGI middleware doesn't get an explicit per-call context object
/// — <c>CallContext</c> reads ambient state set by <c>_StickyMiddleware.process_request</c>. This
/// port's <see cref="ICallContext"/> is already an explicit object passed into every
/// <c>InvokeAsync</c> call (unary and per stream turn), so <c>RpcHttpEndpoints</c> can resolve the
/// session, build a concrete sticky-aware call context carrying it directly, and read back what
/// the method did (minted token / closed flag) after the call returns — no contextvar dance
/// needed at all.</para>
/// </summary>
public static class StickySessions
{
    /// <summary>Client opt-in: methods may call <c>ctx.OpenSession</c> only on requests carrying this.</summary>
    public const string SessionAcceptHeader = "VGI-Session-Accept";

    /// <summary>Request: echoes a previously-minted token. Response: a newly-minted token.</summary>
    public const string SessionHeader = "VGI-Session";

    /// <summary>Response-only: tells the client to drop its captured token.</summary>
    public const string SessionCloseHeader = "VGI-Session-Close";

    /// <summary>Prefix for the once-only, session-opening-response-only echo headers (§2.4).</summary>
    public const string EchoHeaderPrefix = "VGI-Echo-";

    public const string StickyEnabledHeader = "VGI-Sticky-Enabled";
    public const string StickyDefaultTtlHeader = "VGI-Sticky-Default-TTL";
    public const string StickyEchoHeadersHeader = "VGI-Sticky-Echo-Headers";

    /// <summary><c>DELETE {prefix}/__session__</c> — idempotent best-effort session teardown (§2.5).</summary>
    public const string SessionEndpoint = "__session__";

    private const int SessionIdLen = 12; // bytes → 24 hex chars, matching Python
    private const byte TokenVersion = 1;

    // AAD prefix — deliberately the SAME literal Python's stream-state token and session token
    // share (spec §3: "Both Python's stream token and the session token share the same envelope
    // construction"). The v5 prefix adds the verified peer-evidence digest after the stable
    // application/peer principal, preventing a token issued under one transport identity from
    // being replayed after the surrounding evidence changes.
    private static readonly byte[] s_aadPrefix = System.Text.Encoding.ASCII.GetBytes("vgi_rpc.state.v4\0");
    private static readonly byte[] s_boundAadPrefix = System.Text.Encoding.ASCII.GetBytes("vgi_rpc.state.v5\0");
    private static readonly byte[] s_callAadPrefix = System.Text.Encoding.ASCII.GetBytes("vgi_rpc.call.v1\0");
    private static readonly byte[] s_boundCallAadPrefix = System.Text.Encoding.ASCII.GetBytes("vgi_rpc.call.v2\0");

    /// <summary>
    /// Computes the AAD binding a session token to its issuing principal — mirrors Python's
    /// <c>_compute_aad</c> exactly: anonymous requests get the literal tail <c>\0anonymous</c>;
    /// authenticated requests get <c>\x01 domain \0 principal</c>. Only <c>Domain</c>/
    /// <c>Principal</c> feed the AAD — never volatile claims (spec §3.1).
    /// </summary>
    public static byte[] ComputeAad(AuthIdentity? identity)
        => ComputeIdentityAad(identity, s_aadPrefix, s_boundAadPrefix);

    /// <summary>Computes principal/evidence AAD for stream call tokens. Its distinct prefix
    /// prevents a call token from being accepted as a state/sticky token.</summary>
    public static byte[] ComputeCallAad(AuthIdentity? identity)
        => ComputeIdentityAad(identity, s_callAadPrefix, s_boundCallAadPrefix);

    private static byte[] ComputeIdentityAad(AuthIdentity? identity, byte[] legacyPrefix, byte[] boundPrefix)
    {
        if (identity is null || !identity.Authenticated)
        {
            var anonymous = new List<byte>([.. (identity?.PeerEvidenceBinding is null ? legacyPrefix : boundPrefix), 0, .. "anonymous"u8]);
            if (identity?.PeerEvidenceBinding is not null)
            {
                anonymous.Add(0);
                anonymous.AddRange(System.Text.Encoding.UTF8.GetBytes(identity.PeerEvidenceBinding));
            }
            return [.. anonymous];
        }

        var prefix = identity.PeerEvidenceBinding is null ? legacyPrefix : boundPrefix;
        var aad = new List<byte>([.. prefix, 1, .. System.Text.Encoding.UTF8.GetBytes(identity.Domain), 0, .. System.Text.Encoding.UTF8.GetBytes(identity.Principal)]);
        if (identity.PeerEvidenceBinding is not null)
        {
            aad.Add(0);
            aad.AddRange(System.Text.Encoding.UTF8.GetBytes(identity.PeerEvidenceBinding));
        }
        return [.. aad];
    }

    /// <summary>Seals a session token. Returns the value for the <see cref="SessionHeader"/>.</summary>
    public static string SealToken(string serverId, byte[] sessionId, long expiresAtUnixSeconds, byte[] tokenKey, ReadOnlySpan<byte> aad)
    {
        var serverIdBytes = System.Text.Encoding.ASCII.GetBytes(serverId);
        if (serverIdBytes.Length > 255)
        {
            throw new ArgumentException($"server_id too long ({serverIdBytes.Length} bytes); max 255", nameof(serverId));
        }

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var plaintext = new MemoryStream();
        Span<byte> u64 = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(u64, (ulong)createdAt);
        plaintext.Write(u64);
        plaintext.WriteByte((byte)serverIdBytes.Length);
        plaintext.Write(serverIdBytes);
        plaintext.Write(sessionId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(u64, (ulong)expiresAtUnixSeconds);
        plaintext.Write(u64);

        var sealedBytes = Crypto.Seal(plaintext.ToArray(), tokenKey, aad, TokenVersion);
        return Convert.ToBase64String(sealedBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Opens a session token, returning <c>(serverId, sessionId, expiresAt)</c>. Every
    /// failure mode — malformed, tampered, wrong-key, wrong-principal (AAD mismatch) — surfaces
    /// identically as <see cref="SessionLostException"/>, matching Python exactly (§3: "all
    /// failure modes are indistinguishable from the caller's perspective").</summary>
    public static (string ServerId, byte[] SessionId, long ExpiresAt) OpenToken(string token, byte[] tokenKey, ReadOnlySpan<byte> aad)
    {
        byte[] raw;
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            raw = Convert.FromBase64String(padded);
        }
        catch (FormatException exc)
        {
            throw new SessionLostException("malformed session token: " + exc.Message);
        }

        byte[] plaintext;
        try
        {
            plaintext = Crypto.Open(raw, tokenKey, aad, TokenVersion);
        }
        catch (Crypto.SealException)
        {
            throw new SessionLostException("session token verification failed");
        }

        const int prefixLen = 9; // u64 createdAt + u8 serverIdLen
        if (plaintext.Length < prefixLen)
        {
            throw new SessionLostException("malformed session token");
        }

        var serverIdLen = plaintext[8];
        var sidPos = prefixLen + serverIdLen;
        var endPos = sidPos + SessionIdLen + 8;
        if (plaintext.Length != endPos)
        {
            throw new SessionLostException("malformed session token");
        }

        var serverId = System.Text.Encoding.ASCII.GetString(plaintext, prefixLen, serverIdLen);
        var sessionId = plaintext[sidPos..(sidPos + SessionIdLen)];
        var expiresAt = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(plaintext.AsSpan(sidPos + SessionIdLen, 8));
        return (serverId, sessionId, expiresAt);
    }

    /// <summary>Derives the registry-partitioning principal key for a request — mirrors Python's
    /// <c>_StickyMiddleware._principal_key</c>. Defense-in-depth behind the AAD binding (§3.1):
    /// AAD already makes cross-principal replay fail decryption, so this is a belt-and-braces
    /// registry-level check, not the primary control.</summary>
    public static string PrincipalKey(AuthIdentity? identity) =>
        identity is null || !identity.Authenticated
            ? identity?.PeerEvidenceBinding is { } binding ? $"\0anonymous\0{binding}" : "\0anonymous"
            : identity.PeerEvidenceBinding is null
            ? $"{identity.Domain}\0{identity.Principal}"
            : $"{identity.Domain}\0{identity.Principal}\0{identity.PeerEvidenceBinding}";
}

/// <summary>
/// Stable identity an authenticate delegate resolved for the current request. Set it on
/// <c>HttpContext.Items</c> (see <see cref="SetOn"/>) from within an
/// <see cref="RpcHttpEndpoints.AuthenticateDelegate"/> to bind sticky-session tokens (and any
/// future dispatch-context propagation) to that identity — mirrors Python's
/// <c>AuthContext.domain</c>/<c>.principal</c>, narrowed to just the two fields the sticky-session
/// AAD binds (spec §3.1: "Only domain and principal feed the AAD. Claims do not.").
///
/// <para>None of this port's existing authenticators (<see cref="BearerAuth"/>,
/// <c>QueryFarm.VgiRpc.Http.OAuth.JwtAuth</c>, <see cref="MtlsAuth"/>) populate this yet —
/// each validates a credential without extracting a portable "who" today. A production deployment
/// wiring sticky sessions behind one of them should have its own authenticate delegate call
/// <see cref="SetOn"/> after successful validation; the conformance worker's
/// <c>--sticky-auth</c> flag does exactly that as a worked example.</para>
/// </summary>
public sealed record AuthIdentity(
    string Domain,
    string Principal,
    string? PeerEvidenceBinding = null,
    bool Authenticated = true)
{
    private const string ItemsKey = "vgi_rpc.auth.identity";

    public static void SetOn(
        HttpContext context,
        string domain,
        string principal,
        string? peerEvidenceBinding = null,
        bool authenticated = true) =>
        context.Items[ItemsKey] = new AuthIdentity(domain, principal, peerEvidenceBinding, authenticated);

    public static AuthIdentity? GetFrom(HttpContext context) => context.Items[ItemsKey] as AuthIdentity;
}

/// <summary>A single live sticky session in the per-worker registry.</summary>
public sealed class StickySessionEntry
{
    public required object State { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    public required string PrincipalKey { get; init; }

    /// <summary>Serializes concurrent dispatch on this session (spec §5) — same-session calls
    /// queue behind this; different sessions never contend on it. <see cref="SemaphoreSlim"/>
    /// rather than a re-entrant <c>lock</c>/<c>Monitor</c> because dispatch is <c>await</c>-based
    /// (holding a <c>lock</c> across an <c>await</c> is unsafe — the releasing thread may differ
    /// from the acquiring one).</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

/// <summary>
/// Per-worker in-process registry of live sticky sessions, plus a background reaper evicting on
/// TTL. One instance is shared by every request <see cref="RpcHttpEndpoints.MapVgiRpc"/> dispatches
/// once constructed and passed as <c>MapVgiRpc(..., sticky: registry)</c> — construct one,
/// keep a reference for <see cref="Drain"/>/<see cref="Shutdown"/>, and pass it in.
///
/// <para>Unlike Python's <c>drain_handle(app)</c> (which walks Falcon's middleware list to find
/// the registry after the fact, because Falcon's app construction doesn't hand back named
/// component references), this port's caller already holds the registry directly — no lookup
/// indirection needed. That's a code simplification, not a behavior difference: <see cref="Drain"/>/
/// <see cref="Shutdown"/> do exactly what Python's <c>DrainHandle.drain</c>/<c>.shutdown</c> do.</para>
/// </summary>
public sealed class StickySessionRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, StickySessionEntry> _entries = new();
    private readonly Timer _reaper;
    private volatile bool _draining;

    /// <summary>Default session TTL applied when <c>OpenSession(state, ttl: null)</c> is called.</summary>
    public TimeSpan DefaultTtl { get; }

    /// <summary>Headers to emit (as <c>VGI-Echo-&lt;name&gt;</c>) on every session-opening response —
    /// see spec §2.4. Empty ⇒ no echo headers.</summary>
    public IReadOnlyDictionary<string, string> EchoHeaders { get; }

    public bool Draining => _draining;

    public StickySessionRegistry(TimeSpan? defaultTtl = null, IReadOnlyDictionary<string, string>? echoHeaders = null, TimeSpan? reaperTick = null)
    {
        DefaultTtl = defaultTtl ?? TimeSpan.FromSeconds(300);
        EchoHeaders = echoHeaders ?? new Dictionary<string, string>();
        var tick = reaperTick ?? TimeSpan.FromSeconds(1);
        _reaper = new Timer(_ => DrainExpired(), null, tick, tick);
    }

    /// <summary>Flips the drain flag — subsequent <see cref="Open"/> calls throw
    /// <see cref="ServerDrainingException"/>. Existing sessions continue to serve until TTL or
    /// explicit close (spec §7).</summary>
    public void Drain() => _draining = true;

    /// <summary>Clears the drain flag. Not part of the normative spec — exposed for
    /// conformance-fixture / test-admin use (mirrors the reference repo's
    /// <c>/__test_drain__</c> admin endpoint clearing it between test runs).</summary>
    public void ClearDrain() => _draining = false;

    public (byte[] SessionId, DateTimeOffset ExpiresAt) Open(object state, TimeSpan? ttl, string principalKey)
    {
        if (_draining)
        {
            throw new ServerDrainingException("server is draining — new sessions are rejected");
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(ttl ?? DefaultTtl);
        var sessionId = RandomNumberGenerator.GetBytes(12);
        var entry = new StickySessionEntry { State = state, ExpiresAt = expiresAt, PrincipalKey = principalKey };
        _entries[Convert.ToHexStringLower(sessionId)] = entry;
        return (sessionId, expiresAt);
    }

    /// <summary>Looks up an entry. Returns <see langword="null"/> on miss, expiry (evicted
    /// in-line), or principal mismatch — all three are observationally identical to the caller,
    /// matching Python.</summary>
    public StickySessionEntry? TryGet(byte[] sessionId, string principalKey)
    {
        var key = Convert.ToHexStringLower(sessionId);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            if (_entries.TryRemove(key, out var expired))
            {
                CloseStateSuppressed(expired.State);
            }

            return null;
        }

        return entry.PrincipalKey != principalKey ? null : entry;
    }

    /// <summary>Removes a session and disposes its state. Returns <see langword="true"/> on hit.</summary>
    public bool Close(byte[] sessionId)
    {
        if (!_entries.TryRemove(Convert.ToHexStringLower(sessionId), out var entry))
        {
            return false;
        }

        CloseStateSuppressed(entry.State);
        return true;
    }

    /// <summary>Evicts every session past its TTL. Returns the eviction count.</summary>
    public int DrainExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _entries.Where(kv => kv.Value.ExpiresAt < now).Select(kv => kv.Key).ToList();
        var count = 0;
        foreach (var key in expiredKeys)
        {
            if (_entries.TryRemove(key, out var entry))
            {
                CloseStateSuppressed(entry.State);
                count++;
            }
        }

        return count;
    }

    /// <summary>Disposes every live session's state and clears the registry — call on app
    /// shutdown after an operator-controlled drain grace period (spec §7).</summary>
    public void Shutdown()
    {
        var entries = _entries.ToArray();
        _entries.Clear();
        foreach (var (_, entry) in entries)
        {
            CloseStateSuppressed(entry.State);
        }
    }

    public int Count => _entries.Count;

    /// <summary>Disposes <paramref name="state"/> if it implements <see cref="IDisposable"/> or
    /// <see cref="IAsyncDisposable"/> — this port's idiomatic translation of Python's duck-typed
    /// "state objects with a close() method get it invoked" convention. Exceptions are suppressed
    /// (cleanup during eviction must never crash the reaper or a request thread).</summary>
    private static void CloseStateSuppressed(object state)
    {
        try
        {
            switch (state)
            {
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                case IAsyncDisposable asyncDisposable:
                    // Fire-and-forget is intentional here: eviction paths (TryGet, DrainExpired,
                    // the reaper timer callback) are synchronous, and blocking on async disposal
                    // from those contexts risks deadlocking the reaper thread.
                    _ = asyncDisposable.DisposeAsync().AsTask();
                    break;
            }
        }
        catch
        {
            // Suppressed — matches Python's _close_state_suppressed (log-and-continue). No
            // ILogger threaded through the registry today; a real deployment with a state class
            // whose Dispose can throw should make Dispose itself defensive.
        }
    }

    public void Dispose() => _reaper.Dispose();
}
