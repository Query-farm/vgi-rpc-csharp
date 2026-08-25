// HTTP server example using ASP.NET Core / Kestrel.
//
// Start the server:
//
//     dotnet run --project examples/04-http/Server
//
// Then run the client in another terminal:
//
//     dotnet run --project examples/04-http/Client

using Microsoft.AspNetCore.Builder;
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.Server;

const int Port = 8234;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var server = new RpcServer(typeof(IDemoService), new DemoServiceImpl());

// compressionLevel: null disables response compression — this example's
// client reads the wire format directly, and skipping negotiation keeps
// that code focused on the RPC framing itself. See docs/wire-protocol.md
// for the Content-Encoding negotiation compression is normally part of.
app.MapVgiRpc(server, compressionLevel: null);

Console.WriteLine($"Serving DemoService on http://127.0.0.1:{Port}");
app.Run($"http://127.0.0.1:{Port}");

public interface IDemoService
{
    Task<string> EchoAsync(string message);
}

public sealed class DemoServiceImpl : IDemoService
{
    public Task<string> EchoAsync(string message) => Task.FromResult(message);
}
