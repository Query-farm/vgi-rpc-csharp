using System.Globalization;

namespace QueryFarm.VgiRpc.External;

/// <summary>A snapshot of an <see cref="ObjectCache"/>'s counters.</summary>
/// <param name="ResidentBytes">Bytes of values currently retained.</param>
/// <param name="Entries">Number of retained values.</param>
/// <param name="MaxBytes">The byte bound.</param>
/// <param name="Hits">Lookups answered from the cache.</param>
/// <param name="Misses">Lookups that fetched or joined a fetch (aged-out entries included).</param>
/// <param name="Evictions">Entries dropped to stay under the bound, or aged out.</param>
/// <param name="Fetches">Fetches actually performed (single-flight joins excluded).</param>
public sealed record ObjectCacheStats(
    long ResidentBytes, long Entries, long MaxBytes, long Hits, long Misses, long Evictions, long Fetches);

/// <summary>
/// The per-process object cache (<c>STATE_REF_V1.md</c> §11): a byte-bounded, single-flight LRU
/// of verified object values keyed by <c>object_id</c>.
/// </summary>
/// <remarks>
/// <para>Required framework behaviour, not wire protocol. The rules, all required:</para>
/// <list type="number">
/// <item><b>Keyed by <c>object_id</c>, never by a digest.</b> Grants mint objects for claimed
/// hashes without seeing the bytes, so a digest-keyed cache would hand one principal's value to
/// another who merely knows its hash. An <c>object_id</c> only reaches a server inside a reference
/// sealed for the caller.</item>
/// <item><b>Single-flight.</b> Concurrent lookups of one <c>object_id</c> share one fetch.</item>
/// <item><b>Byte-bounded LRU</b> (<c>VGI_RPC_OBJECT_CACHE_BYTES</c>, default
/// <c>min(1 GiB, 40%</c> of the cgroup memory limit, else of physical memory<c>)</c>). A value
/// larger than the bound is returned but not retained.</item>
/// <item><b>Callers hold the <c>object_id</c>, never the value</b>, and look it up again on use,
/// so resident bytes stay bounded by this cache alone.</item>
/// <item><b>One-day age.</b> A hit on an entry fetched more than <see cref="MaxAge"/> ago is a
/// miss: an object may be deleted by the bucket lifecycle after a day, and the cache must not
/// outlive it by more than that.</item>
/// </list>
/// <para>Values are the verified bytes the caller's fetch returns; the cache never inspects them.
/// Thread-safe.</para>
/// </remarks>
public sealed class ObjectCache
{
    /// <summary>Environment variable overriding the default cache's byte bound.</summary>
    public const string BytesEnvironmentVariable = "VGI_RPC_OBJECT_CACHE_BYTES";

    /// <summary>The default age after which an entry is treated as a miss: one day.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(1);

    private const long OneGiB = 1L << 30;

    private static readonly Lazy<ObjectCache> s_default = new(() => new ObjectCache(DefaultBytes()));

