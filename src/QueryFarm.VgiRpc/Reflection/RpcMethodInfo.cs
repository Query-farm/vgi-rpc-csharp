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

    /// <summary>The record type this stream method declares as its per-stream header, or null.</summary>
    /// <remarks>
    /// Declared via <see cref="Attributes.StreamHeaderAttribute"/> because this port supplies the
    /// header value at run time, which leaves the method signature silent about whether one
    /// exists. <c>has_header</c> and the header schema are part of the wire surface and therefore
    /// of the protocol hash, so a port that cannot state them statically cannot agree with any
    /// other port about what protocol it speaks.
    /// </remarks>
    public Type? HeaderClrType { get; }

    /// <summary>The stream kind this method declares, or null when it declares none.</summary>
    /// <remarks>
    /// Declared via <see cref="Attributes.StreamKindAttribute"/> because this port decides per
    /// call. It is the only field in a description that says whether a stream accepts input, so
    /// leaving it unstated makes reflection unable to answer the question it exists for.
    /// </remarks>
    public Attributes.StreamKind? DeclaredStreamKind { get; }

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
        HeaderClrType = method.GetCustomAttribute<Attributes.StreamHeaderAttribute>()?.HeaderType;
        DeclaredStreamKind = method.GetCustomAttribute<Attributes.StreamKindAttribute>()?.Kind;
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
                : new Schema([SchemaDerivation.FieldForReturn("result", method, ResultClrType)], metadata: null);
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
    /// Refuses a request batch that does not carry exactly this method's declared parameter
    /// contract, before any argument is decoded or the method is dispatched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the canonical Python server (<c>_read_request</c>'s row check, then
    /// <c>_validate_call_signature</c>): a non-empty request batch has exactly one row, and its
    /// schema has the declared field count, and field by field the declared name, Arrow type and
    /// top-level nullability. Schema and field metadata are not part of the contract.
    /// </para>
    /// <para>
    /// Argument decoding is positional and lenient, so without this a request that disagreed
    /// with the protocol reached the method anyway: an extra column was ignored, a renamed or
    /// reordered one was read as whatever declared parameter shared its position, a second row
    /// was dropped, and a nullability flip went unnoticed. The caller got an answer to a question
    /// it did not ask instead of a refusal naming the disagreement.
    /// </para>
    /// <para>
    /// Types are compared by their canonical protocol-hash token (<see cref="Hash.TypeTokens"/>),
    /// the same normalisation that decides whether two ports declare the same protocol: child
    /// nullability counts, a list's child field name does not.
    /// </para>
    /// </remarks>
    /// <exception cref="Errors.RpcException">The batch breaks the declared contract.</exception>
    internal void ValidateRequestBatch(RecordBatch batch)
    {
        var actual = batch.Schema;
        if (actual.FieldsList.Count > 0 && batch.Length != 1)
        {
            throw new Errors.RpcException(
                "ProtocolError",
                $"Expected 1 row in request batch, got {batch.Length}. Each parameter is a column (not a row).");
        }

        var declared = ParamsSchema;
        if (actual.FieldsList.Count != declared.FieldsList.Count)
        {
            throw new Errors.RpcException(
                "TypeError",
                $"{WireName}() parameter schema expected {declared.FieldsList.Count} fields, got {actual.FieldsList.Count}");
        }

        for (var index = 0; index < declared.FieldsList.Count; index++)
        {
            var expected = declared.GetFieldByIndex(index);
            var field = actual.GetFieldByIndex(index);
            if (field.Name != expected.Name)
            {
                throw new Errors.RpcException(
                    "TypeError",
                    $"{WireName}() parameter schema field {index} expected name '{expected.Name}', got '{field.Name}'");
            }

            if (!SameArrowType(field, expected))
            {
                throw new Errors.RpcException(
                    "TypeError",
                    $"{WireName}() parameter '{field.Name}' expected Arrow type {expected.DataType} but the request batch carried {field.DataType}");
            }

            if (field.IsNullable != expected.IsNullable)
            {
                throw new Errors.RpcException(
                    "TypeError",
                    $"{WireName}() parameter '{field.Name}' expected nullable={expected.IsNullable}, but the request batch carried nullable={field.IsNullable}");
            }
        }
    }

    private static bool SameArrowType(Field field, Field expected)
    {
        try
        {
            return Hash.TypeTokens.TypeToken(field) == Hash.TypeTokens.TypeToken(expected);
        }
        catch (Hash.TypeTokens.UnsupportedArrowTypeException)
        {
            // A type with no canonical token is not one any declared parameter has.
            return false;
        }
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
