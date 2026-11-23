namespace QueryFarm.VgiRpc.Conformance.Types;

/// <summary>The worker process's object-cache counters (<c>object_cache_stats</c>). Mirrors
/// <c>_types.ObjectCacheStatsResult</c>.</summary>
public sealed class ObjectCacheStatsResult
{
    public long ResidentBytes { get; set; }
    public long Entries { get; set; }
    public long MaxBytes { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long Evictions { get; set; }
    public long Fetches { get; set; }
}
