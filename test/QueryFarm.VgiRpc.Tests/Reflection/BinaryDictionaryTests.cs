using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using Xunit;
namespace QueryFarm.VgiRpc.Tests.Reflection;
public sealed class BinaryDictionaryTests
{
    public sealed class Record { [DictionaryEncoded] public byte[] Value { get; set; } = []; [DictionaryEncoded] public byte[]? Optional { get; set; } }
    [Fact]
    public void AValidDictionaryIndexReferencingNullDecodesNull()
    {
        var type = new DictionaryType(Int32Type.Default, BinaryType.Default, false);
        var schema = new Schema([new Field("value", type, true)], null);
        using var row = new RecordBatch(schema, [new DictionaryArray(type, new Int32Array.Builder().Append(0).Build(), new BinaryArray.Builder().AppendNull().Build())], 1);
        Assert.Null(ValueCodec.ExtractRow(row, [typeof(byte[])])[0]);
    }

    [Fact]
    public void AnnotationPreservesBytesAndNullsInSingletonAndEmptyRows()
    {
        var schema = SchemaDerivation.InnerSchemaFor(typeof(Record));
        var type = Assert.IsType<DictionaryType>(schema.GetFieldByIndex(0).DataType);
        Assert.IsType<Int32Type>(type.IndexType); Assert.IsType<BinaryType>(type.ValueType);
        foreach (byte[]? optional in new byte[]?[] { null, [], [0xff, 0, 0x80] })
        {
            using var row = ValueCodec.BuildRow(schema, [new byte[] { 0xff, 0, 0x80 }, optional]);
            var back = ValueCodec.ExtractRow(row, [typeof(byte[]), typeof(byte[])]);
            Assert.Equal(new byte[] { 0xff, 0, 0x80 }, Assert.IsType<byte[]>(back[0]));
            if (optional is null) Assert.Null(back[1]); else Assert.Equal(optional, Assert.IsType<byte[]>(back[1]));
        }
        using var empty = ValueCodec.EmptyRow(schema);
        Assert.Equal(0, empty.Length);
    }
}
