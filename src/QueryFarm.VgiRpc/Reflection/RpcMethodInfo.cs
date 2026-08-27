using System.Reflection;
using Apache.Arrow;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Streaming;

namespace QueryFarm.VgiRpc.Reflection;

/// <summary>
/// Everything the RPC engine needs to know about one registered method: its wire name, the
/// Arrow schemas derived from its parameters/return type, and how to invoke it.
/// </summary>
public sealed class RpcMethodInfo
{
    private readonly Func<object, object?[], object?> _invoke;
    private readonly Func<object, object?>? _getAwaitedResult;
    private readonly Func<object, Task>? _valueTaskAsTask;

    public string WireName { get; }
    public MethodInfo Method { get; }
    public RpcMethodKind Kind { get; }
    public Schema ParamsSchema { get; }

    /// <summary>The unary result schema — meaningless for a stream method (<see cref="Kind"/>
    /// is <see cref="RpcMethodKind.Stream"/>), whose output/input schemas come from the
    /// per-call <see cref="IRpcStream"/> instance the method returns instead.</summary>
    public Schema ResultSchema { get; }

    /// <summary>The subset of <see cref="Method"/>'s parameters that ARE wire fields, in
    /// <see cref="ParamsSchema"/> field order — i.e. everything except a trailing
    /// <see cref="ICallContext"/> parameter, if the method declares one.</summary>
    public IReadOnlyList<ParameterInfo> Parameters { get; }

    /// <summary>The wire parameter CLR types, cached once instead of allocated per dispatch.</summary>
    public IReadOnlyList<Type> ParameterTypes { get; }

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

        var allParams = method.GetParameters();
        HasContextParameter = allParams.Length > 0 && typeof(ICallContext).IsAssignableFrom(allParams[^1].ParameterType);
        Parameters = HasContextParameter ? allParams[..^1] : allParams;
        ParameterTypes = Parameters.Select(p => p.ParameterType).ToArray();

        var paramFields = Parameters
            .Select(p => SchemaDerivation.FieldForParameter(WireNaming.ForParameter(p), p))
            .ToArray();
        ParamsSchema = new Schema(paramFields, metadata: null);

        (ResultClrType, IsAsync) = UnwrapReturnType(method.ReturnType);
        _invoke = CompileInvoker(method);
        if (method.ReturnType.IsGenericType)
        {
            var returnDefinition = method.ReturnType.GetGenericTypeDefinition();
            if (returnDefinition == typeof(Task<>))
            {
                _getAwaitedResult = CompileResultGetter(method.ReturnType);
            }
            else if (returnDefinition == typeof(ValueTask<>))
            {
                _valueTaskAsTask = CompileValueTaskAsTask(method.ReturnType);
                _getAwaitedResult = CompileResultGetter(typeof(Task<>).MakeGenericType(ResultClrType));
            }
        }


        if (typeof(IRpcStream).IsAssignableFrom(ResultClrType))
        {
            Kind = RpcMethodKind.Stream;
            ResultSchema = new Schema([], metadata: null); // unused for streams
        }
        else
        {
            Kind = RpcMethodKind.Unary;
            ResultSchema = ResultClrType == typeof(void)
                ? new Schema([], metadata: null)
                : new Schema([SchemaDerivation.FieldFor("result", ResultClrType, method.ReturnParameter.IsDefined(typeof(LargeWidthAttribute)))], metadata: null);
        }
    }

    private static Func<object, object?[], object?> CompileInvoker(MethodInfo method)
    {
        var implementation = System.Linq.Expressions.Expression.Parameter(typeof(object), "implementation");
        var arguments = System.Linq.Expressions.Expression.Parameter(typeof(object[]), "arguments");
        var parameters = method.GetParameters();
        var callArguments = parameters
            .Select((parameter, index) =>
                System.Linq.Expressions.Expression.Convert(
                    System.Linq.Expressions.Expression.ArrayIndex(arguments, System.Linq.Expressions.Expression.Constant(index)),
                    parameter.ParameterType))
            .ToArray();
        var instance = method.IsStatic
            ? null
            : System.Linq.Expressions.Expression.Convert(implementation, method.DeclaringType!);
        var call = System.Linq.Expressions.Expression.Call(instance, method, callArguments);
        System.Linq.Expressions.Expression body = method.ReturnType == typeof(void)
            ? System.Linq.Expressions.Expression.Block(call, System.Linq.Expressions.Expression.Constant(null, typeof(object)))
            : System.Linq.Expressions.Expression.Convert(call, typeof(object));
        return System.Linq.Expressions.Expression
            .Lambda<Func<object, object?[], object?>>(body, implementation, arguments)
            .Compile();
    }

    private static Func<object, object?> CompileResultGetter(Type taskType)
    {
        var task = System.Linq.Expressions.Expression.Parameter(typeof(object), "task");
        var result = System.Linq.Expressions.Expression.Property(
            System.Linq.Expressions.Expression.Convert(task, taskType),
            "Result");
        return System.Linq.Expressions.Expression
            .Lambda<Func<object, object?>>(
                System.Linq.Expressions.Expression.Convert(result, typeof(object)),
                task)
            .Compile();
    }

    private static Func<object, Task> CompileValueTaskAsTask(Type valueTaskType)
    {
        var valueTask = System.Linq.Expressions.Expression.Parameter(typeof(object), "valueTask");
        var asTask = System.Linq.Expressions.Expression.Call(
            System.Linq.Expressions.Expression.Convert(valueTask, valueTaskType),
            valueTaskType.GetMethod("AsTask")!);
        return System.Linq.Expressions.Expression
            .Lambda<Func<object, Task>>(
                System.Linq.Expressions.Expression.Convert(asTask, typeof(Task)),
                valueTask)
            .Compile();
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
        var raw = _invoke(implementation, args);
        if (!IsAsync)
        {
            return raw;
        }

        switch (raw)
        {
            case Task task:
                await task.ConfigureAwait(false);
                return _getAwaitedResult?.Invoke(task);
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;
            default:
                // ValueTask<T> is boxed as object; use the delegate compiled for the declared
                // return type rather than rediscovering AsTask()/Result through reflection.
                if (raw is not null && _valueTaskAsTask is not null)
                {
                    var asTask = _valueTaskAsTask(raw);
                    await asTask.ConfigureAwait(false);
                    return _getAwaitedResult!(asTask);
                }

                return raw;
        }
    }
}
