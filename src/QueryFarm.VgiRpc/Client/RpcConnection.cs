using Apache.Arrow;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

/// <summary>
/// A connection to an RPC server speaking service interface <typeparamref name="T"/>.
/// <see cref="CreateProxy"/> returns a <see cref="RpcClientProxy{T}"/>-backed <typeparamref name="T"/>
/// whose methods marshal calls over <see cref="Transport"/> — see
/// <see cref="RpcClientProxy{T}"/> for the invocation/return-type machinery.
/// </summary>
public sealed class RpcConnection<T>(IRpcTransport transport)
    where T : class
{
    private static readonly Schema s_emptySchema = new([], metadata: null);

    public IRpcTransport Transport { get; } = transport;

    public T CreateProxy() => RpcClientProxy<T>.Create(this);

    /// <summary>Performs one unary call and returns the decoded result (<see langword="null"/> for a void method).</summary>
    public async Task<object?> CallUnaryAsync(RpcMethodInfo info, object?[] args, CancellationToken cancellationToken)
    {
        var requestBatch = ValueCodec.BuildRow(info.ParamsSchema, args);
        var requestMetadata = new Dictionary<string, string>
        {
            [MetadataKeys.Method] = info.WireName,
            [MetadataKeys.RequestVersion] = MetadataKeys.CurrentRequestVersion,
        };

        await using (var writer = new WireWriter(Transport.Output, info.ParamsSchema))
        {
            await writer.WriteBatchAsync(new AnnotatedBatch(requestBatch, requestMetadata), cancellationToken).ConfigureAwait(false);
        }

        using var reader = new WireReader(Transport.Input);
        await reader.ReadSchemaAsync(cancellationToken).ConfigureAwait(false);

        AnnotatedBatch? terminal = null;
        while (true)
        {
            var batch = await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                break;
            }

            var level = batch.GetMetadata(MetadataKeys.LogLevel);
            if (level is null)
            {
                terminal = batch;
            }
            else if (level == "EXCEPTION")
            {
                throw BuildException(batch);
            }

            // Non-exception log batches (INFO/WARN/etc.) are silently dropped for now — client
            // log callback wiring is a later milestone (see docs/roadmap.md).
        }

        if (info.ResultSchema.FieldsList.Count == 0)
        {
            return null;
        }

        if (terminal is null)
        {
            throw new RpcException("RpcException", $"Server closed the connection without returning a result for '{info.WireName}'.");
        }

        return ValueCodec.ExtractRow(terminal.Batch, [info.ResultClrType])[0];
    }

    private static RpcException BuildException(AnnotatedBatch batch)
    {
        var summary = batch.GetMetadata(MetadataKeys.LogMessage) ?? "Unknown remote error";
        var errorKind = batch.GetMetadata(MetadataKeys.ErrorKind);
        var requestId = batch.GetMetadata(MetadataKeys.RequestId) ?? "";

        var errorType = "RpcException";
        var message = summary;
        var traceback = "";
        var extraJson = batch.GetMetadata(MetadataKeys.LogExtra);
        if (extraJson is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(extraJson);
                if (doc.RootElement.TryGetProperty("exception_type", out var t))
                {
                    errorType = t.GetString() ?? errorType;
                }

                if (doc.RootElement.TryGetProperty("exception_message", out var m))
                {
                    message = m.GetString() ?? summary;
                }

                if (doc.RootElement.TryGetProperty("traceback", out var tb))
                {
                    traceback = tb.GetString() ?? "";
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed/absent log_extra — fall back to the summary message alone.
            }
        }

        return errorKind switch
        {
            MetadataKeys.ErrorKinds.MethodNotImplemented => new MethodNotImplementedException(message),
            MetadataKeys.ErrorKinds.ProtocolVersionMismatch => new ProtocolVersionException(message),
            MetadataKeys.ErrorKinds.SessionLost => new SessionLostException(message),
            MetadataKeys.ErrorKinds.ServerDraining => new ServerDrainingException(message),
            _ => new RpcException(errorType, message, traceback, requestId, errorKind),
        };
    }
}
