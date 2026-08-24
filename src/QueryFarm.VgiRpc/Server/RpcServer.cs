using Apache.Arrow;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
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
        using var reader = new WireReader(transport.Input);
        Schema paramsSchema;
        AnnotatedBatch? request;
        try
        {
            paramsSchema = await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
            request = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
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

        await using var writer = new WireWriter(transport.Output, info.ResultSchema);
        try
        {
            var result = await info.InvokeAsync(_implementation, args).ConfigureAwait(false);
            var resultBatch = info.ResultSchema.FieldsList.Count == 0
                ? ValueCodec.EmptyRow(info.ResultSchema)
                : ValueCodec.BuildRow(info.ResultSchema, [result]);
            await writer.WriteBatchAsync(new AnnotatedBatch(resultBatch, null), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            var actual = exc is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : exc;
            var metadata = LogMessage.FromException(actual).AddToMetadata();
            await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(info.ResultSchema), metadata), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static readonly Schema s_emptySchema = new([], metadata: null);

    private static async Task WriteErrorStreamAsync(Stream output, Schema schema, Exception exception, CancellationToken cancellationToken)
    {
        var metadata = LogMessage.FromException(exception).AddToMetadata();
        await using var writer = new WireWriter(output, schema);
        await writer.WriteBatchAsync(new AnnotatedBatch(ValueCodec.EmptyRow(schema), metadata), cancellationToken).ConfigureAwait(false);
    }
}
