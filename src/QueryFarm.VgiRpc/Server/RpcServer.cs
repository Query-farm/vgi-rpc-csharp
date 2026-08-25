using System.Security.Cryptography;
using System.Text;
using Apache.Arrow;
using QueryFarm.VgiRpc.AccessLog;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Shm;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Server;

/// <summary>
/// Dispatches RPC calls (unary and streaming) from a service interface to a plain
/// implementation object. See docs/roadmap.md — auth and the `__describe__` synthetic method
/// land in a later milestone; `__transport_options__` (M14) is implemented here.
/// </summary>
public sealed class RpcServer
{
    private readonly IReadOnlyDictionary<string, RpcMethodInfo> _methods;
    private readonly object _implementation;
    private readonly string _serverId;
    private readonly IAccessLogSink? _accessLog;
    private readonly IRpcDispatchHook? _dispatchHook;

    /// <summary>The service interface's simple name — the access log's <c>protocol</c> field.</summary>
    public string ProtocolName { get; }

    /// <summary>
    /// A SHA-256 hex digest derived from the registered methods' wire names and schemas —
    /// the access log's <c>protocol_hash</c> field. Unlike the canonical Python implementation's
    /// hash (computed from its `__describe__` payload, not yet implemented here), this is NOT
    /// guaranteed byte-identical across ports — per the Python repo's own CLAUDE.md, that was
    /// never the cross-language contract to begin with (protocol_version is); this hash only
    /// needs to be a stable, real value for this one server process.
    /// </summary>
    public string ProtocolHash { get; }

    public string? ServerVersion { get; init; }

    /// <summary>
    /// The registered methods, keyed by wire name — exposed for transports (see
    /// <c>QueryFarm.VgiRpc.Http</c>) that dispatch outside <see cref="ServeAsync"/>'s own loop
    /// and need to resolve a method themselves. Mirrors Python's public <c>RpcServer.methods</c>.
    /// </summary>
    public IReadOnlyDictionary<string, RpcMethodInfo> Methods => _methods;

    /// <summary>The service implementation instance, for transports that invoke methods directly
    /// rather than through <see cref="ServeOneAsync"/>.</summary>
    internal object Implementation => _implementation;

    /// <summary>This server instance's id — see <see cref="AccessLogRecord.ServerId"/>.</summary>
    internal string ServerId => _serverId;

    /// <summary>The configured access-log sink, or <see langword="null"/> if none — for
    /// transports that emit their own <see cref="AccessLogRecord"/>s outside this dispatch loop.</summary>
    internal IAccessLogSink? AccessLog => _accessLog;

    /// <summary>The configured dispatch hook (see <see cref="IRpcDispatchHook"/>, M16), or
    /// <see langword="null"/> if none — for transports (<c>QueryFarm.VgiRpc.Http</c>) that
    /// dispatch outside this class's own <see cref="ServeOneAsync"/>/<see cref="ServeStreamAsync"/>
    /// loop and so call <see cref="IRpcDispatchHook.OnDispatchStart"/>/
    /// <see cref="IRpcDispatchHook.OnDispatchEnd"/> themselves, around their own dispatch points.</summary>
    internal IRpcDispatchHook? DispatchHook => _dispatchHook;

