using Apache.Arrow;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

public sealed class StreamCallRegistryTests
{
    [Fact]
    public void CrossIdentityMissDoesNotEvictVictimsStream()
    {
        var registry = new StreamCallRegistry();
        var stream = new RpcStream<NoopProducer>(new Schema([], null), new NoopProducer());
        var key = registry.Register(stream, "domain\0alice\0binding-a");

        Assert.False(registry.TryGet(key, "domain\0mallory\0binding-b", out _));
        Assert.True(registry.TryGet(key, "domain\0alice\0binding-a", out var recovered));
        Assert.Same(stream, recovered);
    }

    private sealed class NoopProducer : ProducerState
    {
        public override Task ProduceAsync(
            OutputCollector output,
            ICallContext? ctx,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
