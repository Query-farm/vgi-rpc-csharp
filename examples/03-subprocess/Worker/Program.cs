// Subprocess server entry point.
//
// This program serves an RPC service over stdin/stdout, designed to be
// spawned as a child process by ../Client (which talks to it via a small
// hand-rolled IRpcTransport wrapping System.Diagnostics.Process — this repo
// doesn't ship a client-side subprocess-transport helper yet, see the
// README for details).
//
// IMPORTANT: stdout is the wire channel. Never Console.WriteLine here —
// use Console.Error for any diagnostic output.
//
// Run the client instead (it spawns this automatically):
//
//     dotnet run --project examples/03-subprocess/Client -- <path to Worker.dll>

using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

var server = new RpcServer(typeof(ICalculator), new CalculatorImpl());
await server.ServeAsync(new StdioTransport());

// This Protocol is duplicated from Client/Program.cs so each side is
// self-contained. In a real project you'd define the interface once in a
// shared project and reference it from both.
public interface ICalculator
{
    Task<double> AddAsync(double a, double b);

    Task<double> MultiplyAsync(double a, double b);

    Task<double> DivideAsync(double a, double b);
}

public sealed class CalculatorImpl : ICalculator
{
    public Task<double> AddAsync(double a, double b) => Task.FromResult(a + b);

    public Task<double> MultiplyAsync(double a, double b) => Task.FromResult(a * b);

    public Task<double> DivideAsync(double a, double b)
    {
        if (b == 0.0)
        {
            throw new InvalidOperationException("Division by zero");
        }

        return Task.FromResult(a / b);
    }
}
