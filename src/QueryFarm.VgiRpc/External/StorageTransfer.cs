using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace QueryFarm.VgiRpc.External;

// Storage transfers (signed PUT/GET to R2/S3/GCS, ExternalLocation fetches) get a connect timeout
// and a stall timeout, never a whole-request deadline. A large object on a slow link can take
// minutes; a total deadline (HttpClient.Timeout, or a CancelAfter around the call) kills such a
// transfer while it is still making progress, and every retry resends the whole body. The stall
// clock restarts whenever bytes move: a request-body chunk (64 KiB) written, response headers
// received, a response-body read. Matches vgi-rpc-python's storage client (httpx connect/read/write
// timeouts of 30 s, each bounding one operation, not the request).

/// <summary>Connect and stall timeouts for storage transfers.</summary>
/// <param name="Connect">TCP + TLS connect timeout.</param>
/// <param name="Stall">Longest period with no bytes moving in either direction before a transfer
/// fails (and is retried).</param>
public sealed record StorageTimeouts(TimeSpan Connect, TimeSpan Stall)
{
    /// <summary>Overrides <see cref="DefaultStall"/>, in seconds (a positive number; fractions
    /// allowed). The same name in every vgi-rpc SDK.</summary>
    public const string StallTimeoutVariable = "VGI_RPC_STORAGE_STALL_TIMEOUT_SECONDS";

    /// <summary>Default connect timeout: 30 s.</summary>
    public static readonly TimeSpan DefaultConnect = TimeSpan.FromSeconds(30);

    /// <summary>Default stall timeout: 30 s.</summary>
    public static readonly TimeSpan DefaultStall = TimeSpan.FromSeconds(30);

    /// <summary>Connect 30 s, stall 30 s.</summary>
    public static StorageTimeouts Default { get; } = new(DefaultConnect, DefaultStall);

    /// <summary>The defaults, with the stall timeout from <see cref="StallTimeoutVariable"/> when it
    /// is set to a positive number of seconds; an unparsable value is ignored.</summary>
    public static StorageTimeouts FromEnvironment(Func<string, string?>? environment = null) =>
        new(DefaultConnect, StallFromEnvironment(environment));

    /// <summary>The stall timeout from <see cref="StallTimeoutVariable"/>, or
    /// <see cref="DefaultStall"/> when it is unset or not a positive number.</summary>
    public static TimeSpan StallFromEnvironment(Func<string, string?>? environment = null)
    {
        var raw = (environment ?? Environment.GetEnvironmentVariable)(StallTimeoutVariable)?.Trim();
        return !string.IsNullOrEmpty(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds > 0 && seconds < 1e9
            ? TimeSpan.FromSeconds(seconds)
            : DefaultStall;
    }
}

/// <summary>
/// Cancels <see cref="Token"/> when no progress has been reported for the stall timeout (before the
/// first progress: for the connect grace plus the stall timeout). One per transfer attempt; report
/// progress with <see cref="Progress"/> whenever bytes move.
/// </summary>
public sealed class StallWatchdog : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Timer _timer;
    private readonly long _start;
    private readonly long _stallTicks;
    private readonly long _firstTicks;
    private long _last;
    private int _fired;

    /// <summary>Starts watching. <paramref name="outer"/> cancellation also cancels
    /// <see cref="Token"/>.</summary>
    public StallWatchdog(TimeSpan stall, TimeSpan connectGrace, CancellationToken outer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stall, TimeSpan.Zero);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _start = Stopwatch.GetTimestamp();
        _stallTicks = (long)(stall.TotalSeconds * Stopwatch.Frequency);
        _firstTicks = (long)((stall + connectGrace).TotalSeconds * Stopwatch.Frequency);
        var tick = TimeSpan.FromTicks(Math.Clamp(stall.Ticks / 4, TimeSpan.FromMilliseconds(10).Ticks, TimeSpan.FromSeconds(1).Ticks));
        _timer = new Timer(static s => ((StallWatchdog)s!).Check(), this, tick, tick);
    }

    /// <summary>Cancelled on a stall or by the outer token.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Whether the watchdog (not the outer token) cancelled.</summary>
    public bool Stalled => Volatile.Read(ref _fired) == 1;

    /// <summary>Bytes moved: restart the stall clock.</summary>
    public void Progress() => Volatile.Write(ref _last, Stopwatch.GetTimestamp());

    private void Check()
    {
        var last = Volatile.Read(ref _last);
        var now = Stopwatch.GetTimestamp();
        var stalled = last == 0 ? now - _start >= _firstTicks : now - last >= _stallTicks;
        if (stalled && Interlocked.Exchange(ref _fired, 1) == 0)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _timer.Dispose();
        _cts.Dispose();
    }
}

/// <summary>A byte-array request body of exact length, written in 64 KiB chunks; each chunk written
/// counts as progress (so a TLS or socket write of a large body is never one long operation).</summary>
internal sealed class WatchedContent : HttpContent
{
    private const int Chunk = 64 * 1024;
    private readonly byte[] _data;
    private readonly StallWatchdog _watchdog;

    public WatchedContent(byte[] data, StallWatchdog watchdog)
    {
        _data = data;
        _watchdog = watchdog;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, _watchdog.Token);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        _watchdog.Progress();
        for (var offset = 0; offset < _data.Length; offset += Chunk)
        {
            var n = Math.Min(Chunk, _data.Length - offset);
            await stream.WriteAsync(_data.AsMemory(offset, n), cancellationToken).ConfigureAwait(false);
            _watchdog.Progress();
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _data.Length;
        return true;
    }
}

/// <summary>Shared pieces of the storage transport.</summary>
internal static class StorageTransport
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);

    /// <summary>A bare client for storage hosts: no redirects, no cookies, no decompression, the
    /// connect timeout, and no total deadline (the stall watchdog bounds each attempt).</summary>
    public static HttpClient NewClient(TimeSpan connectTimeout) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = connectTimeout,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>Exponential backoff before attempt <paramref name="attempt"/> (1-based retries),
    /// capped at 5 s, jittered ×[0.5, 1.0).</summary>
    public static TimeSpan Backoff(TimeSpan baseDelay, int attempt)
    {
        var delay = TimeSpan.FromTicks(Math.Min(baseDelay.Ticks * (1L << Math.Min(attempt - 1, 16)), MaxBackoff.Ticks));
        return TimeSpan.FromTicks((long)(delay.Ticks * (0.5 + (Random.Shared.NextDouble() / 2))));
    }

    /// <summary>Reads a response body into memory, at most <paramref name="max"/> bytes, reporting
    /// progress per read; reads are cancelled by the watchdog's token.</summary>
    /// <exception cref="StorageBodyTooLargeException">Past the cap.</exception>
    public static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, long max, StallWatchdog watchdog)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(watchdog.Token).ConfigureAwait(false);
        var declared = response.Content.Headers.ContentLength;
        using var buffer = declared is { } d && d <= max && d <= int.MaxValue ? new MemoryStream((int)d) : new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, watchdog.Token).ConfigureAwait(false)) > 0)
        {
            watchdog.Progress();
            if (buffer.Length + read > max)
            {
                throw new StorageBodyTooLargeException(max);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>A storage response body was larger than its cap.</summary>
internal sealed class StorageBodyTooLargeException(long max) : Exception($"response exceeds the {max}-byte cap")
{
    public long Max { get; } = max;
}
