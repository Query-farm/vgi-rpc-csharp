using Apache.Arrow;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client.Http;

public sealed partial class HttpRpcClient
{
    internal async Task<ParsedStreamResponse> ParseStreamResponseAsync(
        byte[] body,
        bool hasHeader,
        CancellationToken cancellationToken,
        bool exchangeTurn = false)
    {
        using var stream = new MemoryStream(body);
        AnnotatedBatch? header = null;
        if (hasHeader)
        {
            using var headerReader = new WireReader(stream);
            await headerReader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            while (await headerReader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } item)
            {
                if (item.GetMetadata(MetadataKeys.LogLevel) is { } level)
                {
                    if (level == "EXCEPTION")
                    {
                        var error = RpcErrorDecoder.Decode(item);
                        item.Batch.Dispose();
                        throw error;
                    }

                    DispatchLog(item);
                    item.Batch.Dispose();
                }
                else
                {
                    header?.Batch.Dispose();
                    header = await ResolveExternalAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var data = new List<AnnotatedBatch>();
        string? state = null;
        string? callState = null;
        try
        {
            using var reader = new WireReader(stream);
            await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } item)
            {
                if (item.GetMetadata(MetadataKeys.LogLevel) is { } level)
                {
                    if (level == "EXCEPTION")
                    {
                        var error = RpcErrorDecoder.Decode(item);
                        item.Batch.Dispose();
                        throw error;
                    }

                    DispatchLog(item);
                    item.Batch.Dispose();
                    continue;
                }

                var inlineState = item.GetMetadata(MetadataKeys.StreamState);
                state = inlineState ?? state;
                callState = item.GetMetadata(MetadataKeys.CallState) ?? callState;
                // Producers always carry continuation state in a trailing zero-row sentinel.
                // Exchanges normally attach state to their one data batch, including legitimate
                // zero-row output. A zero-column exchange may instead serialize a distinct data
                // batch followed by a sentinel; once data is buffered, that second zero-row item
                // is control framing rather than another lockstep result.
                if (item.Batch.Length == 0 && inlineState is not null && (!exchangeTurn || data.Count > 0))
                {
                    item.Batch.Dispose();
                    continue;
                }

                var resolved = await ResolveExternalAsync(item, cancellationToken).ConfigureAwait(false);
                state = resolved.GetMetadata(MetadataKeys.StreamState) ?? state;
                callState = resolved.GetMetadata(MetadataKeys.CallState) ?? callState;
                data.Add(StripStateMetadata(resolved));
            }

            return new ParsedStreamResponse(header, data, state, callState, state is null);
        }
        catch
        {
            header?.Batch.Dispose();
            foreach (var item in data)
            {
                item.Batch.Dispose();
            }

            throw;
        }
    }

    private static AnnotatedBatch StripStateMetadata(AnnotatedBatch batch)
    {
        if (batch.Metadata is null)
        {
            return batch;
        }

        var metadata = new Dictionary<string, string>(batch.Metadata);
        metadata.Remove(MetadataKeys.StreamState);
        metadata.Remove(MetadataKeys.CallState);
        return new AnnotatedBatch(batch.Batch, metadata.Count == 0 ? null : metadata);
    }

    internal sealed record ParsedStreamResponse(
        AnnotatedBatch? Header,
        List<AnnotatedBatch> Data,
        string? State,
        string? CallState,
        bool Finished);
}

public abstract class HttpStreamSession : IRpcStreamSession
{
    private readonly HttpRpcClient _client;
    private bool _disposed;

    protected HttpStreamSession(
        HttpRpcClient client,
        string method,
        AnnotatedBatch? header,
        string? state,
        string? callState,
        bool finished)
    {
        _client = client;
        Method = method;
        Header = header;
        State = state;
        CallState = callState;
        Finished = finished;
    }

    protected string Method { get; }

    protected string? State { get; set; }

    protected string? CallState { get; }

    protected bool Finished { get; set; }

    public AnnotatedBatch? Header { get; }

    public string? ContinuationToken => State is null
        ? null
        : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{State}\n{CallState}"));

