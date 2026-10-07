namespace QueryFarm.VgiRpc.AccessLog;

/// <summary>Receives one <see cref="AccessLogRecord"/> per completed RPC call.</summary>
/// <remarks>
/// A record never carries a request or response value, or serialized stream state, at any
/// level -- see <see cref="RequestShape"/>. There is deliberately no sink option that turns
/// payload capture back on.
/// </remarks>
public interface IAccessLogSink
{
    void Write(AccessLogRecord record);
}
