using System.Reflection;
using QueryFarm.VgiRpc.Reflection;

namespace QueryFarm.VgiRpc.Client;

/// <summary>
/// A <see cref="DispatchProxy"/>-generated implementation of service interface
/// <typeparamref name="T"/> that marshals every call over an <see cref="RpcConnection{T}"/>.
/// The C# analog of Java's <c>java.lang.reflect.Proxy.newProxyInstance</c> and every other
/// port's client proxy — built into .NET, no extra dependency, no source generation.
///
/// <para>Service interface methods must return <see cref="Task"/> or <see cref="Task{TResult}"/>
/// (the idiomatic async C# shape — see docs/roadmap.md's async-model note); <c>ValueTask</c>
/// return types aren't supported by the client proxy yet.</para>
/// </summary>
// Not sealed: DispatchProxy.Create<TInterface, TProxy>() generates a dynamic subclass of
// TProxy, which the CLR requires to be inheritable.
public class RpcClientProxy<T> : DispatchProxy
    where T : class
{
    private RpcConnection<T> _connection = null!;
    private IReadOnlyDictionary<MethodInfo, RpcMethodInfo> _byReflectedMethod = null!;

    public static T Create(RpcConnection<T> connection)
    {
        var proxy = (RpcClientProxy<T>)(object)Create<T, RpcClientProxy<T>>()!;
        proxy._connection = connection;
        proxy._byReflectedMethod = ServiceRegistry.GetMethods(typeof(T)).Values.ToDictionary(m => m.Method);
        return (T)(object)proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new InvalidOperationException("No target method — this should not happen via a DispatchProxy-generated interface implementation.");
        }

        if (!_byReflectedMethod.TryGetValue(targetMethod, out var info))
        {
            throw new InvalidOperationException($"'{targetMethod}' is not a registered RPC method on '{typeof(T)}'.");
        }

        if (!info.IsAsync)
        {
            throw new NotSupportedException(
                $"'{targetMethod}' must return Task or Task<T> to be callable through {nameof(RpcClientProxy<T>)} — synchronous service interfaces aren't supported client-side. See docs/roadmap.md.");
        }

        var callTask = _connection.CallUnaryAsync(info, args ?? [], CancellationToken.None);

        if (info.ResultClrType == typeof(void))
        {
            return AwaitVoidAsync(callTask);
        }

        // The target method's declared return type is Task<TResult> for a *specific* TResult —
        // DispatchProxy needs an object castable to exactly that type, so a plain Task<object?>
        // won't do. Bridge through a reflectively-typed TaskCompletionSource<TResult>.
        var tcsType = typeof(TaskCompletionSource<>).MakeGenericType(info.ResultClrType);
        var tcs = Activator.CreateInstance(tcsType)!;
        _ = CompleteTypedTaskAsync(callTask, tcs, tcsType);
        return tcsType.GetProperty("Task")!.GetValue(tcs);
    }

    private static async Task AwaitVoidAsync(Task<object?> callTask) => await callTask.ConfigureAwait(false);

    private static async Task CompleteTypedTaskAsync(Task<object?> callTask, object tcs, Type tcsType)
    {
        try
        {
            var result = await callTask.ConfigureAwait(false);
            tcsType.GetMethod("SetResult")!.Invoke(tcs, [result]);
        }
        catch (Exception exc)
        {
            tcsType.GetMethod("SetException", [typeof(Exception)])!.Invoke(tcs, [exc]);
        }
    }
}
