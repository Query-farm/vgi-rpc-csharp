using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using QueryFarm.VgiRpc.External;
using Xunit;

namespace QueryFarm.VgiRpc.S3.Tests;

/// <summary>
/// Throttle tests for <see cref="S3Storage"/> against RustFS through a local TCP proxy that trickles
/// the request body at a fixed slow rate, or stops forwarding the response. A transfer longer than
/// the stall timeout succeeds in one attempt (no whole-request deadline); a stalled one fails within
/// the stall timeout per attempt, 3 attempts in all.
/// </summary>
public sealed class S3StorageThrottleTests(S3Fixture fixture) : IClassFixture<S3Fixture>
{
    private const string Prefix = "csharp-throttle/";

    [Fact]
    public async Task ThrottledPut_SucceedsInOneAttempt()
    {
        var data = RandomNumberGenerator.GetBytes(24 << 20);
        await using var proxy = new ThrottleProxy(fixture.Endpoint) { UpstreamDelayPer16K = TimeSpan.FromMilliseconds(5) };
        await using var storage = NewStorage(proxy.Endpoint, TimeSpan.FromSeconds(4));
        var sw = Stopwatch.StartNew();
        var key = await storage.PutObjectAsync(data, CancellationToken.None);
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");

        await using var direct = NewStorage(fixture.Endpoint, TimeSpan.FromSeconds(30));
        Assert.Equal(data, await direct.GetAsync(key, null, CancellationToken.None));
        Assert.Equal(1, proxy.Connections);
    }

    [Fact]
    public async Task StalledGet_IsRetriedThenFails()
    {
        var data = RandomNumberGenerator.GetBytes(4 << 20);
        await using var direct = NewStorage(fixture.Endpoint, TimeSpan.FromSeconds(30));
        var key = await direct.PutObjectAsync(data, CancellationToken.None);

        await using var proxy = new ThrottleProxy(fixture.Endpoint) { DownstreamStallAfter = 64 * 1024 };
        await using var storage = NewStorage(proxy.Endpoint, TimeSpan.FromSeconds(1));
        var sw = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => storage.GetAsync(key, null, CancellationToken.None));
        Assert.Contains("stalled", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, proxy.Connections);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    private static S3Storage NewStorage(Uri endpoint, TimeSpan stall) =>
        S3Storage.CreateBuilder(S3Fixture.Bucket)
            .WithServiceUrl(endpoint.ToString())
            .WithForcePathStyle(true)
            .WithCredentials(new BasicAWSCredentials(S3Fixture.AccessKey, S3Fixture.SecretKey))
            .WithRegion(RegionEndpoint.USEast1)
            .WithKeyPrefix(Prefix)
            .WithTransferTimeouts(new StorageTimeouts(TimeSpan.FromSeconds(5), stall))
            .Build();

    /// <summary>A TCP forwarder to <c>upstream</c>: optionally slows client→server traffic, or stops
    /// forwarding server→client traffic after a number of bytes (holding the connection open).</summary>
    private sealed class ThrottleProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Uri _upstream;
        private readonly Task _accept;
        private int _connections;

        public ThrottleProxy(Uri upstream)
        {
            _upstream = upstream;
            _listener.Start();
            _accept = AcceptAsync();
        }

        public TimeSpan UpstreamDelayPer16K { get; init; }

        public long DownstreamStallAfter { get; init; } = -1;

        public int Connections => Volatile.Read(ref _connections);

        public Uri Endpoint => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _accept;
            }
            catch (Exception)
            {
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                Interlocked.Increment(ref _connections);
                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var upstream = new TcpClient())
                    {
                        try
                        {
                            await upstream.ConnectAsync(_upstream.Host, _upstream.Port, _stop.Token);
                            await Task.WhenAll(
                                PumpAsync(client.GetStream(), upstream.GetStream(), UpstreamDelayPer16K, -1),
                                PumpAsync(upstream.GetStream(), client.GetStream(), TimeSpan.Zero, DownstreamStallAfter));
                        }
                        catch (Exception)
                        {
                        }
                    }
                });
            }
        }

        private async Task PumpAsync(NetworkStream from, NetworkStream to, TimeSpan delayPer16K, long stallAfter)
        {
            var buffer = new byte[16 * 1024];
            long moved = 0;
            while (true)
            {
                var n = await from.ReadAsync(buffer, _stop.Token);
                if (n == 0)
                {
                    to.Socket.Shutdown(SocketShutdown.Send);
                    return;
                }

                if (stallAfter >= 0 && moved + n > stallAfter)
                {
                    await to.WriteAsync(buffer.AsMemory(0, (int)Math.Max(0, stallAfter - moved)), _stop.Token);
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }

                await to.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                moved += n;
                if (delayPer16K > TimeSpan.Zero)
                {
                    await Task.Delay(delayPer16K, _stop.Token);
                }
            }
        }
    }
}
