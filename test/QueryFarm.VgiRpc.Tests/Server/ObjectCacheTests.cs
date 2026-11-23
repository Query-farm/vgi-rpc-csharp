using QueryFarm.VgiRpc.External;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

/// <summary>The object cache's required behaviour (STATE_REF_V1.md §11).</summary>
public sealed class ObjectCacheTests
{
    [Fact]
    public async Task ConcurrentLookups_ShareOneFetch()
    {
        var cache = new ObjectCache(1 << 20);
        var gate = new TaskCompletionSource();
        var fetches = 0;
        async Task<byte[]> Fetch(CancellationToken _)
        {
            Interlocked.Increment(ref fetches);
            await gate.Task;
            return [1, 2, 3];
        }

        var lookups = Enumerable.Range(0, 8).Select(_ => cache.GetOrFetchAsync("id", Fetch)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(lookups);
        Assert.Equal(1, fetches);
        Assert.All(results, r => Assert.Equal([1, 2, 3], r.Value));
        var (_, hit) = await cache.GetOrFetchAsync("id", Fetch);
        Assert.True(hit);
        Assert.Equal(1, fetches);
        Assert.Equal(1, cache.Stats().Fetches);
    }

    [Fact]
    public async Task ByteBound_EvictsLeastRecentlyUsed()
    {
        var cache = new ObjectCache(100);
        await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[60]));
        await cache.GetOrFetchAsync("b", _ => Task.FromResult(new byte[60]));
        var stats = cache.Stats();
        Assert.Equal(60, stats.ResidentBytes);
        Assert.Equal(1, stats.Entries);
        Assert.Equal(1, stats.Evictions);
        var (_, hit) = await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[60]));
        Assert.False(hit);
    }

    [Fact]
    public async Task OversizedValue_IsReturnedButNotRetained()
    {
        var cache = new ObjectCache(10);
        long reported = 0;
        cache.NotRetained += size => reported = size;
        var (value, hit) = await cache.GetOrFetchAsync("big", _ => Task.FromResult(new byte[11]));
        Assert.Equal(11, value.Length);
        Assert.False(hit);
        Assert.Equal(11, reported);
        Assert.Equal(0, cache.Stats().ResidentBytes);
    }

    [Fact]
    public async Task ZeroBound_RetainsNothing()
    {
        var cache = new ObjectCache(0);
        cache.NotRetained += _ => { };
        await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[1]));
        var (_, hit) = await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[1]));
        Assert.False(hit);
        Assert.Equal(2, cache.Stats().Fetches);
    }

    [Fact]
    public async Task EntriesOlderThanADay_AreMisses()
    {
        var now = DateTimeOffset.UnixEpoch;
        var cache = new ObjectCache(1024, clock: () => now);
        await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[1]));
        now += TimeSpan.FromHours(23);
        Assert.True((await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[1]))).Hit);
        now += TimeSpan.FromHours(2);
        Assert.False((await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[1]))).Hit);

        Assert.Equal(1, cache.AgeEntries(TimeSpan.FromSeconds(86401)));
        Assert.False((await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[1]))).Hit);
    }

    [Fact]
    public async Task AFailedFetch_FailsItsWaitersAndIsNotCached()
    {
        var cache = new ObjectCache(1024);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrFetchAsync("a", _ => Task.FromException<byte[]>(new InvalidOperationException("boom"))));
        var (value, hit) = await cache.GetOrFetchAsync("a", _ => Task.FromResult(new byte[] { 9 }));
        Assert.False(hit);
        Assert.Equal([9], value);
    }

    [Theory]
    [InlineData("12345", 12345L)]
    [InlineData("0", 0L)]
    public void DefaultBytes_ReadsTheEnvironment(string raw, long expected) =>
        Assert.Equal(expected, ObjectCache.DefaultBytes(name => name == ObjectCache.BytesEnvironmentVariable ? raw : null));

    [Theory]
    [InlineData("-1")]
    [InlineData("lots")]
    public void DefaultBytes_RefusesInvalidValues(string raw) =>
        Assert.Throws<ArgumentException>(() => ObjectCache.DefaultBytes(_ => raw));

    [Fact]
    public void DefaultBytes_IsAtMostOneGiB() =>
        Assert.InRange(ObjectCache.DefaultBytes(_ => null), 1, 1L << 30);
}
