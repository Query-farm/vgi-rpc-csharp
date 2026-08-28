using QueryFarm.VgiRpc.Reflection;
using QueryFarm.VgiRpc.Transport;

namespace QueryFarm.VgiRpc.Client;

/// <summary>
/// Source-compatible typed unary facade for 0.7 callers. The implementation now lives in the
/// dedicated client package and delegates to the schema-first <see cref="RpcClient"/>.
/// </summary>
public sealed class RpcConnection<TContract> : IAsyncDisposable where TContract : class
{
    private readonly RpcClient _client;

    public RpcConnection(IRpcTransport transport) => _client = new RpcClient(transport);

    public IRpcTransport Transport => _client.Transport;

    public TContract CreateProxy() => _client.CreateProxy<TContract>();

    public async Task<object?> CallUnaryAsync(
        RpcMethodInfo method,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        using var parameters = ValueCodec.BuildRow(method.ParamsSchema, arguments);
        var response = await _client.CallUnaryAsync(method.WireName, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        using (response.Batch)
        {
            return method.ResultSchema.FieldsList.Count == 0
                ? null
                : ValueCodec.ExtractRow(response.Batch, [method.ResultClrType])[0];
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
