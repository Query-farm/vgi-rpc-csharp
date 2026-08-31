using System.Collections.Concurrent;
using QueryFarm.VgiRpc.Streaming;

namespace QueryFarm.VgiRpc.Http;

/// <summary>
/// Holds live <see cref="IRpcStream"/> instances (and their mutable <see cref="StreamState"/>)
/// across the separate HTTP requests one stream call spans — <c>POST {method}/init</c> opens the
/// entry, each <c>POST {method}/exchange</c> looks it up, and it's removed on completion, error,
/// cancel, or TTL expiry.
///
/// This is a deliberately simpler design than the canonical Python repo's split call/cursor
/// AEAD-sealed tokens (which carry the actual serialized <c>StreamState</c> bytes so any request
/// can land on any stateless worker process — see <c>vgi_rpc/http/server/_state_token.py</c>).
/// Serializing an arbitrary C# <see cref="StreamState"/> subclass generically would need real
/// reflection-based state (de)serialization infrastructure with no conformance-test pressure
/// forcing that design yet; keeping the live object server-side and only sealing a random
/// <c>call_id</c> into the token (see <see cref="Crypto"/>) is functionally equivalent for a
/// single-process deployment — which is what this port has (no multi-worker HTTP hosting yet) —
/// at the cost of streams not surviving a process restart or being resumable on a different node.
/// Revisit if/when this port needs horizontal HTTP scaling.
/// </summary>
public sealed class StreamCallRegistry(TimeSpan? ttl = null)
{
    private readonly ConcurrentDictionary<string, Entry> _calls = new();
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(10);

    private sealed class Entry(IRpcStream stream, string principalKey)
    {
        public IRpcStream Stream { get; } = stream;
        public string PrincipalKey { get; } = principalKey;
        public DateTimeOffset LastAccess { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Registers a newly-dispatched stream under a fresh random call id (16 bytes, hex-encoded key).</summary>
    public string Register(IRpcStream stream, string principalKey = "\0anonymous")
    {
        var callId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var key = Convert.ToHexStringLower(callId);
        _calls[key] = new Entry(stream, principalKey);
        return key;
    }

    /// <summary>Looks up a live stream by its call-id key, refreshing its TTL on a hit. Returns
    /// <see langword="false"/> for an unknown or TTL-expired key (the caller should surface this
    /// as a session-lost error — the same wire shape a sticky-session eviction produces).</summary>
    public bool TryGet(string key, out IRpcStream stream) => TryGet(key, "\0anonymous", out stream);

    /// <summary>Looks up a live stream only when the current request has the same stable
    /// principal/evidence partition as the request that registered it.</summary>
    public bool TryGet(string key, string principalKey, out IRpcStream stream)
    {
        if (_calls.TryGetValue(key, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.LastAccess >= _ttl)
            {
                _calls.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                stream = null!;
                return false;
            }

            // A cross-identity lookup is a miss, not an eviction. Otherwise a
            // caller who learns another stream's opaque id could terminate the
            // victim's live stream even though AAD correctly prevents replay.
            if (!StringComparer.Ordinal.Equals(entry.PrincipalKey, principalKey))
            {
                stream = null!;
                return false;
            }

            entry.LastAccess = DateTimeOffset.UtcNow;
            stream = entry.Stream;
            return true;
        }

        stream = null!;
        return false;
    }

    /// <summary>Removes a call's entry — on normal completion, cancel, or an error that ends the stream.</summary>
    public void Remove(string key) => _calls.TryRemove(key, out _);
}
