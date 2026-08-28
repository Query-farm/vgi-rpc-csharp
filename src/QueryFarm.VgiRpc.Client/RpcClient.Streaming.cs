using Apache.Arrow;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Shm;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

public sealed partial class RpcClient
{
    private static readonly Schema s_emptySchema = new([], metadata: null);

    async Task<IRpcProducerSession> IRpcClient.OpenProducerAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken) =>
        await OpenProducerAsync(method, parameters, hasHeader, metadata, cancellationToken).ConfigureAwait(false);

    async Task<IRpcExchangeSession> IRpcClient.OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        Schema inputSchema,
        bool hasHeader,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken) =>
        await OpenExchangeAsync(method, parameters, inputSchema, hasHeader, metadata, cancellationToken).ConfigureAwait(false);

    async Task<IRpcExchangeSession> IRpcClient.OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken) =>
        await OpenExchangeAsync(method, parameters, hasHeader, metadata, cancellationToken).ConfigureAwait(false);

    public Task<RpcProducerSession> OpenProducerAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        OpenStreamAsync(
            method,
            parameters,
            s_emptySchema,
            hasHeader,
            static (client, input, output, header) => new RpcProducerSession(client, input, output, header),
            metadata,
            cancellationToken);

    public Task<RpcExchangeSession> OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        Schema inputSchema,
        bool hasHeader = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputSchema);
        return OpenStreamAsync(
            method,
            parameters,
            inputSchema,
            hasHeader,
            static (client, input, output, header) => new RpcExchangeSession(client, input, output, header),
            metadata,
            cancellationToken);
    }

    /// <summary>
    /// Opens an exchange whose input schema is established by its first turn. This is required
    /// for dynamic Arrow exchanges whose schema is not known when the stream constructor runs.
    /// </summary>
    public async Task<RpcExchangeSession> OpenExchangeAsync(
        string method,
        RecordBatch parameters,
        bool hasHeader = false,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        AnnotatedBatch? header = null;
        try
        {
            await EnsureTransportOptionsAsync(cancellationToken).ConfigureAwait(false);
            _sharedMemory?.Reset();
            var requestMetadata = RequestMetadata(method, metadata);
            AddSharedMemoryMetadata(requestMetadata);
            var outgoing = await ShmPointerBatch.MaybeWriteAsync(parameters, requestMetadata, _sharedMemory, cancellationToken).ConfigureAwait(false);
            await using (var writer = new WireWriter(_transport.Output, parameters.Schema))
            {
                try
                {
                    await writer.WriteBatchAsync(new AnnotatedBatch(outgoing.Batch, outgoing.Metadata), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (!ReferenceEquals(outgoing.Batch, parameters)) outgoing.Batch.Dispose();
                }
            }

            if (hasHeader)
            {
                header = await ReadSingleDataStreamAsync(_transport.Input, cancellationToken).ConfigureAwait(false);
            }

            var session = new RpcExchangeSession(this, header);
            header = null;
            return session;
        }
        catch
        {
            header?.Batch.Dispose();
            _operationLock.Release();
            throw;
        }
    }

    private async Task<TSession> OpenStreamAsync<TSession>(
        string method,
        RecordBatch parameters,
        Schema inputSchema,
        bool hasHeader,
        Func<RpcClient, WireWriter, WireReader?, AnnotatedBatch?, TSession> create,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
        where TSession : RpcStreamSession
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        WireWriter? inputWriter = null;
        WireReader? outputReader = null;
        AnnotatedBatch? header = null;
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
            await using (var requestWriter = new WireWriter(_transport.Output, parameters.Schema))
            {
                try
                {
                    await requestWriter.WriteBatchAsync(
                        new AnnotatedBatch(outgoing.Batch, outgoing.Metadata),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (!ReferenceEquals(outgoing.Batch, parameters))
                    {
                        outgoing.Batch.Dispose();
                    }
                }
            }

            if (hasHeader)
            {
                header = await ReadSingleDataStreamAsync(_transport.Input, cancellationToken).ConfigureAwait(false);
            }

            // Python workers wait for the input schema before publishing the output schema;
            // C# workers publish output first. Sending this eagerly is compatible with both.
            inputWriter = new WireWriter(_transport.Output, inputSchema);
            await inputWriter.WriteStartAsync(cancellationToken).ConfigureAwait(false);
            await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return create(this, inputWriter, outputReader, header);
        }
        catch
        {
            if (inputWriter is not null)
            {
                await inputWriter.DisposeAsync().ConfigureAwait(false);
            }

            outputReader?.Dispose();
            header?.Batch.Dispose();
            _operationLock.Release();
            throw;
        }
    }

    private async Task<AnnotatedBatch?> ReadSingleDataStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new WireReader(stream);
        await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        AnnotatedBatch? result = null;
        try
        {
            while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } batch)
            {
                var level = batch.GetMetadata(MetadataKeys.LogLevel);
                if (level == "EXCEPTION")
                {
                    var exception = RpcErrorDecoder.Decode(batch);
                    batch.Batch.Dispose();
                    throw exception;
                }

                if (level is not null)
                {
                    DispatchLog(batch);
                    batch.Batch.Dispose();
                    continue;
                }

                result?.Batch.Dispose();
                result = batch;
            }

            var transfer = result;
            result = null;
            return transfer;
        }
        finally
        {
            result?.Batch.Dispose();
        }
    }

    internal async Task<AnnotatedBatch?> ReadStreamDataAsync(
        WireReader reader,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } batch)
        {
            var level = batch.GetMetadata(MetadataKeys.LogLevel);
            if (level == "EXCEPTION")
            {
                var exception = RpcErrorDecoder.Decode(batch);
                batch.Batch.Dispose();
                throw exception;
            }

            if (level is not null)
            {
                DispatchLog(batch);
                batch.Batch.Dispose();
                continue;
            }

            return await ResolveIncomingAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    internal async Task WriteStreamBatchAsync(
        WireWriter writer,
        RecordBatch batch,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        var outgoing = await ShmPointerBatch.MaybeWriteAsync(batch, metadata, _sharedMemory, cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(outgoing.Batch, outgoing.Metadata), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(outgoing.Batch, batch))
            {
                outgoing.Batch.Dispose();
            }
        }

        await _transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void ReleaseStream() => _operationLock.Release();
}
