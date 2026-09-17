using Apache.Arrow;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.External;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Shm;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

/// <summary>
/// Async schema-first client over one persistent byte-stream transport. A connection permits one
/// active call at a time; use a worker pool for concurrent subprocess workloads.
/// </summary>
public sealed partial class RpcClient : IRpcClient
{
    private readonly IRpcTransport _transport;
    private readonly bool _ownsTransport;
    private readonly RpcClientOptions _options;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private ShmSegment? _sharedMemory;
    private bool _transportOptionsChecked;
    private bool _disposed;
    private string _protocol;

    internal bool IsReusable => !_disposed && _operationLock.CurrentCount == 1;

    public RpcClient(IRpcTransport transport, RpcClientOptions? options = null, bool ownsTransport = true)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new RpcClientOptions();
        _ownsTransport = ownsTransport;
        _protocol = _options.Protocol ?? "";
    }

    /// <summary>
    /// Names the protocol this client addresses, if it has not been named already.
    /// </summary>
    /// <remarks>
    /// The typed entry points know the contract and therefore know the protocol, so they call
    /// this rather than making every caller repeat a name the type already carries. An explicit
    /// <see cref="RpcClientOptions.Protocol"/> always wins, and the first contract to adopt wins
    /// over a later one: a connection addresses one protocol, and silently re-pointing it at
    /// another mid-life is not something a caller could have meant.
    /// </remarks>
    internal void AdoptProtocol(string protocol)
    {
        if (_protocol.Length == 0 && !string.IsNullOrEmpty(protocol))
        {
            _protocol = protocol;
        }
    }

    public IRpcTransport Transport => _transport;

    /// <summary>
    /// One unary call addressed to <paramref name="protocol"/> instead of the one this client
    /// was told it addresses.
    /// </summary>
    /// <param name="protocol">The routing key for this call alone.</param>
    /// <param name="method">The method to call.</param>
    /// <param name="parameters">The request batch.</param>
    /// <param name="metadata">Extra request metadata.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <remarks>
    /// For the protocols a server co-hosts beside its application surface —
    /// <c>vgi_rpc.Reflection.v1</c>, <c>vgi_rpc.Identity.v1</c> — which a client reaches over the
    /// same connection without becoming a client of them. The connection's own protocol is
    /// unchanged: this addresses one call, and a client still addresses one protocol.
    /// </remarks>
    public Task<AnnotatedBatch> CallUnaryOnAsync(
        string protocol,
        string method,
        RecordBatch parameters,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        return CallUnaryAsync(method, parameters, metadata, cancellationToken, protocol);
    }

    public async Task<AnnotatedBatch> CallUnaryAsync(
        string method,
        RecordBatch parameters,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        await CallUnaryAsync(method, parameters, metadata, cancellationToken, protocol: null)
            .ConfigureAwait(false);

    private async Task<AnnotatedBatch> CallUnaryAsync(
        string method,
        RecordBatch parameters,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken,
        string? protocol)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureTransportOptionsAsync(cancellationToken).ConfigureAwait(false);
            _sharedMemory?.Reset();
            var requestMetadata = RequestMetadata(method, metadata, protocol);
            AddSharedMemoryMetadata(requestMetadata);
            var outgoing = await ShmPointerBatch.MaybeWriteAsync(
                parameters,
                requestMetadata,
                _sharedMemory,
                cancellationToken).ConfigureAwait(false);
            await using (var writer = new WireWriter(_transport.Output, parameters.Schema))
            {
                try
                {
                    await writer.WriteBatchAsync(new AnnotatedBatch(outgoing.Batch, outgoing.Metadata), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (!ReferenceEquals(outgoing.Batch, parameters))
                    {
                        outgoing.Batch.Dispose();
                    }
                }
            }

            return await ReadUnaryResponseAsync(method, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<AnnotatedBatch> ReadUnaryResponseAsync(string method, CancellationToken cancellationToken)
    {
        using var reader = new WireReader(_transport.Input);
        await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);

        AnnotatedBatch? terminal = null;
        try
        {
            while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } batch)
            {
                var level = batch.GetMetadata(MetadataKeys.LogLevel);
                if (level is null || IsExternalPointer(batch))
                {
                    terminal?.Batch.Dispose();
                    terminal = await ResolveIncomingAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                else if (level == "EXCEPTION")
                {
                    var exception = RpcErrorDecoder.Decode(batch);
                    batch.Batch.Dispose();
                    throw exception;
                }
                else
                {
                    DispatchLog(batch);
                    batch.Batch.Dispose();
                }
            }

            if (terminal is null)
            {
                throw new RpcException("TransportError", $"Server closed the response without returning a result for '{method}'.");
            }

            var result = terminal;
            terminal = null;
            return result;
        }
        finally
        {
            terminal?.Batch.Dispose();
        }
    }

    private async Task EnsureTransportOptionsAsync(CancellationToken cancellationToken)
    {
        if (_transportOptionsChecked || _options.SharedMemorySize is null)
        {
            return;
        }

        _transportOptionsChecked = true;
        _sharedMemory = ShmSegment.Create(_options.SharedMemorySize.Value);
        try
        {
            using var empty = ValueCodec.EmptyRow(new Schema([], metadata: null));
            await using (var writer = new WireWriter(_transport.Output, empty.Schema))
            {
                await writer.WriteBatchAsync(
                    new AnnotatedBatch(empty, RequestMetadata("__transport_options__", null)),
                    cancellationToken).ConfigureAwait(false);
            }

            var response = await ReadUnaryResponseAsync("__transport_options__", cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.Equals(response.GetMetadata(MetadataKeys.TransportShm), "true", StringComparison.OrdinalIgnoreCase))
                {
                    _sharedMemory.Unlink();
                    _sharedMemory.Dispose();
                    _sharedMemory = null;
                }
            }
            finally
            {
                response.Batch.Dispose();
            }
        }
        catch
        {
            _sharedMemory?.Unlink();
            _sharedMemory?.Dispose();
            _sharedMemory = null;
            throw;
        }
    }

    private void AddSharedMemoryMetadata(Dictionary<string, string> metadata)
    {
        if (_sharedMemory is null)
        {
            return;
        }

        metadata[MetadataKeys.ShmSegmentName] = _sharedMemory.Name;
        metadata[MetadataKeys.ShmSegmentSize] = _sharedMemory.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether <paramref name="batch"/> is an external-storage pointer this client would resolve.
    /// </summary>
    /// <remarks>
    /// Every reader below asks this <i>before</i> classifying a batch as a log or control batch,
    /// as WIRE_PROTOCOL.md §1.5 and §12 require. A pointer is zero-row by construction, so a
    /// reader that classifies first drops it and then reports the payload absent rather than
    /// malformed -- the failure mode that cost four ports their stream header. This port keys its
    /// log classification on <c>vgi_rpc.log_level</c> rather than on row count, which a pointer
    /// never carries, so the order is belt and braces here; it is written explicitly anyway so a
    /// later edit toward row-count classification cannot reintroduce the bug silently.
    /// </remarks>
    private bool IsExternalPointer(AnnotatedBatch batch) =>
        _options.ExternalLocation is not null
        && ExternalLocation.IsExternalLocationBatch(batch.Batch, batch.Metadata);

    /// <summary>
    /// Resolves whichever kind of pointer batch <paramref name="incoming"/> is — a shared-memory
    /// pointer, or a <c>vgi_rpc.location</c> external-storage pointer — and returns the batch the
    /// caller should see. Any other batch is returned untouched.
    /// </summary>
    /// <remarks>
    /// Both kinds are checked here, on one path, because every reader on this transport family
    /// funnels through it. Resolving only shm is not a narrower feature but a silent one: an
    /// external pointer is zero-row by construction, so it reaches the caller as an empty batch
    /// and every row of that response is simply gone, with nothing raised anywhere.
    /// </remarks>
    private async Task<AnnotatedBatch> ResolveIncomingAsync(AnnotatedBatch incoming, CancellationToken cancellationToken)
    {
        var (batch, metadata, release) = await ShmPointerBatch.ResolveAsync(
            incoming.Batch,
            incoming.Metadata,
            _sharedMemory,
            cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(batch, incoming.Batch))
        {
            incoming.Batch.Dispose();
            release?.Invoke();
            incoming = new AnnotatedBatch(batch, metadata);
        }

        if (_options.ExternalLocation is not { } external
            || !ExternalLocation.IsExternalLocationBatch(incoming.Batch, incoming.Metadata))
        {
            return incoming;
        }

        try
        {
            var (resolved, resolvedMetadata) = await ExternalLocation.ResolveAsync(
                incoming.Batch,
                incoming.Metadata,
                external,
                cancellationToken,
                DispatchLog).ConfigureAwait(false);
            return new AnnotatedBatch(resolved, resolvedMetadata);
        }
        finally
        {
            // The pointer is never the batch handed back (it was one by the guard above), so it
            // is this method's to release on every exit -- a fetch that throws included.
            incoming.Batch.Dispose();
        }
    }

    // `protocol` addresses this one call elsewhere (see CallUnaryOnAsync); null means the
    // client's own protocol.
    private Dictionary<string, string> RequestMetadata(
        string method,
        IReadOnlyDictionary<string, string>? additional,
        string? protocol = null)
    {
        var result = additional is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(additional);
        result[MetadataKeys.Method] = method;
        // Required on this transport family: `vgi_rpc.protocol` is the protocol's only carrier
        // here, and the server refuses a request that does not name the protocol it addresses.
        var routingKey = protocol is { Length: > 0 } ? protocol : _protocol;
        result[MetadataKeys.Protocol] = routingKey.Length > 0
            ? routingKey
            : throw new InvalidOperationException(
                "This client has not been told which protocol it addresses. Set "
                + "RpcClientOptions.Protocol, or call through a typed entry point "
                + "(CreateProxy<TContract>() / RpcConnection<TContract>), which reads the name "
                + "from the contract type.");
        result[MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion;
        result.TryAdd(MetadataKeys.RequestId, Guid.NewGuid().ToString("n"));
        if (_options.ProtocolVersion is not null)
        {
            result.TryAdd(MetadataKeys.ProtocolVersion, _options.ProtocolVersion);
        }

        if (System.Diagnostics.Activity.Current?.Id is { } traceparent)
        {
            result.TryAdd("traceparent", traceparent);
        }

        return result;
    }

    private void DispatchLog(AnnotatedBatch batch)
    {
        if (_options.OnLog is null)
        {
            return;
        }

        var wireLevel = batch.GetMetadata(MetadataKeys.LogLevel);
        if (!Enum.TryParse<VgiLogLevel>(wireLevel, ignoreCase: true, out var level))
        {
            level = VgiLogLevel.Info;
        }

        IReadOnlyDictionary<string, object?>? extra = null;
        if (batch.GetMetadata(MetadataKeys.LogExtra) is { } json)
        {
            try
            {
                extra = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed extras never hide the log's level/message.
            }
        }

        _options.OnLog(new LogMessage(level, batch.GetMetadata(MetadataKeys.LogMessage) ?? "", extra));
    }

    public TContract CreateProxy<TContract>() where TContract : class
    {
        AdoptProtocol(WireNaming.ForProtocol(typeof(TContract)));
        return RpcClientProxy<TContract>.Create(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sharedMemory is not null)
        {
            try
            {
                _sharedMemory.Unlink();
            }
            finally
            {
                _sharedMemory.Dispose();
            }

            _sharedMemory = null;
        }

        if (_ownsTransport)
        {
            switch (_transport)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        _operationLock.Dispose();
    }
}
