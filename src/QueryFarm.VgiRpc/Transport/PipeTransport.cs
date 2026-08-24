using System.IO.Pipelines;

namespace QueryFarm.VgiRpc.Transport;

/// <summary>
/// An in-process, in-memory transport — the C# analog of Python's <c>serve_pipe</c>/
/// <c>make_pipe_pair</c>. Used for unit/integration tests and for embedding a server and client
/// in the same process without any real I/O. Backed by <see cref="System.IO.Pipelines.Pipe"/>,
/// which gives real, correctly-blocking async <see cref="Stream"/> semantics for free.
/// </summary>
public sealed class PipeTransport(Stream input, Stream output) : IRpcTransport
{
    public Stream Input { get; } = input;
    public Stream Output { get; } = output;

    /// <summary>Creates a connected client/server pair sharing two independent in-memory pipes (one per direction).</summary>
    public static (IRpcTransport Client, IRpcTransport Server) CreatePair()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var client = new PipeTransport(input: serverToClient.Reader.AsStream(), output: clientToServer.Writer.AsStream());
        var server = new PipeTransport(input: clientToServer.Reader.AsStream(), output: serverToClient.Writer.AsStream());
        return (client, server);
    }
}
