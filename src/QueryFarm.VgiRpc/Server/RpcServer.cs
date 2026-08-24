using Apache.Arrow;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Server;

/// <summary>
/// Dispatches unary RPC calls from a service interface to a plain implementation object. See
/// docs/roadmap.md — streaming, access logging, auth, and the <c>__describe__</c>/
/// <c>__transport_options__</c> synthetic methods land in later milestones; this is the
/// Milestone 1/2 unary-only core.
/// </summary>
public sealed class RpcServer
{
    private readonly IReadOnlyDictionary<string, RpcMethodInfo> _methods;
    private readonly object _implementation;
    private readonly string _serverId;

    public RpcServer(Type serviceInterface, object implementation, string? serverId = null)
    {
        _methods = ServiceRegistry.GetMethods(serviceInterface);
        _implementation = implementation;
        _serverId = serverId ?? Guid.NewGuid().ToString("n");
    }

    /// <summary>Serves requests off <paramref name="transport"/> until the channel closes.</summary>
    public async Task ServeAsync(IRpcTransport transport, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var more = await ServeOneAsync(transport, cancellationToken).ConfigureAwait(false);
            if (!more)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Handles exactly one request/response cycle. Returns <see langword="false"/> when the
    /// channel has reached a clean end-of-stream with no request to read (the normal way a
    /// <see cref="ServeAsync"/> loop terminates); protocol/dispatch errors are written back to
    /// the client as an error response and this returns <see langword="true"/> so the caller's
    /// serve loop continues.
    /// </summary>
    public async Task<bool> ServeOneAsync(IRpcTransport transport, CancellationToken cancellationToken = default)
    {
        AnnotatedBatch? request;
        try
        {
            // Deliberately NOT `using var` held for the rest of this method: a stream method
            // opens a second WireReader over the same transport.Input for its tick/exchange
            // loop, and some Stream implementations (observed with NetworkStream — Unix/TCP
            // sockets) read ahead into an internal buffer, silently stealing bytes that belong
            // to that second reader if the first one is still alive when it's constructed.
            // Disposing this one immediately after the request is fully read avoids that.
            using var reader = new WireReader(transport.Input);
            _ = await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            request = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The channel closed (cleanly or otherwise) before a full request arrived — the
            // normal way a ServeAsync loop ends when the client disconnects. Apache.Arrow
            // doesn't document a single exception type for "stream ended mid-schema", so this
            // catches broadly rather than risk an unhandled exception tearing down the worker
            // on a plain client disconnect.
            return false;
        }

        if (request is null)
        {
            return false;
        }

        var methodName = request.GetMetadata(MetadataKeys.Method);
        if (methodName is null)
        {
            await WriteErrorStreamAsync(transport.Output, s_emptySchema, new RpcException("RpcException", "Request batch is missing vgi_rpc.method metadata."), cancellationToken).ConfigureAwait(false);
            return true;
        }

        var requestVersion = request.GetMetadata(MetadataKeys.RequestVersion);
        if (requestVersion != MetadataKeys.CurrentRequestVersion)
        {
            await WriteErrorStreamAsync(
                transport.Output,
                s_emptySchema,
                new VersionException(nameof(VersionException), $"Unsupported request_version '{requestVersion}' (expected '{MetadataKeys.CurrentRequestVersion}')."),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!_methods.TryGetValue(methodName, out var info))
        {
            var available = string.Join(", ", _methods.Keys.OrderBy(k => k, StringComparer.Ordinal));
            await WriteErrorStreamAsync(
                transport.Output,
                s_emptySchema,
                new MethodNotImplementedException($"Unknown method: '{methodName}'. Available methods: [{available}]"),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        object?[] args;
        try
        {
            args = ValueCodec.ExtractRow(request.Batch, info.Parameters.Select(p => p.ParameterType).ToArray());
        }
        catch (Exception exc)
        {
            await WriteErrorStreamAsync(transport.Output, info.ResultSchema, exc, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (info.Kind == RpcMethodKind.Stream)
        {
            return await ServeStreamAsync(transport, info, args, cancellationToken).ConfigureAwait(false);
        }

        await using var writer = new WireWriter(transport.Output, info.ResultSchema);
        var context = info.HasContextParameter ? new BufferedCallContext() : null;
        try
        {
            var result = await info.InvokeAsync(_implementation, args, context).ConfigureAwait(false);
            if (context is not null)
            {
                foreach (var logMessage in context.Buffered)
                {
                    await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(info.ResultSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
                }
            }

            var resultBatch = info.ResultSchema.FieldsList.Count == 0
                ? ValueCodec.EmptyRow(info.ResultSchema)
                : ValueCodec.BuildRow(info.ResultSchema, [result]);
            await writer.WriteBatchAsync(new AnnotatedBatch(resultBatch, null), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            var actual = Unwrap(exc);
            var metadata = LogMessage.FromException(actual).AddToMetadata();
            await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(info.ResultSchema), metadata), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Drives a streaming call's lockstep turns: one continuous output IPC stream (opened once,
    /// for <see cref="IRpcStream.OutputSchema"/>) and one continuous input IPC stream (opened
    /// once, reading successive tick/exchange batches) for the lifetime of the call. See
    /// <see cref="StreamState"/>/<see cref="ProducerState"/>/<see cref="ExchangeState"/> and
    /// WIRE_PROTOCOL.md's lockstep streaming section (canonical Python repo).
    /// </summary>
    private async Task<bool> ServeStreamAsync(IRpcTransport transport, RpcMethodInfo info, object?[] args, CancellationToken cancellationToken)
    {
        var invokeContext = info.HasContextParameter ? new BufferedCallContext() : null;
        IRpcStream stream;
        try
        {
            var raw = await info.InvokeAsync(_implementation, args, invokeContext).ConfigureAwait(false);
            stream = (IRpcStream)raw!;
        }
        catch (Exception exc)
        {
            await WriteErrorStreamAsync(transport.Output, s_emptySchema, Unwrap(exc), cancellationToken).ConfigureAwait(false);
            return true;
        }

        // A stream header is its own complete IPC stream (schema + one row + EOS), written
        // before the main output stream begins — see IRpcStream.Header's doc comment.
        if (stream.Header is not null)
        {
            var headerType = stream.Header.GetType();
            var headerSchema = SchemaDerivation.InnerSchemaFor(headerType);
            var headerValues = headerSchema.FieldsList
                .Select(f => headerType.GetProperty(ValueCodec.FindClrPropertyName(headerType, f))!.GetValue(stream.Header))
                .ToList();
            var headerBatch = ValueCodec.BuildRow(headerSchema, headerValues);
            await using var headerWriter = new WireWriter(transport.Output, headerSchema);
            if (invokeContext is not null)
            {
                foreach (var logMessage in invokeContext.Buffered)
                {
                    await headerWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(headerSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
                }

                invokeContext.Buffered.Clear();
            }

            await headerWriter.WriteBatchAsync(new AnnotatedBatch(headerBatch, null), cancellationToken).ConfigureAwait(false);
        }

        var outputSchema = stream.OutputSchema;
        await using var outputWriter = new WireWriter(transport.Output, outputSchema);
        // Write the schema eagerly, not lazily-on-first-batch: a stream that finishes with zero
        // batches (e.g. an empty producer) must still produce a valid (schema, EOS) IPC stream.
        await outputWriter.WriteStartAsync(cancellationToken).ConfigureAwait(false);

        if (invokeContext is not null)
        {
            foreach (var logMessage in invokeContext.Buffered)
            {
                await outputWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
            }
        }

        using var inputReader = new WireReader(transport.Input);
        try
        {
            _ = await inputReader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return true; // client never opened the tick/exchange input stream
        }

        while (true)
        {
            AnnotatedBatch? inputBatch;
            try
            {
                inputBatch = await inputReader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                break; // client disconnected mid-stream
            }

            if (inputBatch is null)
            {
                break; // client closed its input stream (EOS) — the normal way an exchange ends
            }

            if (inputBatch.GetMetadata(MetadataKeys.Cancel) is not null)
            {
                stream.State.OnCancel(invokeContext);
                break;
            }

            var collector = new OutputCollector(outputSchema);
            var turnContext = info.HasContextParameter ? new StreamCallContext(collector) : null;
            try
            {
                if (stream.InputSchema is { FieldsList.Count: > 0 } declaredInputSchema)
                {
                    inputBatch = inputBatch with { Batch = ValueCodec.CoerceBatch(inputBatch.Batch, declaredInputSchema) };
                }

                await stream.State.ProcessAsync(inputBatch, collector, turnContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                var metadata = LogMessage.FromException(Unwrap(exc)).AddToMetadata();
                await outputWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), metadata), cancellationToken).ConfigureAwait(false);
                break;
            }

            foreach (var logMessage in collector.Logs)
            {
                await outputWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
            }

            if (collector.EmittedBatch is not null)
            {
                await outputWriter.WriteBatchAsync(new AnnotatedBatch(collector.EmittedBatch, null), cancellationToken).ConfigureAwait(false);
            }

            if (collector.Finished)
            {
                break;
            }
        }

        return true;
    }

    private static Exception Unwrap(Exception exc) =>
        exc is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : exc;

    /// <summary>Forwards a stream turn's <see cref="ICallContext.EmitLog"/> calls into that
    /// turn's <see cref="OutputCollector"/> — matching Python's unified <c>ctx.emit_client_log</c>/
    /// <c>out.client_log()</c> (the same sink) during stream processing.</summary>
    private sealed class StreamCallContext(OutputCollector collector) : ICallContext
    {
        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            collector.ClientLog(level, message, extra);
    }

    /// <summary>
    /// Buffers <see cref="ICallContext.EmitLog"/> calls made during a synchronous method body,
    /// flushed as zero-row log batches immediately before the result batch. Since a method body
    /// runs to completion before <see cref="ServeOneAsync"/> gets a chance to write anything,
    /// buffer-then-flush produces the same wire sequence true incremental interleaving would.
    /// </summary>
    private sealed class BufferedCallContext : ICallContext
    {
        public List<LogMessage> Buffered { get; } = [];

        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
            Buffered.Add(new LogMessage(level, message, extra));
    }

    private static readonly Schema s_emptySchema = new([], metadata: null);

    private static async Task WriteErrorStreamAsync(Stream output, Schema schema, Exception exception, CancellationToken cancellationToken)
    {
        var metadata = LogMessage.FromException(exception).AddToMetadata();
        await using var writer = new WireWriter(output, schema);
        await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(schema), metadata), cancellationToken).ConfigureAwait(false);
    }
}
