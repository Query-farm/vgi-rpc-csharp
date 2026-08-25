using System.Diagnostics;
using System.Diagnostics.Metrics;
using QueryFarm.VgiRpc.Server;

namespace QueryFarm.VgiRpc.OpenTelemetry;

/// <summary>
/// OpenTelemetry tracing (spans) and metrics (counter + histogram) for
/// <see cref="RpcServer"/> dispatch — a port of the canonical Python repo's
/// <c>vgi_rpc.otel._OtelDispatchHook</c> (<c>vgi_rpc/otel.py</c>) onto this port's
/// <see cref="IRpcDispatchHook"/> seam (M16).
///
/// <para><b>Built on <see cref="ActivitySource"/>/<see cref="Meter"/>, not the <c>OpenTelemetry</c>
/// NuGet SDK's own API surface</b> — matches the original plan's own framing exactly ("native W3C
/// trace-context propagation, no manual carrier plumbing"). These are .NET's own diagnostics
/// primitives: any OpenTelemetry SDK the *hosting application* configures (via
/// <c>AddSource("vgi_rpc")</c>/<c>AddMeter("vgi_rpc")</c>) automatically picks up every span and
/// metric this hook records — this package needs no exporter/provider configuration of its own,
/// unlike Python's explicit <c>TracerProvider</c>/<c>MeterProvider</c> wiring. This is genuinely
/// simpler than the reference implementation, not a narrowed port of it.</para>
///
/// <para><b>Scope narrower than Python here</b> — see <see cref="IRpcDispatchHook"/>'s own doc
/// comment: no caller-identity (auth principal/domain/claims) or per-call row/byte-count
/// attributes, since this port's dispatch-hook contract doesn't carry either yet. Also: wired only
/// into <see cref="RpcServer"/>'s own dispatch loop (pipe/unix/tcp transports) — HTTP dispatch
/// (<c>QueryFarm.VgiRpc.Http.RpcHttpEndpoints</c>) is a structurally separate code path this
/// hook isn't wired into yet (see <c>docs/roadmap.md</c> M16's "not implemented" note).</para>
/// </summary>
public sealed class OtelDispatchHook : IRpcDispatchHook, IDisposable
{
    private const string SourceName = "vgi_rpc";

    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _requestCounter;
    private readonly Histogram<double> _durationHistogram;
    private readonly bool _recordExceptions;
    private readonly IReadOnlyDictionary<string, object?> _customAttributes;

    /// <param name="recordExceptions">Whether to attach exception details to error spans via
    /// <see cref="Activity.AddException"/> — default <see langword="true"/>, matching Python's
    /// own default.</param>
    /// <param name="customAttributes">Extra tags merged into every span and metric — matches
    /// Python's <c>OtelConfig.custom_attributes</c>.</param>
    public OtelDispatchHook(bool recordExceptions = true, IReadOnlyDictionary<string, object?>? customAttributes = null)
    {
        _activitySource = new ActivitySource(SourceName, "0.1.0");
        _meter = new Meter(SourceName, "0.1.0");
        _requestCounter = _meter.CreateCounter<long>("rpc.server.requests", unit: "{request}", description: "Number of RPC requests handled");
        _durationHistogram = _meter.CreateHistogram<double>("rpc.server.duration", unit: "s", description: "Duration of RPC requests");
        _recordExceptions = recordExceptions;
        _customAttributes = customAttributes ?? new Dictionary<string, object?>();
    }

    public object? OnDispatchStart(DispatchHookInfo info)
    {
        var activity = _activitySource.StartActivity($"vgi_rpc/{info.MethodName}", ActivityKind.Server);
        if (activity is not null)
        {
            activity.SetTag("rpc.system", "vgi_rpc");
            activity.SetTag("rpc.service", info.ProtocolName);
            activity.SetTag("rpc.method", info.MethodName);
            activity.SetTag("rpc.vgi_rpc.method_type", info.MethodType);
            activity.SetTag("rpc.vgi_rpc.server_id", info.ServerId);
            foreach (var (key, value) in _customAttributes)
            {
                activity.SetTag(key, value);
            }
        }

        return new OtelHookState(activity, Stopwatch.GetTimestamp());
    }

    public void OnDispatchEnd(object? token, DispatchHookInfo info, Exception? error)
    {
        if (token is not OtelHookState state)
        {
            return;
        }

        var duration = Stopwatch.GetElapsedTime(state.StartTimestamp).TotalSeconds;

        if (state.Activity is { } activity)
        {
            if (error is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, error.Message);
                activity.SetTag("rpc.vgi_rpc.error_type", error.GetType().Name);
                if (_recordExceptions)
                {
                    activity.AddException(error);
                }
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }

            activity.Dispose(); // Stop()s and records the final duration.
        }

        var status = error is not null ? "error" : "ok";
        var tags = new KeyValuePair<string, object?>[]
        {
            new("rpc.system", "vgi_rpc"),
            new("rpc.service", info.ProtocolName),
            new("rpc.method", info.MethodName),
            new("rpc.vgi_rpc.method_type", info.MethodType),
            new("status", status),
        };
        _requestCounter.Add(1, tags);
        _durationHistogram.Record(duration, tags);
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }

    private sealed record OtelHookState(Activity? Activity, long StartTimestamp);
}
