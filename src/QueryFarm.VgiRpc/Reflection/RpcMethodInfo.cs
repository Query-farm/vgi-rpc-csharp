using System.Reflection;
using Apache.Arrow;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Everything the RPC engine needs to know about one registered method: its wire name, the
/// Arrow schemas derived from its parameters/return type, and how to invoke it.
/// </summary>
public sealed class RpcMethodInfo
{
    public string WireName { get; }
    public MethodInfo Method { get; }
    public RpcMethodKind Kind { get; }
    public Schema ParamsSchema { get; }
    public Schema ResultSchema { get; }

    /// <summary>The subset of <see cref="Method"/>'s parameters that ARE wire fields, in
    /// <see cref="ParamsSchema"/> field order — i.e. everything except a trailing
    /// <see cref="ICallContext"/> parameter, if the method declares one.</summary>
    public IReadOnlyList<ParameterInfo> Parameters { get; }

    /// <summary>True if the method's last parameter is an <see cref="ICallContext"/> the server
    /// must inject at invocation time (not a wire field — see <see cref="ICallContext"/>).</summary>
    public bool HasContextParameter { get; }

    /// <summary>The CLR type actually returned by the method body — <see cref="void"/> for a
    /// void/Task result, else the unwrapped Task&lt;T&gt;/ValueTask&lt;T&gt;/plain-T type.</summary>
    public Type ResultClrType { get; }

    /// <summary>True if the method's declared return type is a <see cref="Task"/>/<see cref="ValueTask"/>
    /// (with or without a result) and dispatch must await it.</summary>
    public bool IsAsync { get; }

    public RpcMethodInfo(MethodInfo method)
    {
        Method = method;
        WireName = WireNaming.ForMethod(method);
        Kind = RpcMethodKind.Unary;

        var allParams = method.GetParameters();
        HasContextParameter = allParams.Length > 0 && typeof(ICallContext).IsAssignableFrom(allParams[^1].ParameterType);
        Parameters = HasContextParameter ? allParams[..^1] : allParams;

        var paramFields = Parameters
            .Select(p => SchemaDerivation.FieldForParameter(WireNaming.ForParameter(p), p))
            .ToArray();
        ParamsSchema = new Schema(paramFields, metadata: null);

        (ResultClrType, IsAsync) = UnwrapReturnType(method.ReturnType);
        ResultSchema = ResultClrType == typeof(void)
            ? new Schema([], metadata: null)
            : new Schema([SchemaDerivation.FieldFor("result", ResultClrType)], metadata: null);
    }

    private static (Type ClrType, bool IsAsync) UnwrapReturnType(Type returnType)
    {
        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return (typeof(void), true);
        }

        if (returnType.IsGenericType)
        {
            var def = returnType.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
            {
                return (returnType.GetGenericArguments()[0], true);
            }
        }

        return (returnType == typeof(void) ? typeof(void) : returnType, false);
    }

    /// <summary>
    /// Invokes the method against <paramref name="implementation"/> with positional
    /// <paramref name="wireArgs"/> (in <see cref="Parameters"/> order — <paramref name="context"/>
    /// is appended automatically when <see cref="HasContextParameter"/>), awaiting a Task/ValueTask
    /// result if <see cref="IsAsync"/>, and returns the unwrapped result value
    /// (<see langword="null"/> for a void/Task-without-result method).
    /// </summary>
    public async Task<object?> InvokeAsync(object implementation, object?[] wireArgs, ICallContext? context = null)
    {
        var args = HasContextParameter ? [.. wireArgs, context] : wireArgs;
        var raw = Method.Invoke(implementation, args);
        if (!IsAsync)
        {
            return raw;
        }

        switch (raw)
        {
            case Task task:
                await task.ConfigureAwait(false);
                return ResultClrType == typeof(void) ? null : task.GetType().GetProperty("Result")!.GetValue(task);
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;
            default:
                // A ValueTask<T> is a struct — `raw` is already boxed as `object`, so it can't be
                // pattern-matched by static type above. Reflect for `AsTask()`/`.Result` instead.
                if (raw is not null && raw.GetType().IsGenericType && raw.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTask = (Task)raw.GetType().GetMethod("AsTask")!.Invoke(raw, null)!;
                    await asTask.ConfigureAwait(false);
                    return asTask.GetType().GetProperty("Result")!.GetValue(asTask);
                }

                return raw;
        }
    }
}
