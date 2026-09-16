using QueryFarm.VgiRpc.Client;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Client;

public sealed class WorkerPoolTests
{
    /// <summary>
    /// A client-side <em>view</em> of the worker's <c>ICalculator</c>: one of its three methods,
    /// with the cancellation token and <see cref="ValueTask{TResult}"/> return the caller wants.
    /// </summary>
    /// <remarks>
    /// Named for what it is — a client — which means its name is <em>not</em> the protocol's.
    /// The worker hosts <c>Calculator</c>; deriving a routing key from this type would address
    /// <c>CalculatorClient</c>, which no server hosts. A view names the protocol it views, and
    /// that is what <see cref="WorkerClientOptions"/> below does.
    /// </remarks>
    public interface ICalculatorClient
    {
        ValueTask<double> AddAsync(double a, double b, CancellationToken cancellationToken = default);
    }

    /// <summary>The protocol the example worker hosts — <c>examples/03-subprocess/Worker</c>
    /// serves <c>ICalculator</c>, so the wire name is <c>Calculator</c>.</summary>
    private static RpcClientOptions WorkerClientOptions => new() { Protocol = "Calculator" };

    [Fact]
    public async Task Borrow_ReturnsHealthyWorkerAndReusesIt()
    {
        var worker = FindWorkerDll();
        await using var pool = new WorkerPool(new WorkerPoolOptions { MaxIdle = 1 });

        await using (var lease = await pool.BorrowAsync(["dotnet", worker], WorkerClientOptions))
        {
            var calculator = lease.CreateProxy<ICalculatorClient>();
            Assert.Equal(5, await calculator.AddAsync(2, 3, TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, pool.Metrics.Idle);
        await using (var lease = await pool.BorrowAsync(["dotnet", worker], WorkerClientOptions))
        {
            var calculator = lease.CreateProxy<ICalculatorClient>();
            Assert.Equal(9, await calculator.AddAsync(4, 5, TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, pool.Metrics.Spawns);
        Assert.Equal(1, pool.Metrics.Reuses);
        Assert.Equal(2, pool.Metrics.Returns);
    }

    private static string FindWorkerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "vgi-rpc-csharp.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var workerBin = Path.Combine(directory!.FullName, "examples", "03-subprocess", "Worker", "bin", "Release", "net10.0", "Worker.dll");
        Assert.True(File.Exists(workerBin), $"Worker was not built at '{workerBin}'.");
        return workerBin;
    }
}
