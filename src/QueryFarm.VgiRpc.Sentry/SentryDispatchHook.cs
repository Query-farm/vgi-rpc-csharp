using QueryFarm.VgiRpc.Server;
using Sentry;

namespace QueryFarm.VgiRpc.Sentry;

/// <summary>
/// Sentry error reporting (and optional performance monitoring) for <see cref="RpcServer"/>
/// dispatch — a port of the core of the canonical Python repo's
/// <c>vgi_rpc.sentry.instrument_server_sentry</c> (<c>vgi_rpc/sentry.py</c>) onto this port's
/// <see cref="IRpcDispatchHook"/> seam (M16): unhandled dispatch exceptions are reported with RPC
/// context (method name, method type, protocol, server id), and (when performance monitoring is
/// enabled — the constructor's <c>enablePerformance</c> parameter) each call gets its own
/// transaction so it shows up in Sentry's Trace Explorer/Performance views.
///
/// <para><b>This class does not manage the Sentry SDK's lifecycle</b> — matches Python's own
/// explicit stance exactly ("Users must initialize Sentry separately via <c>sentry_sdk.init()</c>
/// — this module does not manage the DSN or SDK lifecycle"). Call <c>SentrySdk.Init(...)</c> (or
/// let ASP.NET Core's own Sentry integration do it) before constructing <see cref="RpcServer"/>
/// with this hook; if the SDK was never initialized, <see cref="SentrySdk.IsEnabled"/> is
/// <see langword="false"/> and every call here is a cheap no-op.</para>
///
/// <para><b>Scope narrower than Python here</b>: the reference implementation's parameter-value
/// recording (<c>record_params</c>, with a credential-name redactor and a tag whitelist), claim
/// tags, per-exception-type ignore list, and the "auto-attach when <c>sentry_sdk.is_initialized()
/// </c>" implicit-registration magic are not ported — this port's posture is the same explicit,
/// opt-in-via-constructor-parameter pattern every other optional feature in this repo already
/// uses (JWT, CORS, sticky sessions, …), not an implicit "if a global happens to be initialized"
/// behavior. Also: wired only into <see cref="RpcServer"/>'s own dispatch loop (pipe/unix/tcp
/// transports) — matches <c>QueryFarm.VgiRpc.OpenTelemetry.OtelDispatchHook</c>'s identical
/// note on why HTTP dispatch isn't wired in yet.</para>
/// </summary>
public sealed class SentryDispatchHook : IRpcDispatchHook
{
    private readonly bool _enablePerformance;
    private readonly IReadOnlyDictionary<string, string> _customTags;

    /// <param name="enablePerformance">Start a Sentry transaction per call (visible in Trace
    /// Explorer/Performance), not just error capture — default <see langword="true"/>, matching
    /// Python's own default.</param>
    /// <param name="customTags">Extra tags attached to every transaction and captured exception.</param>
    public SentryDispatchHook(bool enablePerformance = true, IReadOnlyDictionary<string, string>? customTags = null)
    {
        _enablePerformance = enablePerformance;
        _customTags = customTags ?? new Dictionary<string, string>();
    }

    public object? OnDispatchStart(DispatchHookInfo info)
    {
        if (!SentrySdk.IsEnabled || !_enablePerformance)
        {
            return null;
        }

        var transaction = SentrySdk.StartTransaction($"vgi_rpc/{info.MethodName}", "rpc.server");
        transaction.SetTag("rpc.system", "vgi_rpc");
        transaction.SetTag("rpc.service", info.ProtocolName);
        transaction.SetTag("rpc.method", info.MethodName);
        transaction.SetTag("rpc.vgi_rpc.method_type", info.MethodType);
        transaction.SetTag("rpc.vgi_rpc.server_id", info.ServerId);
        foreach (var (key, value) in _customTags)
        {
            transaction.SetTag(key, value);
        }

        return transaction;
    }

    public void OnDispatchEnd(object? token, DispatchHookInfo info, Exception? error)
    {
        if (!SentrySdk.IsEnabled)
        {
            return;
        }

        if (error is not null)
        {
            SentrySdk.CaptureException(error, scope =>
            {
                scope.SetTag("rpc.system", "vgi_rpc");
                scope.SetTag("rpc.service", info.ProtocolName);
                scope.SetTag("rpc.method", info.MethodName);
                scope.SetTag("rpc.vgi_rpc.method_type", info.MethodType);
                scope.SetTag("rpc.vgi_rpc.server_id", info.ServerId);
                foreach (var (key, value) in _customTags)
                {
                    scope.SetTag(key, value);
                }
            });
        }

        if (token is ITransactionTracer transaction)
        {
            transaction.Finish(error is null ? SpanStatus.Ok : SpanStatus.InternalError);
        }
    }
}
