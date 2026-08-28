namespace QueryFarm.VgiRpc.Client;

public sealed class WorkerPoolOptions
{
    public int MaxIdle { get; init; } = 4;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public SubprocessStderrMode Stderr { get; init; } = SubprocessStderrMode.Inherit;

    public Action<string>? OnStderr { get; init; }

    public long? SharedMemorySize { get; init; }
}

public sealed record WorkerPoolMetrics(
    long Borrows,
    long Spawns,
    long Reuses,
    long Returns,
    long Discards,
    long IdleEvictions,
    long CapacityEvictions,
    int Idle,
    int Active);

/// <summary>Command-keyed LIFO subprocess pool for persistent vgi-rpc workers.</summary>
public sealed class WorkerPool : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly WorkerPoolOptions _options;
    private readonly Dictionary<string, List<IdleWorker>> _idle = [];
    private readonly CancellationTokenSource _reaperCancellation = new();
    private readonly Task _reaper;
    private bool _closed;
    private long _borrows;
    private long _spawns;
    private long _reuses;
    private long _returns;
    private long _discards;
    private long _idleEvictions;
    private long _capacityEvictions;
    private int _active;

    public WorkerPool(WorkerPoolOptions? options = null)
    {
        _options = options ?? new WorkerPoolOptions();
        if (_options.MaxIdle < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxIdle cannot be negative.");
        }

        if (_options.IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "IdleTimeout must be positive.");
        }

        _reaper = ReapLoopAsync();
    }

    public WorkerPoolMetrics Metrics
    {
        get
        {
            lock (_gate)
            {
                return new WorkerPoolMetrics(
                    _borrows,
                    _spawns,
                    _reuses,
                    _returns,
                    _discards,
                    _idleEvictions,
                    _capacityEvictions,
                    _idle.Sum(pair => pair.Value.Count),
                    _active);
            }
        }
    }

    public async Task<WorkerLease> BorrowAsync(
        IReadOnlyList<string> command,
        RpcClientOptions? clientOptions = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("A worker command cannot be empty.", nameof(command));
        }

        var commandCopy = command.ToArray();
        var key = Key(commandCopy, environment, workingDirectory);
        SubprocessTransport? transport = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _borrows++;
            _active++;
            if (_idle.TryGetValue(key, out var candidates))
            {
                while (candidates.Count > 0)
                {
                    var last = candidates.Count - 1;
                    var candidate = candidates[last];
                    candidates.RemoveAt(last);
                    if (!candidate.Transport.HasExited)
                    {
                        transport = candidate.Transport;
                        _reuses++;
                        break;
                    }

                    _discards++;
                    _ = candidate.Transport.DisposeAsync();
                }

                if (candidates.Count == 0)
                {
                    _idle.Remove(key);
                }
            }
        }

        if (transport is null)
        {
            try
            {
                transport = new SubprocessTransport(
                    commandCopy,
                    _options.Stderr,
                    _options.OnStderr,
                    environment: environment,
                    workingDirectory: workingDirectory);
                lock (_gate)
                {
                    _spawns++;
                }
            }
            catch
            {
                lock (_gate)
                {
                    _active--;
                }

                throw;
            }
        }

        var effectiveOptions = clientOptions is null
            ? new RpcClientOptions { SharedMemorySize = _options.SharedMemorySize }
            : new RpcClientOptions
            {
                OnLog = clientOptions.OnLog,
                ProtocolVersion = clientOptions.ProtocolVersion,
                SharedMemorySize = clientOptions.SharedMemorySize ?? _options.SharedMemorySize,
            };
        var client = new RpcClient(transport, effectiveOptions, ownsTransport: false);
        await Task.CompletedTask.ConfigureAwait(false);
        return new WorkerLease(this, key, transport, client);
    }

    private async Task ReturnAsync(string key, SubprocessTransport transport, bool reusable)
    {
        SubprocessTransport? evicted = null;
        var closeReturned = false;
        lock (_gate)
        {
            _active--;
            if (_closed || !reusable || transport.HasExited || _options.MaxIdle == 0)
            {
                _discards++;
                closeReturned = true;
            }
            else
            {
                var idleCount = _idle.Sum(pair => pair.Value.Count);
                if (idleCount >= _options.MaxIdle)
                {
                    evicted = EvictOldestLocked();
                    _capacityEvictions++;
                }

                if (!_idle.TryGetValue(key, out var workers))
                {
                    workers = [];
                    _idle[key] = workers;
                }

                workers.Add(new IdleWorker(transport, DateTimeOffset.UtcNow));
                _returns++;
            }
        }

        if (closeReturned)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        if (evicted is not null)
        {
            await evicted.DisposeAsync().ConfigureAwait(false);
        }
    }

    private SubprocessTransport? EvictOldestLocked()
    {
        string? oldestKey = null;
        var oldestIndex = -1;
        var oldestTime = DateTimeOffset.MaxValue;
        foreach (var (key, workers) in _idle)
        {
            for (var index = 0; index < workers.Count; index++)
            {
                if (workers[index].ReturnedAt < oldestTime)
                {
                    oldestTime = workers[index].ReturnedAt;
                    oldestKey = key;
                    oldestIndex = index;
                }
            }
        }

        if (oldestKey is null)
        {
            return null;
        }

        var entries = _idle[oldestKey];
        var transport = entries[oldestIndex].Transport;
        entries.RemoveAt(oldestIndex);
        if (entries.Count == 0)
        {
            _idle.Remove(oldestKey);
        }

        return transport;
    }

    private async Task ReapLoopAsync()
    {
        var interval = TimeSpan.FromSeconds(Math.Min(_options.IdleTimeout.TotalSeconds, 5));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_reaperCancellation.Token).ConfigureAwait(false))
            {
                List<SubprocessTransport> expired = [];
                var cutoff = DateTimeOffset.UtcNow - _options.IdleTimeout;
                lock (_gate)
                {
                    foreach (var key in _idle.Keys.ToArray())
                    {
                        var workers = _idle[key];
                        for (var index = workers.Count - 1; index >= 0; index--)
                        {
                            if (workers[index].ReturnedAt <= cutoff)
                            {
                                expired.Add(workers[index].Transport);
                                workers.RemoveAt(index);
                                _idleEvictions++;
                            }
                        }

                        if (workers.Count == 0)
                        {
                            _idle.Remove(key);
                        }
                    }
                }

                foreach (var transport in expired)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_reaperCancellation.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<SubprocessTransport> idle;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            idle = _idle.Values.SelectMany(value => value).Select(entry => entry.Transport).ToList();
            _idle.Clear();
            _discards += idle.Count;
        }

        _reaperCancellation.Cancel();
        await _reaper.ConfigureAwait(false);
        foreach (var transport in idle)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        _reaperCancellation.Dispose();
    }

    private static string Key(
        IReadOnlyList<string> command,
        IReadOnlyDictionary<string, string?>? environment,
        string? workingDirectory)
    {
        var env = environment is null
            ? ""
            : string.Join('\u001e', environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        return $"{string.Join('\u001f', command)}\u001d{workingDirectory}\u001d{env}";
    }

    private sealed record IdleWorker(SubprocessTransport Transport, DateTimeOffset ReturnedAt);

    public sealed class WorkerLease : IAsyncDisposable
    {
        private readonly WorkerPool _pool;
        private readonly string _key;
        private readonly SubprocessTransport _transport;
        private bool _disposed;

        internal WorkerLease(WorkerPool pool, string key, SubprocessTransport transport, RpcClient client)
        {
            _pool = pool;
            _key = key;
            _transport = transport;
            Client = client;
        }

        public RpcClient Client { get; }

        public TContract CreateProxy<TContract>() where TContract : class => Client.CreateProxy<TContract>();

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var reusable = Client.IsReusable;
            await Client.DisposeAsync().ConfigureAwait(false);
            await _pool.ReturnAsync(_key, _transport, reusable).ConfigureAwait(false);
        }
    }
}
