using System.Diagnostics;
using QueryFarm.VgiRpc.Transport;

namespace QueryFarm.VgiRpc.Client;

public enum SubprocessStderrMode
{
    Inherit,
    Pipe,
    Discard,
}

/// <summary>A subprocess stdin/stdout transport with deterministic child-process cleanup.</summary>
public sealed class SubprocessTransport : IRpcTransport, IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task? _stderrPump;
    private readonly TimeSpan _shutdownTimeout;
    private bool _disposed;

    public SubprocessTransport(
        IReadOnlyList<string> command,
        SubprocessStderrMode stderr = SubprocessStderrMode.Inherit,
        Action<string>? onStderr = null,
        TimeSpan? shutdownTimeout = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0 || string.IsNullOrWhiteSpace(command[0]))
        {
            throw new ArgumentException("A subprocess command must contain an executable.", nameof(command));
        }

        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(10);
        if (_shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = stderr is not SubprocessStderrMode.Inherit,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        for (var index = 1; index < command.Count; index++)
        {
            startInfo.ArgumentList.Add(command[index]);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{command[0]}'.");
        Input = _process.StandardOutput.BaseStream;
        Output = _process.StandardInput.BaseStream;

        if (stderr is SubprocessStderrMode.Pipe)
        {
            _stderrPump = PumpStderrAsync(_process.StandardError, onStderr ?? Console.Error.WriteLine);
        }
        else if (stderr is SubprocessStderrMode.Discard)
        {
            _stderrPump = _process.StandardError.ReadToEndAsync();
        }
    }

    public Process Process => _process;

    public Stream Input { get; }

    public Stream Output { get; }

    public bool HasExited => _process.HasExited;

    private static async Task PumpStderrAsync(StreamReader reader, Action<string> callback)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0)
            {
                callback(line);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await Output.DisposeAsync().ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(_shutdownTimeout);
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                await _process.WaitForExitAsync().ConfigureAwait(false);
            }

            if (_stderrPump is not null)
            {
                await _stderrPump.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
            }
        }
        finally
        {
            await Input.DisposeAsync().ConfigureAwait(false);
            _process.Dispose();
        }
    }
}
