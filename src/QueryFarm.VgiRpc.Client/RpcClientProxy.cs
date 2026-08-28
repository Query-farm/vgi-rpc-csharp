using System.Diagnostics;
using System.Reflection;
using Apache.Arrow;
using QueryFarm.VgiRpc.Reflection;

namespace QueryFarm.VgiRpc.Client;

/// <summary>Reflection-generated typed facade over the schema-first <see cref="RpcClient"/>.</summary>
public class RpcClientProxy<TContract> : DispatchProxy where TContract : class
{
    private IRpcClient _client = null!;
    private IReadOnlyDictionary<MethodInfo, ClientMethod> _methods = null!;

    public static TContract Create(IRpcClient client)
    {
        if (!typeof(TContract).IsInterface)
        {
            throw new ArgumentException($"Client contract '{typeof(TContract)}' must be an interface.");
        }

        var proxy = (RpcClientProxy<TContract>)(object)Create<TContract, RpcClientProxy<TContract>>()!;
        proxy._client = client;
        proxy._methods = typeof(TContract).GetMethods().ToDictionary(method => method, method => new ClientMethod(method));
        return (TContract)(object)proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null || !_methods.TryGetValue(targetMethod, out var method))
        {
            throw new InvalidOperationException($"Method '{targetMethod}' is not registered on '{typeof(TContract)}'.");
        }

        var supplied = args ?? [];
        var cancellationToken = method.HasCancellationToken
            ? (CancellationToken)supplied[^1]!
            : CancellationToken.None;
        var arguments = method.HasCancellationToken ? supplied[..^1] : supplied;
        Task<object?> call = method.Kind switch
        {
            ClientMethodKind.Unary => CallUnaryAsync(method, arguments, cancellationToken),
            ClientMethodKind.Producer => OpenProducerAsync(method, arguments, cancellationToken),
            ClientMethodKind.Exchange => OpenExchangeAsync(method, arguments, cancellationToken),
            _ => throw new UnreachableException(),
        };

        return AdaptReturn(call, method.ResultType, method.ReturnsValueTask);
    }

    private async Task<object?> CallUnaryAsync(ClientMethod method, object?[] arguments, CancellationToken cancellationToken)
    {
        using var parameters = ValueCodec.BuildRow(method.ParamsSchema, arguments);
        var response = await _client.CallUnaryAsync(method.WireName, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        using (response.Batch)
        {
            return method.ResultSchema.FieldsList.Count == 0
                ? null
                : ValueCodec.ExtractRow(response.Batch, [method.ResultType])[0];
        }
    }

    private async Task<object?> OpenProducerAsync(ClientMethod method, object?[] arguments, CancellationToken cancellationToken)
    {
        using var parameters = ValueCodec.BuildRow(method.ParamsSchema, arguments);
        return await _client.OpenProducerAsync(
            method.WireName,
            parameters,
            method.HasHeader,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> OpenExchangeAsync(ClientMethod method, object?[] arguments, CancellationToken cancellationToken)
    {
        using var parameters = ValueCodec.BuildRow(method.ParamsSchema, arguments);
        var raw = await _client.OpenExchangeAsync(
            method.WireName,
            parameters,
            method.InputSchema!,
            method.HasHeader,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var wrapperType = typeof(RpcExchangeSession<>).MakeGenericType(method.ExchangeInputType!);
        return Activator.CreateInstance(
            wrapperType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [raw],
            culture: null)!;
    }

    private static object AdaptReturn(Task<object?> call, Type resultType, bool valueTask)
    {
        if (resultType == typeof(void))
        {
            return valueTask ? new ValueTask(AwaitVoidAsync(call)) : AwaitVoidAsync(call);
        }

        var methodName = valueTask ? nameof(CastValueTask) : nameof(CastTask);
        return typeof(RpcClientProxy<TContract>)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(resultType)
            .Invoke(null, [call])!;
    }

    private static async Task AwaitVoidAsync(Task<object?> call) => await call.ConfigureAwait(false);

    private static async Task<TResult> CastTask<TResult>(Task<object?> call) =>
        (TResult)(await call.ConfigureAwait(false))!;

    private static ValueTask<TResult> CastValueTask<TResult>(Task<object?> call) =>
        new(CastTask<TResult>(call));

    private enum ClientMethodKind
    {
        Unary,
        Producer,
        Exchange,
    }

    private sealed class ClientMethod
    {
        public ClientMethod(MethodInfo method)
        {
            WireName = WireNaming.ForMethod(method);
            var parameters = method.GetParameters();
            HasCancellationToken = parameters.Length > 0 && parameters[^1].ParameterType == typeof(CancellationToken);
            var wireParameters = HasCancellationToken ? parameters[..^1] : parameters;
            ParamsSchema = new Schema(
                wireParameters.Select(parameter => SchemaDerivation.FieldForParameter(WireNaming.ForParameter(parameter), parameter)),
                metadata: null);

            (ResultType, ReturnsValueTask) = UnwrapReturn(method.ReturnType);
            HasHeader = method.IsDefined(typeof(RpcStreamHeaderAttribute));
            if (ResultType == typeof(IRpcProducerSession) || ResultType == typeof(RpcProducerSession))
            {
                Kind = ClientMethodKind.Producer;
                ResultSchema = new Schema([], metadata: null);
            }
            else if (ResultType.IsGenericType && ResultType.GetGenericTypeDefinition() == typeof(RpcExchangeSession<>))
            {
                Kind = ClientMethodKind.Exchange;
                ExchangeInputType = ResultType.GetGenericArguments()[0];
                InputSchema = SchemaDerivation.InnerSchemaFor(ExchangeInputType);
                ResultSchema = new Schema([], metadata: null);
            }
            else
            {
                Kind = ClientMethodKind.Unary;
                ResultSchema = ResultType == typeof(void)
                    ? new Schema([], metadata: null)
                    : new Schema([SchemaDerivation.FieldFor("result", ResultType, method.ReturnParameter.IsDefined(typeof(LargeWidthAttribute)))], metadata: null);
            }
        }

        public string WireName { get; }
        public ClientMethodKind Kind { get; }
        public Schema ParamsSchema { get; }
        public Schema ResultSchema { get; }
        public Schema? InputSchema { get; }
        public Type? ExchangeInputType { get; }
        public Type ResultType { get; }
        public bool ReturnsValueTask { get; }
        public bool HasCancellationToken { get; }
        public bool HasHeader { get; }

        private static (Type ResultType, bool ValueTask) UnwrapReturn(Type returnType)
        {
            if (returnType == typeof(Task))
            {
                return (typeof(void), false);
            }

            if (returnType == typeof(ValueTask))
            {
                return (typeof(void), true);
            }

            if (returnType.IsGenericType)
            {
                var definition = returnType.GetGenericTypeDefinition();
                if (definition == typeof(Task<>))
                {
                    return (returnType.GetGenericArguments()[0], false);
                }

                if (definition == typeof(ValueTask<>))
                {
                    return (returnType.GetGenericArguments()[0], true);
                }
            }

            throw new NotSupportedException($"Client contract methods must return Task, Task<T>, ValueTask, or ValueTask<T>; got '{returnType}'.");
        }
    }
}
