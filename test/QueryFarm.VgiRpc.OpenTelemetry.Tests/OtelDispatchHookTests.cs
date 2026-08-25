using System.Diagnostics;
using System.Diagnostics.Metrics;
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;
using Xunit;

namespace QueryFarm.VgiRpc.OpenTelemetry.Tests;

/// <summary>
/// Direct + end-to-end coverage for <see cref="OtelDispatchHook"/> — a real
/// <see cref="ActivityListener"/> and <see cref="MeterListener"/> registered against the
/// "vgi_rpc" source/meter this hook creates (exactly the mechanism a real OpenTelemetry SDK
/// exporter uses), then driven both directly and through a real <see cref="RpcServer"/> dispatch
/// over an in-process pipe transport (mirrors <c>RpcServerClientTests</c>' own pattern).
/// </summary>
public sealed class OtelDispatchHookTests : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly List<(string Instrument, object? Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    public OtelDispatchHookTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "vgi_rpc",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => _activities.Add(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "vgi_rpc")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _measurements.Add((instrument.Name, value, tags.ToArray())));
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => _measurements.Add((instrument.Name, value, tags.ToArray())));
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    [Fact]
    public void OnDispatchEnd_Success_RecordsOkSpanAndMetrics()
    {
        using var hook = new OtelDispatchHook();
        var info = new DispatchHookInfo("echo_string", "unary", "Greeter", "server-1");

        var token = hook.OnDispatchStart(info);
        hook.OnDispatchEnd(token, info, error: null);

        var activity = Assert.Single(_activities);
        Assert.Equal("vgi_rpc/echo_string", activity.DisplayName);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Equal("vgi_rpc", activity.GetTagItem("rpc.system"));
        Assert.Equal("Greeter", activity.GetTagItem("rpc.service"));
        Assert.Equal("echo_string", activity.GetTagItem("rpc.method"));
        Assert.Equal("unary", activity.GetTagItem("rpc.vgi_rpc.method_type"));
        Assert.Equal("server-1", activity.GetTagItem("rpc.vgi_rpc.server_id"));

        var counter = _measurements.Single(m => m.Instrument == "rpc.server.requests");
        Assert.Equal(1L, counter.Value);
        Assert.Contains(counter.Tags, t => t.Key == "status" && (string?)t.Value == "ok");

        Assert.Contains(_measurements, m => m.Instrument == "rpc.server.duration");
    }

    [Fact]
    public void OnDispatchEnd_Error_RecordsErrorSpanAndException()
    {
        using var hook = new OtelDispatchHook();
        var info = new DispatchHookInfo("boom", "unary", "Greeter", "server-1");
        var error = new InvalidOperationException("kaboom");

        var token = hook.OnDispatchStart(info);
        hook.OnDispatchEnd(token, info, error);

        var activity = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("InvalidOperationException", activity.GetTagItem("rpc.vgi_rpc.error_type"));
        Assert.Contains(activity.Events, e => e.Name == "exception");

        var counter = _measurements.Single(m => m.Instrument == "rpc.server.requests");
        Assert.Contains(counter.Tags, t => t.Key == "status" && (string?)t.Value == "error");
    }

    [Fact]
    public void OnDispatchEnd_RecordExceptionsFalse_OmitsExceptionEvent()
    {
        using var hook = new OtelDispatchHook(recordExceptions: false);
        var info = new DispatchHookInfo("boom", "unary", "Greeter", "server-1");

        var token = hook.OnDispatchStart(info);
        hook.OnDispatchEnd(token, info, new InvalidOperationException("kaboom"));

        var activity = Assert.Single(_activities);
        Assert.DoesNotContain(activity.Events, e => e.Name == "exception");
    }

    [Fact]
    public void CustomAttributes_AppliedToSpan()
    {
        using var hook = new OtelDispatchHook(customAttributes: new Dictionary<string, object?> { ["deployment.environment"] = "test" });
        var info = new DispatchHookInfo("echo_string", "unary", "Greeter", "server-1");

        var token = hook.OnDispatchStart(info);
        hook.OnDispatchEnd(token, info, error: null);

        var activity = Assert.Single(_activities);
        Assert.Equal("test", activity.GetTagItem("deployment.environment"));
    }

    [Fact]
    public void StreamCall_UsesStreamMethodType()
    {
        using var hook = new OtelDispatchHook();
        var info = new DispatchHookInfo("produce_n", "stream", "Greeter", "server-1");

        var token = hook.OnDispatchStart(info);
        hook.OnDispatchEnd(token, info, error: null);

        var activity = Assert.Single(_activities);
        Assert.Equal("stream", activity.GetTagItem("rpc.vgi_rpc.method_type"));
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
    public async Task RealDispatch_UnarySuccess_RecordsSpan()
    {
        using var hook = new OtelDispatchHook();
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter(), dispatchHooks: [hook]);
        var connection = new RpcConnection<IGreeter>(clientTransport);
        var client = connection.CreateProxy();

        var serveTask = server.ServeOneAsync(serverTransport);
        var result = await client.EchoStringAsync("hi");
        await serveTask;

        Assert.Equal("hi", result);
        var activity = Assert.Single(_activities);
        Assert.Equal("echo_string", activity.GetTagItem("rpc.method"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    [Fact]
    public async Task RealDispatch_UnaryError_RecordsErrorSpan()
    {
        using var hook = new OtelDispatchHook();
        var (clientTransport, serverTransport) = PipeTransport.CreatePair();
        var server = new RpcServer(typeof(IGreeter), new Greeter(), dispatchHooks: [hook]);
        var connection = new RpcConnection<IGreeter>(clientTransport);
        var client = connection.CreateProxy();

        var serveTask = server.ServeOneAsync(serverTransport);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ThrowAsync());
        await serveTask;

        var activity = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }
}
