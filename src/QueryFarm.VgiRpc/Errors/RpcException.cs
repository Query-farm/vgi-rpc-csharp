namespace QueryFarm.VgiRpc.Errors;

/// <summary>
/// The exception a client raises for any error a server reports on the wire. Mirrors Python's
/// <c>RpcError</c> (named <c>RpcException</c> here per C# convention: exception types end in
/// "Exception", not "Error"). <see cref="ErrorKind"/> is an open string set, never a closed
/// enum — new kinds may appear on the wire that this port's version doesn't know about yet.
/// </summary>
public class RpcException : Exception
{
    public string ErrorType { get; }
    public string ErrorMessage { get; }
    public string RemoteTraceback { get; }
    public string RequestId { get; }
    public string? ErrorKind { get; }

    public RpcException(
        string errorType,
        string errorMessage,
        string remoteTraceback = "",
        string requestId = "",
        string? errorKind = null)
        : base($"{errorType}: {errorMessage}")
    {
        ErrorType = errorType;
        ErrorMessage = errorMessage;
        RemoteTraceback = remoteTraceback;
        RequestId = requestId;
        ErrorKind = errorKind;
    }
}

/// <summary>Base for version-mismatch errors (request/protocol version). Mirrors Python's <c>VersionError</c>.</summary>
public class VersionException : RpcException
{
    public VersionException(string errorType, string message, string? errorKind = null)
        : base(errorType, message, errorKind: errorKind)
    {
    }
}

/// <summary>The client and server declared incompatible <c>protocol_version</c> major.minor values.</summary>
public sealed class ProtocolVersionException : VersionException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.ProtocolVersionMismatch;

    public ProtocolVersionException(string message)
        : base(nameof(ProtocolVersionException), message, ErrorKindConst)
    {
    }
}

/// <summary>The server has no method registered under the requested name.</summary>
public sealed class MethodNotImplementedException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.MethodNotImplemented;

    public MethodNotImplementedException(string message)
        : base(nameof(MethodNotImplementedException), message, errorKind: ErrorKindConst)
    {
    }
}

/// <summary>A sticky-session call referenced a session that no longer exists (evicted/expired).</summary>
public sealed class SessionLostException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.SessionLost;

    public SessionLostException(string message)
        : base(nameof(SessionLostException), message, errorKind: ErrorKindConst)
    {
    }
}

/// <summary>The server is shutting down and is no longer accepting new sticky sessions/calls.</summary>
public sealed class ServerDrainingException : RpcException
{
    public const string ErrorKindConst = Wire.MetadataKeys.ErrorKinds.ServerDraining;

    public ServerDrainingException(string message)
        : base(nameof(ServerDrainingException), message, errorKind: ErrorKindConst)
    {
    }
}
