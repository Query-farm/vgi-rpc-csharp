using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using QueryFarm.VgiRpc.External;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>
/// Throttle tests for storage transfers: a local fake vendor + storage host that trickles bytes at a
/// fixed slow rate, or stops moving bytes. A transfer longer than every configured deadline succeeds
/// with exactly one attempt; a stalled one fails within the stall timeout (plus slack) per attempt,
/// and is retried 3 times in all.
/// </summary>
public sealed class StorageThrottleTests
{
    private const string Token = "throttle-token";

    [Fact]
    public void StallTimeout_FromEnvironment()
    {
        string? Env(string name, string? value) => name == StorageTimeouts.StallTimeoutVariable ? value : null;
        Assert.Equal(TimeSpan.FromSeconds(2.5), StorageTimeouts.StallFromEnvironment(n => Env(n, " 2.5 ")));
        Assert.Equal(StorageTimeouts.DefaultStall, StorageTimeouts.StallFromEnvironment(n => Env(n, null)));
        foreach (var bad in new[] { "0", "-1", "abc", "NaN", "Infinity" })
        {
            Assert.Equal(StorageTimeouts.DefaultStall, StorageTimeouts.StallFromEnvironment(n => Env(n, bad)));
        }

        Assert.Equal(TimeSpan.FromSeconds(30), StorageTimeouts.Default.Connect);
        Assert.Equal(TimeSpan.FromSeconds(30), StorageTimeouts.Default.Stall);
    }

    /// <summary>A 24 MiB object the storage host takes at ~3 MiB/s (about 8 s) uploads in one PUT,
    /// though the vendor call timeout is 2 s and the stall timeout 4 s.</summary>
    [Fact]
    public async Task ThrottledUpload_SucceedsInOneAttempt()
    {
        var data = RandomNumberGenerator.GetBytes(24 << 20);
        await using var fake = new FakeVendor(data) { PutReadDelay = TimeSpan.FromMilliseconds(5) };
        using var storage = new VendorStorage(fake.Url, Token, options: new VendorOptions
        {
            Timeout = TimeSpan.FromSeconds(2),
            StallTimeout = TimeSpan.FromSeconds(4),
        });
        var sw = Stopwatch.StartNew();
        var id = await storage.PutObjectAsync(data, CancellationToken.None);
        Assert.Equal("o1", id);
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
        Assert.Equal(1, fake.StoragePuts);
        Assert.Equal(data.Length, fake.PutBytes);
    }

    /// <summary>A 6 MiB object the storage host sends at ~2 MiB/s (about 3 s) downloads in one GET
    /// with 1 s vendor-call and stall timeouts.</summary>
    [Fact]
    public async Task ThrottledDownload_SucceedsInOneAttempt()
    {
        var data = RandomNumberGenerator.GetBytes(6 << 20);
        await using var fake = new FakeVendor(data) { GetWriteDelay = TimeSpan.FromMilliseconds(8) };
        using var storage = new VendorStorage(fake.Url, Token, options: new VendorOptions
        {
            Timeout = TimeSpan.FromSeconds(1),
            StallTimeout = TimeSpan.FromSeconds(1),
        });
        var sw = Stopwatch.StartNew();
        var got = await storage.GetAsync("o1", null, CancellationToken.None);
        Assert.Equal(data, got);
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        Assert.Equal(1, fake.StorageGets);
    }

    /// <summary>A storage host that goes silent mid-body fails each attempt within the stall
    /// timeout, 3 attempts in all.</summary>
    [Fact]
    public async Task StalledDownload_IsRetriedThenFails()
    {
        var data = RandomNumberGenerator.GetBytes(1 << 20);
        await using var fake = new FakeVendor(data) { GetStallAfter = 1000 };
        using var storage = new VendorStorage(fake.Url, Token, options: new VendorOptions { StallTimeout = TimeSpan.FromSeconds(1) });
        var sw = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BlobVendorException>(() => storage.GetAsync("o1", null, CancellationToken.None));
        Assert.Contains("after 3 attempts (stalled)", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, fake.StorageGets);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
    }

