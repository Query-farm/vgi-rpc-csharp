using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Reflection;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Wire;

/// <summary>
/// The vendored writer must keep the batch it is writing reachable until the batch's buffer bodies
/// have been copied (third_party/apache-arrow-dotnet/README.md, "Seventh patch").
///
/// A builder-made <see cref="ArrowBuffer"/> is owned by a <c>SharedMemoryHandle</c> whose finalizer
/// frees the native memory, while the <c>ReadOnlyMemory&lt;byte&gt;</c> the writer records for each
/// buffer references only the memory manager inside it. <c>WriteRecordBatch</c> used to stop
/// referencing the batch before copying the bodies, so a batch nobody else referenced — one built
/// only to be written — could be finalized mid-write: the copy then read a zeroed pointer
/// (<see cref="NullReferenceException"/> in <c>WriteBufferData</c>), or pooled memory another
/// allocation had already taken.
///
/// Only optimized code reports a variable dead before the end of its method, so this project
/// disables quick JIT (see its csproj); under default tiering these would pass until the writer
/// happened to be promoted. They are meaningful in a Release build, which is what CI tests.
/// </summary>
public class ArrowStreamWriterKeepAliveTests
{
    /// <summary>Collects garbage and drains the finalizer queue before every write, so anything
    /// unreachable while a batch is being written is finalized in that window — deterministically.
    /// </summary>
    private sealed class CollectBeforeEveryWriteStream(bool yieldOnAsyncWrites = false) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Collect();
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Collect();
            base.Write(buffer, offset, count);
        }

        public override void WriteByte(byte value)
        {
            Collect();
            base.WriteByte(value);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (yieldOnAsyncWrites)
            {
                // Suspend for real, as a socket write would, so the writer's async state machine
                // is boxed rather than completing inline on the caller's stack.
                await Task.Yield();
            }

            Collect();
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static readonly Schema s_batchSchema = new(
        [
            new Field("id", Int64Type.Default, nullable: true),
            new Field("name", StringType.Default, nullable: true),
            new Field("values", new ListType(new Field("item", Int64Type.Default, nullable: true)), nullable: true),
        ],
        metadata: null);

    private static readonly DictionaryType s_colorType = new(Int32Type.Default, StringType.Default, ordered: false);

    private static readonly Schema s_dictionarySchema = new([new Field("color", s_colorType, nullable: true)], metadata: null);

    /// <summary>A batch whose only reference is the one handed back to the caller.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RecordBatch TemporaryBatch()
    {
        const int rows = 64;
        var ids = new Int64Array.Builder();
        var names = new StringArray.Builder();
        var values = new ListArray.Builder(Int64Type.Default);
        var items = (Int64Array.Builder)values.ValueBuilder;
        for (var row = 0; row < rows; row++)
        {
            ids.Append(row);
            names.Append($"row_{row}");
            values.Append();
            for (var item = 0; item <= row % 5; item++)
            {
                items.Append(row * 10 + item);
            }
        }

        return new RecordBatch(s_batchSchema, [ids.Build(), names.Build(), values.Build()], rows);
    }

    private static readonly Schema s_wideSchema = new(
        [.. Enumerable.Range(0, 24).Select(i => (i % 3) switch
        {
            0 => new Field($"i{i}", Int64Type.Default, nullable: true),
            1 => new Field($"s{i}", StringType.Default, nullable: true),
            _ => new Field($"l{i}", new ListType(new Field("item", Int64Type.Default, nullable: true)), nullable: true),
        })],
        metadata: null);

    /// <summary>Like <see cref="TemporaryBatch"/>, but with enough columns (~70 buffers) that the
    /// window a concurrent collection has to land in is a real share of each write.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RecordBatch TemporaryWideBatch()
    {
        const int rows = 16;
        var columns = new IArrowArray[s_wideSchema.FieldsList.Count];
        for (var column = 0; column < columns.Length; column++)
        {
            switch (column % 3)
            {
                case 0:
                    var ints = new Int64Array.Builder();
                    for (var row = 0; row < rows; row++)
                    {
                        ints.Append(column * 100 + row);
                    }

                    columns[column] = ints.Build();
                    break;
                case 1:
                    var strings = new StringArray.Builder();
                    for (var row = 0; row < rows; row++)
                    {
                        strings.Append($"c{column}r{row}");
                    }

                    columns[column] = strings.Build();
                    break;
                default:
                    var lists = new ListArray.Builder(Int64Type.Default);
                    var items = (Int64Array.Builder)lists.ValueBuilder;
                    for (var row = 0; row < rows; row++)
                    {
                        lists.Append();
                        items.Append(row).Append(column);
                    }

                    columns[column] = lists.Build();
                    break;
            }
        }

        return new RecordBatch(s_wideSchema, columns, rows);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RecordBatch TemporaryDictionaryBatch()
    {
        var indices = new Int32Array.Builder();
        for (var row = 0; row < 64; row++)
        {
            indices.Append(row % 3);
        }

        var dictionary = new StringArray.Builder().Append("red").Append("green").Append("blue").Build();
        var colors = new DictionaryArray(s_colorType, indices.Build(), dictionary);
        return new RecordBatch(s_dictionarySchema, [colors], colors.Length);
    }

    private static readonly Dictionary<string, string> s_metadata = new() { ["k"] = "v" };

    public enum Entry
    {
        StreamWriter,
        StreamWriterWithMetadata,
        FileWriter,
        DictionaryColumn,
    }

    private static Schema SchemaFor(Entry entry) => entry == Entry.DictionaryColumn ? s_dictionarySchema : s_batchSchema;

    private static RecordBatch TemporaryBatchFor(Entry entry) =>
        entry == Entry.DictionaryColumn ? TemporaryDictionaryBatch() : TemporaryBatch();

    private static ArrowStreamWriter WriterFor(Entry entry, Stream destination) => entry == Entry.FileWriter
        ? new ArrowFileWriter(destination, s_batchSchema, leaveOpen: true)
        : new ArrowStreamWriter(destination, SchemaFor(entry), leaveOpen: true);

    /// <summary>Writes a temporary batch: the argument is the only reference to it.</summary>
    private static void WriteTemporary(Entry entry, ArrowStreamWriter writer)
    {
        if (entry == Entry.StreamWriterWithMetadata)
        {
            writer.WriteRecordBatch(TemporaryBatchFor(entry), s_metadata);
        }
        else
        {
            writer.WriteRecordBatch(TemporaryBatchFor(entry));
        }
    }

    /// <summary>The same stream, written from a batch that is reachable throughout.</summary>
    private static byte[] Expected(Entry entry)
    {
        using var batch = TemporaryBatchFor(entry);
        using var destination = new MemoryStream();
        using (var writer = WriterFor(entry, destination))
        {
            if (entry == Entry.StreamWriterWithMetadata)
            {
                writer.WriteRecordBatch(batch, s_metadata);
            }
            else
            {
                writer.WriteRecordBatch(batch);
            }

            writer.WriteEnd();
        }

        return destination.ToArray();
    }

    [Theory]
    [InlineData(Entry.StreamWriter)]
    [InlineData(Entry.StreamWriterWithMetadata)]
    [InlineData(Entry.FileWriter)]
    [InlineData(Entry.DictionaryColumn)]
    public void WriteRecordBatch_KeepsATemporaryBatchAlive_UntilItsBodyIsWritten(Entry entry)
    {
        var expected = Expected(entry);

        using var destination = new CollectBeforeEveryWriteStream();
        using (var writer = WriterFor(entry, destination))
        {
            WriteTemporary(entry, writer);
            writer.WriteEnd();
        }

        Assert.Equal(expected, destination.ToArray());
    }

    /// <summary>Passes without the patch too — the async state machine holds its parameter — so
    /// this guards the patch's async half rather than reproducing a failure. <c>true</c> makes
    /// every write really suspend, as a socket write would.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteRecordBatchAsync_KeepsATemporaryBatchAlive_UntilItsBodyIsWritten(bool yieldOnWrites)
    {
        var expected = Expected(Entry.StreamWriter);

        using var destination = new CollectBeforeEveryWriteStream(yieldOnWrites);
        using (var writer = new ArrowStreamWriter(destination, s_batchSchema, leaveOpen: true))
        {
            await writer.WriteRecordBatchAsync(TemporaryBatch());
            await writer.WriteEndAsync();
        }

        Assert.Equal(expected, destination.ToArray());
    }

    /// <summary>Runs <paramref name="encode"/> on four threads for 1.5 s while another thread
    /// collects and drains finalizers continuously, asserting every result equals
    /// <paramref name="expected"/>. Probabilistic, unlike the tests above: a collection has to land
    /// while a body is being copied, so it exercises write paths the test cannot hook.</summary>
    private static void AssertStableUnderConcurrentCollection(byte[] expected, Func<byte[]> encode)
    {
        using var stop = new CancellationTokenSource();
        var collector = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        })
        { IsBackground = true };
        collector.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(1.5);
            var encoders = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
            {
                for (var iteration = 0; DateTime.UtcNow < deadline; iteration++)
                {
                    var actual = encode();
                    Assert.True(actual.AsSpan().SequenceEqual(expected), $"iteration {iteration}: written bytes differ");
                }
            }, TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(encoders);
        }
        finally
        {
            stop.Cancel();
            collector.Join();
        }
    }

    [Fact]
    public void WriteRecordBatch_IsByteStable_WhileTheCollectorRunsConcurrently()
    {
        static byte[] Write(RecordBatch batch)
        {
            using var destination = new MemoryStream();
            using (var writer = new ArrowStreamWriter(destination, s_wideSchema, leaveOpen: true))
            {
                writer.WriteRecordBatch(batch);
                writer.WriteEnd();
            }

            return destination.ToArray();
        }

        using var reference = TemporaryWideBatch();
        AssertStableUnderConcurrentCollection(Write(reference), () => Write(TemporaryWideBatch()));
    }

    /// <summary>A dataclass-equivalent value: <see cref="ValueCodec"/> encodes it as an embedded
    /// one-row IPC stream built only to be written.</summary>
    public sealed class Payload
    {
        public string Name { get; set; } = "";

        public List<long> Values { get; set; } = [];

        public string? Note { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<string> Labels { get; set; } = [];

        public List<string> Categories { get; set; } = [];

        public List<long> Counts { get; set; } = [];

        public List<long> Offsets { get; set; } = [];

        public string Description { get; set; } = "";

        public string? Comment { get; set; }

        public long Version { get; set; }

        public List<string> Aliases { get; set; } = [];

        public List<string> Owners { get; set; } = [];

        public List<string> Readers { get; set; } = [];

        public List<string> Writers { get; set; } = [];

        public List<string> Regions { get; set; } = [];

        public List<string> Formats { get; set; } = [];

        public List<string> Sources { get; set; } = [];

        public List<string> Sinks { get; set; } = [];

        public List<string> Keywords { get; set; } = [];
    }

    private static Payload NewPayload() => new()
    {
        Name = "payload",
        Values = [1, 2, 3, 4, 5],
        Note = "a note",
        Tags = ["a", "b"],
        Labels = ["x", "y", "z"],
        Categories = ["generator"],
        Counts = [10, 20],
        Offsets = [0, 7, 9],
        Description = "a payload wide enough to have many buffers",
        Comment = "c",
        Version = 3,
        Aliases = ["p", "pay"],
        Owners = ["o"],
        Readers = ["r1", "r2"],
        Writers = ["w"],
        Regions = ["us", "eu"],
        Formats = ["arrow"],
        Sources = ["s"],
        Sinks = ["k"],
        Keywords = ["wide", "payload"],
    };

    private static readonly Schema s_embeddedSchema = new([new Field("payload", BinaryType.Default, nullable: false)], metadata: null);

    private static byte[] EncodeThroughValueCodec(Payload payload)
    {
        using var row = ValueCodec.BuildRow(s_embeddedSchema, [payload]);
        return ((BinaryArray)row.Column(0)).GetBytes(0).ToArray();
    }

    /// <summary>vgi-rpc-csharp's own batch built only to be written — the one-row embedded record
    /// <see cref="ValueCodec"/> writes for every dataclass-valued field and result — is covered by
    /// the writer's fix, with no change of its own. (A <see cref="RecordBatch"/>-valued field goes
    /// through the same writer, but <see cref="ValueCodec.BuildRow"/>'s own argument list keeps
    /// that batch reachable, so it has no window to test.)</summary>
    [Fact]
    public void ValueCodec_EmbeddedRecord_IsByteStable_WhileTheCollectorRunsConcurrently()
    {
        AssertStableUnderConcurrentCollection(EncodeThroughValueCodec(NewPayload()), () => EncodeThroughValueCodec(NewPayload()));
    }
}
