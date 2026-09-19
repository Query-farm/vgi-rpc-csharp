using Apache.Arrow;
using Apache.Arrow.Types;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Errors;
using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Server;

/// <summary>
/// A request whose parameter batch is valid Arrow but not the method's declared contract is
/// refused before dispatch, and the connection stays usable -- the reference suite's
/// <c>TestAdversarialRawRequestContract</c>.
/// </summary>
/// <remarks>
/// Argument decoding is positional, so each of these used to reach the method: an extra column
/// was ignored, a renamed or reordered one was read as whatever declared parameter shared its
/// position, a nullability flip went unnoticed and a second row was dropped. The requests here are
/// fabricated (that is the point); the answers are the real server's.
/// </remarks>
public sealed class RequestContractTests
{
    private static readonly Field s_a = new("a", Int32Type.Default, nullable: false);
    private static readonly Field s_b = new("b", Int32Type.Default, nullable: false);

    public static TheoryData<string> Mutations => new()
    {
        "extra_field",
        "wrong_name",
        "wrong_order",
        "wrong_type",
        "wrong_nullability",
        "two_rows",
        "missing_field",
        "zero_rows",
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task ContractBreach_IsRefused_AndTheConnectionStaysUsable(string mutation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter());
        await using var client = new RpcClient(
            clientTransport,
            new RpcClientOptions { Protocol = WireNaming.ForProtocol(typeof(IGreeter)) });

        var refusedServe = server.ServeOneAsync(serverTransport, cancellationToken);
        using (var breach = Mutate(mutation))
        {
            var refusal = await Assert.ThrowsAsync<RpcException>(
                () => client.CallUnaryAsync("add", breach, cancellationToken: cancellationToken));
            Assert.False(string.IsNullOrEmpty(refusal.ErrorType));
        }

        Assert.True(await refusedServe.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));

        var validServe = server.ServeOneAsync(serverTransport, cancellationToken);
        using var valid = Row([s_a, s_b], 2, 3);
        var response = await client.CallUnaryAsync("add", valid, cancellationToken: cancellationToken);
        using (response.Batch)
        {
            Assert.Equal(5, ((Int32Array)response.Batch.Column(0)).GetValue(0));
        }

        Assert.True(await validServe.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
    }

    private static RecordBatch Mutate(string mutation) => mutation switch
    {
        "extra_field" => Row([s_a, s_b, new Field("unexpected", Int32Type.Default, false)], 2, 3, 4),
        "wrong_name" => Row([new Field("wrong_name", Int32Type.Default, false), s_b], 2, 3),
        "wrong_order" => Row([s_b, s_a], 3, 2),
        "wrong_type" => new RecordBatch(
            new Schema([new Field("a", StringType.Default, false), s_b], null),
            [new StringArray.Builder().Append("2").Build(), new Int32Array.Builder().Append(3).Build()],
            1),
        "wrong_nullability" => Row([new Field("a", Int32Type.Default, nullable: true), s_b], 2, 3),
        "two_rows" => new RecordBatch(
            new Schema([s_a, s_b], null),
            [new Int32Array.Builder().Append(2).Append(20).Build(), new Int32Array.Builder().Append(3).Append(30).Build()],
            2),
        "missing_field" => Row([s_a], 2),
        "zero_rows" => new RecordBatch(
            new Schema([s_a, s_b], null),
            [new Int32Array.Builder().Build(), new Int32Array.Builder().Build()],
            0),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
    };

    private static RecordBatch Row(Field[] fields, params int[] values) =>
        new(
            new Schema(fields, null),
            values.Select(value => (IArrowArray)new Int32Array.Builder().Append(value).Build()).ToArray(),
            1);
}