    private readonly object _lock = new();
    private readonly LinkedList<(string Id, byte[] Value, DateTimeOffset FetchedAt)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Id, byte[] Value, DateTimeOffset FetchedAt)>> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<byte[]>> _flights = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private long _resident;
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _fetches;

    /// <summary>Creates a cache.</summary>
    /// <param name="maxBytes">Byte bound on retained values. <c>0</c> retains nothing (every
    /// lookup fetches, still single-flight).</param>
    /// <param name="maxAge">Entries fetched longer ago than this are misses (default one day).</param>
    /// <param name="clock">Wall-clock source, injectable for tests.</param>
    public ObjectCache(long maxBytes, TimeSpan? maxAge = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        MaxBytes = maxBytes;
        MaxAge = maxAge ?? DefaultMaxAge;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The process-wide cache, created on first use with <see cref="DefaultBytes"/>.</summary>
    public static ObjectCache Default => s_default.Value;

    /// <summary>The byte bound.</summary>
    public long MaxBytes { get; }

    /// <summary>The age after which an entry is a miss.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>Raised when a value is too large to retain (resolved but not kept), with its size.
    /// With nothing subscribed the warning goes to standard error.</summary>
    public event Action<long>? NotRetained;

    /// <summary>Returns the default bound: <see cref="BytesEnvironmentVariable"/> when set, else
    /// <c>min(1 GiB, 40%</c> of the cgroup memory limit, else of the memory .NET reports
    /// available<c>)</c>.</summary>
    /// <param name="environment">Reads an environment variable; defaults to the process
    /// environment.</param>
    /// <exception cref="ArgumentException">The variable is set but is not a non-negative
    /// integer.</exception>
    public static long DefaultBytes(Func<string, string?>? environment = null)
    {
        var raw = (environment ?? Environment.GetEnvironmentVariable)(BytesEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!long.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new ArgumentException($"{BytesEnvironmentVariable} must be a non-negative integer, got '{raw}'");
            }

            return value;
        }

        var memory = CgroupMemoryLimit() ?? PhysicalMemory();
        return memory is { } bytes ? Math.Min(OneGiB, bytes / 100 * 40) : OneGiB;
    }

    /// <summary>A snapshot of the counters.</summary>
    public ObjectCacheStats Stats()
    {
        lock (_lock)
        {
            return new ObjectCacheStats(_resident, _entries.Count, MaxBytes, _hits, _misses, _evictions, _fetches);
        }
    }

    /// <summary>Drops every entry.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lru.Clear();
            _resident = 0;
        }
    }

    /// <summary>Moves every entry's fetch time back by <paramref name="by"/> (test hook for the
    /// one-day rule, the conformance method <c>object_cache_age</c>).</summary>
    /// <returns>The number of entries moved.</returns>
    public int AgeEntries(TimeSpan by)
    {
        lock (_lock)
        {
            for (var node = _lru.First; node is not null; node = node.Next)
            {
                node.Value = node.Value with { FetchedAt = node.Value.FetchedAt - by };
            }

            return _entries.Count;
        }
    }

    /// <summary>Returns the value for <paramref name="objectId"/>, fetching it at most once across
    /// concurrent callers.</summary>
    /// <param name="objectId">The storage id (the cache key).</param>
    /// <param name="fetch">Produces the verified value on a miss. Exactly one caller runs it per
    /// miss; concurrent callers share its result or its exception.</param>
    /// <param name="cancellationToken">Cancels this caller's wait. A fetch already running for
    /// other callers keeps running.</param>
    /// <returns>The value, and whether it came from the cache rather than from a fetch.</returns>
    public async Task<(byte[] Value, bool Hit)> GetOrFetchAsync(
        string objectId, Func<CancellationToken, Task<byte[]>> fetch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(fetch);
        Task<byte[]> flight;
        TaskCompletionSource<byte[]>? leader = null;
        lock (_lock)
        {
            if (_entries.TryGetValue(objectId, out var node))
            {
                if (_clock() - node.Value.FetchedAt <= MaxAge)
                {
                    _lru.Remove(node);
                    _lru.AddLast(node);
                    _hits++;
                    return (node.Value.Value, true);
                }

                // Aged out: the object may already be gone, so never serve it.
                RemoveLocked(node);
                _evictions++;
            }

            _misses++;
            if (!_flights.TryGetValue(objectId, out flight!))
            {
                leader = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                flight = leader.Task;
                _flights[objectId] = flight;
                _fetches++;
            }
        }

        if (leader is not null)
        {
            // The fetch belongs to every caller that joins it, so it is not cancelled with this
            // caller's token: one abandoned request must not fail the others waiting on it.
            _ = RunFlightAsync(objectId, fetch, leader);
        }

        var value = await flight.WaitAsync(cancellationToken).ConfigureAwait(false);
        return (value, false);
    }

    private async Task RunFlightAsync(string objectId, Func<CancellationToken, Task<byte[]>> fetch, TaskCompletionSource<byte[]> leader)
    {
        byte[] value;
        try
        {
            value = await fetch(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            lock (_lock)
            {
                _flights.Remove(objectId);
            }

            leader.SetException(exc);
            return;
        }

        bool retained;
        lock (_lock)
        {
            _flights.Remove(objectId);
            retained = InsertLocked(objectId, value);
        }

        if (!retained)
        {
            if (NotRetained is { } handler)
            {
                handler(value.LongLength);
            }
            else
            {
                Console.Error.WriteLine(
                    $"vgi_rpc object_cache: a {value.LongLength}-byte value exceeds the cache bound ({MaxBytes} bytes, {BytesEnvironmentVariable}); resolved but not retained");
            }
        }

        leader.SetResult(value);
    }

    private bool InsertLocked(string objectId, byte[] value)
    {
        if (value.LongLength > MaxBytes)
        {
            return false;
        }

        if (_entries.TryGetValue(objectId, out var previous))
        {
            RemoveLocked(previous);
        }

        var node = _lru.AddLast((objectId, value, _clock()));
        _entries[objectId] = node;
        _resident += value.LongLength;
        while (_resident > MaxBytes && _lru.First is { } oldest)
        {
            RemoveLocked(oldest);
            _evictions++;
        }

        return true;
    }

    private void RemoveLocked(LinkedListNode<(string Id, byte[] Value, DateTimeOffset FetchedAt)> node)
    {
        _lru.Remove(node);
        _entries.Remove(node.Value.Id);
        _resident -= node.Value.Value.LongLength;
    }

    private static long? CgroupMemoryLimit()
    {
        foreach (var path in new[] { "/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes" })
        {
            string raw;
            try
            {
                raw = File.ReadAllText(path).Trim();
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (raw.Length == 0 || raw == "max")
            {
                return null;
            }

            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            // cgroup v1 reports "unlimited" as a huge page-aligned number.
            return value <= 0 || value >= (1L << 60) ? null : value;
        }

        return null;
    }

    private static long? PhysicalMemory()
    {
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return total > 0 ? total : null;
    }
}
