using QueryFarm.VgiRpc.Conformance.Types;

namespace QueryFarm.VgiRpc.ConformanceWorker;

/// <summary>
/// Builds a <see cref="RichHeader"/> deterministically from a seed. This is a reference
/// specification (mirrors Python's <c>build_rich_header</c> field-for-field, value-for-value) —
/// every language port must produce identical field values for the same seed.
/// </summary>
public static class RichHeaderBuilder
{
    private static readonly Status[] s_statusCycle = [Status.Pending, Status.Active, Status.Closed];

    public static RichHeader Build(long seed) => new()
    {
        StrField = $"seed-{seed}",
        BytesField = [(byte)(seed % 256), (byte)((seed + 1) % 256), (byte)((seed + 2) % 256)],
        IntField = seed * 7,
        FloatField = seed * 1.5,
        BoolField = seed % 2 == 0,
        ListOfInt = [seed, seed + 1, seed + 2],
        ListOfStr = [$"item-{seed}", $"item-{seed + 1}"],
        DictField = new Dictionary<string, long> { ["a"] = seed, ["b"] = seed + 1 },
        EnumField = s_statusCycle[(int)(seed % 3)],
        NestedPoint = new Point { X = seed, Y = seed * 2 },
        OptionalStr = seed % 2 == 0 ? $"opt-{seed}" : null,
        OptionalInt = seed % 2 == 1 ? seed * 3 : null,
        OptionalNested = seed % 3 == 0 ? new Point { X = seed, Y = 0.0 } : null,
        ListOfNested = [new Point { X = seed, Y = seed + 1 }],
        NestedList = [[seed, seed + 1], [seed + 2]],
        AnnotatedInt32 = (int)(seed % 1000),
        AnnotatedFloat32 = seed / 3.0f,
        DictStrStr = new Dictionary<string, string> { ["key"] = $"val-{seed}" },
    };
}
