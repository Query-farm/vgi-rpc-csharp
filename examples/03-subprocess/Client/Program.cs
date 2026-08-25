// Client that spawns a subprocess server and calls methods on it.
//
// Uses the small SubprocessTransport in this project (see
// SubprocessTransport.cs) to launch the worker as a child process and
// communicate over its stdin/stdout pipes.
//
// Build the worker first, then run this:
//
//     dotnet build examples/03-subprocess/Worker
//     dotnet run --project examples/03-subprocess/Client

using System.Runtime.CompilerServices;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Examples.Subprocess.Client;

var workerDll = args.Length > 0 ? args[0] : FindWorkerDll();

using var transport = new SubprocessTransport("dotnet", workerDll);
var connection = new RpcConnection<ICalculator>(transport);
var calc = connection.CreateProxy();

Console.WriteLine($"add(2, 3)      = {await calc.AddAsync(2, 3)}");
Console.WriteLine($"multiply(4, 5) = {await calc.MultiplyAsync(4, 5)}");
Console.WriteLine($"divide(10, 3)  = {await calc.DivideAsync(10, 3):F4}");

// Server-side exceptions are propagated as RpcException.
try
{
    await calc.DivideAsync(1, 0);
}
catch (RpcException e)
{
    Console.WriteLine();
    Console.WriteLine($"Caught remote error: {e.ErrorType}: {e.ErrorMessage}");
}

// Locates Worker.dll under ../Worker/bin, most-recently-built first, so
// `dotnet run --project examples/03-subprocess/Client` works with no
// arguments regardless of Debug/Release configuration — as long as the
// worker has been built at least once.
static string FindWorkerDll([CallerFilePath] string here = "")
{
    var subprocessExampleDir = Path.GetDirectoryName(Path.GetDirectoryName(here))!; // .../03-subprocess
    var workerBinDir = Path.Combine(subprocessExampleDir, "Worker", "bin");
    var candidates = Directory.Exists(workerBinDir)
        ? Directory.GetFiles(workerBinDir, "Worker.dll", SearchOption.AllDirectories)
        : [];

    if (candidates.Length == 0)
    {
        throw new FileNotFoundException(
            $"Worker.dll not found under '{workerBinDir}'. Build it first: dotnet build examples/03-subprocess/Worker " +
            "(or pass its path explicitly: dotnet run --project examples/03-subprocess/Client -- <path to Worker.dll>).");
    }

    return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
}

// This interface is duplicated from Worker/Program.cs so each side is
// self-contained. In a real project you'd define it once in a shared
// project and reference it from both.
public interface ICalculator
{
    Task<double> AddAsync(double a, double b);

    Task<double> MultiplyAsync(double a, double b);

    Task<double> DivideAsync(double a, double b);
}
