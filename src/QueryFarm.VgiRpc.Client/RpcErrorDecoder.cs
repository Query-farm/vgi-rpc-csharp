using System.Text.Json;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Wire;

namespace QueryFarm.VgiRpc.Client;

public static class RpcErrorDecoder
{
    public static RpcException Decode(AnnotatedBatch batch)
    {
        var summary = batch.GetMetadata(MetadataKeys.LogMessage) ?? "Unknown remote error";
        var errorKind = batch.GetMetadata(MetadataKeys.ErrorKind);
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
                    traceback = remoteTraceback.GetString() ?? "";
                }
            }
            catch (JsonException)
            {
                // The summary still provides a useful, safely bounded remote error.
            }
        }

        return errorKind switch
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
    }
}
