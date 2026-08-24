using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>A producer whose per-call OUTPUT SCHEMA varies based on boolean flags — exercises
/// that output_schema is a per-call value, not fixed per method. Mirrors <c>DynamicProducerState</c>.</summary>
public sealed class DynamicProducerState(Schema schema, long count, bool includeStrings, bool includeFloats) : ProducerState
{
    private long _current;

    public static Schema BuildSchema(bool includeStrings, bool includeFloats)
    {
        // Matches Python's build_dynamic_schema — pa.schema() from plain field type
        // constructors defaults every field to nullable=True (Python's out.emit_pydict-inferred
        // schema follows the same default), so these must be nullable too for
        // ab.batch.schema.equals(expected_schema) to hold on the client side.
        var fields = new List<Field> { new("index", Int64Type.Default, nullable: true) };
        if (includeStrings)
        {
            fields.Add(new Field("label", StringType.Default, nullable: true));
        }

        if (includeFloats)
        {
            fields.Add(new Field("score", DoubleType.Default, nullable: true));
        }

        return new Schema(fields, metadata: null);
    }

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (_current >= count)
        {
            output.Finish();
            return Task.CompletedTask;
        }

        var values = new List<object?> { _current };
        if (includeStrings)
        {
            values.Add($"row-{_current}");
        }

        if (includeFloats)
        {
            values.Add(_current * 1.5);
        }

        output.Emit(ValueCodec.BuildRow(schema, values));
        _current++;
        return Task.CompletedTask;
    }
}
