namespace QueryFarm.VgiRpc.Identity;

/// <summary>Fixed-window request limiter, keyed by caller.</summary>
/// <remarks>
/// <para>
/// Present because introspection is a credential-to-identity oracle even when correctly
/// restricted: an allowlisted caller whose own credential leaks can still test guesses. Rate
/// limiting does not close that, it bounds it.
/// </para>
/// <para>
/// Fixed-window rather than a token bucket: a window admits at most twice the rate across a
/// boundary, which is a rounding error here, and the state is two integers per caller rather
/// than a float that has to be aged.
/// </para>
/// <para>
/// A near-duplicate of <c>QueryFarm.VgiRpc.Http.IntrospectionRateLimiter</c>, which limits the
/// predecessor HTTP JSON route. The duplication is one-directional and unavoidable as long as
/// both exist: the HTTP assembly depends on this core one, never the reverse, so the protocol's
/// limiter cannot live over there. If the HTTP route is ever retired in favour of this protocol,
/// retire that copy with it.
/// </para>
/// </remarks>
/// <param name="perWindow">Requests admitted per caller per window.</param>
/// <param name="windowSeconds">Window length in seconds.</param>
public sealed class IdentityRateLimiter(int perWindow, double windowSeconds = 1.0)
{
    private readonly Dictionary<string, int> _counts = [];
    private readonly Lock _lock = new();
    private double _windowStart;

    /// <summary>
    /// How many callers the limiter is currently tracking. Exposed because the whole-map-reset
    /// behaviour below is a security property (an attacker cycling keys must not be able to grow
    /// this map without bound) and a property that cannot be observed cannot be tested.
    /// </summary>
    public int TrackedKeyCount
    {
        get
        {
            lock (_lock)
            {
                return _counts.Count;
            }
        }
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="key"/> may make a request in
    /// the current window.</summary>
    /// <param name="key">The caller principal the budget is charged to.</param>
    /// <param name="now">Monotonic seconds, for tests. Defaults to the process clock.</param>
    /// <remarks>
    /// <c>Environment.TickCount64</c> rather than wall time: a limiter driven by a clock that can
    /// step backwards over an NTP correction would hand out a free window.
    /// </remarks>
    public bool Allow(string key, double? now = null)
    {
        var current = now ?? Environment.TickCount64 / 1000.0;
        lock (_lock)
        {
            if (current - _windowStart >= windowSeconds)
            {
                // Whole-map reset rather than per-key ageing: a caller cycling keys cannot grow
                // the map beyond one window's worth.
                _counts.Clear();
                _windowStart = current;
            }

            var count = _counts.GetValueOrDefault(key, 0);
            if (count >= perWindow)
            {
                return false;
            }

            _counts[key] = count + 1;
            return true;
        }
    }
}