    /// <param name="serviceInterface">The service interface type to reflect method schemas from.</param>
    /// <param name="implementation">The service implementation instance to dispatch calls to.</param>
    /// <param name="serverId">This server instance's id — a random GUID when omitted.</param>
    /// <param name="accessLog">Optional access-log sink — see <see cref="AccessLog"/>.</param>
    /// <param name="dispatchHooks">Zero or more observability hooks (M16) — e.g.
    /// <c>QueryFarm.VgiRpc.OpenTelemetry.OtelDispatchHook</c>,
    /// <c>QueryFarm.VgiRpc.Sentry.SentryDispatchHook</c> — fanned out via
    /// <see cref="CompositeDispatchHook"/>. Empty/<see langword="null"/> (the default) means no
    /// hooks at all, matching every other optional-observability feature's opt-in-by-default
    /// posture in this port.</param>
    public RpcServer(Type serviceInterface, object implementation, string? serverId = null, IAccessLogSink? accessLog = null, IReadOnlyList<IRpcDispatchHook>? dispatchHooks = null)
    {
        _methods = ServiceRegistry.GetMethods(serviceInterface);
        _implementation = implementation;
        _serverId = serverId ?? Guid.NewGuid().ToString("n");
        _accessLog = accessLog;
        _dispatchHook = dispatchHooks is { Count: > 0 } ? new CompositeDispatchHook(dispatchHooks) : null;
        ProtocolName = serviceInterface.Name;
        ProtocolHash = ComputeProtocolHash(_methods);
    }

