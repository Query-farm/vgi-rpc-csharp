using Apache.Arrow;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Wire;

public sealed class WireWriterFlushTests
{
    [Fact]
    public async Task EndingLogicalStreamFlushesUnderlyingTransport()
    {
        var stream = new FlushCountingStream();
        await using (var writer = new WireWriter(stream, new Schema([], metadata: null)))
        {
            await writer.WriteStartAsync();
        }

        Assert.True(stream.FlushCount > 0);
    }

    [Fact]
    public async Task StartingLongLivedStreamFlushesSchema()
    {
        var stream = new FlushCountingStream();
        await using var writer = new WireWriter(stream, new Schema([], metadata: null));

        await writer.WriteStartAsync();

        Assert.True(stream.FlushCount > 0);
    }


    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }
}
