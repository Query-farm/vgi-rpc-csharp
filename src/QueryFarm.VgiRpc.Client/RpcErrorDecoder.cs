using System.Text.Json;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

/// <summary>Turns an EXCEPTION batch into the <see cref="RpcException"/> a caller catches.</summary>
/// <remarks>
/// The single client decode point: every path -- pipe/stdio/unix/TCP unary and stream, HTTP
/// unary, stream init and exchange, and an externalized error batch once resolved -- funnels its
/// EXCEPTION batches through here, so the error model's three layers (WIRE_PROTOCOL.md §8) cannot
/// be surfaced on one path and dropped on another. Each is read from its top-level key first and
/// from the <c>log_extra</c> mirror when the key is absent. Nothing here retries: see
/// <see cref="RpcException.IsRetryable"/>.
/// </remarks>
public static class RpcErrorDecoder
{
    public static RpcException Decode(AnnotatedBatch batch)
    {
        var summary = batch.GetMetadata(MetadataKeys.LogMessage) ?? "Unknown remote error";
        var errorKind = batch.GetMetadata(MetadataKeys.ErrorKind);
        var errorCode = batch.GetMetadata(MetadataKeys.ErrorCode);
        var rawDetails = batch.GetMetadata(MetadataKeys.ErrorDetails);
        IReadOnlyList<JsonElement> details = rawDetails is not null ? ErrorModel.Decode(rawDetails) : [];
        var requestId = batch.GetMetadata(MetadataKeys.RequestId) ?? "";
        var errorType = "RpcException";
        var message = summary;
        var traceback = "";

        if (batch.GetMetadata(MetadataKeys.LogExtra) is { } extraJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(extraJson);
                if (doc.RootElement.TryGetProperty("exception_type", out var type))
                {
                    errorType = type.GetString() ?? errorType;
                }

                if (doc.RootElement.TryGetProperty("exception_message", out var detail))
                {
                    message = detail.GetString() ?? summary;
                }

                if (doc.RootElement.TryGetProperty("traceback", out var remoteTraceback))
                {
                    traceback = remoteTraceback.ValueKind == JsonValueKind.String ? remoteTraceback.GetString() ?? "" : "";
                }

                // The log_extra mirror is the fallback for a server -- or an intermediary that
                // rebuilt the batch -- that set only one of the two.
                if (errorCode is null && doc.RootElement.TryGetProperty("error_code", out var mirroredCode)
                    && mirroredCode.ValueKind == JsonValueKind.String)
                {
                    errorCode = mirroredCode.GetString();
                }

                if (errorKind is null && doc.RootElement.TryGetProperty("error_kind", out var mirroredKind)
                    && mirroredKind.ValueKind == JsonValueKind.String)
                {
                    errorKind = mirroredKind.GetString();
                }

                if (rawDetails is null && doc.RootElement.TryGetProperty("error_details", out var mirroredDetails))
                {
                    details = ErrorModel.FromArray(mirroredDetails);
                }
            }
            catch (JsonException)
            {
                // The summary still provides a useful, safely bounded remote error.
            }
        }

        RpcException error = errorKind switch
        {
            MetadataKeys.ErrorKinds.MethodNotImplemented => new MethodNotImplementedException(message),
            MetadataKeys.ErrorKinds.ProtocolVersionMismatch => new ProtocolVersionException(message),
            MetadataKeys.ErrorKinds.SessionLost => new SessionLostException(message),
            MetadataKeys.ErrorKinds.ServerDraining => new ServerDrainingException(message),
            _ => errorType switch
            {
                "SessionLostError" => new SessionLostException(message),
                "ServerDrainingError" => new ServerDrainingException(message),
                _ => new RpcException(errorType, message, traceback, requestId, errorKind),
            },
        };

        // What the server sent, verbatim -- never the subclass's own default. "" (the server sent
        // no code) and "UNKNOWN" (it sent that) are different answers, and a subclass default
        // would also invent a RetryInfo the server never sent.
        return WithWireModel(error, errorCode ?? "", string.IsNullOrEmpty(errorKind) ? null : errorKind, details);
    }

    private static RpcException WithWireModel(
        RpcException error, string code, string? kind, IReadOnlyList<JsonElement> details) => error switch
        {
            MethodNotImplementedException e => new MethodNotImplementedException(e.ErrorMessage) { ErrorCode = code, ErrorKind = kind, ErrorDetails = details },
            ProtocolVersionException e => new ProtocolVersionException(e.ErrorMessage) { ErrorCode = code, ErrorKind = kind, ErrorDetails = details },
            SessionLostException e => new SessionLostException(e.ErrorMessage) { ErrorCode = code, ErrorKind = kind, ErrorDetails = details },
            ServerDrainingException e => new ServerDrainingException(e.ErrorMessage) { ErrorCode = code, ErrorKind = kind, ErrorDetails = details },
            _ => new RpcException(error.ErrorType, error.ErrorMessage, error.RemoteTraceback, error.RequestId, kind, code, details),
        };
}