    private static string ComputeProtocolHash(IReadOnlyDictionary<string, RpcMethodInfo> methods)
    {
        var sb = new StringBuilder();
        foreach (var name in methods.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var info = methods[name];
            sb.Append(name).Append(':').Append(info.Kind).Append('|');
            foreach (var field in info.ParamsSchema.FieldsList)
            {
                sb.Append(field.Name).Append(':').Append(field.DataType.TypeId).Append(field.IsNullable).Append(',');
            }

            sb.Append('>');
            foreach (var field in info.ResultSchema.FieldsList)
            {
                sb.Append(field.Name).Append(':').Append(field.DataType.TypeId).Append(field.IsNullable).Append(',');
            }

            sb.Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
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

        // __transport_options__ (M14, WIRE_PROTOCOL.md §15): a built-in synthetic method,
        // parallel to __describe__, through which client and server negotiate transport
        // capabilities — chiefly SHM. Handled here rather than via _methods (it's not a real
        // service method) and answered unconditionally: this port always supports the SHM side
        // channel on every non-HTTP transport ServeAsync itself is ever invoked from (HTTP has
        // its own, entirely separate dispatch path in QueryFarm.VgiRpc.Http that never calls
        // this method at all — the same "exemption needs zero extra code" structural argument
        // M11's proxy-proof note made for its own HTTP-only exemptions).
        if (methodName == TransportOptionsMethodName)
        {
            var responseMetadata = new Dictionary<string, string>
            {
                [MetadataKeys.TransportShm] = "true",
                [MetadataKeys.ServerId] = _serverId,
                [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
            };
            await using var transportWriter = new WireWriter(transport.Output, s_emptySchema);
            await transportWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(s_emptySchema), responseMetadata), cancellationToken).ConfigureAwait(false);
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

        // Dynamic SHM attach (M14): a client that has negotiated SHM support advertises its
        // segment in the request batch's own metadata (WIRE_PROTOCOL.md §11) — on every unary/
        // stream-init request, but NOT on later stream turns (see ServeStreamAsync's own SHM
        // handling for why that matters: the segment attached here must be threaded through and
        // reused for a stream's whole lifetime, not re-derived per turn). Malformed metadata or a
        // segment that fails to attach (wrong size, doesn't exist) is treated as "no SHM for this
        // request" rather than a hard error — matches Python's _maybe_attach_shm posture exactly:
        // a dynamically-attached segment is the caller's optimization, not a contract the server
        // must enforce.
        var shm = TryAttachShm(request);
        if (shm is not null)
        {
            try
            {
                var (resolvedBatch, resolvedMetadata, release) = await ShmPointerBatch.ResolveAsync(request.Batch, request.Metadata, shm, cancellationToken).ConfigureAwait(false);
                request = request with { Batch = resolvedBatch, Metadata = resolvedMetadata };
                release?.Invoke();
            }
            catch (Exception exc)
            {
                shm.Dispose();
                await WriteErrorStreamAsync(transport.Output, s_emptySchema, exc, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        object?[] args;
        try
        {
            args = ValueCodec.ExtractRow(request.Batch, info.Parameters.Select(p => p.ParameterType).ToArray());
        }
        catch (Exception exc)
        {
            shm?.Dispose();
            await WriteErrorStreamAsync(transport.Output, info.ResultSchema, exc, cancellationToken).ConfigureAwait(false);
            await EmitAccessLogAsync(info.WireName, "unary", "error", exc.GetType().Name, exc.Message, System.Diagnostics.Stopwatch.GetTimestamp(), requestForLog: request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();

        if (info.Kind == RpcMethodKind.Stream)
        {
            // ServeStreamAsync owns shm from here on (a stream's whole lifetime, across every
            // turn) — it disposes it, ServeOneAsync must not.
            return await ServeStreamAsync(transport, info, args, start, shm, cancellationToken).ConfigureAwait(false);
        }

        using var shmForUnary = shm;
        await using var writer = new WireWriter(transport.Output, info.ResultSchema);
        var context = info.HasContextParameter ? new BufferedCallContext() : null;
        var status = "ok";
        var errorType = "";
        var errorMessage = "";
        Exception? hookError = null;
        var hookInfo = new DispatchHookInfo(info.WireName, "unary", ProtocolName, _serverId);
        var hookToken = _dispatchHook?.OnDispatchStart(hookInfo);
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
            IReadOnlyDictionary<string, string>? resultMetadata = null;
            if (shmForUnary is not null)
            {
                (resultBatch, resultMetadata) = await ShmPointerBatch.MaybeWriteAsync(resultBatch, null, shmForUnary, cancellationToken).ConfigureAwait(false);
            }

            await writer.WriteBatchAsync(new AnnotatedBatch(resultBatch, resultMetadata), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            var actual = Unwrap(exc);
            status = "error";
            errorType = actual.GetType().Name;
            errorMessage = actual.Message;
            hookError = actual;
            var metadata = LogMessage.FromException(actual).AddToMetadata();
            await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(info.ResultSchema), metadata), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, hookError);
            await EmitAccessLogAsync(info.WireName, "unary", status, errorType, errorMessage, start, requestForLog: request, cancellationToken: cancellationToken).ConfigureAwait(false);
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
    /// <param name="transport">The transport this stream's turns are read from/written to.</param>
    /// <param name="info">Reflection info for the RPC method that constructs this stream.</param>
    /// <param name="args">Already-extracted/resolved constructor arguments.</param>
    /// <param name="start">Timestamp (from <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>)
    /// the whole call began, for access-log duration.</param>
    /// <param name="shm">The SHM segment attached from the stream-init request's own metadata
    /// (if any), owned by this method for the stream's whole lifetime and disposed on every exit
    /// path. Unlike unary calls, a stream's later turns never re-advertise the segment identity
    /// (WIRE_PROTOCOL.md §11 documents the pointer keys `SHM_OFFSET_KEY`/`SHM_LENGTH_KEY` a turn
    /// may carry, but the *segment identity* — `SHM_SEGMENT_NAME_KEY`/`SHM_SEGMENT_SIZE_KEY` — is
    /// only ever sent once, on the request that establishes the call; confirmed against the
    /// canonical Python client's own `_write_request`/`_write_batch` split), so this one
    /// attachment must be reused across every subsequent turn, never re-derived per turn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<bool> ServeStreamAsync(IRpcTransport transport, RpcMethodInfo info, object?[] args, long start, ShmSegment? shm, CancellationToken cancellationToken)
    {
        using var ownedShm = shm;

        // Required by access_log.schema.json whenever method_type=stream (no exception for the
        // error paths below) — generated up front so every exit from this method can log it.
        // Matches Python's uuid.uuid4().hex (32 lowercase hex chars).
        var streamId = Guid.NewGuid().ToString("N");

        var hookInfo = new DispatchHookInfo(info.WireName, "stream", ProtocolName, _serverId);
        var hookToken = _dispatchHook?.OnDispatchStart(hookInfo);

        var invokeContext = info.HasContextParameter ? new BufferedCallContext() : null;
        IRpcStream stream;
        try
        {
            var raw = await info.InvokeAsync(_implementation, args, invokeContext).ConfigureAwait(false);
            stream = (IRpcStream)raw!;
        }
        catch (Exception exc)
        {
            var actual = Unwrap(exc);
            _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, actual);
            await WriteErrorStreamAsync(transport.Output, s_emptySchema, actual, cancellationToken).ConfigureAwait(false);
            await EmitAccessLogAsync(info.WireName, "stream", "error", actual.GetType().Name, actual.Message, start, streamId: streamId, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            // client never opened the tick/exchange input stream
            _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, null);
            await EmitAccessLogAsync(info.WireName, "stream", "ok", "", "", start, streamId: streamId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }

        var streamStatus = "ok";
        var streamErrorType = "";
        var streamErrorMessage = "";
        Exception? streamHookError = null;
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
            // Always construct a per-turn context — unlike invokeContext above (which mirrors
            // whether the RPC method that RETURNED the stream declared a ctx parameter, since
            // that gates a reflection-invoke arg count), StreamState.ProcessAsync's own signature
            // always accepts an ICallContext?, independent of the constructor method's shape. A
            // StreamState reading ctx.Session (sticky sessions, docs/roadmap.md M10) needs a real
            // object here even when the constructor method itself took no ctx param.
            var turnContext = new StreamCallContext(collector);
            try
            {
                // Per-turn SHM pointer resolution (M14) — reuses the ONE segment attached at
                // stream-init (ownedShm), never re-derived: see this method's doc comment on
                // `shm` for why later turns don't re-advertise the segment identity.
                if (ownedShm is not null)
                {
                    var (resolvedBatch, resolvedMetadata, release) = await ShmPointerBatch.ResolveAsync(inputBatch.Batch, inputBatch.Metadata, ownedShm, cancellationToken).ConfigureAwait(false);
                    inputBatch = inputBatch with { Batch = resolvedBatch, Metadata = resolvedMetadata };
                    release?.Invoke();
                }

                if (stream.InputSchema is { FieldsList.Count: > 0 } declaredInputSchema)
                {
                    inputBatch = inputBatch with { Batch = ValueCodec.CoerceBatch(inputBatch.Batch, declaredInputSchema) };
                }

                await stream.State.ProcessAsync(inputBatch, collector, turnContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exc)
            {
                var actual = Unwrap(exc);
                streamStatus = "error";
                streamErrorType = actual.GetType().Name;
                streamErrorMessage = actual.Message;
                streamHookError = actual;
                var metadata = LogMessage.FromException(actual).AddToMetadata();
                await outputWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), metadata), cancellationToken).ConfigureAwait(false);
                break;
            }

            foreach (var logMessage in collector.Logs)
            {
                await outputWriter.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(outputSchema), logMessage.AddToMetadata()), cancellationToken).ConfigureAwait(false);
            }

            if (collector.EmittedBatch is not null)
            {
                var emitted = collector.EmittedBatch;
                IReadOnlyDictionary<string, string>? emittedMetadata = null;
                if (ownedShm is not null)
                {
                    (emitted, emittedMetadata) = await ShmPointerBatch.MaybeWriteAsync(emitted, null, ownedShm, cancellationToken).ConfigureAwait(false);
                }

                await outputWriter.WriteBatchAsync(new AnnotatedBatch(emitted, emittedMetadata), cancellationToken).ConfigureAwait(false);
            }

            if (collector.Finished)
            {
                break;
            }
        }

        _dispatchHook?.OnDispatchEnd(hookToken, hookInfo, streamHookError);
        await EmitAccessLogAsync(info.WireName, "stream", streamStatus, streamErrorType, streamErrorMessage, start, streamId: streamId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Builds and hands an <see cref="AccessLogRecord"/> to <see cref="_accessLog"/>, if one is
    /// configured. <paramref name="requestForLog"/> (unary calls only) is re-serialized as a
    /// self-contained Arrow IPC stream to satisfy access_log.schema.json's "unary requires
    /// request_data unless truncated" rule; <paramref name="streamId"/> (stream calls only)
    /// satisfies its "stream requires stream_id" rule. See docs/access-log-spec.md.
    /// </summary>
    private async Task EmitAccessLogAsync(
        string method,
        string methodType,
        string status,
        string errorType,
        string errorMessage,
        long startTimestamp,
        AnnotatedBatch? requestForLog = null,
        string? streamId = null,
        CancellationToken cancellationToken = default)
    {
        if (_accessLog is null)
        {
            return;
        }

        var durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        string? requestData = null;
        string? truncated = null;
        long? originalRequestBytes = null;
        if (requestForLog is not null)
        {
            var raw = await SerializeForAccessLogAsync(requestForLog, cancellationToken).ConfigureAwait(false);
            if (_accessLog.IncludeRequestData)
            {
                requestData = Convert.ToBase64String(raw);
            }
            else
            {
                // base64 length is a pure function of the byte count — matches Python's
                // `4 * ((len(raw) + 2) // 3)` rather than paying to encode a payload nobody
                // asked to see at this log level.
                originalRequestBytes = 4L * ((raw.Length + 2) / 3);
                truncated = "payload_omitted";
            }
        }

        _accessLog.Write(new AccessLogRecord(
            Timestamp: DateTimeOffset.UtcNow,
            ServerId: _serverId,
            Protocol: ProtocolName,
            ProtocolHash: ProtocolHash,
            Method: method,
            MethodType: methodType,
            Status: status,
            DurationMs: durationMs,
            ErrorType: errorType,
            ErrorMessage: string.IsNullOrEmpty(errorMessage) ? null : errorMessage,
            ServerVersion: ServerVersion,
            StreamId: streamId,
            RequestData: requestData,
            Truncated: truncated,
            OriginalRequestBytes: originalRequestBytes));
    }

    /// <summary>
    /// Re-frames an already-read request <see cref="AnnotatedBatch"/> as a fresh, self-contained
    /// Arrow IPC stream (schema message, the one batch with its original custom_metadata, EOS) —
    /// what <c>pyarrow.ipc.open_stream</c> (and the conformance suite's access-log validator)
    /// requires. Mirrors Python's <c>_request_wire_bytes</c> fallback path (used there whenever
    /// the raw wire bytes aren't separately available, which for a shared pipe/socket stream is
    /// always).
    /// </summary>
    private static async Task<byte[]> SerializeForAccessLogAsync(AnnotatedBatch batch, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        await using (var writer = new WireWriter(ms, batch.Batch.Schema))
        {
            await writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        return ms.ToArray();
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

    private const string TransportOptionsMethodName = "__transport_options__";

    /// <summary>Attaches to a client-advertised SHM segment named in <paramref name="request"/>'s
    /// own metadata (WIRE_PROTOCOL.md §11 "SHM segment identity in request metadata"), or returns
    /// <see langword="null"/> if none was advertised or the metadata is malformed/the segment
    /// can't be attached. Never throws — matches Python's <c>_maybe_attach_shm</c>: a caller that
    /// advertises a bad segment just gets treated as if it advertised none at all, since SHM is
    /// purely the caller's own optimization, never a contract this port enforces.</summary>
    private static ShmSegment? TryAttachShm(AnnotatedBatch request)
    {
        var name = request.GetMetadata(MetadataKeys.ShmSegmentName);
        if (name is null)
        {
            return null;
        }

        var sizeText = request.GetMetadata(MetadataKeys.ShmSegmentSize);
        if (sizeText is null || !long.TryParse(sizeText, out var size))
        {
            return null;
        }

        try
        {
            return ShmSegment.Attach(name, size);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task WriteErrorStreamAsync(Stream output, Schema schema, Exception exception, CancellationToken cancellationToken)
    {
        var metadata = LogMessage.FromException(exception).AddToMetadata();
        await using var writer = new WireWriter(output, schema);
        await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(schema), metadata), cancellationToken).ConfigureAwait(false);
    }
}
