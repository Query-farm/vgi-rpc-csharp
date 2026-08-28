using Apache.Arrow;
using QueryFarm.VgiRpc.Errors;
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

    internal bool IsReusable => !_disposed && _operationLock.CurrentCount == 1;

    public RpcClient(IRpcTransport transport, RpcClientOptions? options = null, bool ownsTransport = true)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new RpcClientOptions();
        _ownsTransport = ownsTransport;
    }

    public IRpcTransport Transport => _transport;

    public async Task<AnnotatedBatch> CallUnaryAsync(
        string method,
        RecordBatch parameters,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureTransportOptionsAsync(cancellationToken).ConfigureAwait(false);
            _sharedMemory?.Reset();
            var requestMetadata = RequestMetadata(method, metadata);
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
                if (level is null)
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

    private async Task<AnnotatedBatch> ResolveIncomingAsync(AnnotatedBatch incoming, CancellationToken cancellationToken)
    {
        var (batch, metadata, release) = await ShmPointerBatch.ResolveAsync(
            incoming.Batch,
            incoming.Metadata,
            _sharedMemory,
            cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(batch, incoming.Batch))
        {
            return incoming;
        }

        incoming.Batch.Dispose();
        release?.Invoke();
        return new AnnotatedBatch(batch, metadata);
    }

    private Dictionary<string, string> RequestMetadata(
        string method,
        IReadOnlyDictionary<string, string>? additional)
    {
        var result = additional is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(additional);
        result[MetadataKeys.Method] = method;
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

    public TContract CreateProxy<TContract>() where TContract : class =>
        RpcClientProxy<TContract>.Create(this);

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
