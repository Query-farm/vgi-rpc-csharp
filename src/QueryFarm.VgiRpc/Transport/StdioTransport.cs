namespace QueryFarm.VgiRpc.Transport;

/// <summary>
/// Speaks the wire protocol over a subprocess's own stdin/stdout — the default transport a
/// worker (see the conformance worker) serves on, and what <c>SubprocessTransport</c> (a later
/// milestone) will dial from the parent-process side. See docs/roadmap.md M4.
/// </summary>
public sealed class StdioTransport : IRpcTransport
{
    public Stream Input { get; }
    public Stream Output { get; }

    public StdioTransport()
    {
        Input = Console.OpenStandardInput();
        Output = Console.OpenStandardOutput();
    }
}
