using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Reflection;

/// <summary>An embedded record from a sender whose schema predates a trailing field: a nullable
/// one decodes as absent (as the reference's dataclass default does), a required one is a clear
/// error rather than an index fault.</summary>
public class EmbeddedRecordEvolutionTests
{
    public sealed class Grown
    {
        public string Name { get; set; } = "";

        public long? Added { get; set; }
    }

    public sealed class GrownRequired
    {
        public string Name { get; set; } = "";

        public long Added { get; set; }
    }

    private static RecordBatch Carrying(byte[] inner)
    {
        var column = new BinaryArray.Builder().Append(inner.AsSpan()).Build();
        return new RecordBatch(new Schema([new Field("record", BinaryType.Default, false)], null), [column], 1);
    }

    private static byte[] OnlyName(string name)
    {
        var batch = new RecordBatch(
            new Schema([new Field("name", StringType.Default, false)], null),
            [new StringArray.Builder().Append(name).Build()], 1);
        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, batch.Schema))
        {
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }

        return stream.ToArray();
    }

    [Fact]
    public void AnAbsentTrailingNullableFieldDecodesAsNull()
    {
        var record = Assert.IsType<Grown>(ValueCodec.ExtractRow(Carrying(OnlyName("x")), [typeof(Grown)])[0]);
        Assert.Equal("x", record.Name);
        Assert.Null(record.Added);
    }

    [Fact]
    public void AnAbsentRequiredFieldIsAClearError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ValueCodec.ExtractRow(Carrying(OnlyName("x")), [typeof(GrownRequired)]));
        Assert.Contains("lacks the required field 'added'", error.Message, StringComparison.Ordinal);
    }
}
