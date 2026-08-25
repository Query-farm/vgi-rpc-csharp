using global::Sentry;
using global::Sentry.Extensibility;
using global::Sentry.Protocol.Envelopes;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.Sentry.Tests;

/// <summary>
/// Direct + end-to-end coverage for <see cref="SentryDispatchHook"/> — a real
/// <c>SentrySdk.Init</c> with a fake, no-network <see cref="ITransport"/> (so nothing ever leaves
/// the test process) and a <c>BeforeSend</c> hook that captures the actual <see cref="SentryEvent"/>
/// objects the hook produces, inspected directly rather than by parsing serialized envelope bytes.
/// </summary>
public sealed class SentryDispatchHookTests : IDisposable
{
    private readonly List<SentryEvent> _capturedEvents = [];
    private readonly IDisposable _sentrySdk;

    private sealed class NoopTransport : ITransport
    {
        public Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public SentryDispatchHookTests()
    {
        _sentrySdk = SentrySdk.Init(options =>
        {
            options.Dsn = "https://abc123@fake.invalid/1";
            options.Transport = new NoopTransport();
            options.TracesSampleRate = 1.0;
            options.SetBeforeSend((@event, _) =>
            {
                _capturedEvents.Add(@event);
                return @event;
            });
        });
    }

    public void Dispose() => _sentrySdk.Dispose();

    [Fact]
    public void SentrySdk_IsEnabledAfterInit()
    {
        // Sanity check the fixture itself — every other test's "no-op if disabled" behavior
        // depends on this being true.
        Assert.True(SentrySdk.IsEnabled);
    }

    [Fact]
    public async Task OnDispatchEnd_NoError_CapturesNoEvent()
    {
        var hook = new SentryDispatchHook();
        var info = new DispatchHookInfo("echo_string", "unary", "Greeter", "server-1");

        var token = hook.OnDispatchStart(info);
        hook.OnDispatchEnd(token, info, error: null);
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(_capturedEvents);
    }

    [Fact]
    public async Task OnDispatchEnd_Error_CapturesEventWithRpcTags()
    {
        var hook = new SentryDispatchHook();
        var info = new DispatchHookInfo("boom", "unary", "Greeter", "server-1");
        var error = new InvalidOperationException("kaboom");

        var token = hook.OnDispatchStart(info);
        hook.OnDispatchEnd(token, info, error);
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));

        var captured = Assert.Single(_capturedEvents);
        Assert.Equal("kaboom", captured.Exception?.Message);
        Assert.Equal("vgi_rpc", captured.Tags["rpc.system"]);
        Assert.Equal("Greeter", captured.Tags["rpc.service"]);
        Assert.Equal("boom", captured.Tags["rpc.method"]);
        Assert.Equal("unary", captured.Tags["rpc.vgi_rpc.method_type"]);
        Assert.Equal("server-1", captured.Tags["rpc.vgi_rpc.server_id"]);
    }

    [Fact]
    public async Task CustomTags_AppliedToCapturedEvent()
    {
        var hook = new SentryDispatchHook(customTags: new Dictionary<string, string> { ["deployment.environment"] = "test" });
        var info = new DispatchHookInfo("boom", "unary", "Greeter", "server-1");

        var token = hook.OnDispatchStart(info);
        hook.OnDispatchEnd(token, info, new InvalidOperationException("kaboom"));
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));

        var captured = Assert.Single(_capturedEvents);
        Assert.Equal("test", captured.Tags["deployment.environment"]);
    }

    [Fact]
    public void OnDispatchStart_PerformanceDisabled_ReturnsNullToken()
    {
        var hook = new SentryDispatchHook(enablePerformance: false);
        var info = new DispatchHookInfo("echo_string", "unary", "Greeter", "server-1");

        var token = hook.OnDispatchStart(info);

        Assert.Null(token);
        // Must not throw even with a null token from the disabled-performance path.
        hook.OnDispatchEnd(token, info, error: null);
    }

    [Fact]
    public void OnDispatchStart_PerformanceEnabled_ReturnsTransactionToken()
    {
        var hook = new SentryDispatchHook();
        var info = new DispatchHookInfo("echo_string", "unary", "Greeter", "server-1");

        var token = hook.OnDispatchStart(info);

        Assert.IsAssignableFrom<ITransactionTracer>(token);
        hook.OnDispatchEnd(token, info, error: null);
    }

    // --- End-to-end through a real RpcServer dispatch ---

    public interface IGreeter
    {
        Task<string> EchoStringAsync(string value);

        Task ThrowAsync();
    }

    public sealed class Greeter : IGreeter
    {
        public Task<string> EchoStringAsync(string value) => Task.FromResult(value);

        public Task ThrowAsync() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task RealDispatch_UnaryError_CapturesEvent()
    {
        var hook = new SentryDispatchHook();
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter(), dispatchHooks: [hook]);
        var connection = new RpcConnection<IGreeter>(clientTransport);
        var client = connection.CreateProxy();

        var serveTask = server.ServeOneAsync(serverTransport);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ThrowAsync());
        await serveTask;
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));

        var captured = Assert.Single(_capturedEvents);
        Assert.Equal("boom", captured.Exception?.Message);
        Assert.Equal("throw", captured.Tags["rpc.method"]);
    }

    [Fact]
    public async Task RealDispatch_UnarySuccess_CapturesNoEvent()
    {
        var hook = new SentryDispatchHook();
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter(), dispatchHooks: [hook]);
        var connection = new RpcConnection<IGreeter>(clientTransport);
        var client = connection.CreateProxy();

        var serveTask = server.ServeOneAsync(serverTransport);
        var result = await client.EchoStringAsync("hi");
        await serveTask;
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("hi", result);
        Assert.Empty(_capturedEvents);
    }
}
