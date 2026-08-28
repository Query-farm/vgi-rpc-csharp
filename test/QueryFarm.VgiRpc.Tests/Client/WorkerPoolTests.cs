using QueryFarm.VgiRpc.Client;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Client;

public sealed class WorkerPoolTests
{
    public interface ICalculatorClient
    {
        ValueTask<double> AddAsync(double a, double b, CancellationToken cancellationToken = default);
    }

    [Fact]
    public async Task Borrow_ReturnsHealthyWorkerAndReusesIt()
    {
        var worker = FindWorkerDll();
        await using var pool = new WorkerPool(new WorkerPoolOptions { MaxIdle = 1 });

        await using (var lease = await pool.BorrowAsync(["dotnet", worker]))
        {
            var calculator = lease.CreateProxy<ICalculatorClient>();
            Assert.Equal(5, await calculator.AddAsync(2, 3, TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, pool.Metrics.Idle);
        await using (var lease = await pool.BorrowAsync(["dotnet", worker]))
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
