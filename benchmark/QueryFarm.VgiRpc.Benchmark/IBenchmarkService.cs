namespace QueryFarm.VgiRpc.Benchmark;

/// <summary>
/// Minimal service surface for M17's perf pass (docs/roadmap.md) — just enough to measure real
/// unary-call throughput/latency over this port's own dispatch path, not a general benchmarking
/// framework. A single small-string echo isolates dispatch/wire overhead from
/// serialization-heavy payload costs, which is the number that matters most for a typical RPC
/// workload (many small calls, not a few huge ones — large_payload has its own conformance
/// coverage instead).
/// </summary>
public interface IBenchmarkService
{
    Task<string> EchoStringAsync(string value);
}

public sealed class BenchmarkServiceImpl : IBenchmarkService
{
    public Task<string> EchoStringAsync(string value) => Task.FromResult(value);
}
