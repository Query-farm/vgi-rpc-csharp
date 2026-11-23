using System.IO.Compression;
using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Logging;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;
using QueryFarm.VgiRpc.Transport;
using QueryFarm.VgiRpc.Wire;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Streaming;

/// <summary>
/// <see cref="StateRef"/> and the token codec (STATE_REF_V1.md §2, §3, §7). The shared suite runs
/// the inline round trip and the misuse refusal on every transport; these pin what it cannot see:
/// the codec bytes, the memo, typed resolve and the refusal of encoded forms this port never mints.
/// </summary>
public sealed class StateRefTests
{
    [Fact]
    public void Codec_TinyPayloadStaysRaw()
    {
        var packed = TokenCodec.Pack("abc"u8);
        Assert.Equal([TokenCodec.Raw, (byte)'a', (byte)'b', (byte)'c'], packed);
        Assert.Equal("abc"u8.ToArray(), TokenCodec.Unpack(packed, 1024));
    }

    [Fact]
    public void Codec_LargePayloadCompressesWithZstd()
    {
        var body = new byte[8192];
        System.Array.Fill(body, (byte)'x');
        var packed = TokenCodec.Pack(body);
        Assert.Equal(TokenCodec.Zstd, packed[0]);
        Assert.True(packed.Length < body.Length / 4);
        Assert.Equal(body, TokenCodec.Unpack(packed, body.Length));
    }

    [Fact]
    public void Codec_ReadsDeflate()
    {
        var body = new byte[4096];
        System.Array.Fill(body, (byte)'y');
        using var output = new MemoryStream();
        output.WriteByte(TokenCodec.Deflate);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(body);
        }

