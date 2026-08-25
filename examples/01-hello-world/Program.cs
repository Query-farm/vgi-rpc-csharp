// Minimal vgi-rpc example: define a service and call it in-process.
//
// This is the quickest way to get started. The service runs in the same
// process and communicates over an in-process pipe (System.IO.Pipelines)
// — no subprocess or network needed.
//
// Run:
//
//     dotnet run --project examples/01-hello-world

using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

// 3. Wire up an in-process transport, start the server, and call methods
//    through a typed client proxy.
var (clientTransport, serverTransport) = PipeTransport.CreatePair();

var server = new RpcServer(typeof(IGreeter), new Greeter());
var serveTask = server.ServeAsync(serverTransport);

var connection = new RpcConnection<IGreeter>(clientTransport);
IGreeter client = connection.CreateProxy();

Console.WriteLine(await client.GreetAsync("World")); // Hello, World!
Console.WriteLine(await client.AddAsync(2.5, 3.5)); // 6

// Closing the client's write side signals end-of-stream so the server's
// ServeAsync loop exits cleanly.
clientTransport.Output.Close();
await serveTask;

// 1. Define the service interface. Methods must return Task/Task<T> — the
//    idiomatic async C# shape vgi-rpc's client proxy requires. The wire
//    method name is derived from the C# name with the "Async" suffix
//    stripped and converted to snake_case (GreetAsync -> "greet"); override
//    with [RpcName("...")] when you need something else.
public interface IGreeter
{
    Task<string> GreetAsync(string name);

    Task<double> AddAsync(double a, double b);
}

// 2. Implement the interface.
public sealed class Greeter : IGreeter
{
    public Task<string> GreetAsync(string name) => Task.FromResult($"Hello, {name}!");

    public Task<double> AddAsync(double a, double b) => Task.FromResult(a + b);
}
