using Apache.Arrow;
using QueryFarm.VgiRpc.Streaming;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Streaming;

/// <summary>
/// Coverage for <see cref="OutputCollector"/>'s metadata-carrying <c>Emit</c> overload — added so
/// an application-level protocol built on this framework (e.g. VGI's per-batch cache-control keys)
/// can attach its own <c>custom_metadata</c> to a stream turn's emitted batch, the same way the
/// framework's own SHM-pointer-batch mechanism already does internally. See
/// <see cref="RpcServer"/>'s stream-turn write path, which merges this with any SHM metadata
/// rather than overwriting it.
/// </summary>
public sealed class OutputCollectorTests
{
    private static readonly Schema s_emptySchema = new([], metadata: null);

    private static RecordBatch EmptyBatch() => new(s_emptySchema, [], 1);

    [Fact]
    public void Emit_WithoutMetadata_LeavesEmittedMetadataNull()
    {
        var collector = new OutputCollector(s_emptySchema);

        collector.Emit(EmptyBatch());

        Assert.NotNull(collector.EmittedBatch);
        Assert.Null(collector.EmittedMetadata);
    }

    [Fact]
    public void Emit_WithMetadata_SetsEmittedMetadata()
    {
        var collector = new OutputCollector(s_emptySchema);
        var metadata = new Dictionary<string, string> { ["vgi.cache.per_value"] = "true", ["vgi.cache.ttl"] = "60" };

        collector.Emit(EmptyBatch(), metadata);

        Assert.NotNull(collector.EmittedBatch);
        Assert.Same(metadata, collector.EmittedMetadata);
    }

    [Fact]
    public void Emit_CalledTwice_ThrowsRegardlessOfWhichOverload()
    {
        var collector = new OutputCollector(s_emptySchema);
        collector.Emit(EmptyBatch());

        Assert.Throws<InvalidOperationException>(() => collector.Emit(EmptyBatch(), new Dictionary<string, string>()));
    }

    [Fact]
    public void TakeEmittedBatch_TransfersOwnership_DisposeLeavesBatchAlive()
    {
        var schema = new Schema([new Field("v", Apache.Arrow.Types.Int64Type.Default, nullable: true)], metadata: null);
        var batch = new RecordBatch(schema, [new Int64Array.Builder().Append(42).Build()], 1);
        RecordBatch? taken;
        using (var collector = new OutputCollector(schema))
        {
            collector.Emit(batch);
            taken = collector.TakeEmittedBatch();
            Assert.Null(collector.EmittedBatch);
            Assert.Null(collector.TakeEmittedBatch());
        }

        Assert.Same(batch, taken);
        // The collector's Dispose must not have released the taken batch's buffers.
        Assert.Equal(42, ((Int64Array)taken!.Column(0)).GetValue(0));
        taken.Dispose();
    }

    [Fact]
    public void TakeEmittedBatch_NothingEmitted_ReturnsNull()
    {
        using var collector = new OutputCollector(s_emptySchema);

        Assert.Null(collector.TakeEmittedBatch());
    }
}