    protected HttpRpcClient Client => _client;

    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public THeader GetHeader<THeader>()
    {
        if (Header is null)
        {
            throw new InvalidOperationException("The stream has no header.");
        }

        var schema = SchemaDerivation.InnerSchemaFor(typeof(THeader));
        if (!ValueCodec.SchemasEqual(Header.Batch.Schema, schema))
        {
            throw new InvalidOperationException($"Header schema does not match {typeof(THeader)}.");
        }

        var properties = schema.FieldsList
            .Select(field => typeof(THeader).GetProperty(ValueCodec.FindClrPropertyName(typeof(THeader), field))!)
            .ToArray();
        var values = ValueCodec.ExtractRow(Header.Batch, properties.Select(property => property.PropertyType).ToArray());
        var result = Activator.CreateInstance<THeader>();
        for (var index = 0; index < properties.Length; index++)
        {
            properties[index].SetValue(result, values[index]);
        }

        return result;
    }

    protected Dictionary<string, string> TurnMetadata(IReadOnlyDictionary<string, string>? metadata, bool cancel = false)
    {
        if (State is null)
        {
            throw new InvalidOperationException("The HTTP stream has finished.");
        }

        var result = metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata);
        result[MetadataKeys.StreamState] = State;
        if (CallState is not null)
        {
            result[MetadataKeys.CallState] = CallState;
        }

        if (cancel)
        {
            result[MetadataKeys.Cancel] = "1";
        }

        return result;
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Finished || State is null)
        {
            return;
        }

        using var empty = ValueCodec.EmptyRow(new Schema([], null));
        _ = await _client.PostTurnAsync(Method, empty, TurnMetadata(null, cancel: true), cancellationToken).ConfigureAwait(false);
        State = null;
        Finished = true;
    }

    protected virtual void DisposePending()
    {
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!Finished && State is not null)
            {
                try
                {
                    await CancelAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Cancellation during disposal is best effort.
                }
            }
        }
        finally
        {
            _disposed = true;
            DisposePending();
            Header?.Batch.Dispose();
        }
    }
}

public sealed class HttpProducerSession : HttpStreamSession, IRpcProducerSession
{
    private readonly Queue<AnnotatedBatch> _pending;

    internal HttpProducerSession(
        HttpRpcClient client,
        string method,
        AnnotatedBatch? header,
        IEnumerable<AnnotatedBatch> pending,
        string? state,
        string? callState,
        bool finished)
        : base(client, method, header, state, callState, finished) => _pending = new Queue<AnnotatedBatch>(pending);

    public async Task<AnnotatedBatch?> ReadNextAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_pending.Count > 0)
        {
            if (metadata is not null)
            {
                throw new InvalidOperationException("Metadata cannot be sent while the init response still has a buffered batch.");
            }

            return _pending.Dequeue();
        }

        if (Finished || State is null)
        {
            return null;
        }

        using var tick = ValueCodec.EmptyRow(new Schema([], null));
        var body = await Client.PostTurnAsync(Method, tick, TurnMetadata(metadata), cancellationToken).ConfigureAwait(false);
        var parsed = await Client.ParseStreamResponseAsync(body, hasHeader: false, cancellationToken).ConfigureAwait(false);
        State = parsed.State;
        Finished = parsed.Finished;
        foreach (var item in parsed.Data)
        {
            _pending.Enqueue(item);
        }

        if (_pending.Count > 1)
        {
            DisposePending();
            throw new InvalidDataException("Producer response violated lockstep by returning multiple data batches.");
        }

        return _pending.Count == 0 ? null : _pending.Dequeue();
    }

    protected override void DisposePending()
    {
        while (_pending.TryDequeue(out var item))
        {
            item.Batch.Dispose();
        }
    }
}

public sealed class HttpExchangeSession : HttpStreamSession, IRpcExchangeSession
{
    internal HttpExchangeSession(
        HttpRpcClient client,
        string method,
        AnnotatedBatch? header,
        string? state,
        string? callState,
        bool finished)
        : base(client, method, header, state, callState, finished)
    {
    }

    public async Task<AnnotatedBatch?> ExchangeAsync(
        RecordBatch input,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Finished || State is null)
        {
            return null;
        }

        var body = await Client.PostTurnAsync(Method, input, TurnMetadata(metadata), cancellationToken).ConfigureAwait(false);
        var parsed = await Client.ParseStreamResponseAsync(body, hasHeader: false, cancellationToken, exchangeTurn: true).ConfigureAwait(false);
        State = parsed.State;
        Finished = parsed.Finished;
        if (parsed.Data.Count > 1)
        {
            foreach (var item in parsed.Data)
            {
                item.Batch.Dispose();
            }

            throw new InvalidDataException("Exchange response violated lockstep by returning multiple data batches.");
        }

        return parsed.Data.Count == 0 ? null : parsed.Data[0];
    }
}
