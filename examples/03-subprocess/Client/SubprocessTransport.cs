using System.Diagnostics;
using QueryFarm.VgiRpc.Transport;

namespace QueryFarm.VgiRpc.Examples.Subprocess.Client;

/// <summary>
/// Spawns a child process and speaks the vgi-rpc wire protocol over its
/// redirected stdin/stdout. <see cref="IRpcTransport"/> is just
/// <c>{ Stream Input; Stream Output; }</c>, so any duplex byte channel can
/// implement it — this repo doesn't ship a client-side subprocess-transport
/// helper yet (see the root README's Transports section), so this is the
/// minimal implementation getting-started code needs.
/// </summary>
public sealed class SubprocessTransport : IRpcTransport, IDisposable
{
    private readonly Process _process;

    public Stream Input { get; }
    public Stream Output { get; }

    public SubprocessTransport(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        Input = _process.StandardOutput.BaseStream; // the child's stdout is our input
        Output = _process.StandardInput.BaseStream; // our output is the child's stdin
    }

    public void Dispose()
    {
        Output.Close();
        _process.WaitForExit();
        _process.Dispose();
    }
}
