namespace QueryFarm.VgiRpc.Transport;

/// <summary>
/// A duplex byte channel an <see cref="Server.RpcServer"/>/<see cref="Client.RpcConnection{T}"/>
/// speaks the wire protocol over. Modeled as separate read/write streams (rather than one
/// bidirectional <see cref="Stream"/>) because that's the natural shape of every concrete
/// transport this abstraction needs to cover: subprocess stdio, a Unix domain socket wrapped in
/// a <see cref="System.Net.Sockets.NetworkStream"/> (which IS one bidirectional stream — assign
/// it to both), or an in-memory pipe pair for testing (two genuinely separate streams).
/// HTTP, SHM, and other future transports plug in here without changing the RPC engine.
/// </summary>
public interface IRpcTransport
{
    /// <summary>The stream this side of the connection reads requests/responses from.</summary>
    Stream Input { get; }

    /// <summary>The stream this side of the connection writes requests/responses to.</summary>
    Stream Output { get; }
}
