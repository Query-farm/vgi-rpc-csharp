using System.Runtime.CompilerServices;
using Apache.Arrow;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

public abstract class RpcStreamSession : IRpcStreamSession
{
    private readonly RpcClient _client;
    private WireWriter? _input;
    private WireReader? _output;
    private bool _terminal;
    private bool _inputClosed;
    private bool _released;
    private bool _disposed;

    protected RpcStreamSession(
        RpcClient client,
        WireWriter input,
        WireReader? output,
        AnnotatedBatch? header)
    {
        _client = client;
        _input = input;
        _output = output;
        Header = header;
    }

    protected RpcStreamSession(RpcClient client, AnnotatedBatch? header)
    {
        _client = client;
        Header = header;
    }

    public AnnotatedBatch? Header { get; }

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
        var instance = Activator.CreateInstance<THeader>();
        for (var index = 0; index < properties.Length; index++)
        {
            if (!properties[index].CanWrite)
            {
                throw new InvalidOperationException($"Header property '{properties[index].Name}' is read-only.");
            }

            properties[index].SetValue(instance, values[index]);
        }

        return instance;
    }

    protected async Task<AnnotatedBatch?> SendAndReadAsync(
        RecordBatch input,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_terminal)
        {
            return null;
        }

        await EnsureInputAsync(input.Schema, cancellationToken).ConfigureAwait(false);
        await _client.WriteStreamBatchAsync(_input!, input, metadata, cancellationToken).ConfigureAwait(false);
        await EnsureOutputAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _client.ReadStreamDataAsync(_output!, cancellationToken).ConfigureAwait(false);
            _terminal = result is null;
            if (_terminal)
            {
                await CompleteTransportAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            _terminal = true;
            try
            {
                await CloseInputAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await DrainOutputAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            await CompleteTransportAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _terminal)
        {
            return;
        }

        await EnsureInputAsync(new Schema([], metadata: null), cancellationToken).ConfigureAwait(false);
        using var cancel = ValueCodec.EmptyRow(_input!.Schema);
        await _client.WriteStreamBatchAsync(
            _input!,
            cancel,
            new Dictionary<string, string> { [MetadataKeys.Cancel] = "true" },
            cancellationToken).ConfigureAwait(false);
        await CloseInputAsync(cancellationToken).ConfigureAwait(false);
        await EnsureOutputAsync(cancellationToken).ConfigureAwait(false);
        await DrainOutputAsync(cancellationToken).ConfigureAwait(false);
        _terminal = true;
        await CompleteTransportAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainOutputAsync(CancellationToken cancellationToken)
    {
        while (await _client.ReadStreamDataAsync(_output!, cancellationToken).ConfigureAwait(false) is { } batch)
        {
            batch.Batch.Dispose();
        }
    }

    private async Task CloseInputAsync(CancellationToken cancellationToken)
    {
        if (_inputClosed)
        {
            return;
        }

        await _input!.WriteEosAsync(cancellationToken).ConfigureAwait(false);
        _inputClosed = true;
    }

    private async Task EnsureInputAsync(Schema inputSchema, CancellationToken cancellationToken)
    {
        if (_input is not null)
        {
            return;
        }

        WireWriter? input = null;
        try
        {
            input = new WireWriter(_client.Transport.Output, inputSchema);
            await input.WriteStartAsync(cancellationToken).ConfigureAwait(false);
            await _client.Transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
            _input = input;
        }
        catch
        {
            if (input is not null) await input.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureOutputAsync(CancellationToken cancellationToken)
    {
        if (_output is not null)
        {
            return;
        }

        var output = new WireReader(_client.Transport.Input);
        try
        {
            await output.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            _output = output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private async Task CompleteTransportAsync(CancellationToken cancellationToken)
    {
        if (_released)
        {
            return;
        }

        try
        {
            if (!_inputClosed)
            {
                await CloseInputAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_input is not null) await _input.DisposeAsync().ConfigureAwait(false);
            _output?.Dispose();
            _released = true;
            _client.ReleaseStream();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_terminal && !_released)
            {
                // Dispose is graceful close, not cancellation. Closing the Arrow input stream
                // sends EOS and lets the peer drain naturally; callers that need on_cancel must
                // opt into CancelAsync. Besides matching the canonical Python/Rust clients,
                // this avoids manufacturing an empty dictionary-encoded batch solely for
                // teardown (dictionary replacement rules make that invalid for some schemas).
                await EnsureInputAsync(new Schema([], metadata: null), CancellationToken.None).ConfigureAwait(false);
                await CloseInputAsync(CancellationToken.None).ConfigureAwait(false);
                await EnsureOutputAsync(CancellationToken.None).ConfigureAwait(false);
                await DrainOutputAsync(CancellationToken.None).ConfigureAwait(false);
            }

        }
        finally
        {
            if (!_released && !_inputClosed)
            {
                try
                {
                    await CloseInputAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The peer may have already closed the connection after a terminal error.
                }
            }

            if (!_released)
            {
                if (_input is not null) await _input.DisposeAsync().ConfigureAwait(false);
                _output?.Dispose();
                _released = true;
                _client.ReleaseStream();
            }

            Header?.Batch.Dispose();
        }
    }
}

public sealed class RpcProducerSession : RpcStreamSession, IRpcProducerSession
{
    internal RpcProducerSession(RpcClient client, WireWriter input, WireReader? output, AnnotatedBatch? header)
        : base(client, input, output, header)
    {
    }

    public async Task<AnnotatedBatch?> ReadNextAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        using var tick = ValueCodec.EmptyRow(new Schema([], metadata: null));
        return await SendAndReadAsync(tick, metadata, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AnnotatedBatch> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await ReadNextAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } batch)
        {
            yield return batch;
        }
    }
}

public sealed class RpcExchangeSession : RpcStreamSession, IRpcExchangeSession
{
    internal RpcExchangeSession(RpcClient client, WireWriter input, WireReader? output, AnnotatedBatch? header)
        : base(client, input, output, header)
    {
    }

    internal RpcExchangeSession(RpcClient client, AnnotatedBatch? header)
        : base(client, header)
    {
    }

    public Task<AnnotatedBatch?> ExchangeAsync(
        RecordBatch input,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        SendAndReadAsync(input, metadata, cancellationToken);
}

/// <summary>Typed one-row convenience over a raw exchange session.</summary>
public sealed class RpcExchangeSession<TInput> : IAsyncDisposable
{
    private static readonly Schema s_inputSchema = SchemaDerivation.InnerSchemaFor(typeof(TInput));
    private readonly IRpcExchangeSession _inner;

    internal RpcExchangeSession(IRpcExchangeSession inner) => _inner = inner;

    public static Schema InputSchema => s_inputSchema;

    public AnnotatedBatch? Header => _inner.Header;

    public THeader GetHeader<THeader>() => _inner.GetHeader<THeader>();

    public Task<AnnotatedBatch?> ExchangeAsync(
        RecordBatch input,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        _inner.ExchangeAsync(input, metadata, cancellationToken);

    public async Task<AnnotatedBatch?> ExchangeAsync(
        TInput input,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var values = s_inputSchema.FieldsList
            .Select(field => typeof(TInput).GetProperty(ValueCodec.FindClrPropertyName(typeof(TInput), field))!.GetValue(input))
            .ToArray();
        using var batch = ValueCodec.BuildRow(s_inputSchema, values);
        return await _inner.ExchangeAsync(batch, metadata, cancellationToken).ConfigureAwait(false);
    }

    public Task CancelAsync(CancellationToken cancellationToken = default) => _inner.CancelAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
