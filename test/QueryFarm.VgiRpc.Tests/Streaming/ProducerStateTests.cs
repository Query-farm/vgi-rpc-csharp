using Apache.Arrow;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Streaming;

/// <summary>
/// Coverage for <see cref="ProducerState"/>'s sealed <c>ProcessAsync</c> override, which copies
/// the incoming tick batch's metadata onto <see cref="OutputCollector.InputMetadata"/> before
/// calling <see cref="ProducerState.ProduceAsync"/> — the only way a producer-shaped
/// implementation can see an application-level protocol's own control data riding an otherwise
/// empty tick batch (e.g. VGI's dynamic Top-N filter tightening).
/// </summary>
public sealed class ProducerStateTests
{
    private static readonly Schema s_emptySchema = new([], metadata: null);

    private sealed class RecordingProducerState : ProducerState
    {
        public IReadOnlyDictionary<string, string>? SeenInputMetadata { get; private set; }

        public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            SeenInputMetadata = output.InputMetadata;
            output.Finish();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProcessAsync_WithTickMetadata_ExposesItViaInputMetadata()
    {
        var state = new RecordingProducerState();
        var tickMetadata = new Dictionary<string, string> { ["vgi.dynamic_filter.top_n"] = "5" };
        var input = new AnnotatedBatch(new RecordBatch(s_emptySchema, [], 1), tickMetadata);
        var output = new OutputCollector(s_emptySchema);

        await state.ProcessAsync(input, output, ctx: null, CancellationToken.None);

        Assert.Same(tickMetadata, state.SeenInputMetadata);
        Assert.Same(tickMetadata, output.InputMetadata);
    }

    [Fact]
    public async Task ProcessAsync_WithNoTickMetadata_LeavesInputMetadataNull()
    {
        var state = new RecordingProducerState();
        var input = new AnnotatedBatch(new RecordBatch(s_emptySchema, [], 1), Metadata: null);
        var output = new OutputCollector(s_emptySchema);

        await state.ProcessAsync(input, output, ctx: null, CancellationToken.None);

        Assert.Null(state.SeenInputMetadata);
    }
}