    /// <summary>A storage host that stops reading the request body fails each attempt within the
    /// stall timeout (once the socket buffers fill), 3 attempts in all.</summary>
    [Fact]
    public async Task StalledUpload_IsRetriedThenFails()
    {
        var data = RandomNumberGenerator.GetBytes(64 << 20);
        await using var fake = new FakeVendor(data) { PutStall = true };
        using var storage = new VendorStorage(fake.Url, Token, options: new VendorOptions { StallTimeout = TimeSpan.FromSeconds(1) });
        var sw = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<BlobVendorException>(() => storage.PutObjectAsync(data, CancellationToken.None));
        Assert.Contains("after 3 attempts (stalled)", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, fake.StoragePuts);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    /// <summary>An ExternalLocation fetch slower than its header deadline (1 s) and stall timeout
    /// (1 s) succeeds in one GET while bytes keep arriving.</summary>
    [Fact]
    public async Task ThrottledExternalFetch_SucceedsInOneAttempt()
    {
        var data = RandomNumberGenerator.GetBytes(6 << 20);
        await using var fake = new FakeVendor(data) { GetWriteDelay = TimeSpan.FromMilliseconds(8) };
        var config = new FetchConfig { Timeout = TimeSpan.FromSeconds(1), StallTimeout = TimeSpan.FromSeconds(1), MaxFetchBytes = 16 << 20 };
        var sw = Stopwatch.StartNew();
        var got = await ExternalFetch.FetchUrlAsync(fake.StorageUrl, config, null, CancellationToken.None);
        Assert.Equal(data, got);
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        Assert.Equal(1, fake.StorageGets);
    }

    /// <summary>An ExternalLocation fetch whose body stalls fails after 3 attempts.</summary>
    [Fact]
    public async Task StalledExternalFetch_IsRetriedThenFails()
    {
        var data = RandomNumberGenerator.GetBytes(1 << 20);
        await using var fake = new FakeVendor(data) { GetStallAfter = 1000 };
        var config = new FetchConfig { StallTimeout = TimeSpan.FromSeconds(1) };
        var sw = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<Errors.RpcException>(() => ExternalFetch.FetchUrlAsync(fake.StorageUrl, config, null, CancellationToken.None));
        Assert.Contains("after 3 attempts: stalled", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, fake.StorageGets);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
    }

    /// <summary>A loopback fake of the blob vendor API and its storage host (one request per
    /// connection), with throttling and stall knobs on the storage side.</summary>
    private sealed class FakeVendor : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _object;
        private readonly Task _accept;
        private int _puts;
        private int _gets;
        private long _putBytes;

        public FakeVendor(byte[] obj)
        {
            _object = obj;
            _listener.Start();
            _accept = AcceptAsync();
        }

        public TimeSpan PutReadDelay { get; init; }

        public bool PutStall { get; init; }

        public TimeSpan GetWriteDelay { get; init; }

        public int GetStallAfter { get; init; } = -1;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string Url => $"http://127.0.0.1:{Port}";

        public string StorageUrl => $"{Url}/s/o1?sig=secret";

        public int StoragePuts => Volatile.Read(ref _puts);

        public int StorageGets => Volatile.Read(ref _gets);

        public long PutBytes => Interlocked.Read(ref _putBytes);

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

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await HandleAsync(client.GetStream());
                        }
                        catch (Exception)
                        {
                        }
                    }
                });
            }
        }

        private async Task HandleAsync(NetworkStream s)
        {
            var head = new List<byte>();
            var one = new byte[1];
            while (head.Count < 4 || !(head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await s.ReadAsync(one, _stop.Token) == 0)
                {
                    return;
                }

                head.Add(one[0]);
            }

            var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
            var parts = lines[0].Split(' ');
            var (method, path) = (parts[0], parts[1].Split('?')[0]);
            var length = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase))
                .Select(p => long.Parse(p[1].Trim(), CultureInfo.InvariantCulture)).FirstOrDefault();

            if (path == "/s/o1" && method == "PUT")
            {
                Interlocked.Increment(ref _puts);
                if (PutStall)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }

                var buf = new byte[16 * 1024];
                long got = 0;
                while (got < length)
                {
                    var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, length - got)), _stop.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    got += n;
                    if (PutReadDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(PutReadDelay, _stop.Token);
                    }
                }

                Interlocked.Add(ref _putBytes, got);
                await WriteAsync(s, "200 OK", "", []);
                return;
            }

            await DrainAsync(s, length);
            if (path == "/s/o1" && method == "GET")
            {
                Interlocked.Increment(ref _gets);
                var header = $"HTTP/1.1 200 OK\r\nContent-Length: {_object.Length}\r\nConnection: close\r\n\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                for (var off = 0; off < _object.Length; off += 16 * 1024)
                {
                    if (GetStallAfter >= 0 && off >= GetStallAfter)
                    {
                        await s.FlushAsync(_stop.Token);
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }

                    var n = Math.Min(16 * 1024, _object.Length - off);
                    if (GetStallAfter >= 0)
                    {
                        n = Math.Min(n, GetStallAfter - off);
                    }

                    await s.WriteAsync(_object.AsMemory(off, n), _stop.Token);
                    if (GetWriteDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(GetWriteDelay, _stop.Token);
                    }
                }

                return;
            }

            var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            var json = path switch
            {
                "/v1/info" => $$"""{"region":"test","tenant":"t","upload_hosts":["127.0.0.1:{{Port}}"],"download_hosts":["127.0.0.1:{{Port}}"]}""",
                "/v1/blobs/put" => $$"""{"object_id":"o1","upload_url":"{{StorageUrl}}","required_headers":{},"download_url":"{{StorageUrl}}","expires_at":{{expires}}}""",
                "/v1/blobs/get" => $$"""{"download_url":"{{StorageUrl}}","size":{{_object.Length}},"sha256_b64":"{{Convert.ToBase64String(SHA256.HashData(_object))}}","expires_at":{{expires}}}""",
                _ => null,
            };
            await (json is null
                ? WriteAsync(s, "404 Not Found", "", [])
                : WriteAsync(s, "200 OK", "application/json", Encoding.UTF8.GetBytes(json)));
        }

        private async Task DrainAsync(NetworkStream s, long length)
        {
            var buf = new byte[8192];
            for (long got = 0; got < length;)
            {
                var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, length - got)), _stop.Token);
                if (n == 0)
                {
                    return;
                }

                got += n;
            }
        }

        private async Task WriteAsync(NetworkStream s, string status, string contentType, byte[] body)
        {
            var head = $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\n{(contentType.Length > 0 ? $"Content-Type: {contentType}\r\n" : "")}Connection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
            await s.WriteAsync(body, _stop.Token);
            await s.FlushAsync(_stop.Token);
        }
    }
}
