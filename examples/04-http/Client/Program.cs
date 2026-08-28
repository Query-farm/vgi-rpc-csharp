// Start the server first:
//
//     dotnet run --project examples/04-http/Server
//
// Then run this client:
//
//     dotnet run --project examples/04-http/Client

using QueryFarm.VgiRpc.Client.Http;

const int Port = 8234;

await using var rpc = new HttpRpcClient(new Uri($"http://127.0.0.1:{Port}"));
var client = rpc.CreateProxy<IDemoService>();

Console.WriteLine($"echo: {await client.EchoAsync("Hello from HTTP!")}");

// In a real application this contract normally lives in a shared project referenced by both
// client and server. It is repeated here only to keep each example independently runnable.
public interface IDemoService
{
    Task<string> EchoAsync(string message);
}
