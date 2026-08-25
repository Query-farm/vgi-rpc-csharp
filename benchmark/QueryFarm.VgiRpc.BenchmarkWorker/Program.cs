// Benchmark worker entry point (M17 perf pass, docs/roadmap.md). Deliberately small: a real,
// measured unary-call throughput/latency number over this port's own in-process pipe dispatch
// path, not a BenchmarkDotNet-based micro-benchmark suite — the goal is "does dispatch have an
// obvious, embarrassing per-call cost" (reflection invoke, schema lookup, Arrow array
// construction), not statistically rigorous microsecond-level comparisons. Run directly:
// `dotnet run -c Release --project benchmark/QueryFarm.VgiRpc.BenchmarkWorker`.

using QueryFarm.VgiRpc.Benchmark;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

const int WarmupIterations = 2_000;
const int MeasuredIterations = 50_000;

var (clientTransport, serverTransport) = PipeTransport.CreatePair();
var server = new RpcServer(typeof(IBenchmarkService), new BenchmarkServiceImpl());
var connection = new RpcConnection<IBenchmarkService>(clientTransport);
var client = connection.CreateProxy();

var serveTask = server.ServeAsync(serverTransport);

for (var i = 0; i < WarmupIterations; i++)
{
    await client.EchoStringAsync("warmup");
}

var latencies = new double[MeasuredIterations];
var overallStart = System.Diagnostics.Stopwatch.GetTimestamp();
for (var i = 0; i < MeasuredIterations; i++)
{
    var callStart = System.Diagnostics.Stopwatch.GetTimestamp();
    await client.EchoStringAsync("the quick brown fox");
    latencies[i] = System.Diagnostics.Stopwatch.GetElapsedTime(callStart).TotalMicroseconds;
}

var overallElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(overallStart);

Array.Sort(latencies);
var p50 = latencies[(int)(MeasuredIterations * 0.50)];
var p99 = latencies[(int)(MeasuredIterations * 0.99)];
var opsPerSecond = MeasuredIterations / overallElapsed.TotalSeconds;

Console.WriteLine($"unary_echo_string: {MeasuredIterations} calls in {overallElapsed.TotalSeconds:F3}s = {opsPerSecond:N0} ops/sec");
Console.WriteLine($"  p50 = {p50:F1} us, p99 = {p99:F1} us, min = {latencies[0]:F1} us, max = {latencies[^1]:F1} us");

clientTransport.Output.Close();
await serveTask;
return 0;
