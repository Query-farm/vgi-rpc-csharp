using QueryFarm.VgiRpc.Reflection;
using Xunit;

namespace QueryFarm.VgiRpc.Tests.Reflection;

public sealed class RpcMethodInfoTests
{
    [Fact]
    public async Task CompiledInvokerSupportsAllReturnShapes()
    {
        var target = new InvocationTarget();

        Assert.Equal(5, await InvokeAsync(target, nameof(InvocationTarget.Add), [2, 3]));
        Assert.Equal("task", await InvokeAsync(target, nameof(InvocationTarget.FromTask), []));
        Assert.Equal("value-task", await InvokeAsync(target, nameof(InvocationTarget.FromValueTask), []));
        Assert.Null(await InvokeAsync(target, nameof(InvocationTarget.VoidValueTask), []));
    }

    private static Task<object?> InvokeAsync(InvocationTarget target, string method, object?[] arguments)
    {
        var info = new RpcMethodInfo(typeof(InvocationTarget).GetMethod(method)!);
        return info.InvokeAsync(target, arguments);
    }

    private sealed class InvocationTarget
    {
        public int Add(int left, int right) => left + right;

        public Task<string> FromTask() => Task.FromResult("task");

        public ValueTask<string> FromValueTask() => ValueTask.FromResult("value-task");

        public ValueTask VoidValueTask() => ValueTask.CompletedTask;
    }
}