        Assert.Equal(body, TokenCodec.Unpack(output.ToArray(), body.Length));
    }

    [Fact]
    public void Codec_RefusesUnknownTagsAndOversizedBodies()
    {
        Assert.Throws<InvalidDataException>(() => TokenCodec.Unpack([0x7f, 1, 2], 1024));
        Assert.Throws<InvalidDataException>(() => TokenCodec.Unpack([], 1024));
        var body = new byte[4096];
        Assert.Throws<InvalidDataException>(() => TokenCodec.Unpack(TokenCodec.Pack(body), 4095));
        Assert.Throws<InvalidDataException>(() => TokenCodec.Unpack([TokenCodec.Zstd, 1, 2, 3], 1024));
    }

    [Fact]
    public void Resolve_DecodesTheInlineForm()
    {
        ICallContext ctx = new BareContext();
        var value = new byte[5000];
        System.Array.Fill(value, (byte)'z');
        var inline = StateRef.FromEncoded([StateRef.InlineForm, .. TokenCodec.Pack(value)]);
        Assert.Equal("inline", inline.Form);
        Assert.Equal(value, ctx.Resolve(inline));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x07, 0x00, 0x61 })]
    [InlineData(new byte[] { 0x01, 0x7f, 0x61 })]
    [InlineData(new byte[] { })]
    public void Resolve_RefusesStoredUnknownAndCorruptForms(byte[] encoded)
    {
        ICallContext ctx = new BareContext();
        var error = Assert.Throws<RpcException>(() => ctx.Resolve(StateRef.FromEncoded(encoded)));
        Assert.Equal(StateRefErrors.InvalidStateRefKind, error.ErrorKind);
        Assert.Equal(ErrorCodes.InvalidArgument, error.ErrorCode);
    }

    [Fact]
    public void ToInlineBytes_RoundTripsWithoutChangingTheForm()
    {
        var reference = StateRef.FromEncoded([StateRef.InlineForm, TokenCodec.Raw, 1, 2, 3]);
        Assert.Equal([StateRef.InlineForm, TokenCodec.Raw, 1, 2, 3], reference.ToInlineBytes());
    }

    [Fact]
    public async Task DefaultContext_RefusesStateRefAndArgumentRefs()
    {
        ICallContext ctx = new BareContext();
        var misuse = Assert.Throws<RpcException>(() => ctx.StateRef([1, 2, 3]));
        Assert.Equal(StateRefErrors.StateRefMisuseKind, misuse.ErrorKind);
        Assert.Equal(ErrorCodes.FailedPrecondition, misuse.ErrorCode);
        Assert.StartsWith(StateRefErrors.MisuseMessage, misuse.ErrorMessage, StringComparison.Ordinal);
        var argumentRef = await Assert.ThrowsAsync<RpcException>(() => ctx.ResolveArgumentRefAsync([1], 1, new string('0', 64)));
        Assert.Equal(StateRefErrors.InvalidArgumentsRefKind, argumentRef.ErrorKind);
    }

    /// <summary>Over a byte stream: the init handler's refs are memoized and held in memory, a
    /// typed resolve returns the original instance, and a turn's StateRef is refused, naming the
    /// method.</summary>
    [Fact]
    public async Task OverAPipe_InitMemoizesAndTurnsRefuse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IStateRefService), new StateRefService());
        await using var client = new RpcClient(
            clientTransport, new RpcClientOptions { Protocol = WireNaming.ForProtocol(typeof(IStateRefService)) });
        var serve = server.ServeOneAsync(serverTransport, cancellationToken);

        using var parameters = new RecordBatch(new Schema([], null), [], 1);
        await using var session = await client.OpenExchangeAsync("hold", parameters, s_input, cancellationToken: cancellationToken);
        using var input = new RecordBatch(s_input, [new Int64Array.Builder().Append(0).Build()], 1);
        var output = (await session.ExchangeAsync(input, cancellationToken: cancellationToken))!;
        using (output.Batch)
        {
            Assert.True(((BooleanArray)output.Batch.Column("memoized")).GetValue(0));
            Assert.True(((BooleanArray)output.Batch.Column("record_memoized")).GetValue(0));
            Assert.True(((BooleanArray)output.Batch.Column("typed_same")).GetValue(0));
            Assert.Equal("memory", ((StringArray)output.Batch.Column("form")).GetString(0));
            Assert.Equal(
                $"{StateRefErrors.MisuseMessage} (StateRefService.hold)",
                ((StringArray)output.Batch.Column("misuse")).GetString(0));
        }

        await session.DisposeAsync();
        Assert.True(await serve.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
    }

    private static readonly Schema s_input = new([new Field("n", Int64Type.Default, false)], null);

    private static readonly Schema s_output = new(
        [
            new Field("memoized", BooleanType.Default, false),
            new Field("record_memoized", BooleanType.Default, false),
            new Field("typed_same", BooleanType.Default, false),
            new Field("form", StringType.Default, false),
            new Field("misuse", StringType.Default, false),
        ],
        null);

    public sealed class Settings
    {
        public string Name { get; set; } = "";

        public long Limit { get; set; }
    }

    public interface IStateRefService
    {
        Task<RpcStream<HoldState>> HoldAsync(ICallContext? ctx = null);
    }

    private sealed class StateRefService : IStateRefService
    {
        public Task<RpcStream<HoldState>> HoldAsync(ICallContext? ctx = null)
        {
            var a = ctx!.StateRef("same bytes"u8.ToArray());
            var b = ctx.StateRef("same bytes"u8.ToArray());
            var settings = new Settings { Name = "n", Limit = 7 };
            var r1 = ctx.StateRef(settings);
            var r2 = ctx.StateRef(settings);
            return Task.FromResult(new RpcStream<HoldState>(s_output, new HoldState(a, ReferenceEquals(a, b), r1, ReferenceEquals(r1, r2), settings), InputSchema: s_input));
        }
    }

    public sealed class HoldState(StateRef bytes, bool memoized, StateRef record, bool recordMemoized, Settings original) : ExchangeState
    {
        public override Task ExchangeAsync(AnnotatedBatch input, OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
        {
            Assert.Equal("same bytes"u8.ToArray(), ctx!.Resolve(bytes));
            var typed = ctx.Resolve<Settings>(record);
            string misuse;
            try
            {
                ctx.StateRef([1]);
                misuse = "accepted";
            }
            catch (RpcException exc) when (exc.ErrorKind == StateRefErrors.StateRefMisuseKind)
            {
                misuse = exc.ErrorMessage;
            }

            output.Emit(ValueCodec.BuildRow(s_output, [memoized, recordMemoized, ReferenceEquals(typed, original), bytes.Form, misuse]));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void TypedResolve_DecodesAnEncodedRecord()
    {
        // A record wrapped elsewhere and carried as inline bytes decodes as the record type.
        var settings = new Settings { Name = "decoded", Limit = 42 };
        var bytes = ValueCodecRoundTrip(settings);
        ICallContext ctx = new BareContext();
        var decoded = ctx.Resolve<Settings>(StateRef.FromEncoded([StateRef.InlineForm, .. TokenCodec.Pack(bytes)]));
        Assert.Equal("decoded", decoded.Name);
        Assert.Equal(42, decoded.Limit);
    }

    private static byte[] ValueCodecRoundTrip(Settings settings)
    {
        // The same bytes a nested record field carries: schema + one row + EOS.
        var schema = SchemaDerivation.InnerSchemaFor(typeof(Settings));
        var row = ValueCodec.BuildRow(schema, [settings.Name, settings.Limit]);
        using var stream = new MemoryStream();
        using (var writer = new Apache.Arrow.Ipc.ArrowStreamWriter(stream, schema, leaveOpen: true))
        {
            writer.WriteStart();
            writer.WriteRecordBatch(row);
            writer.WriteEnd();
        }

        return stream.ToArray();
    }

    private sealed class BareContext : ICallContext
    {
        public void EmitLog(VgiLogLevel level, string message, IReadOnlyDictionary<string, object?>? extra = null)
        {
        }
    }
}
